using System;
using Sdcb.LibRaw;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class RawSensorDecoder
{
    public static byte[]? TryExtractPreviewBytes(string filePath, out ExifMetadata? exif, out DecodedImage? bitmapThumbnail)
    {
        exif = null;
        bitmapThumbnail = null;

        try
        {
            NativeRuntimeLoader.EnsureLibRawLoaded();
            using var ctx = RawContext.OpenFile(filePath);
            exif = TryExtractExif(ctx);

            ctx.UnpackThumbnail(0);
            using var thumb = ctx.MakeDcrawMemoryThumbnail();

            if (thumb.ImageType == ProcessedImageType.Jpeg && thumb.DataSize > 512)
            {
                return thumb.AsSpan<byte>().ToArray();
            }

            if (thumb.ImageType == ProcessedImageType.Bitmap && thumb.Width > 0 && thumb.Height > 0 && thumb.Channels >= 3)
            {
                bitmapThumbnail = ConvertProcessedBitmap(thumb, isFullSensor: false, exif);
            }
        }
        catch
        {
            // Caller falls back to full sensor decode or WIC
        }

        return null;
    }

    public static DecodedImage DecodeFullSensor16Bit(string filePath, ExifMetadata? fallbackExif = null)
    {
        NativeRuntimeLoader.EnsureLibRawLoaded();
        using var ctx = RawContext.OpenFile(filePath);
        var exif = TryExtractExif(ctx) ?? fallbackExif;

        ctx.Unpack();

        try
        {
            ctx.DcrawProcess(p =>
            {
                p.UseCameraWb = true;
                p.OutputBps = 16;
                p.OutputColor = LibRawColorSpace.SRGB;
                p.NoAutoBright = true;
                p.UserQual = DemosaicAlgorithm.LinearInterpolation;
            });
        }
        catch
        {
            ctx.OutputBitsPerSample = 16;
            ctx.OutputColorSpace = LibRawColorSpace.SRGB;
            ctx.AutoBright = false;
            ctx.DemosaicAlgorithm = DemosaicAlgorithm.LinearInterpolation;
            ctx.DcrawProcess();
        }

        using var img = ctx.MakeDcrawMemoryImage();
        return ConvertProcessedBitmap(img, isFullSensor: true, exif);
    }

    private static unsafe DecodedImage ConvertProcessedBitmap(ProcessedImage img, bool isFullSensor, ExifMetadata? exif)
    {
        int width = img.Width;
        int height = img.Height;
        int channels = img.Channels;

        if (img.Bits == 16)
        {
            int stride = width * 8;
            var dstBytes = new byte[height * stride];
            var srcSpan = img.AsSpan<ushort>();

            fixed (ushort* pSrc = srcSpan)
            fixed (byte* pDstBytes = dstBytes)
            {
                var pDst = (ushort*)pDstBytes;
                int pixelCount = width * height;

                if (channels == 3)
                {
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int s = i * 3;
                        int d = i * 4;
                        pDst[d] = pSrc[s];
                        pDst[d + 1] = pSrc[s + 1];
                        pDst[d + 2] = pSrc[s + 2];
                        pDst[d + 3] = 65535;
                    }
                }
                else if (channels >= 4)
                {
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int s = i * channels;
                        int d = i * 4;
                        pDst[d] = pSrc[s];
                        pDst[d + 1] = pSrc[s + 1];
                        pDst[d + 2] = pSrc[s + 2];
                        pDst[d + 3] = 65535;
                    }
                }
                else
                {
                    for (int i = 0; i < pixelCount; i++)
                    {
                        ushort v = pSrc[i];
                        int d = i * 4;
                        pDst[d] = v;
                        pDst[d + 1] = v;
                        pDst[d + 2] = v;
                        pDst[d + 3] = 65535;
                    }
                }
            }

            return new DecodedImage(
                pixels: dstBytes,
                width: width,
                height: height,
                stride: stride,
                pixelFormat: ImagePixelFormat.Rgba64,
                originalWidth: width,
                originalHeight: height,
                isDownscaled: false,
                isRaw: true,
                isFullSensorRaw: isFullSensor,
                iccProfile: null,
                colorProfileName: isFullSensor ? "RAW 16-bit" : "sRGB",
                exif: exif
            );
        }
        else
        {
            int stride = width * 4;
            var dstBytes = new byte[height * stride];
            var srcSpan = img.AsSpan<byte>();

            fixed (byte* pSrc = srcSpan)
            fixed (byte* pDst = dstBytes)
            {
                int pixelCount = width * height;
                for (int i = 0; i < pixelCount; i++)
                {
                    int s = i * channels;
                    int d = i * 4;
                    byte r = pSrc[s];
                    byte g = channels >= 3 ? pSrc[s + 1] : r;
                    byte b = channels >= 3 ? pSrc[s + 2] : r;
                    pDst[d] = b;
                    pDst[d + 1] = g;
                    pDst[d + 2] = r;
                    pDst[d + 3] = 255;
                }
            }

            return new DecodedImage(
                pixels: dstBytes,
                width: width,
                height: height,
                stride: stride,
                pixelFormat: ImagePixelFormat.Bgra32,
                originalWidth: width,
                originalHeight: height,
                isDownscaled: !isFullSensor,
                isRaw: true,
                isFullSensorRaw: isFullSensor,
                iccProfile: null,
                colorProfileName: isFullSensor ? "RAW" : "sRGB",
                exif: exif
            );
        }
    }

    public static ExifMetadata? TryExtractExif(string filePath)
    {
        try
        {
            NativeRuntimeLoader.EnsureLibRawLoaded();
            using var ctx = RawContext.OpenFile(filePath);
            return TryExtractExif(ctx);
        }
        catch
        {
            return null;
        }
    }

    private static ExifMetadata? TryExtractExif(RawContext ctx)
    {
        try
        {
            var ip = ctx.ImageParams;
            var op = ctx.ImageOtherParams;
            var li = ctx.LensInfo;

            string? dateStr = null;
            if (op.Timestamp > 0)
            {
                dateStr = DateTimeOffset.FromUnixTimeSeconds(op.Timestamp)
                    .LocalDateTime.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }

            return new ExifMetadata(
                CameraMake: NullIfEmpty(ip.Make),
                CameraModel: NullIfEmpty(ip.Model),
                LensModel: NullIfEmpty(li.Lens),
                ShutterSpeed: op.Shutter > 0 ? op.Shutter : null,
                Aperture: op.Aperture > 0 ? op.Aperture : null,
                Iso: op.IsoSpeed > 0 ? (int)Math.Round(op.IsoSpeed) : null,
                FocalLength: op.FocalLength > 0 ? op.FocalLength : null,
                FocalLength35mm: li.FocalLengthIn35mmFormat > 0 ? li.FocalLengthIn35mmFormat : null,
                DateTimeOriginal: dateStr
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var trimmed = s.Trim('\0', ' ', '\t', '\r', '\n');
        return trimmed.Length > 0 ? trimmed : null;
    }
}
