using System;

namespace WinGlance.Model;

public enum ImagePixelFormat
{
    Bgra32 = 0, // 8-bit per channel: B, G, R, A (DXGI_FORMAT_B8G8R8A8_UNORM)
    Rgba64 = 1  // 16-bit per channel: R16, G16, B16, A16 (DXGI_FORMAT_R16G16B16A16_UNORM)
}

public sealed class DecodedImage
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public ImagePixelFormat PixelFormat { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public bool IsDownscaled { get; }
    public bool IsRaw { get; }
    public bool IsFullSensorRaw { get; }
    public byte[]? IccProfile { get; }
    public string ColorProfileName { get; }
    public ExifMetadata? Exif { get; }

    public int BitsPerChannel => PixelFormat == ImagePixelFormat.Rgba64 ? 16 : 8;
    public double AspectRatio => OriginalHeight > 0 ? (double)OriginalWidth / OriginalHeight : 1.0;

    public DecodedImage(
        byte[] pixels,
        int width,
        int height,
        int stride,
        ImagePixelFormat pixelFormat,
        int originalWidth,
        int originalHeight,
        bool isDownscaled,
        bool isRaw,
        bool isFullSensorRaw = false,
        byte[]? iccProfile = null,
        string colorProfileName = "sRGB",
        ExifMetadata? exif = null)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        OriginalWidth = originalWidth > 0 ? originalWidth : width;
        OriginalHeight = originalHeight > 0 ? originalHeight : height;
        IsDownscaled = isDownscaled;
        IsRaw = isRaw;
        IsFullSensorRaw = isFullSensorRaw;
        IccProfile = iccProfile;
        ColorProfileName = colorProfileName;
        Exif = exif;
    }
}
