using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;
using WinGlance.Vision;

namespace WinGlance.Engine;

/// <summary>
/// Per-image technical aesthetic quality metrics scaled cleanly to 0.0 - 10.0.
/// </summary>
public sealed record AestheticScoreResult(
    double OverallScore,
    double SharpnessNorm,
    double ExposureNorm,
    double FaceNorm,
    bool HasFaces)
{
    public string FormattedScore => OverallScore.ToString("0.0", CultureInfo.InvariantCulture);

    public string FormattedBadge => $"AI {FormattedScore}";

    /// <summary>
    /// Pairwise aesthetic comparison with a 0.3 tie tolerance matching upstream Burst Hero matchup rules.
    /// </summary>
    public static int CompareMatchup(AestheticScoreResult a, AestheticScoreResult b)
    {
        double diff = a.OverallScore - b.OverallScore;
        if (diff > 0.3) return 1;
        if (diff < -0.3) return -1;
        return 0;
    }
}

/// <summary>
/// Full evaluation result for a burst cluster including the winning Hero frame and per-frame scores.
/// </summary>
public sealed record BurstHeroEvaluation(
    int BurstHeroLocalIndex,
    SlideItem HeroItem,
    AestheticScoreResult HeroScore,
    IReadOnlyDictionary<int, AestheticScoreResult> FrameScores);

