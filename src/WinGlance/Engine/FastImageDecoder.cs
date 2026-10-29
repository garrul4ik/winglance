using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Vortice.WIC;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class FastImageDecoder
{
    [ThreadStatic]
    private static IWICImagingFactory2? _threadWicFactory;

    private static IWICImagingFactory2 GetWicFactory()
    {
        return _threadWicFactory ??= new IWICImagingFactory2();
    }

    /// <summary>
    /// Decodes an image or camera RAW file into CPU/GPU-ready BGRA32 or 16-bit RGBA64 pixels.
    /// When maxBoundingPixelSize &lt;= 0 on a RAW file, performs full 16-bit LibRaw sensor demosaicing.
    /// </summary>
    public static DecodedImage Decode(string filePath, int maxBoundingPixelSize = 0)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file not found", filePath);
        }

        bool isRaw = SupportedFormats.IsRaw(filePath);
        int? xmpOrientation = XmpSidecarManager.ReadMetadata(filePath)?.Orientation;

        if (isRaw)
        {
            if (maxBoundingPixelSize <= 0)
            {
                // 100% 1:1 Zoom: Full 16-bit sensor demosaicing via bundled LibRaw
                try
                {
                    var fallbackExif = ReadExifMetadata(filePath);
                    var raw16 = RawSensorDecoder.DecodeFullSensor16Bit(filePath, fallbackExif);
                    if (xmpOrientation is >= 2 and <= 8 && xmpOrientation != (fallbackExif?.Orientation ?? 1))
                    {
                        return TransformRgbaOrBgraSoftware(raw16, xmpOrientation.Value);
                    }
                    return raw16;
                }
                catch
                {
                    // Fall back to embedded preview or WIC if sensor unpack fails
                }
            }

            // Fast screen-bounded preview: zero-copy MemoryMapped header scan + LibRaw thumb fallback
            var rawPreview = TryDecodeEmbeddedRawPreview(filePath, maxBoundingPixelSize, xmpOrientation);
            if (rawPreview != null)
            {
                return rawPreview;
            }
        }

        // Standard WIC decode (JPEG, PNG, HEIC, AVIF, WebP, TIFF, JXL, etc.)
        try
        {
            return DecodeViaWic(filePath, maxBoundingPixelSize, isRaw, xmpOrientation);
        }
        catch when (isRaw)
        {
            var fallbackExif = ReadExifMetadata(filePath);
            return RawSensorDecoder.DecodeFullSensor16Bit(filePath, fallbackExif);
        }
    }

    private static DecodedImage DecodeViaWic(string filePath, int maxBoundingPixelSize, bool isRaw, int? xmpOrientation)
    {
        var wic = GetWicFactory();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);

        var exif = ReadExifFromFileOrWic(filePath, frame);
        int orientation = xmpOrientation ?? exif?.Orientation ?? 1;

        var (iccBytes, profileName) = ExtractIccProfile(wic, frame);
        return DecodeWicFrame(wic, frame, maxBoundingPixelSize, isRaw, orientation, iccBytes, profileName, exif);
    }

    private static DecodedImage DecodeJpegBytesViaWic(
        byte[] jpegBytes,
        int maxBoundingPixelSize,
        bool isRaw,
        ExifMetadata? parentExif,
        int? xmpOrientation)
    {
        var wic = GetWicFactory();
        using var ms = new MemoryStream(jpegBytes, writable: false);
        using var decoder = wic.CreateDecoderFromStream(ms, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);

        var previewExif = ParseBinaryExifFromJpeg(jpegBytes) ?? ReadExifFromWicFrame(frame);
        var mergedExif = MergeExif(parentExif, previewExif);
        int orientation = xmpOrientation
            ?? (previewExif?.Orientation is > 1 ? previewExif.Orientation : null)
            ?? parentExif?.Orientation
            ?? 1;

        var (iccBytes, profileName) = ExtractIccProfile(wic, frame);
        return DecodeWicFrame(wic, frame, maxBoundingPixelSize, isRaw, orientation, iccBytes, profileName, mergedExif);
    }

    private static DecodedImage DecodeWicFrame(
        IWICImagingFactory2 wic,
        IWICBitmapFrameDecode frame,
        int maxBoundingPixelSize,
        bool isRaw,
        int orientation,
        byte[]? iccBytes,
        string profileName,
        ExifMetadata? exif)
    {
        var origSize = frame.Size;
        int rawOrigW = origSize.Width;
        int rawOrigH = origSize.Height;

        int targetW = rawOrigW;
        int targetH = rawOrigH;
        bool isDownscaled = false;

        if (maxBoundingPixelSize > 0 && Math.Max(rawOrigW, rawOrigH) > maxBoundingPixelSize)
        {
            double scale = (double)maxBoundingPixelSize / Math.Max(rawOrigW, rawOrigH);
            targetW = Math.Max(1, (int)Math.Round(rawOrigW * scale));
            targetH = Math.Max(1, (int)Math.Round(rawOrigH * scale));
            isDownscaled = true;
        }

        Guid srcFormat = frame.PixelFormat;
        bool want16Bit = maxBoundingPixelSize <= 0 && IsHighBitDepthPixelFormat(srcFormat);
        Guid dstWicFormat = want16Bit ? PixelFormat.Format64bppRGBA : PixelFormat.Format32bppBGRA;
        var outPixelFormat = want16Bit ? ImagePixelFormat.Rgba64 : ImagePixelFormat.Bgra32;
        int bytesPerPixel = want16Bit ? 8 : 4;

        IWICBitmapScaler? scaler = null;
        IWICFormatConverter? converter = null;
        IWICBitmapFlipRotator? rotator = null;

        try
        {
            IWICBitmapSource currentSource = frame;

            if (isDownscaled)
            {
                scaler = wic.CreateBitmapScaler();
                scaler.Initialize(currentSource, (uint)targetW, (uint)targetH, BitmapInterpolationMode.HighQualityCubic);
                currentSource = scaler;
            }

            if (currentSource.PixelFormat != dstWicFormat)
            {
                converter = wic.CreateFormatConverter();
                var hr = converter.Initialize(currentSource, dstWicFormat);
                if (hr.Failure && want16Bit)
                {
                    converter.Dispose();
                    converter = wic.CreateFormatConverter();
                    dstWicFormat = PixelFormat.Format32bppBGRA;
                    outPixelFormat = ImagePixelFormat.Bgra32;
                    bytesPerPixel = 4;
                    converter.Initialize(currentSource, dstWicFormat).CheckError();
                }
                else
                {
                    hr.CheckError();
                }
                currentSource = converter;
            }

            var transform = MapOrientationToWicTransform(orientation);
            if (transform != BitmapTransformOptions.Rotate0)
            {
                rotator = wic.CreateBitmapFlipRotator();
                rotator.Initialize(currentSource, transform);
                currentSource = rotator;
            }

            var finalSize = currentSource.Size;
            int finalW = finalSize.Width;
            int finalH = finalSize.Height;
            int stride = finalW * bytesPerPixel;
            var pixels = new byte[finalH * stride];
            currentSource.CopyPixels((uint)stride, pixels);

            bool swapsAxes = orientation is 5 or 6 or 7 or 8;
            int reportedOrigW = swapsAxes ? rawOrigH : rawOrigW;
            int reportedOrigH = swapsAxes ? rawOrigW : rawOrigH;

            return new DecodedImage(
                pixels: pixels,
                width: finalW,
                height: finalH,
                stride: stride,
                pixelFormat: outPixelFormat,
                originalWidth: reportedOrigW,
                originalHeight: reportedOrigH,
                isDownscaled: isDownscaled || (isRaw && maxBoundingPixelSize > 0),
                isRaw: isRaw,
                isFullSensorRaw: false,
                iccProfile: iccBytes,
                colorProfileName: profileName,
                exif: exif
            );
        }
        finally
        {
            rotator?.Dispose();
            converter?.Dispose();
            scaler?.Dispose();
        }
    }

    private static bool IsHighBitDepthPixelFormat(Guid fmt)
    {
        return fmt == PixelFormat.Format64bppRGBA ||
               fmt == PixelFormat.Format64bppBGRA ||
               fmt == PixelFormat.Format64bppPRGBA ||
               fmt == PixelFormat.Format64bppPBGRA ||
               fmt == PixelFormat.Format48bppRGB ||
               fmt == PixelFormat.Format48bppBGR ||
               fmt == PixelFormat.Format64bppRGBAHalf ||
               fmt == PixelFormat.Format48bppRGBHalf ||
               fmt == PixelFormat.Format32bppRGBA1010102 ||
               fmt == PixelFormat.Format32bppR10G10B10A2 ||
               fmt == PixelFormat.Format32bppR10G10B10A2HDR10;
    }

    private static BitmapTransformOptions MapOrientationToWicTransform(int orientation) => orientation switch
    {
        2 => BitmapTransformOptions.FlipHorizontal,
        3 => BitmapTransformOptions.Rotate180,
        4 => BitmapTransformOptions.FlipVertical,
        5 => BitmapTransformOptions.Rotate270 | BitmapTransformOptions.FlipHorizontal,
        6 => BitmapTransformOptions.Rotate90,
        7 => BitmapTransformOptions.Rotate90 | BitmapTransformOptions.FlipHorizontal,
        8 => BitmapTransformOptions.Rotate270,
        _ => BitmapTransformOptions.Rotate0
    };

    #region ICC Color Profile Extraction

    private static (byte[]? Bytes, string Name) ExtractIccProfile(IWICImagingFactory2 wic, IWICBitmapFrameDecode frame)
    {
        try
        {
            frame.GetColorContexts(0, null!, out uint count);
            if (count == 0) return (null, "sRGB");

            using var cc = wic.CreateColorContext();
            var contexts = new IWICColorContext[] { cc };
            frame.GetColorContexts(1, contexts, out uint actual);
            if (actual == 0) return (null, "sRGB");

            if (cc.Type == ColorContextType.Profile)
            {
                cc.GetProfileBytes(null!, out uint byteLen);
                if (byteLen > 128 && byteLen < 4 * 1024 * 1024)
                {
                    var bytes = new byte[byteLen];
                    if (cc.GetProfileBytes(bytes, out uint readLen).Success && readLen > 128)
                    {
                        var name = DetectIccProfileName(bytes);
                        return (bytes, name);
                    }
                }
            }
            else if (cc.Type == ColorContextType.ExifColorSpace && cc.ExifColorSpace == 2)
            {
                return (null, "Adobe RGB");
            }
        }
        catch
        {
            // Ignore malformed ICC profile headers
        }

        return (null, "sRGB");
    }

    public static string DetectIccProfileName(ReadOnlySpan<byte> icc)
    {
        if (icc.Length < 132) return "sRGB";

        string? desc = TryReadIccDescriptionTag(icc);
        if (string.IsNullOrWhiteSpace(desc))
        {
            return "ICC Custom";
        }

        if (desc.Contains("Display P3", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("DCI-P3", StringComparison.OrdinalIgnoreCase) ||
            desc.Equals("P3", StringComparison.OrdinalIgnoreCase))
        {
            return "Display P3";
        }
        if (desc.Contains("Adobe RGB", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("AdobeRGB", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("ClayRGB", StringComparison.OrdinalIgnoreCase))
        {
            return "Adobe RGB";
        }
        if (desc.Contains("ProPhoto", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("ROMM RGB", StringComparison.OrdinalIgnoreCase))
        {
            return "ProPhoto RGB";
        }
        if (desc.Contains("2020", StringComparison.OrdinalIgnoreCase))
        {
            return "Rec.2020";
        }
        if (desc.Contains("sRGB", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("IEC61966", StringComparison.OrdinalIgnoreCase))
        {
            return "sRGB";
        }

        var clean = desc.Trim();
        return clean.Length <= 22 ? clean : clean[..22].Trim();
    }

    private static string? TryReadIccDescriptionTag(ReadOnlySpan<byte> icc)
    {
        uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(icc.Slice(128, 4));
        if (tagCount > 128) tagCount = 128;

        for (int i = 0; i < (int)tagCount; i++)
        {
            int entryPos = 132 + i * 12;
            if (entryPos + 12 > icc.Length) break;

            // 'desc' = 0x64657363
            uint sig = BinaryPrimitives.ReadUInt32BigEndian(icc.Slice(entryPos, 4));
            if (sig != 0x64657363) continue;

            uint offset = BinaryPrimitives.ReadUInt32BigEndian(icc.Slice(entryPos + 4, 4));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(icc.Slice(entryPos + 8, 4));
            if (offset + 12 > icc.Length || size < 12) return null;

            int tagEnd = (int)Math.Min((long)icc.Length, (long)offset + size);
            var tagSpan = icc.Slice((int)offset, tagEnd - (int)offset);
            uint typeSig = BinaryPrimitives.ReadUInt32BigEndian(tagSpan[..4]);

            // ICC v2 'desc'
            if (typeSig == 0x64657363 && tagSpan.Length >= 12)
            {
                uint asciiLen = BinaryPrimitives.ReadUInt32BigEndian(tagSpan.Slice(8, 4));
                int len = (int)Math.Min((long)asciiLen, tagSpan.Length - 12);
                if (len > 0)
                {
                    return Encoding.ASCII.GetString(tagSpan.Slice(12, len)).TrimEnd('\0', ' ');
                }
            }
            // ICC v4 'mluc' (UTF-16BE)
            else if (typeSig == 0x6D6C7563 && tagSpan.Length >= 28)
            {
                uint recordCount = BinaryPrimitives.ReadUInt32BigEndian(tagSpan.Slice(8, 4));
                if (recordCount >= 1)
                {
                    uint strLen = BinaryPrimitives.ReadUInt32BigEndian(tagSpan.Slice(20, 4));
                    uint strOffset = BinaryPrimitives.ReadUInt32BigEndian(tagSpan.Slice(24, 4));
                    if (strOffset < tagSpan.Length && strLen > 0)
                    {
                        int avail = (int)Math.Min((long)strLen, tagSpan.Length - (int)strOffset);
                        return Encoding.BigEndianUnicode.GetString(tagSpan.Slice((int)strOffset, avail)).TrimEnd('\0', ' ');
                    }
                }
            }
        }

        return null;
    }

    #endregion

    #region Zero-Copy MemoryMapped RAW Preview Extraction

    private static DecodedImage? TryDecodeEmbeddedRawPreview(string filePath, int maxBoundingPixelSize, int? xmpOrientation)
    {
        try
        {
            var parentExif = ReadExifMetadata(filePath);
            var jpegBytes = ExtractEmbeddedRawPreviewBytes(filePath);

            if (jpegBytes == null || jpegBytes.Length < 50_000)
            {
                var fallbackJpeg = RawSensorDecoder.TryExtractPreviewBytes(filePath, out var libRawExif, out var bitmapThumb);
                if (fallbackJpeg != null && (jpegBytes == null || fallbackJpeg.Length > jpegBytes.Length))
                {
                    jpegBytes = fallbackJpeg;
                    parentExif ??= libRawExif;
                }
                else if (bitmapThumb != null && jpegBytes == null)
                {
                    return xmpOrientation is >= 2 and <= 8
                        ? TransformRgbaOrBgraSoftware(bitmapThumb, xmpOrientation.Value)
                        : bitmapThumb;
                }
            }

            if (jpegBytes == null || jpegBytes.Length < 256)
            {
                return null;
            }

            return DecodeJpegBytesViaWic(jpegBytes, maxBoundingPixelSize, isRaw: true, parentExif, xmpOrientation);
        }
        catch
        {
            return null;
        }
    }

    public static unsafe byte[]? ExtractEmbeddedRawPreviewBytes(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length < 32) return null;

        long fileLen = fi.Length;
        var ext = fi.Extension.ToLowerInvariant();

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.Read);
        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            if (ext == ".raf")
            {
                return ExtractFujiRafPreview(basePtr, fileLen);
            }
            if (ext == ".cr3")
            {
                return ExtractCr3Preview(basePtr, fileLen);
            }

            // Try Fujifilm magic first in case extension differs, then TIFF/RW2/ORF
            return ExtractFujiRafPreview(basePtr, fileLen)
                ?? ExtractTiffOrRw2Preview(basePtr, fileLen);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe byte[]? ExtractFujiRafPreview(byte* ptr, long fileLen)
    {
        // Fujifilm .RAF header: 16-byte magic "FUJIFILMCCD-RAW ", uint32 BE JPEG offset @ 0x54, uint32 BE JPEG len @ 0x58
        if (fileLen < 0x60) return null;
        ReadOnlySpan<byte> header = new(ptr, 16);
        if (!header.SequenceEqual("FUJIFILMCCD-RAW "u8)) return null;

        uint jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(ptr + 0x54, 4));
        uint jpegLen = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(ptr + 0x58, 4));

        if (jpegOffset > 0 && jpegLen > 256 && (long)jpegOffset + jpegLen <= fileLen && IsJpegSoi(ptr + jpegOffset, jpegLen))
        {
            var result = new byte[jpegLen];
            new ReadOnlySpan<byte>(ptr + jpegOffset, (int)jpegLen).CopyTo(result);
            return result;
        }
        return null;
    }

    private static unsafe byte[]? ExtractCr3Preview(byte* ptr, long fileLen)
    {
        byte[]? bestJpeg = null;
        long bestLen = 0;
        ScanIsoBmffForPrvw(ptr, 0, fileLen, fileLen, depth: 0, ref bestJpeg, ref bestLen);
        return bestJpeg;
    }

    private static unsafe void ScanIsoBmffForPrvw(
        byte* ptr,
        long start,
        long end,
        long fileLen,
        int depth,
        ref byte[]? bestJpeg,
        ref long bestLen)
    {
        if (depth > 8) return;
        long pos = start;

        while (pos + 8 <= end)
        {
            uint boxLen32 = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(ptr + pos, 4));
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(ptr + pos + 4, 4));

            long boxLen = boxLen32;
            long headerSize = 8;
            if (boxLen32 == 1)
            {
                if (pos + 16 > end) break;
                boxLen = BinaryPrimitives.ReadInt64BigEndian(new ReadOnlySpan<byte>(ptr + pos + 8, 8));
                headerSize = 16;
            }
            else if (boxLen32 == 0)
            {
                boxLen = end - pos;
            }

            if (boxLen < headerSize || pos + boxLen > end) break;

            // 'PRVW' = 0x50525657, 'THMB' = 0x54484D42, 'thmb' = 0x74686D62
            if (boxType == 0x50525657 || boxType == 0x54484D42 || boxType == 0x74686D62)
            {
                long payloadStart = pos + headerSize;
                long payloadLen = boxLen - headerSize;
                for (long i = 0; i + 3 < payloadLen && i < 256; i++)
                {
                    if (IsJpegSoi(ptr + payloadStart + i, payloadLen - i))
                    {
                        int jpegLen = (int)(payloadLen - i);
                        if (jpegLen > bestLen)
                        {
                            bestLen = jpegLen;
                            var jpeg = new byte[jpegLen];
                            new ReadOnlySpan<byte>(ptr + payloadStart + i, jpegLen).CopyTo(jpeg);
                            bestJpeg = jpeg;
                        }
                        break;
                    }
                }
            }
            // Container boxes:
            // 'uuid' = 0x75756964 (16-byte UUID after box header)
            else if (boxType == 0x75756964 && boxLen > headerSize + 16)
            {
                long childStart = pos + headerSize + 16;
                // Canon preview UUID container (EAF42B5E...) has an 8-byte subheader (version 1): 0x00000000_00000001
                if (childStart + 8 <= pos + boxLen && BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(ptr + childStart, 4)) == 0)
                {
                    childStart += 8;
                }
                ScanIsoBmffForPrvw(ptr, childStart, pos + boxLen, fileLen, depth + 1, ref bestJpeg, ref bestLen);
            }
            // Container boxes: 'moov', 'trak', 'mdia', 'minf', 'stbl', 'edts'
            else if ((boxType == 0x6D6F6F76 || boxType == 0x7472616B || boxType == 0x6D646961 ||
                      boxType == 0x6D696E66 || boxType == 0x7374626C || boxType == 0x65647473) && boxLen > headerSize)
            {
                ScanIsoBmffForPrvw(ptr, pos + headerSize, pos + boxLen, fileLen, depth + 1, ref bestJpeg, ref bestLen);
            }

            pos += boxLen;
        }
    }

    private static unsafe byte[]? ExtractTiffOrRw2Preview(byte* ptr, long fileLen)
    {
        if (fileLen < 16) return null;

        ushort bom = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ptr, 2));
        bool le;
        if (bom == 0x4949) le = true;
        else if (bom == 0x4D4D) le = false;
        else return null;

        ushort magic = ReadU16(ptr + 2, le);
        // 42 = Standard TIFF/ARW/NEF/CR2/DNG, 0x4F52 / 0x5352 = Olympus ORF, 0x0055 = Panasonic RW2
        if (magic != 42 && magic != 0x4F52 && magic != 0x5352 && magic != 0x0055)
        {
            return null;
        }

        uint ifd0Offset = ReadU32(ptr + 4, le);
        if (ifd0Offset == 0 || ifd0Offset >= fileLen - 2) return null;

        long bestOffset = 0;
        long bestLength = 0;

        var visited = new HashSet<long>();
        var queue = new Queue<long>();
        queue.Enqueue(ifd0Offset);

        while (queue.Count > 0 && visited.Count < 16)
        {
            long ifdPos = queue.Dequeue();
            if (ifdPos <= 0 || ifdPos >= fileLen - 2 || !visited.Add(ifdPos)) continue;

            ushort numEntries = ReadU16(ptr + ifdPos, le);
            if (numEntries > 512) continue;

            long curJpegOffset = 0;
            long curJpegLen = 0;
            long curStripOffset = 0;
            long curStripLen = 0;

            long entryPos = ifdPos + 2;
            for (int i = 0; i < numEntries; i++, entryPos += 12)
            {
                if (entryPos + 12 > fileLen) break;

                ushort tag = ReadU16(ptr + entryPos, le);
                ushort type = ReadU16(ptr + entryPos + 2, le);
                uint count = ReadU32(ptr + entryPos + 4, le);
                uint valOrOffset = ReadU32(ptr + entryPos + 8, le);

                if (tag == 0x002E) // Panasonic RW2 JpgFromRaw
                {
                    if (valOrOffset > 0 && count > 256 && (long)valOrOffset + count <= fileLen &&
                        IsJpegSoi(ptr + valOrOffset, count))
                    {
                        if (count > bestLength)
                        {
                            bestOffset = valOrOffset;
                            bestLength = count;
                        }
                    }
                }
                else if (tag == 0x0201) // JPEGInterchangeFormat
                {
                    curJpegOffset = valOrOffset;
                }
                else if (tag == 0x0202) // JPEGInterchangeFormatLength
                {
                    curJpegLen = valOrOffset;
                }
                else if (tag == 0x0111 && count == 1) // StripOffsets
                {
                    curStripOffset = valOrOffset;
                }
                else if (tag == 0x0117 && count == 1) // StripByteCounts
                {
                    curStripLen = valOrOffset;
                }
                else if (tag == 0x014A && count > 0) // SubIFDs
                {
                    if (count == 1)
                    {
                        if (valOrOffset > 0 && valOrOffset < fileLen) queue.Enqueue(valOrOffset);
                    }
                    else if (count <= 16 && (long)valOrOffset + count * 4 <= fileLen)
                    {
                        for (uint s = 0; s < count; s++)
                        {
                            uint subIfd = ReadU32(ptr + valOrOffset + s * 4, le);
                            if (subIfd > 0 && subIfd < fileLen) queue.Enqueue(subIfd);
                        }
                    }
                }
                else if (type == 13 && count == 1 && valOrOffset > 0 && valOrOffset < fileLen)
                {
                    queue.Enqueue(valOrOffset);
                }
            }

            TryAcceptCandidate(ptr, fileLen, curJpegOffset, curJpegLen, ref bestOffset, ref bestLength);
            TryAcceptCandidate(ptr, fileLen, curStripOffset, curStripLen, ref bestOffset, ref bestLength);

            if (entryPos + 4 <= fileLen)
            {
                uint nextIfd = ReadU32(ptr + entryPos, le);
                if (nextIfd > 0 && nextIfd < fileLen) queue.Enqueue(nextIfd);
            }
        }

        if (bestOffset > 0 && bestLength > 256 && bestOffset + bestLength <= fileLen)
        {
            var result = new byte[bestLength];
            new ReadOnlySpan<byte>(ptr + bestOffset, (int)bestLength).CopyTo(result);
            return result;
        }

        return null;
    }

    private static unsafe void TryAcceptCandidate(
        byte* ptr,
        long fileLen,
        long offset,
        long length,
        ref long bestOffset,
        ref long bestLength)
    {
        if (offset > 0 && length > 256 && offset + length <= fileLen && length > bestLength &&
            IsJpegSoi(ptr + offset, length))
        {
            bestOffset = offset;
            bestLength = length;
        }
    }

    private static unsafe bool IsJpegSoi(byte* p, long availLen)
    {
        if (availLen < 4 || p[0] != 0xFF || p[1] != 0xD8 || p[2] != 0xFF) return false;
        byte marker = p[3];
        // Standard viewable JPEG marker: APP0..APP15 (0xE0..0xEF), DQT (0xDB), SOF0 Baseline (0xC0), SOF2 Progressive (0xC2)
        // Explicitly excludes 0xC3 (SOF3 Lossless JPEG raw bayer sensor streams)
        return marker is (>= 0xE0 and <= 0xEF) or 0xDB or 0xC0 or 0xC2;
    }

    #endregion

    #region Monolithic Binary EXIF + WIC Metadata Reader

    public static ExifMetadata? ReadExifMetadata(string filePath)
    {
        return ReadExifFromFileOrWic(filePath, wicFrame: null);
    }

    private static ExifMetadata? ReadExifFromFileOrWic(string filePath, IWICBitmapFrameDecode? wicFrame)
    {
        try
        {
            if (File.Exists(filePath))
            {
                // Read first 256 KB to parse JPEG APP1, TIFF/RAW IFD0+ExifIFD, Fuji RAF, or Canon CR3 CMT1/CMT2
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int toRead = (int)Math.Min(fs.Length, 262_144);
                if (toRead >= 12)
                {
                    var header = new byte[toRead];
                    int read = fs.Read(header, 0, toRead);
                    var binExif = ParseBinaryExifFromContainer(header.AsSpan(0, read));
                    if (binExif != null) return binExif;
                }
            }
        }
        catch { }

        if (wicFrame != null)
        {
            var fromFrame = ReadExifFromWicFrame(wicFrame);
            if (fromFrame != null) return fromFrame;
        }
        else
        {
            try
            {
                var wic = GetWicFactory();
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dec = wic.CreateDecoderFromStream(fs, DecodeOptions.CacheOnDemand);
                using var frame = dec.GetFrame(0);
                var fromWic = ReadExifFromWicFrame(frame);
                if (fromWic != null) return fromWic;
            }
            catch { }
        }

        if (SupportedFormats.IsRaw(filePath))
        {
            return RawSensorDecoder.TryExtractExif(filePath);
        }

        return null;
    }

    private static ExifMetadata? ParseBinaryExifFromContainer(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;

        // 1. JPEG (FF D8)
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return ParseBinaryExifFromJpeg(data);
        }

        // 2. TIFF / ARW / NEF / DNG / CR2 / ORF / RW2
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
        {
            return ParseBinaryTiffExif(data);
        }

        // 3. Fujifilm .RAF: jump to embedded JPEG header if within read window, or scan for Exif\0\0
        if (data.Length >= 0x60 && data[..16].SequenceEqual("FUJIFILMCCD-RAW "u8))
        {
            uint jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x54, 4));
            if (jpegOffset > 0 && jpegOffset + 16 < data.Length)
            {
                return ParseBinaryExifFromJpeg(data[(int)jpegOffset..]);
            }
        }

        // 4. Canon .CR3: scan for CMT1 / CMT2 TIFF blocks inside moov/uuid
        for (int i = 4; i + 16 < data.Length; i++)
        {
            if (data[i] == (byte)'C' && data[i + 1] == (byte)'M' && data[i + 2] == (byte)'T' && data[i + 3] == (byte)'1')
            {
                var ifd0 = ParseBinaryTiffExif(data[(i + 4)..]);
                // Also look for CMT2 (ExifIFD) right after CMT1
                ExifMetadata? exifIfd = null;
                for (int j = i + 8; j + 16 < data.Length; j++)
                {
                    if (data[j] == (byte)'C' && data[j + 1] == (byte)'M' && data[j + 2] == (byte)'T' && data[j + 3] == (byte)'2')
                    {
                        exifIfd = ParseBinaryTiffExif(data[(j + 4)..]);
                        break;
                    }
                }
                return MergeExif(ifd0, exifIfd);
            }
        }

        return null;
    }

    private static ExifMetadata? ParseBinaryExifFromJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) break;
            while (pos < jpeg.Length && jpeg[pos] == 0xFF) pos++;
            if (pos >= jpeg.Length) break;

            byte marker = jpeg[pos++];
            if (marker == 0xD9 || marker == 0xDA) break; // EOI or SOS
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            if (pos + 2 > jpeg.Length) break;
            ushort segLen = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos, 2));
            if (segLen < 2 || pos + segLen > jpeg.Length) break;

            if (marker == 0xE1 && segLen >= 14)
            {
                var payload = jpeg.Slice(pos + 2, segLen - 2);
                if (payload.Length >= 8 && payload[..6].SequenceEqual("Exif\0\0"u8))
                {
                    var parsed = ParseBinaryTiffExif(payload[6..]);
                    if (parsed != null) return parsed;
                }
            }

            pos += segLen;
        }

        return null;
    }

    private static ExifMetadata? ParseBinaryTiffExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool le;
        if (tiff[0] == 0x49 && tiff[1] == 0x49) le = true;
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D) le = false;
        else return null;

        ushort magic = le
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(2, 2));
        if (magic != 42 && magic != 0x4F52 && magic != 0x5352 && magic != 0x0055) return null;

        uint ifd0 = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(4, 4));
        if (ifd0 >= tiff.Length - 2) return null;

        string? make = null, model = null, lens = null, dateOrig = null;
        double? shutter = null, aperture = null, focal = null, bias = null;
        int? iso = null, focal35 = null, orientation = null;
        bool? flash = null;
        uint exifIfdOffset = 0;

        ParseSingleIfd(tiff, (int)ifd0, le,
            ref make, ref model, ref lens, ref dateOrig,
            ref shutter, ref aperture, ref iso, ref focal, ref focal35, ref bias, ref flash, ref orientation,
            ref exifIfdOffset);

        if (exifIfdOffset > 0 && exifIfdOffset < tiff.Length - 2)
        {
            uint dummy = 0;
            ParseSingleIfd(tiff, (int)exifIfdOffset, le,
                ref make, ref model, ref lens, ref dateOrig,
                ref shutter, ref aperture, ref iso, ref focal, ref focal35, ref bias, ref flash, ref orientation,
                ref dummy);
        }

        if (make == null && model == null && lens == null && shutter == null &&
            aperture == null && iso == null && focal == null && orientation == null && dateOrig == null)
        {
            return null;
        }

        return new ExifMetadata(
            CameraMake: make,
            CameraModel: model,
            LensModel: lens,
            ShutterSpeed: shutter,
            Aperture: aperture,
            Iso: iso,
            FocalLength: focal,
            FocalLength35mm: focal35,
            ExposureBias: bias,
            FlashFired: flash,
            DateTimeOriginal: dateOrig,
            Orientation: orientation
        );
    }

    private static void ParseSingleIfd(
        ReadOnlySpan<byte> tiff,
        int ifdOffset,
        bool le,
        ref string? make,
        ref string? model,
        ref string? lens,
        ref string? dateOrig,
        ref double? shutter,
        ref double? aperture,
        ref int? iso,
        ref double? focal,
        ref int? focal35,
        ref double? bias,
        ref bool? flash,
        ref int? orientation,
        ref uint exifIfdOffset)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length) return;
        ushort countEntries = ReadSpanU16(tiff, ifdOffset, le);
        if (countEntries > 512) return;

        for (int i = 0; i < countEntries; i++)
        {
            int pos = ifdOffset + 2 + i * 12;
            if (pos + 12 > tiff.Length) break;

            ushort tag = ReadSpanU16(tiff, pos, le);
            ushort type = ReadSpanU16(tiff, pos + 2, le);
            uint count = ReadSpanU32(tiff, pos + 4, le);
            var valBytes = tiff.Slice(pos + 8, 4);
            uint valOffset = ReadSpanU32(tiff, pos + 8, le);

            switch (tag)
            {
                case 0x010F: // Make
                    make ??= ReadTiffAscii(tiff, type, count, valBytes, valOffset);
                    break;
                case 0x0110: // Model
                    model ??= ReadTiffAscii(tiff, type, count, valBytes, valOffset);
                    break;
                case 0x0112: // Orientation
                    orientation ??= ReadTiffInt(tiff, type, count, valBytes, valOffset, le);
                    break;
                case 0x8769: // ExifIFDPointer
                    exifIfdOffset = valOffset;
                    break;
                case 0x829A: // ExposureTime
                    shutter ??= ReadTiffUnsignedRational(tiff, type, valOffset, le);
                    break;
                case 0x829D: // FNumber
                    aperture ??= ReadTiffUnsignedRational(tiff, type, valOffset, le);
                    break;
                case 0x8827: // PhotographicSensitivity (ISO)
                    iso ??= ReadTiffInt(tiff, type, count, valBytes, valOffset, le);
                    break;
                case 0x9003: // DateTimeOriginal
                    dateOrig ??= ReadTiffAscii(tiff, type, count, valBytes, valOffset);
                    break;
                case 0x9204: // ExposureBiasValue
                    bias ??= ReadTiffSignedRational(tiff, type, valOffset, le);
                    break;
                case 0x9209: // Flash
                    var flashVal = ReadTiffInt(tiff, type, count, valBytes, valOffset, le);
                    if (flashVal.HasValue) flash ??= (flashVal.Value & 1) != 0;
                    break;
                case 0x920A: // FocalLength
                    focal ??= ReadTiffUnsignedRational(tiff, type, valOffset, le);
                    break;
                case 0xA405: // FocalLengthIn35mmFilm
                    focal35 ??= ReadTiffInt(tiff, type, count, valBytes, valOffset, le);
                    break;
                case 0xA434: // LensModel
                    lens ??= ReadTiffAscii(tiff, type, count, valBytes, valOffset);
                    break;
            }
        }
    }

    private static string? ReadTiffAscii(ReadOnlySpan<byte> tiff, ushort type, uint count, ReadOnlySpan<byte> inlineBytes, uint offset)
    {
        if (type != 2 || count == 0 || count > 256) return null;
        ReadOnlySpan<byte> slice;
        if (count <= 4)
        {
            slice = inlineBytes[..(int)count];
        }
        else
        {
            if (offset + count > tiff.Length) return null;
            slice = tiff.Slice((int)offset, (int)count);
        }
        var str = Encoding.UTF8.GetString(slice).Trim('\0', ' ', '\t', '\r', '\n');
        return str.Length > 0 ? str : null;
    }

    private static int? ReadTiffInt(ReadOnlySpan<byte> tiff, ushort type, uint count, ReadOnlySpan<byte> inlineBytes, uint offset, bool le)
    {
        if (count == 0) return null;
        if (type == 3) // USHORT
        {
            if (count <= 2) return le ? BinaryPrimitives.ReadUInt16LittleEndian(inlineBytes) : BinaryPrimitives.ReadUInt16BigEndian(inlineBytes);
            if (offset + 2 <= tiff.Length) return ReadSpanU16(tiff, (int)offset, le);
        }
        else if (type == 4) // ULONG
        {
            return (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(inlineBytes) : BinaryPrimitives.ReadUInt32BigEndian(inlineBytes));
        }
        return null;
    }

    private static double? ReadTiffUnsignedRational(ReadOnlySpan<byte> tiff, ushort type, uint offset, bool le)
    {
        if (type != 5 || offset + 8 > tiff.Length) return null;
        uint num = ReadSpanU32(tiff, (int)offset, le);
        uint den = ReadSpanU32(tiff, (int)offset + 4, le);
        return den > 0 ? (double)num / den : null;
    }

    private static double? ReadTiffSignedRational(ReadOnlySpan<byte> tiff, ushort type, uint offset, bool le)
    {
        if ((type != 10 && type != 5) || offset + 8 > tiff.Length) return null;
        int num = (int)ReadSpanU32(tiff, (int)offset, le);
        int den = (int)ReadSpanU32(tiff, (int)offset + 4, le);
        return den != 0 ? (double)num / den : null;
    }

    private static ExifMetadata? ReadExifFromWicFrame(IWICBitmapFrameDecode frame)
    {
        try
        {
            using var reader = frame.MetadataQueryReader;
            if (reader == null) return null;

            string? make = GetWicString(reader, "System.Photo.CameraManufacturer") ?? GetWicString(reader, "/app1/ifd/{ushort=271}");
            string? model = GetWicString(reader, "System.Photo.CameraModel") ?? GetWicString(reader, "/app1/ifd/{ushort=272}");
            string? lens = GetWicString(reader, "System.Photo.LensModel") ?? GetWicString(reader, "/app1/ifd/exif/{ushort=42036}");
            int? orientation = GetWicInt(reader, "System.Photo.Orientation") ?? GetWicInt(reader, "/app1/ifd/{ushort=274}") ?? GetWicInt(reader, "/ifd/{ushort=274}");
            int? iso = GetWicInt(reader, "System.Photo.ISOSpeed") ?? GetWicInt(reader, "/app1/ifd/exif/{ushort=34855}");
            double? shutter = GetWicDouble(reader, "System.Photo.ExposureTime");
            double? aperture = GetWicDouble(reader, "System.Photo.FNumber");
            double? focal = GetWicDouble(reader, "System.Photo.FocalLength");

            if (make == null && model == null && lens == null && orientation == null && iso == null && shutter == null && aperture == null)
            {
                return null;
            }

            return new ExifMetadata(
                CameraMake: make,
                CameraModel: model,
                LensModel: lens,
                ShutterSpeed: shutter,
                Aperture: aperture,
                Iso: iso,
                FocalLength: focal,
                Orientation: orientation
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWicString(IWICMetadataQueryReader reader, string query)
    {
        try
        {
            var v = reader.GetMetadataByName(query);
            return v.Value?.ToString()?.Trim();
        }
        catch { return null; }
    }

    private static int? GetWicInt(IWICMetadataQueryReader reader, string query)
    {
        try
        {
            var val = reader.GetMetadataByName(query).Value;
            return val switch
            {
                ushort u16 => u16,
                uint u32 => (int)u32,
                int i32 => i32,
                short i16 => i16,
                byte b => b,
                _ => null
            };
        }
        catch { return null; }
    }

    private static double? GetWicDouble(IWICMetadataQueryReader reader, string query)
    {
        try
        {
            var val = reader.GetMetadataByName(query).Value;
            return val switch
            {
                double d => d,
                float f => f,
                ulong u64 when (u64 >> 32) > 0 => (double)(uint)(u64 & 0xFFFFFFFF) / (uint)(u64 >> 32),
                _ => null
            };
        }
        catch { return null; }
    }

    private static ExifMetadata? MergeExif(ExifMetadata? primary, ExifMetadata? secondary)
    {
        if (primary == null) return secondary;
        if (secondary == null) return primary;

        return new ExifMetadata(
            CameraMake: primary.CameraMake ?? secondary.CameraMake,
            CameraModel: primary.CameraModel ?? secondary.CameraModel,
            LensModel: primary.LensModel ?? secondary.LensModel,
            ShutterSpeed: primary.ShutterSpeed ?? secondary.ShutterSpeed,
            Aperture: primary.Aperture ?? secondary.Aperture,
            Iso: primary.Iso ?? secondary.Iso,
            FocalLength: primary.FocalLength ?? secondary.FocalLength,
            FocalLength35mm: primary.FocalLength35mm ?? secondary.FocalLength35mm,
            ExposureBias: primary.ExposureBias ?? secondary.ExposureBias,
            FlashFired: primary.FlashFired ?? secondary.FlashFired,
            DateTimeOriginal: primary.DateTimeOriginal ?? secondary.DateTimeOriginal,
            Orientation: primary.Orientation ?? secondary.Orientation
        );
    }

    #endregion

    #region Software Orientation Helper for 16-bit RAW XMP Overrides

    private static unsafe DecodedImage TransformRgbaOrBgraSoftware(DecodedImage src, int orientation)
    {
        if (orientation is <= 1 or > 8) return src;

        bool swap = orientation is 5 or 6 or 7 or 8;
        int dstW = swap ? src.Height : src.Width;
        int dstH = swap ? src.Width : src.Height;
        int bpp = src.PixelFormat == ImagePixelFormat.Rgba64 ? 8 : 4;
        int dstStride = dstW * bpp;
        var dstPixels = new byte[dstH * dstStride];

        fixed (byte* pSrc = src.Pixels)
        fixed (byte* pDst = dstPixels)
        {
            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    var (dx, dy) = orientation switch
                    {
                        2 => (src.Width - 1 - x, y),
                        3 => (src.Width - 1 - x, src.Height - 1 - y),
                        4 => (x, src.Height - 1 - y),
                        5 => (y, x),
                        6 => (src.Height - 1 - y, x),
                        7 => (src.Height - 1 - y, src.Width - 1 - x),
                        8 => (y, src.Width - 1 - x),
                        _ => (x, y)
                    };

                    byte* s = pSrc + y * src.Stride + x * bpp;
                    byte* d = pDst + dy * dstStride + dx * bpp;
                    if (bpp == 8) *(ulong*)d = *(ulong*)s;
                    else *(uint*)d = *(uint*)s;
                }
            }
        }

        return new DecodedImage(
            pixels: dstPixels,
            width: dstW,
            height: dstH,
            stride: dstStride,
            pixelFormat: src.PixelFormat,
            originalWidth: swap ? src.OriginalHeight : src.OriginalWidth,
            originalHeight: swap ? src.OriginalWidth : src.OriginalHeight,
            isDownscaled: src.IsDownscaled,
            isRaw: src.IsRaw,
            isFullSensorRaw: src.IsFullSensorRaw,
            iccProfile: src.IccProfile,
            colorProfileName: src.ColorProfileName,
            exif: src.Exif
        );
    }

    #endregion

    private static unsafe ushort ReadU16(byte* p, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(p, 2))
           : BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(p, 2));

    private static unsafe uint ReadU32(byte* p, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p, 4))
           : BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(p, 4));

    private static ushort ReadSpanU16(ReadOnlySpan<byte> s, int offset, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2))
           : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    private static uint ReadSpanU32(ReadOnlySpan<byte> s, int offset, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4))
           : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));
}
