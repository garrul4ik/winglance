using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class FastImageDecoder
{
    /// <summary>
    /// Decodes an image or camera RAW file, optionally bounded to a maximum pixel size.
    /// Freezes the BitmapSource so it can be safely passed across threads and rendered on GPU.
    /// </summary>
    public static DecodedImage Decode(string filePath, int maxBoundingPixelSize = 0)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file not found", filePath);
        }

        var isRaw = SupportedFormats.IsRaw(filePath);
        if (isRaw)
        {
            // 1. Try extracting embedded full-size JPEG preview
            var rawPreview = TryExtractEmbeddedRawPreview(filePath, maxBoundingPixelSize);
            if (rawPreview != null)
            {
                return rawPreview;
            }
        }

        // 2. Standard WIC decode
        return DecodeViaWic(filePath, maxBoundingPixelSize, isRaw);
    }

    private static DecodedImage DecodeViaWic(string filePath, int maxBoundingPixelSize, bool isRaw)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        
        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmapImage.StreamSource = stream;

        if (maxBoundingPixelSize > 0)
        {
            // Read dimensions first without decoding full bitmap
            var (origW, origH) = GetDimensionsQuick(filePath);
            if (origW > 0 && origH > 0 && Math.Max(origW, origH) > maxBoundingPixelSize)
            {
                if (origW >= origH)
                {
                    bitmapImage.DecodePixelWidth = maxBoundingPixelSize;
                }
                else
                {
                    bitmapImage.DecodePixelHeight = maxBoundingPixelSize;
                }
            }
        }

        bitmapImage.EndInit();
        bitmapImage.Freeze();

        var exif = ReadExifMetadata(filePath);
        var width = bitmapImage.PixelWidth;
        var height = bitmapImage.PixelHeight;

        var (originalWidth, originalHeight) = GetDimensionsQuick(filePath);
        if (originalWidth <= 0) originalWidth = width;
        if (originalHeight <= 0) originalHeight = height;

        var isDownscaled = width < originalWidth || height < originalHeight;

        return new DecodedImage(
            Image: bitmapImage,
            OriginalWidth: originalWidth,
            OriginalHeight: originalHeight,
            IsDownscaled: isDownscaled,
            IsRaw: isRaw,
            Exif: exif
        );
    }

    private static (int Width, int Height) GetDimensionsQuick(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                return (frame.PixelWidth, frame.PixelHeight);
            }
        }
        catch { }
        return (0, 0);
    }

    public static ExifMetadata? ReadExifMetadata(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata meta)
            {
                string? cameraMake = GetMetadataString(meta, "/app1/ifd/{ushort=271}") ?? GetMetadataString(meta, "System.Photo.CameraManufacturer");
                string? cameraModel = GetMetadataString(meta, "/app1/ifd/{ushort=272}") ?? GetMetadataString(meta, "System.Photo.CameraModel");
                string? lensModel = GetMetadataString(meta, "/app1/ifd/exif/{ushort=42036}") ?? GetMetadataString(meta, "System.Photo.LensModel");
                string? dateTimeOriginal = GetMetadataString(meta, "/app1/ifd/exif/{ushort=36867}") ?? GetMetadataString(meta, "System.Photo.DateTaken");

                double? shutterSpeed = GetMetadataRational(meta, "/app1/ifd/exif/{ushort=33434}") ?? GetMetadataDouble(meta, "System.Photo.ExposureTime");
                double? aperture = GetMetadataRational(meta, "/app1/ifd/exif/{ushort=33437}") ?? GetMetadataDouble(meta, "System.Photo.FNumber");
                int? iso = GetMetadataInt(meta, "/app1/ifd/exif/{ushort=34855}") ?? GetMetadataInt(meta, "System.Photo.ISOSpeed");
                double? focalLength = GetMetadataRational(meta, "/app1/ifd/exif/{ushort=37386}") ?? GetMetadataDouble(meta, "System.Photo.FocalLength");
                int? focal35mm = GetMetadataInt(meta, "/app1/ifd/exif/{ushort=41989}") ?? GetMetadataInt(meta, "System.Photo.FocalLengthInFilm");
                double? exposureBias = GetMetadataSignedRational(meta, "/app1/ifd/exif/{ushort=37380}") ?? GetMetadataDouble(meta, "System.Photo.ExposureBias");

                int? orientation = GetMetadataInt(meta, "/app1/ifd/{ushort=274}") ?? GetMetadataInt(meta, "System.Photo.Orientation");

                return new ExifMetadata(
                    CameraMake: cameraMake?.Trim(),
                    CameraModel: cameraModel?.Trim(),
                    LensModel: lensModel?.Trim(),
                    ShutterSpeed: shutterSpeed,
                    Aperture: aperture,
                    Iso: iso,
                    FocalLength: focalLength,
                    FocalLength35mm: focal35mm,
                    ExposureBias: exposureBias,
                    DateTimeOriginal: dateTimeOriginal,
                    Orientation: orientation
                );
            }
        }
        catch { }

        return null;
    }

    private static string? GetMetadataString(BitmapMetadata meta, string query)
    {
        try { return meta.ContainsQuery(query) ? meta.GetQuery(query)?.ToString() : null; }
        catch { return null; }
    }

    private static int? GetMetadataInt(BitmapMetadata meta, string query)
    {
        try
        {
            if (meta.ContainsQuery(query))
            {
                var val = meta.GetQuery(query);
                if (val is ushort u16) return u16;
                if (val is uint u32) return (int)u32;
                if (val is int i32) return i32;
            }
        }
        catch { }
        return null;
    }

    private static double? GetMetadataDouble(BitmapMetadata meta, string query)
    {
        try
        {
            if (meta.ContainsQuery(query))
            {
                var val = meta.GetQuery(query);
                if (val is double d) return d;
                if (val is float f) return f;
            }
        }
        catch { }
        return null;
    }

    private static double? GetMetadataRational(BitmapMetadata meta, string query)
    {
        try
        {
            if (meta.ContainsQuery(query))
            {
                var val = meta.GetQuery(query);
                if (val is ulong u64)
                {
                    var num = (uint)(u64 & 0xFFFFFFFF);
                    var den = (uint)(u64 >> 32);
                    if (den > 0) return (double)num / den;
                }
            }
        }
        catch { }
        return null;
    }

    private static double? GetMetadataSignedRational(BitmapMetadata meta, string query)
    {
        try
        {
            if (meta.ContainsQuery(query))
            {
                var val = meta.GetQuery(query);
                if (val is long i64)
                {
                    var num = (int)(i64 & 0xFFFFFFFF);
                    var den = (int)(i64 >> 32);
                    if (den > 0) return (double)num / den;
                }
            }
        }
        catch { }
        return null;
    }

    #region RAW Embedded Preview Extraction

    /// <summary>
    /// Fast extraction of embedded JPEG preview from camera RAW files (Sony ARW, Canon CR2/CR3, Nikon NEF, DNG, etc.).
    /// </summary>
    private static DecodedImage? TryExtractEmbeddedRawPreview(string filePath, int maxBoundingPixelSize)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            byte[]? jpegBytes = null;

            if (ext == ".cr3")
            {
                jpegBytes = ExtractCr3Preview(filePath);
            }
            else
            {
                jpegBytes = ExtractTiffRawPreview(filePath);
            }

            if (jpegBytes == null || jpegBytes.Length < 1000)
            {
                return null;
            }

            using var memStream = new MemoryStream(jpegBytes, writable: false);
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmapImage.StreamSource = memStream;

            if (maxBoundingPixelSize > 0)
            {
                bitmapImage.DecodePixelWidth = maxBoundingPixelSize;
            }

            bitmapImage.EndInit();
            bitmapImage.Freeze();

            var exif = ReadExifMetadata(filePath);

            return new DecodedImage(
                Image: bitmapImage,
                OriginalWidth: bitmapImage.PixelWidth,
                OriginalHeight: bitmapImage.PixelHeight,
                IsDownscaled: maxBoundingPixelSize > 0 && Math.Max(bitmapImage.PixelWidth, bitmapImage.PixelHeight) <= maxBoundingPixelSize,
                IsRaw: true,
                Exif: exif
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses TIFF IFD tags to locate the embedded full-resolution JPEG preview offset & length.
    /// </summary>
    private static byte[]? ExtractTiffRawPreview(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var reader = new BinaryReader(fs);

        if (fs.Length < 16) return null;

        var byteOrderMark = reader.ReadUInt16();
        bool isLittleEndian;
        if (byteOrderMark == 0x4949) // "II"
        {
            isLittleEndian = true;
        }
        else if (byteOrderMark == 0x4D4D) // "MM"
        {
            isLittleEndian = false;
        }
        else
        {
            return null;
        }

        var magic = isLittleEndian ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        if (magic != 42 && magic != 0x4F52) // 42 standard TIFF, 0x4F52 Olympus ORF
        {
            return null;
        }

        var ifd0Offset = isLittleEndian ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
        if (ifd0Offset >= fs.Length) return null;

        long bestJpegOffset = 0;
        long bestJpegLength = 0;

        // Traverse IFDs (IFD0, SubIFD, IFD1)
        var visitedIfds = new HashSet<long>();
        var ifdQueue = new Queue<long>();
        ifdQueue.Enqueue(ifd0Offset);

        while (ifdQueue.Count > 0 && visitedIfds.Count < 10)
        {
            var offset = ifdQueue.Dequeue();
            if (offset <= 0 || offset >= fs.Length - 2 || !visitedIfds.Add(offset)) continue;

            fs.Position = offset;
            var numEntries = isLittleEndian ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
            if (numEntries > 500) continue; // safety check

            long currentJpegOffset = 0;
            long currentJpegLength = 0;

            for (int i = 0; i < numEntries; i++)
            {
                if (fs.Position + 12 > fs.Length) break;

                var tag = isLittleEndian ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                var type = isLittleEndian ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                var count = isLittleEndian ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                var valOrOffset = isLittleEndian ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());

                if (tag == 0x0201) // JPEGInterchangeFormat (Offset)
                {
                    currentJpegOffset = valOrOffset;
                }
                else if (tag == 0x0202) // JPEGInterchangeFormatLength
                {
                    currentJpegLength = valOrOffset;
                }
                else if (tag == 0x0111 && count == 1) // StripOffsets (sometimes used for preview)
                {
                    if (currentJpegOffset == 0) currentJpegOffset = valOrOffset;
                }
                else if (tag == 0x0117 && count == 1) // StripByteCounts
                {
                    if (currentJpegLength == 0) currentJpegLength = valOrOffset;
                }
                else if (tag == 0x014A) // SubIFDs
                {
                    if (count == 1 && valOrOffset > 0)
                    {
                        ifdQueue.Enqueue(valOrOffset);
                    }
                }
            }

            if (currentJpegOffset > 0 && currentJpegLength > bestJpegLength && currentJpegOffset + currentJpegLength <= fs.Length)
            {
                bestJpegOffset = currentJpegOffset;
                bestJpegLength = currentJpegLength;
            }

            // Next IFD offset
            if (fs.Position + 4 <= fs.Length)
            {
                var nextIfd = isLittleEndian ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                if (nextIfd > 0 && nextIfd < fs.Length)
                {
                    ifdQueue.Enqueue(nextIfd);
                }
            }
        }

        if (bestJpegOffset > 0 && bestJpegLength > 50_000)
        {
            fs.Position = bestJpegOffset;
            return reader.ReadBytes((int)bestJpegLength);
        }

        return null;
    }

    /// <summary>
    /// Parses Canon CR3 (ISOBMFF) container looking for the 'PRVW' box containing embedded JPEG.
    /// </summary>
    private static byte[]? ExtractCr3Preview(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var reader = new BinaryReader(fs);

        if (fs.Length < 32) return null;

        var buffer = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            var boxLength = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
            var boxType = reader.ReadInt32(); // 4 chars

            // "PRVW" in ASCII (0x50, 0x52, 0x56, 0x57)
            if (boxType == 0x57565250) // little-endian representation of "PRVW"
            {
                if (boxLength <= 14 || fs.Position + boxLength - 8 > fs.Length) return null;
                // PRVW header is typically 14 bytes: 4 bytes unknown + 2 bytes width + 2 bytes height + ...
                // Skip directly to JPEG SOI (0xFF 0xD8 0xFF)
                var boxDataLength = (int)(boxLength - 8);
                var boxData = reader.ReadBytes(boxDataLength);

                for (int i = 0; i < boxData.Length - 3; i++)
                {
                    if (boxData[i] == 0xFF && boxData[i + 1] == 0xD8 && boxData[i + 2] == 0xFF)
                    {
                        var jpegLen = boxData.Length - i;
                        var jpeg = new byte[jpegLen];
                        Buffer.BlockCopy(boxData, i, jpeg, 0, jpegLen);
                        return jpeg;
                    }
                }
            }

            if (boxLength == 1) // 64-bit length
            {
                var boxLength64 = BinaryPrimitives.ReverseEndianness(reader.ReadInt64());
                if (boxLength64 < 16) break;
                fs.Position += boxLength64 - 16;
            }
            else if (boxLength > 8)
            {
                fs.Position += boxLength - 8;
            }
            else
            {
                break;
            }
        }

        return null;
    }

    #endregion
}
