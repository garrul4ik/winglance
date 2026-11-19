using System;
using WinGlance.Model;

namespace WinGlance.Engine;

/// <summary>
/// ARRI-style False Color exposure verification engine mapping Rec.709 scene luminance
/// to standard 0–100 IRE exposure bands (Crush, Shadow, 18% Gray, Skin, High, Roll-off, Clip)
/// while preserving intermediate tonal values in monochrome grayscale.
/// </summary>
public static class FalseColorEngine
{
    private static readonly uint[] BgraLut = BuildBgraLut();

    private static uint[] BuildBgraLut()
    {
        var table = new uint[256];
        for (int y = 0; y < 256; y++)
        {
            double ire = y * 100.0 / 255.0;
            byte r, g, b;

            if (ire >= 99.0)
            {
                // Blown Specular Highlights / Sensor Clip: Pure Red (#FF002B)
                r = 255; g = 0; b = 43;
            }
            else if (ire >= 97.0)
            {
                // Near-Clipping Warning / Roll-off: Bright Yellow (#FFE600)
                r = 255; g = 230; b = 0;
            }
            else if (ire >= 78.0 && ire <= 82.0)
            {
                // High Highlights / Specular Skin: Amber Orange (#FF7A00)
                r = 255; g = 122; b = 0;
            }
            else if (ire >= 52.0 && ire <= 56.0)
            {
                // Skin Tone Reference: Orchid Pink (#FF66B2)
                r = 255; g = 102; b = 178;
            }
            else if (ire >= 38.0 && ire <= 42.0)
            {
                // 18% Neutral Gray Reference: Kelly Green (#00E64D)
                r = 0; g = 230; b = 77;
            }
            else if (ire >= 2.5 && ire <= 4.0)
            {
                // Deep Shadow Detail: Cyan-Blue (#007AFF)
                r = 0; g = 122; b = 255;
            }
            else if (ire <= 2.5)
            {
                // Crushed Blacks: Neon Purple (#8B1FA2)
                r = 139; g = 31; b = 162;
            }
            else
            {
                byte v = (byte)y;
                r = v; g = v; b = v;
            }

            table[y] = (0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return table;
    }

    public static (byte R, byte G, byte B) MapLuminanceToRgb(byte luma)
    {
        uint bgra = BgraLut[luma];
        return (
            R: (byte)((bgra >> 16) & 0xFF),
            G: (byte)((bgra >> 8) & 0xFF),
            B: (byte)(bgra & 0xFF)
        );
    }

    public static unsafe DecodedImage? GenerateFalseColorImage(DecodedImage source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Pixels.Length == 0)
        {
            return null;
        }

        int srcW = source.Width;
        int srcH = source.Height;
        int maxDim = Math.Max(srcW, srcH);
        double scale = maxDim > 1920 ? (1920.0 / maxDim) : 1.0;

        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        int dstStride = dstW * 4;
        var dstPixels = new byte[dstH * dstStride];

        var xMap = new int[dstW];
        for (int x = 0; x < dstW; x++)
        {
            xMap[x] = Math.Clamp((int)((long)x * srcW / dstW), 0, srcW - 1);
        }

        fixed (byte* pSrcBase = source.Pixels)
        fixed (byte* pDstBase = dstPixels)
        fixed (uint* pLut = BgraLut)
        {
            if (source.PixelFormat == ImagePixelFormat.Rgba64)
            {
                for (int dy = 0; dy < dstH; dy++)
                {
                    int sy = Math.Clamp((int)((long)dy * srcH / dstH), 0, srcH - 1);
                    ushort* sRow = (ushort*)(pSrcBase + (long)sy * source.Stride);
                    uint* dRow = (uint*)(pDstBase + (long)dy * dstStride);

                    for (int dx = 0; dx < dstW; dx++)
                    {
                        int idx = xMap[dx] * 4;
                        int r = sRow[idx] >> 8;
                        int g = sRow[idx + 1] >> 8;
                        int b = sRow[idx + 2] >> 8;
                        int lum = (54 * r + 183 * g + 19 * b) >> 8;
                        dRow[dx] = pLut[Math.Clamp(lum, 0, 255)];
                    }
                }
            }
            else
            {
                for (int dy = 0; dy < dstH; dy++)
                {
                    int sy = Math.Clamp((int)((long)dy * srcH / dstH), 0, srcH - 1);
                    uint* sRow = (uint*)(pSrcBase + (long)sy * source.Stride);
                    uint* dRow = (uint*)(pDstBase + (long)dy * dstStride);

                    for (int dx = 0; dx < dstW; dx++)
                    {
                        uint px = sRow[xMap[dx]];
                        int b = (int)(px & 0xFF);
                        int g = (int)((px >> 8) & 0xFF);
                        int r = (int)((px >> 16) & 0xFF);
                        int lum = (54 * r + 183 * g + 19 * b) >> 8;
                        dRow[dx] = pLut[Math.Clamp(lum, 0, 255)];
                    }
                }
            }
        }

        return new DecodedImage(
            pixels: dstPixels,
            width: dstW,
            height: dstH,
            stride: dstStride,
            pixelFormat: ImagePixelFormat.Bgra32,
            originalWidth: source.OriginalWidth,
            originalHeight: source.OriginalHeight,
            isDownscaled: dstW < source.OriginalWidth || dstH < source.OriginalHeight,
            isRaw: source.IsRaw,
            isFullSensorRaw: false,
            iccProfile: null,
            colorProfileName: "ARRI IRE",
            exif: source.Exif
        );
    }
}
