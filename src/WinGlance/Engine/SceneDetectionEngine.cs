using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

/// <summary>
/// Compact visual + temporal fingerprint for a slide, used for perceptual scene clustering and burst detection.
/// </summary>
public readonly record struct SceneFingerprint(
    ulong PerceptualHash,
    float[] ColorHistogram,
    DateTime? CaptureTime);

/// <summary>
/// Similarity comparison metrics between two slide fingerprints.
/// </summary>
public readonly record struct SceneComparisonResult(
    bool IsSameScene,
    double Similarity,
    double HistogramDistance,
    int HammingDistance,
    double? TimeDeltaSeconds);

/// <summary>
/// HUD badge metadata describing the active photo's burst cluster and optional AI Hero winner.
/// </summary>
public sealed record BurstBreadcrumbInfo(
    int CurrentBurstIndex,
    int BurstCount,
    int GlobalStartIndex,
    string ElapsedSecondsText,
    int? GlobalHeroIndex = null,
    int? BurstHeroLocalIndex = null,
    double? HeroScore = null,
    bool IsCurrentHero = false)
{
    public string FormattedBadge => IsCurrentHero
        ? $"👑 HERO · {CurrentBurstIndex + 1}/{BurstCount}"
        : $"⚡ BURST {CurrentBurstIndex + 1}/{BurstCount} ({ElapsedSecondsText})";
}

/// <summary>
/// Pure NativeAOT C# scene clustering & burst detection engine using:
/// - 32x32 separable Type-II DCT perceptual hash (63 AC frequency bits, invariant to global brightness)
/// - 128-bin HSV color histogram (8H x 4S x 4V) compared via Hellinger distance
/// - EXIF DateTimeOriginal + SubSecTimeOriginal capture timestamp proximity.
/// </summary>
public sealed class SceneDetectionEngine
{
    public const int DctSize = 32;
    public const int HashBlockSize = 8;
    public const int HistogramBins = 128; // 8 Hue x 4 Sat x 4 Val
    public const double SameSceneThreshold = 0.72;
    public const double BurstGapThresholdSeconds = 2.5;
    public const int MaxScanSteps = 60;
    public const int MaxCacheEntries = 500;

