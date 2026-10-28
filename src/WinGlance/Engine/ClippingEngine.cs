using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinGlance.Engine;

public static class ClippingEngine
{
    public static BitmapSource? GenerateClippingMask(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return null;
        }

        BitmapSource formatted = source;
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
        {
            formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;

        // Bounding resolution for speed (max 1920 dimension)
        var maxDim = Math.Max(width, height);
        if (maxDim > 1920)
        {
            var scale = 1920.0 / maxDim;
            formatted = new TransformedBitmap(formatted, new ScaleTransform(scale, scale));
            width = formatted.PixelWidth;
            height = formatted.PixelHeight;
        }

        var stride = width * 4;
        var srcBytes = new byte[height * stride];
        formatted.CopyPixels(srcBytes, stride, 0);

        var maskBytes = new byte[height * stride];
        var hasClipping = false;

        unsafe
        {
            fixed (byte* pSrc = srcBytes)
            fixed (byte* pDst = maskBytes)
            {
                var totalPixels = width * height;
                var src = (uint*)pSrc;
                var dst = (uint*)pDst;

                for (int i = 0; i < totalPixels; i++)
                {
                    var px = src[i];
                    var b = (byte)(px & 0xFF);
                    var g = (byte)((px >> 8) & 0xFF);
                    var r = (byte)((px >> 16) & 0xFF);

                    if (r >= 254 || g >= 254 || b >= 254)
                    {
                        // Blown highlights: Warning Red (#FF0033 with alpha 225)
                        // Bgra32: B=0x33, G=0x00, R=0xFF, A=0xE1
                        dst[i] = 0xE1FF0033;
                        hasClipping = true;
                    }
                    else if (r <= 2 && g <= 2 && b <= 2)
                    {
                        // Crushed shadows: Warning Blue (#0066FF with alpha 225)
                        // Bgra32: B=0xFF, G=0x66, R=0x00, A=0xE1
                        dst[i] = 0xE10066FF;
                        hasClipping = true;
                    }
                    else
                    {
                        dst[i] = 0; // transparent
                    }
                }
            }
        }

        if (!hasClipping) return null;

        var mask = BitmapSource.Create(
            width, height,
            96, 96,
            PixelFormats.Bgra32,
            null,
            maskBytes,
            stride
        );
        mask.Freeze();
        return mask;
    }
}