/// <summary>
/// Pure NativeAOT aesthetic &amp; technical focus evaluator combining:
/// 1) Central 70% ROI 8x8 block Laplacian variance (top 20% blocks so shallow DoF bokeh is not penalized),
/// 2) Luminance histogram exposure balance &amp; clip resistance,
/// 3) Optional YuNet neural face/eye sharpness bonus when faces are present.
/// </summary>
public sealed class AestheticsEngine
{
    public const int MaxCacheEntries = 300;
    public const int MaxBurstEvaluationFrames = 30;
    public const int MaxFaceAnalysisFrames = 15;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (AestheticScoreResult Score, LinkedListNode<string> Node)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    public bool TryGetCachedScore(string itemId, out AestheticScoreResult? result)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                result = entry.Score;
                return true;
            }
        }
        result = null;
        return false;
    }

    private void PutCache(string itemId, AestheticScoreResult score)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                _cache[itemId] = (score, existing.Node);
                return;
            }

            if (_cache.Count >= MaxCacheEntries && _lru.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lru.RemoveLast();
            }

            var node = _lru.AddFirst(itemId);
            _cache[itemId] = (score, node);
        }
    }

    /// <summary>
    /// Computes technical aesthetic quality from an in-memory DecodedImage.
    /// </summary>
    public static AestheticScoreResult Analyze(DecodedImage image, bool detectFaces = true)
    {
        double sharpnessNorm = ComputeTop20PercentSharpness(image);
        double exposureNorm = ComputeExposureQuality(image);

        double faceNorm = 0.0;
        bool hasFaces = false;

        if (detectFaces && EyeFocusDetector.IsNeuralEngineAvailable)
        {
            var targets = EyeFocusDetector.DetectFacesAndEyes(image, scoreThreshold: 0.45f);
            SubjectEyeTarget? neuralFace = null;
            foreach (var t in targets)
            {
                if (!string.Equals(t.Backend, "Contrast", StringComparison.OrdinalIgnoreCase))
                {
                    neuralFace = t;
                    break;
                }
            }

            if (neuralFace != null)
            {
                hasFaces = true;
                float eyeSharp = EyeFocusDetector.MeasureLocalSharpness(image, neuralFace.PrimaryEye);
                double eyeNorm = Math.Clamp(eyeSharp / 0.14f, 0.0, 1.0);
                faceNorm = Math.Clamp(0.75 * eyeNorm + 0.25 * neuralFace.Score, 0.0, 1.0);
            }
        }

        double combinedNorm = hasFaces
            ? (0.42 * sharpnessNorm + 0.25 * exposureNorm + 0.33 * faceNorm)
            : (0.62 * sharpnessNorm + 0.38 * exposureNorm);

        double overall = Math.Clamp(Math.Round(combinedNorm * 10.0, 1), 0.5, 9.9);
        return new AestheticScoreResult(overall, sharpnessNorm, exposureNorm, faceNorm, hasFaces);
    }

    /// <summary>
    /// Computes or returns cached score for a SlideItem, reusing cached screen buffers when available.
    /// </summary>
    public async Task<AestheticScoreResult?> ScoreItemAsync(
        SlideItem item,
        SlidePipeline? pipeline = null,
        bool detectFaces = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (TryGetCachedScore(item.Id, out var cached) && cached != null)
            return cached;

        if (pipeline != null && pipeline.TryGetCachedScreenImage(item.Id, out var screenImg) && screenImg != null)
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var score = Analyze(screenImg, detectFaces);
                PutCache(item.Id, score);
                return score;
            }, ct).ConfigureAwait(false);
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var decoded = FastImageDecoder.Decode(item.Path, maxBoundingPixelSize: 640);
            if (decoded == null) return null;

            var score = Analyze(decoded, detectFaces);
            PutCache(item.Id, score);
            return score;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates frames inside a burst cluster (capped at 30 frames) and identifies the Hero frame by highest AI score.
    /// </summary>
    public async Task<BurstHeroEvaluation?> EvaluateBurstHeroAsync(
        IReadOnlyList<SlideItem> burstItems,
        SlidePipeline? pipeline = null,
        CancellationToken ct = default)
    {
        if (burstItems.Count < 2)
            return null;

        int count = Math.Min(burstItems.Count, MaxBurstEvaluationFrames);
        bool runFaces = count <= MaxFaceAnalysisFrames && EyeFocusDetector.IsNeuralEngineAvailable;

        var scores = new Dictionary<int, AestheticScoreResult>(count);
        int bestIdx = -1;
        AestheticScoreResult? bestScore = null;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var score = await ScoreItemAsync(burstItems[i], pipeline, runFaces, ct).ConfigureAwait(false);
            if (score == null) continue;

            scores[i] = score;
            if (bestScore == null || score.OverallScore > bestScore.OverallScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        if (bestIdx < 0 || bestScore == null)
            return null;

        return new BurstHeroEvaluation(bestIdx, burstItems[bestIdx], bestScore, scores);
    }

    /// <summary>
    /// Evaluates sharpness inside the central 70% ROI partitioned into an 8x8 block grid and averages the sharpest 20%.
    /// </summary>
    public static double ComputeTop20PercentSharpness(DecodedImage image)
    {
        int w = image.Width;
        int h = image.Height;
        if (w < 16 || h < 16 || image.Pixels.Length == 0)
            return 0.0;

        int xStart = (int)(w * 0.15);
        int xEnd = (int)(w * 0.85);
        int yStart = (int)(h * 0.15);
        int yEnd = (int)(h * 0.85);
        int roiW = xEnd - xStart;
        int roiH = yEnd - yStart;
        if (roiW < 16 || roiH < 16)
            return 0.0;

        const int grid = 8;
        int step = Math.Max(1, Math.Min(roiW, roiH) / 480);
        int stride = image.Stride;
        ReadOnlySpan<byte> px = image.Pixels;
        bool isRgba64 = image.PixelFormat == ImagePixelFormat.Rgba64;

        Span<double> blockEnergies = stackalloc double[grid * grid];

        for (int by = 0; by < grid; by++)
        {
            int py0 = Math.Max(1, yStart + (by * roiH) / grid);
            int py1 = Math.Min(h - 2, yStart + ((by + 1) * roiH) / grid);

            for (int bx = 0; bx < grid; bx++)
            {
                int px0 = Math.Max(1, xStart + (bx * roiW) / grid);
                int px1 = Math.Min(w - 2, xStart + ((bx + 1) * roiW) / grid);

                double sumSq = 0.0;
                int samples = 0;

                for (int y = py0; y < py1; y += step)
                {
                    for (int x = px0; x < px1; x += step)
                    {
                        double c = SceneDetectionEngine.ReadLuminance(px, stride, x, y, isRgba64);
                        double l = SceneDetectionEngine.ReadLuminance(px, stride, x - step >= 0 ? x - step : x - 1, y, isRgba64);
                        double r = SceneDetectionEngine.ReadLuminance(px, stride, x + step < w ? x + step : x + 1, y, isRgba64);
                        double t = SceneDetectionEngine.ReadLuminance(px, stride, x, y - step >= 0 ? y - step : y - 1, isRgba64);
                        double b = SceneDetectionEngine.ReadLuminance(px, stride, x, y + step < h ? y + step : y + 1, isRgba64);
                        double lap = 4.0 * c - l - r - t - b;
                        sumSq += lap * lap;
                        samples++;
                    }
                }

                blockEnergies[by * grid + bx] = samples > 0 ? sumSq / samples : 0.0;
            }
        }

        blockEnergies.Sort();
        int topCount = Math.Max(1, (int)Math.Ceiling(blockEnergies.Length * 0.20)); // top 13 of 64 blocks
        double topSum = 0.0;
        for (int i = blockEnergies.Length - topCount; i < blockEnergies.Length; i++)
        {
            topSum += blockEnergies[i];
        }
        double top20Mean = topSum / topCount;
        return Math.Clamp(Math.Tanh(Math.Sqrt(top20Mean) * 4.5), 0.0, 1.0);
    }

    /// <summary>
    /// Evaluates luminance exposure balance, tonal dynamic spread, and highlight/shadow clipping penalties.
    /// </summary>
    public static double ComputeExposureQuality(DecodedImage image)
    {
        var hist = HistogramEngine.Compute(image);
        long total = 0;
        double weightedSum = 0.0;
        long clippedLow = 0;
        long clippedHigh = 0;

        for (int i = 0; i < 256; i++)
        {
            int count = hist.Luminance[i];
            if (count <= 0) continue;
            total += count;
            weightedSum += (i / 255.0) * count;
            if (i <= 4) clippedLow += count;
            else if (i >= 251) clippedHigh += count;
        }

        if (total == 0) return 0.5;

        double mean = weightedSum / total;
        double varSum = 0.0;
        for (int i = 0; i < 256; i++)
        {
            int count = hist.Luminance[i];
            if (count <= 0) continue;
            double diff = (i / 255.0) - mean;
            varSum += diff * diff * count;
        }
        double stdDev = Math.Sqrt(varSum / total);

        double lowRatio = (double)clippedLow / total;
        double highRatio = (double)clippedHigh / total;
        double penalty = highRatio * 2.5 + lowRatio * 1.8;

        double midtoneScore = Math.Clamp(1.0 - Math.Abs(mean - 0.48) * 1.6, 0.0, 1.0);
        double dynamicSpread = Math.Clamp(stdDev / 0.20, 0.0, 1.0);
        return Math.Clamp(0.65 * midtoneScore + 0.35 * dynamicSpread - penalty, 0.05, 1.0);
    }
}