    // Precomputed cos((2*n + 1) * k * PI / (2 * N)) for k in [0..7], n in [0..31]
    private static readonly double[,] CosTable = BuildDctCosineTable();

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (SceneFingerprint Fingerprint, LinkedListNode<string> Node)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    private static double[,] BuildDctCosineTable()
    {
        var table = new double[HashBlockSize, DctSize];
        const double factor = Math.PI / (2.0 * DctSize);
        for (int k = 0; k < HashBlockSize; k++)
        {
            for (int n = 0; n < DctSize; n++)
            {
                table[k, n] = Math.Cos((2.0 * n + 1.0) * k * factor);
            }
        }
        return table;
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    public bool TryGetCachedFingerprint(string itemId, out SceneFingerprint fingerprint)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                fingerprint = entry.Fingerprint;
                return true;
            }
        }
        fingerprint = default;
        return false;
    }

    private void PutCache(string itemId, SceneFingerprint fingerprint)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                _cache[itemId] = (fingerprint, existing.Node);
                return;
            }

            if (_cache.Count >= MaxCacheEntries && _lru.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lru.RemoveLast();
            }

            var node = _lru.AddFirst(itemId);
            _cache[itemId] = (fingerprint, node);
        }
    }

    /// <summary>
    /// Computes a deterministic perceptual fingerprint from an already-decoded image.
    /// </summary>
    public static SceneFingerprint ExtractFingerprint(DecodedImage image, DateTime? captureTimeOverride = null)
    {
        ulong pHash = ComputePerceptualHash(image);
        float[] hist = ComputeHsvHistogram(image);
        DateTime? captureTime = captureTimeOverride ?? image.Exif?.ParsedCaptureTime;
        return new SceneFingerprint(pHash, hist, captureTime);
    }

    /// <summary>
    /// Computes or returns cached fingerprint for a SlideItem, preferring already cached screen-res buffers
    /// and falling back to lightweight 512px decoding off the UI thread.
    /// </summary>
    public async Task<SceneFingerprint?> GetFingerprintAsync(
        SlideItem item,
        SlidePipeline? pipeline = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (TryGetCachedFingerprint(item.Id, out var cached))
            return cached;

        if (pipeline != null && pipeline.TryGetCachedScreenImage(item.Id, out var cachedImage) && cachedImage != null)
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                DateTime? captureTime = cachedImage.Exif?.ParsedCaptureTime ?? ReadCaptureTimeFromItem(item);
                var fp = ExtractFingerprint(cachedImage, captureTime);
                PutCache(item.Id, fp);
                return fp;
            }, ct).ConfigureAwait(false);
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var decoded = FastImageDecoder.Decode(item.Path, maxBoundingPixelSize: 512);
            if (decoded == null) return (SceneFingerprint?)null;

            DateTime? captureTime = decoded.Exif?.ParsedCaptureTime ?? ReadCaptureTimeFromItem(item);
            var fp = ExtractFingerprint(decoded, captureTime);
            PutCache(item.Id, fp);
            return fp;
        }, ct).ConfigureAwait(false);
    }

    private static DateTime? ReadCaptureTimeFromItem(SlideItem item)
    {
        var exif = FastImageDecoder.ReadExifMetadata(item.Path);
        if (exif?.ParsedCaptureTime is { } dt) return dt;
        if (!string.IsNullOrEmpty(item.RawPath) && !string.Equals(item.RawPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            return FastImageDecoder.ReadExifMetadata(item.RawPath)?.ParsedCaptureTime;
        }
        return null;
    }

    /// <summary>
    /// Compares two fingerprints using blended perceptual Hamming distance and HSV Hellinger histogram distance.
    /// </summary>
    public static SceneComparisonResult Compare(SceneFingerprint a, SceneFingerprint b)
    {
        int hamming = BitOperations.PopCount(a.PerceptualHash ^ b.PerceptualHash);
        // Bit 0 is excluded (DC-free hash uses 63 AC bits: 1..63).
        double pHashSim = 1.0 - Math.Min(hamming, 63) / 63.0;
        double histDist = ComputeHellingerDistance(a.ColorHistogram, b.ColorHistogram);
        double histSim = 1.0 - histDist;
        double similarity = 0.60 * pHashSim + 0.40 * histSim;

        double? timeDelta = (a.CaptureTime.HasValue && b.CaptureTime.HasValue)
            ? Math.Abs((b.CaptureTime.Value - a.CaptureTime.Value).TotalSeconds)
            : null;

        bool isSameScene = similarity >= SameSceneThreshold
            || (timeDelta.HasValue && timeDelta.Value <= BurstGapThresholdSeconds && similarity >= 0.55);

        return new SceneComparisonResult(isSameScene, similarity, histDist, hamming, timeDelta);
    }

    /// <summary>
    /// Scans from <paramref name="startIndex"/> in direction <paramref name="direction"/> (+1 next, -1 previous)
    /// to locate the first frame of the next or previous distinct scene.
    /// Returns null if already at the boundary with no further scene transition within <see cref="MaxScanSteps"/>.
    /// </summary>
    public async Task<int?> FindSceneBoundaryAsync(
        IReadOnlyList<SlideItem> items,
        int startIndex,
        int direction,
        SlidePipeline? pipeline = null,
        CancellationToken ct = default)
    {
        if (items.Count <= 1 || startIndex < 0 || startIndex >= items.Count || (direction != 1 && direction != -1))
            return null;

        var anchorFp = await GetFingerprintAsync(items[startIndex], pipeline, ct).ConfigureAwait(false);
        if (anchorFp == null) return null;

        int steps = 0;
        int boundaryIndex = -1;

        for (int idx = startIndex + direction; idx >= 0 && idx < items.Count && steps < MaxScanSteps; idx += direction, steps++)
        {
            ct.ThrowIfCancellationRequested();
            var candFp = await GetFingerprintAsync(items[idx], pipeline, ct).ConfigureAwait(false);
            if (candFp == null) continue;

            var cmp = Compare(anchorFp.Value, candFp.Value);
            if (!cmp.IsSameScene)
            {
                boundaryIndex = idx;
                break;
            }
        }

        if (boundaryIndex < 0)
        {
            // No scene break found: allow jumping to the start of a single uniform shoot when going backward from > 0
            if (direction < 0 && startIndex > 0)
                return 0;
            return null;
        }

        if (direction > 0)
        {
            // Next scene starts right at the first different frame
            return boundaryIndex;
        }

        // Previous scene: walk backwards through all frames belonging to that previous scene so we land at its start
        var targetSceneFp = await GetFingerprintAsync(items[boundaryIndex], pipeline, ct).ConfigureAwait(false);
        if (targetSceneFp == null) return boundaryIndex;

        int sceneStart = boundaryIndex;
        int prevSteps = 0;
        for (int idx = boundaryIndex - 1; idx >= 0 && prevSteps < MaxScanSteps; idx--, prevSteps++)
        {
            ct.ThrowIfCancellationRequested();
            var candFp = await GetFingerprintAsync(items[idx], pipeline, ct).ConfigureAwait(false);
            if (candFp == null) break;

            var cmp = Compare(targetSceneFp.Value, candFp.Value);
            if (!cmp.IsSameScene)
                break;

            sceneStart = idx;
        }

        return sceneStart;
    }

    /// <summary>
    /// Detects contiguous burst sequence around <paramref name="centerIndex"/> based on consecutive sub-second/short
    /// capture time gaps (&lt;= 2.5s) combined with strong visual similarity (&gt;= 0.65).
    /// Returns null if the photo is a standalone single shot (burst count &lt; 2).
    /// </summary>
    public async Task<BurstBreadcrumbInfo?> ComputeBurstBoundsAsync(
        IReadOnlyList<SlideItem> items,
        int centerIndex,
        SlidePipeline? pipeline = null,
        CancellationToken ct = default)
    {
        if (items.Count < 2 || centerIndex < 0 || centerIndex >= items.Count)
            return null;

        var centerFp = await GetFingerprintAsync(items[centerIndex], pipeline, ct).ConfigureAwait(false);
        if (centerFp == null || centerFp.Value.CaptureTime == null)
            return null;

        int start = centerIndex;
        var prevFp = centerFp.Value;
        int maxLookback = Math.Max(0, centerIndex - 30);
        for (int i = centerIndex - 1; i >= maxLookback; i--)
        {
            ct.ThrowIfCancellationRequested();
            var fp = await GetFingerprintAsync(items[i], pipeline, ct).ConfigureAwait(false);
            if (fp == null || fp.Value.CaptureTime == null) break;

            var cmp = Compare(fp.Value, prevFp);
            if (!cmp.TimeDeltaSeconds.HasValue || cmp.TimeDeltaSeconds.Value > BurstGapThresholdSeconds || cmp.Similarity < 0.65)
                break;

            start = i;
            prevFp = fp.Value;
        }

        int end = centerIndex;
        prevFp = centerFp.Value;
        int maxLookahead = Math.Min(items.Count - 1, centerIndex + 30);
        for (int i = centerIndex + 1; i <= maxLookahead; i++)
        {
            ct.ThrowIfCancellationRequested();
            var fp = await GetFingerprintAsync(items[i], pipeline, ct).ConfigureAwait(false);
            if (fp == null || fp.Value.CaptureTime == null) break;

            var cmp = Compare(prevFp, fp.Value);
            if (!cmp.TimeDeltaSeconds.HasValue || cmp.TimeDeltaSeconds.Value > BurstGapThresholdSeconds || cmp.Similarity < 0.65)
                break;

            end = i;
            prevFp = fp.Value;
        }

        int count = end - start + 1;
        if (count < 2) return null;

        var firstFp = await GetFingerprintAsync(items[start], pipeline, ct).ConfigureAwait(false);
        double elapsedSeconds = 0.0;
        if (firstFp?.CaptureTime is { } firstTime && centerFp.Value.CaptureTime is { } currTime)
        {
            elapsedSeconds = Math.Max(0.0, (currTime - firstTime).TotalSeconds);
        }

        string elapsedText = elapsedSeconds == 0.0
            ? "+0.0s"
            : elapsedSeconds < 0.1
                ? string.Create(CultureInfo.InvariantCulture, $"+{elapsedSeconds:0.00}s")
                : string.Create(CultureInfo.InvariantCulture, $"+{elapsedSeconds:0.0}s");

        return new BurstBreadcrumbInfo(
            CurrentBurstIndex: centerIndex - start,
            BurstCount: count,
            GlobalStartIndex: start,
            ElapsedSecondsText: elapsedText);
    }

    /// <summary>
    /// Computes a 63-bit AC perceptual hash via 32x32 bilinear luminance downsample + separable Type-II DCT.
    /// Bit 0 (DC) is left zero so global brightness shifts between burst frames do not alter the hash.
    /// </summary>
    public static ulong ComputePerceptualHash(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length == 0)
            return 0UL;

        Span<double> gray = stackalloc double[DctSize * DctSize];
        DownsampleLuminance32x32(image, gray);

        // Separable 2D DCT top-left 8x8: horizontal pass into 32x8, then vertical pass into 8x8
        Span<double> horiz = stackalloc double[DctSize * HashBlockSize];
        for (int y = 0; y < DctSize; y++)
        {
            int rowOff = y * DctSize;
            int hOff = y * HashBlockSize;
            for (int u = 0; u < HashBlockSize; u++)
            {
                double sum = 0.0;
                for (int x = 0; x < DctSize; x++)
                {
                    sum += gray[rowOff + x] * CosTable[u, x];
                }
                horiz[hOff + u] = sum;
            }
        }

        const double alpha0 = 0.17677669529663687; // 1 / sqrt(32)
        const double alphaK = 0.25;                // sqrt(2 / 32)
        Span<double> acCoeffs = stackalloc double[63];
        Span<double> sortedAc = stackalloc double[63];
        int acIdx = 0;

        for (int v = 0; v < HashBlockSize; v++)
        {
            double av = v == 0 ? alpha0 : alphaK;
            for (int u = 0; u < HashBlockSize; u++)
            {
                if (u == 0 && v == 0) continue; // skip DC component

                double sum = 0.0;
                for (int y = 0; y < DctSize; y++)
                {
                    sum += horiz[y * HashBlockSize + u] * CosTable[v, y];
                }
                double au = u == 0 ? alpha0 : alphaK;
                double val = av * au * sum;
                acCoeffs[acIdx] = val;
                sortedAc[acIdx] = val;
                acIdx++;
            }
        }

        sortedAc.Sort();
        double median = sortedAc[31]; // 63 AC elements -> middle index 31

        ulong hash = 0UL;
        for (int i = 0; i < 63; i++)
        {
            if (acCoeffs[i] > median)
            {
                hash |= 1UL << (i + 1);
            }
        }
        return hash;
    }

    private static void DownsampleLuminance32x32(DecodedImage image, Span<double> dst)
    {
        int w = image.Width;
        int h = image.Height;
        int stride = image.Stride;
        ReadOnlySpan<byte> px = image.Pixels;
        bool isRgba64 = image.PixelFormat == ImagePixelFormat.Rgba64;

        for (int dy = 0; dy < DctSize; dy++)
        {
            double sy = (dy + 0.5) * h / DctSize - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, h - 1);
            int y1 = Math.Clamp(y0 + 1, 0, h - 1);
            double fy = Math.Clamp(sy - y0, 0.0, 1.0);

            for (int dx = 0; dx < DctSize; dx++)
            {
                double sx = (dx + 0.5) * w / DctSize - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, w - 1);
                int x1 = Math.Clamp(x0 + 1, 0, w - 1);
                double fx = Math.Clamp(sx - x0, 0.0, 1.0);

                double l00 = ReadLuminance(px, stride, x0, y0, isRgba64);
                double l10 = ReadLuminance(px, stride, x1, y0, isRgba64);
                double l01 = ReadLuminance(px, stride, x0, y1, isRgba64);
                double l11 = ReadLuminance(px, stride, x1, y1, isRgba64);

                double top = l00 + (l10 - l00) * fx;
                double bot = l01 + (l11 - l01) * fx;
                dst[dy * DctSize + dx] = top + (bot - top) * fy;
            }
        }
    }

    internal static double ReadLuminance(ReadOnlySpan<byte> px, int stride, int x, int y, bool isRgba64)
    {
        if (isRgba64)
        {
            int off = y * stride + x * 8;
            if (off + 6 > px.Length) return 0.0;
            ushort r = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off, 2));
            ushort g = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 2, 2));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 4, 2));
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 65535.0;
        }
        else
        {
            int off = y * stride + x * 4;
            if (off + 3 > px.Length) return 0.0;
            byte b = px[off];
            byte g = px[off + 1];
            byte r = px[off + 2];
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }
    }

    /// <summary>
    /// Computes an L1-normalized 128-bin HSV histogram (8 Hue x 4 Saturation x 4 Value).
    /// </summary>
    public static float[] ComputeHsvHistogram(DecodedImage image)
    {
        var hist = new float[HistogramBins];
        int w = image.Width;
        int h = image.Height;
        if (w <= 0 || h <= 0 || image.Pixels.Length == 0)
            return hist;

        int stride = image.Stride;
        ReadOnlySpan<byte> px = image.Pixels;
        bool isRgba64 = image.PixelFormat == ImagePixelFormat.Rgba64;
        int step = Math.Max(1, Math.Min(w, h) / 128);
        int samples = 0;

        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                float r, g, b;
                if (isRgba64)
                {
                    int off = y * stride + x * 8;
                    if (off + 6 > px.Length) continue;
                    r = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off, 2)) / 65535f;
                    g = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 2, 2)) / 65535f;
                    b = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 4, 2)) / 65535f;
                }
                else
                {
                    int off = y * stride + x * 4;
                    if (off + 3 > px.Length) continue;
                    b = px[off] / 255f;
                    g = px[off + 1] / 255f;
                    r = px[off + 2] / 255f;
                }

                RgbToHsv(r, g, b, out float hue, out float sat, out float val);
                int hBin = Math.Clamp((int)(hue * 8f), 0, 7);
                int sBin = Math.Clamp((int)(sat * 4f), 0, 3);
                int vBin = Math.Clamp((int)(val * 4f), 0, 3);
                hist[hBin * 16 + sBin * 4 + vBin] += 1f;
                samples++;
            }
        }

        if (samples > 0)
        {
            float inv = 1f / samples;
            for (int i = 0; i < HistogramBins; i++)
            {
                hist[i] *= inv;
            }

            // Separable mass-preserving scatter smoothing [0.25, 0.50, 0.25] across Saturation (0..3) then Value (0..3),
            // leaving Hue (0..7) sharp so exposure shifts don't spike Hellinger distance across coarse bin edges.
            Span<float> tmp = stackalloc float[HistogramBins];
            for (int hIdx = 0; hIdx < 8; hIdx++)
            {
                for (int sIdx = 0; sIdx < 4; sIdx++)
                {
                    for (int vIdx = 0; vIdx < 4; vIdx++)
                    {
                        float val = hist[hIdx * 16 + sIdx * 4 + vIdx];
                        if (val == 0f) continue;

                        tmp[hIdx * 16 + Math.Max(0, sIdx - 1) * 4 + vIdx] += val * 0.25f;
                        tmp[hIdx * 16 + sIdx * 4 + vIdx] += val * 0.50f;
                        tmp[hIdx * 16 + Math.Min(3, sIdx + 1) * 4 + vIdx] += val * 0.25f;
                    }
                }
            }

            Array.Clear(hist);
            for (int hIdx = 0; hIdx < 8; hIdx++)
            {
                for (int sIdx = 0; sIdx < 4; sIdx++)
                {
                    for (int vIdx = 0; vIdx < 4; vIdx++)
                    {
                        float val = tmp[hIdx * 16 + sIdx * 4 + vIdx];
                        if (val == 0f) continue;

                        hist[hIdx * 16 + sIdx * 4 + Math.Max(0, vIdx - 1)] += val * 0.25f;
                        hist[hIdx * 16 + sIdx * 4 + vIdx] += val * 0.50f;
                        hist[hIdx * 16 + sIdx * 4 + Math.Min(3, vIdx + 1)] += val * 0.25f;
                    }
                }
            }
        }
        return hist;
    }

    private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;
        v = max;

        if (max <= 1e-5f || delta <= 1e-5f)
        {
            s = 0f;
            h = 0f;
            return;
        }

        s = delta / max;
        if (r >= max)
            h = (g - b) / delta;
        else if (g >= max)
            h = 2f + (b - r) / delta;
        else
            h = 4f + (r - g) / delta;

        h /= 6f;
        if (h < 0f) h += 1f;
    }

    /// <summary>
    /// Hellinger distance in [0, 1] between two L1-normalized histograms (0 = identical, 1 = disjoint).
    /// </summary>
    public static double ComputeHellingerDistance(ReadOnlySpan<float> h1, ReadOnlySpan<float> h2)
    {
        int len = Math.Min(h1.Length, h2.Length);
        if (len == 0) return 1.0;

        double bhattacharyya = 0.0;
        for (int i = 0; i < len; i++)
        {
            double prod = (double)h1[i] * h2[i];
            if (prod > 0.0)
                bhattacharyya += Math.Sqrt(prod);
        }

        double clamped = Math.Clamp(bhattacharyya, 0.0, 1.0);
        return Math.Sqrt(Math.Max(0.0, 1.0 - clamped));
    }
}
