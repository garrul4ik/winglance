using System;
using WinGlance.Model;

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
    public static unsafe HistogramData Compute(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length == 0)
        {
            return HistogramData.Empty;
        }

        int width = image.Width;
        int height = image.Height;
        int stride = image.Stride;
        int step = Math.Max(1, Math.Max(width, height) / 1920);

        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];
        var lumHist = new int[256];

        fixed (byte* basePtr = image.Pixels)
        {
            if (image.PixelFormat == ImagePixelFormat.Rgba64)
            {
                for (int y = 0; y < height; y += step)
                {
                    ushort* row = (ushort*)(basePtr + (long)y * stride);
                    for (int x = 0; x < width; x += step)
                    {
                        int idx = x * 4;
                        int r = row[idx] >> 8;
                        int g = row[idx + 1] >> 8;
                        int b = row[idx + 2] >> 8;
                        int lum = (54 * r + 183 * g + 19 * b) >> 8;

                        rHist[r]++;
                        gHist[g]++;
                        bHist[b]++;
                        lumHist[lum]++;
                    }
                }
            }
            else
            {
                for (int y = 0; y < height; y += step)
                {
                    uint* row = (uint*)(basePtr + (long)y * stride);
                    for (int x = 0; x < width; x += step)
                    {
                        uint px = row[x];
                        int b = (int)(px & 0xFF);
                        int g = (int)((px >> 8) & 0xFF);
                        int r = (int)((px >> 16) & 0xFF);
                        int lum = (54 * r + 183 * g + 19 * b) >> 8;

                        rHist[r]++;
                        gHist[g]++;
                        bHist[b]++;
                        lumHist[lum]++;
                    }
                }
            }
        }

        int peak = 1;
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
