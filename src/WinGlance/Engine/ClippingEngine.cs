using System;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class ClippingEngine
{
    public static unsafe DecodedImage? GenerateClippingMask(DecodedImage source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Pixels.Length == 0)
        {
            return null;
        }

        int srcW = source.Width;
        int srcH = source.Height;
        int maxDim = Math.Max(srcW, srcH);

        int dstW = srcW;
        int dstH = srcH;
        if (maxDim > 1920)
        {
            double scale = 1920.0 / maxDim;
            dstW = Math.Max(1, (int)Math.Round(srcW * scale));
            dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        }

        int dstStride = dstW * 4;
        var maskBytes = new byte[dstH * dstStride];
        bool hasClipping = false;

        fixed (byte* pSrcBase = source.Pixels)
        fixed (byte* pDstBase = maskBytes)
        {
            uint* pDst = (uint*)pDstBase;

            if (source.PixelFormat == ImagePixelFormat.Rgba64)
            {
                for (int y = 0; y < dstH; y++)
                {
                    int sy = (int)((long)y * srcH / dstH);
                    ushort* srcRow = (ushort*)(pSrcBase + (long)sy * source.Stride);
                    uint* dstRow = pDst + y * dstW;

                    for (int x = 0; x < dstW; x++)
                    {
                        int sx = (int)((long)x * srcW / dstW) * 4;
                        ushort r = srcRow[sx];
                        ushort g = srcRow[sx + 1];
                        ushort b = srcRow[sx + 2];

                        if (r >= 65020 || g >= 65020 || b >= 65020)
                        {
                            dstRow[x] = 0xE1FF0033; // Warning Red (B=0x33, G=0x00, R=0xFF, A=0xE1)
                            hasClipping = true;
                        }
                        else if (r <= 512 && g <= 512 && b <= 512)
                        {
                            dstRow[x] = 0xE10066FF; // Warning Blue (B=0xFF, G=0x66, R=0x00, A=0xE1)
                            hasClipping = true;
                        }
                    }
                }
            }
            else
            {
                for (int y = 0; y < dstH; y++)
                {
                    int sy = (int)((long)y * srcH / dstH);
                    uint* srcRow = (uint*)(pSrcBase + (long)sy * source.Stride);
                    uint* dstRow = pDst + y * dstW;

                    for (int x = 0; x < dstW; x++)
                    {
                        int sx = (int)((long)x * srcW / dstW);
                        uint px = srcRow[sx];
                        byte b = (byte)(px & 0xFF);
                        byte g = (byte)((px >> 8) & 0xFF);
                        byte r = (byte)((px >> 16) & 0xFF);

                        if (r >= 254 || g >= 254 || b >= 254)
                        {
                            dstRow[x] = 0xE1FF0033;
                            hasClipping = true;
                        }
                        else if (r <= 2 && g <= 2 && b <= 2)
                        {
                            dstRow[x] = 0xE10066FF;
                            hasClipping = true;
                        }
                    }
                }
            }
        }

        if (!hasClipping) return null;

        return new DecodedImage(
            pixels: maskBytes,
            width: dstW,
            height: dstH,
            stride: dstStride,
            pixelFormat: ImagePixelFormat.Bgra32,
            originalWidth: source.OriginalWidth,
            originalHeight: source.OriginalHeight,
            isDownscaled: dstW < srcW || dstH < srcH,
            isRaw: false
        );
    }
}
