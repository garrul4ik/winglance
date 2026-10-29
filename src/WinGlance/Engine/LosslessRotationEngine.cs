using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using WinGlance.Model;

namespace WinGlance.Engine;

public enum RotationDirection
{
    Clockwise,        // 90° CW ("]" or "R")
    CounterClockwise  // 90° CCW ("[" or "L")
}

public static class LosslessRotationEngine
{
    public static int NextOrientation(int current, RotationDirection direction)
    {
        var curr = (current is >= 1 and <= 8) ? current : 1;
        return direction switch
        {
            RotationDirection.Clockwise => curr switch
            {
                1 => 6,
                6 => 3,
                3 => 8,
                8 => 1,
                2 => 7,
                7 => 4,
                4 => 5,
                5 => 2,
                _ => 6
            },
            RotationDirection.CounterClockwise => curr switch
            {
                1 => 8,
                8 => 3,
                3 => 6,
                6 => 1,
                2 => 5,
                5 => 4,
                4 => 7,
                7 => 2,
                _ => 8
            },
            _ => 1
        };
    }

    public static int Rotate(SlideItem item, RotationDirection direction)
    {
        var primaryTarget = item.RawPath ?? item.Path;

        // 1. Read current orientation from XMP sidecar first, falling back to embedded EXIF
        var currentMeta = XmpSidecarManager.ReadMetadata(primaryTarget) ?? XmpSidecarManager.ReadMetadata(item.Path);
        int currentOrientation = currentMeta?.Orientation
            ?? FastImageDecoder.ReadExifMetadata(item.Path)?.Orientation
            ?? 1;

        int newOrientation = NextOrientation(currentOrientation, direction);

        // 2. Losslessly update embedded EXIF orientation tag (0x0112) on JPEG/TIFF files without re-encoding pixels
        foreach (var path in item.AllPaths)
        {
            if (!SupportedFormats.IsRaw(path))
            {
                TryWriteLosslessExifOrientation(path, (ushort)newOrientation);
            }
        }

        // 3. Sync XMP sidecar (canonical for RAW + Lightroom/Camera Raw interop)
        var meta = currentMeta ?? new CullingMetadata();
        meta = meta with { Orientation = newOrientation };
        XmpSidecarManager.Save(meta, primaryTarget);

        return newOrientation;
    }

    public static bool TryWriteLosslessExifOrientation(string filePath, ushort orientation)
    {
        if (!File.Exists(filePath) || orientation is < 1 or > 8) return false;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isJpeg = ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
        bool isTiff = ext is ".tif" or ".tiff";
        if (!isJpeg && !isTiff) return false;

        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fs.Length < 12) return false;

                int scanLen = (int)Math.Min(fs.Length, 262_144);
                var buf = new byte[scanLen];
                int read = fs.Read(buf, 0, scanLen);

