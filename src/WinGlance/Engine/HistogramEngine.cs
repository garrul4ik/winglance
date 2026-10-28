using System;
using System.Windows.Media.Imaging;

namespace WinGlance.Engine;

public sealed record HistogramData(
    int[] Red,
    int[] Green,
    int[] Blue,
    int[] Luminance,
    int MaxCount
)
{
    public static readonly HistogramData Empty = new(
        new int[256],
        new int[256],
        new int[256],
        new int[256],
        1
    );
}

public static class HistogramEngine
{
    public static HistogramData Compute(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return HistogramData.Empty;
        }

        // Convert to Bgra32 if not already
        BitmapSource formatted = source;
        if (source.Format != System.Windows.Media.PixelFormats.Bgra32 &&
            source.Format != System.Windows.Media.PixelFormats.Bgr32)
        {
            formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        }

        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;

        // Bounding resolution for speed (at most 1920 max dim for histogram calculation)
        var maxDim = Math.Max(width, height);
        if (maxDim > 1920)
        {
            var scale = 1920.0 / maxDim;
            var targetW = (int)(width * scale);
            var targetH = (int)(height * scale);
            formatted = new TransformedBitmap(formatted, new System.Windows.Media.ScaleTransform(scale, scale));
            width = formatted.PixelWidth;
            height = formatted.PixelHeight;
        }

        var stride = width * 4;
        var pixelBytes = new byte[height * stride];
        formatted.CopyPixels(pixelBytes, stride, 0);

        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];
        var lumHist = new int[256];

        int peak = 1;

        unsafe
        {
            fixed (byte* ptr = pixelBytes)
            {
                var totalPixels = width * height;
                var p = (uint*)ptr;

                for (int i = 0; i < totalPixels; i++)
                {
                    var px = p[i];
                    var b = (int)(px & 0xFF);
                    var g = (int)((px >> 8) & 0xFF);
                    var r = (int)((px >> 16) & 0xFF);

                    // Rec. 709 Luminance: (54*R + 183*G + 19*B) >> 8
                    var lum = (54 * r + 183 * g + 19 * b) >> 8;

                    rHist[r]++;
                    gHist[g]++;
                    bHist[b]++;
                    lumHist[lum]++;
                }
            }
        }

        for (int i = 0; i < 256; i++)
        {
            if (rHist[i] > peak) peak = rHist[i];
            if (gHist[i] > peak) peak = gHist[i];
            if (bHist[i] > peak) peak = bHist[i];
            if (lumHist[i] > peak) peak = lumHist[i];
        }

        return new HistogramData(rHist, gHist, bHist, lumHist, peak);
    }
}