                if (isJpeg && read >= 4 && buf[0] == 0xFF && buf[1] == 0xD8)
                {
                    if (TryPatchJpegExifOrientationInPlace(fs, buf.AsSpan(0, read), orientation, out bool hasApp1Exif))
                    {
                        return true;
                    }

                    if (!hasApp1Exif)
                    {
                        fs.Close();
                        return InjectLosslessJpegExifSegment(filePath, orientation);
                    }
                }
                else if (isTiff)
                {
                    return TryPatchTiffIfd0OrientationInPlace(fs, 0, buf.AsSpan(0, read), orientation);
                }
            }
        }
        catch
        {
            // File may be read-only; XMP sidecar still preserves orientation
        }

        return false;
    }

    private static bool TryPatchJpegExifOrientationInPlace(
        FileStream fs,
        ReadOnlySpan<byte> header,
        ushort orientation,
        out bool hasApp1Exif)
    {
        hasApp1Exif = false;
        int pos = 2;

        while (pos + 4 <= header.Length)
        {
            if (header[pos] != 0xFF) break;
            while (pos < header.Length && header[pos] == 0xFF) pos++;
            if (pos >= header.Length) break;

            byte marker = header[pos++];
            if (marker == 0xD9 || marker == 0xDA) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            if (pos + 2 > header.Length) break;
            ushort segLen = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(pos, 2));
            if (segLen < 2 || pos + segLen > header.Length) break;

            if (marker == 0xE1 && segLen >= 14)
            {
                int payloadStart = pos + 2;
                var payload = header.Slice(payloadStart, segLen - 2);
                if (payload.Length >= 8 && payload[..6].SequenceEqual("Exif\0\0"u8))
                {
                    hasApp1Exif = true;
                    long tiffFileBase = payloadStart + 6;
                    return TryPatchTiffIfd0OrientationInPlace(fs, tiffFileBase, payload[6..], orientation);
                }
            }

            pos += segLen;
        }

        return false;
    }

    private static bool TryPatchTiffIfd0OrientationInPlace(
        FileStream fs,
        long tiffFileBase,
        ReadOnlySpan<byte> tiff,
        ushort orientation)
    {
        if (tiff.Length < 8) return false;
        bool le;
        if (tiff[0] == 0x49 && tiff[1] == 0x49) le = true;
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D) le = false;
        else return false;

        ushort magic = le
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(2, 2));
        if (magic != 42) return false;

        uint ifd0 = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(4, 4));
        if (ifd0 >= tiff.Length - 2) return false;

        ushort entries = le
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice((int)ifd0, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice((int)ifd0, 2));
        if (entries > 512) return false;

        for (int i = 0; i < entries; i++)
        {
            int entryPos = (int)ifd0 + 2 + i * 12;
            if (entryPos + 12 > tiff.Length) break;

            ushort tag = le
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryPos, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(entryPos, 2));
            ushort type = le
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryPos + 2, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(entryPos + 2, 2));

            if (tag == 0x0112 && type == 3)
            {
                Span<byte> valBuf = stackalloc byte[2];
                if (le) BinaryPrimitives.WriteUInt16LittleEndian(valBuf, orientation);
                else BinaryPrimitives.WriteUInt16BigEndian(valBuf, orientation);

                fs.Position = tiffFileBase + entryPos + 8;
                fs.Write(valBuf);
                fs.Flush();
                return true;
            }
        }

        return false;
    }

    private static bool InjectLosslessJpegExifSegment(string filePath, ushort orientation)
    {
        var origBytes = File.ReadAllBytes(filePath);
        if (origBytes.Length < 4 || origBytes[0] != 0xFF || origBytes[1] != 0xD8) return false;

        // Insert right after SOI (or after APP0 JFIF segment if APP0 immediately follows SOI)
        int insertPos = 2;
        if (origBytes.Length >= 6 && origBytes[2] == 0xFF && origBytes[3] == 0xE0)
        {
            ushort app0Len = BinaryPrimitives.ReadUInt16BigEndian(origBytes.AsSpan(4, 2));
            if (app0Len >= 2 && 4 + app0Len <= origBytes.Length)
            {
                insertPos = 4 + app0Len;
            }
        }

        // Minimal 32-byte APP1 Exif segment with little-endian TIFF IFD0 and 1 tag (0x0112 Orientation)
        Span<byte> app1 = stackalloc byte[32];
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.Slice(2, 2), 30); // segment length excluding FF E1
        "Exif\0\0"u8.CopyTo(app1.Slice(4, 6));
        // TIFF header @ offset 10
        app1[10] = (byte)'I';
        app1[11] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(12, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(app1.Slice(14, 4), 8); // IFD0 @ TIFF offset 8 (byte 18)
        // IFD0 @ offset 18
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(18, 2), 1); // 1 entry
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(20, 2), 0x0112); // Orientation tag
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(22, 2), 3);      // Type = SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(app1.Slice(24, 4), 1);      // Count = 1
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(28, 2), orientation);
        BinaryPrimitives.WriteUInt16LittleEndian(app1.Slice(30, 2), 0);

        // Plus 4-byte next-IFD pointer = 0 -> total segment = 36 bytes (length field = 34)
        Span<byte> fullApp1 = stackalloc byte[36];
        app1.CopyTo(fullApp1);
        BinaryPrimitives.WriteUInt16BigEndian(fullApp1.Slice(2, 2), 34);
        fullApp1.Slice(32, 4).Clear();

        var tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                outFs.Write(origBytes, 0, insertPos);
                outFs.Write(fullApp1);
                outFs.Write(origBytes, insertPos, origBytes.Length - insertPos);
            }
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
