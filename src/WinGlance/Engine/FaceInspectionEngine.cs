using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;
using WinGlance.Vision;

namespace WinGlance.Engine;

/// <summary>
/// Cropped face tile extracted for the Face Verification Dock (A).
/// </summary>
public sealed record FaceCropTile(
    int FaceIndex,
    DecodedImage CropImage,
    NormalizedPoint EyeCenter,
    NormalizedRect FaceBox,
    float Sharpness,
    bool IsSharp,
    float Confidence);

/// <summary>
/// Extracts 128x128 square face crops (with 30% headroom/margin) and eye-region focus sharpness scores
/// from YuNet detections, backed by a thread-safe 100-entry LRU cache.
/// </summary>
public sealed class FaceInspectionEngine
{
    public const int MaxCacheEntries = 100;
    public const int MaxFacesPerPhoto = 8;
    public const int CropOutputSize = 128;
    public const float SharpEyeThreshold = 0.032f;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (IReadOnlyList<FaceCropTile> Tiles, LinkedListNode<string> Node)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    public bool TryGetCachedFaces(string itemId, out IReadOnlyList<FaceCropTile> tiles)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                tiles = entry.Tiles;
                return true;
            }
        }
        tiles = Array.Empty<FaceCropTile>();
        return false;
    }

    private void PutCache(string itemId, IReadOnlyList<FaceCropTile> tiles)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(itemId, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                _cache[itemId] = (tiles, existing.Node);
                return;
            }

            if (_cache.Count >= MaxCacheEntries && _lru.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lru.RemoveLast();
            }

            var node = _lru.AddFirst(itemId);
            _cache[itemId] = (tiles, node);
        }
    }

    /// <summary>
    /// Detects neural YuNet faces and builds cropped inspection tiles off the UI thread.
    /// Returns empty list when YuNet finds zero faces.
    /// </summary>
    public async Task<IReadOnlyList<FaceCropTile>> GetFacesAsync(
        SlideItem item,
        DecodedImage decoded,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (TryGetCachedFaces(item.Id, out var cached))
            return cached;

        if (!EyeFocusDetector.IsNeuralEngineAvailable)
            return Array.Empty<FaceCropTile>();

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var targets = EyeFocusDetector.DetectFacesAndEyes(decoded, scoreThreshold: 0.45f);
            var neuralFaces = targets
                .Where(t => !string.Equals(t.Backend, "Contrast", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FaceBox.X)
                .Take(MaxFacesPerPhoto)
                .ToList();

            if (neuralFaces.Count == 0)
            {
                PutCache(item.Id, Array.Empty<FaceCropTile>());
                return (IReadOnlyList<FaceCropTile>)Array.Empty<FaceCropTile>();
            }

            var result = new List<FaceCropTile>(neuralFaces.Count);
            for (int i = 0; i < neuralFaces.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = neuralFaces[i];
                var crop = ExtractCrop(decoded, target.FaceBox, CropOutputSize);
                float sharp = EyeFocusDetector.MeasureLocalSharpness(decoded, target.PrimaryEye);
                bool isSharp = sharp >= SharpEyeThreshold;

                result.Add(new FaceCropTile(
                    FaceIndex: i + 1,
                    CropImage: crop,
                    EyeCenter: target.PrimaryEye,
                    FaceBox: target.FaceBox,
                    Sharpness: sharp,
                    IsSharp: isSharp,
                    Confidence: target.Score));
            }

            IReadOnlyList<FaceCropTile> readonlyResult = result;
            PutCache(item.Id, readonlyResult);
            return readonlyResult;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts a square 8-bit BGRA crop centered on <paramref name="faceBox"/> with +30% padding on each side.
    /// </summary>
    public static DecodedImage ExtractCrop(DecodedImage source, NormalizedRect faceBox, int outputSize = CropOutputSize)
    {
        int outSize = Math.Max(16, outputSize);
        int srcW = Math.Max(1, source.Width);
        int srcH = Math.Max(1, source.Height);

        float cx = (faceBox.X + faceBox.Width * 0.5f) * srcW;
        float cy = (faceBox.Y + faceBox.Height * 0.5f) * srcH;
        float boxW = MathF.Max(8f, faceBox.Width * srcW);
        float boxH = MathF.Max(8f, faceBox.Height * srcH);

        // Expand by 30% padding on all 4 sides => total span = 1.6x max dimension
        float side = MathF.Min(MathF.Min(srcW, srcH), MathF.Max(boxW, boxH) * 1.60f);
        float half = side * 0.5f;

        float x0 = Math.Clamp(cx - half, 0f, MathF.Max(0f, srcW - side));
        float y0 = Math.Clamp(cy - half, 0f, MathF.Max(0f, srcH - side));

        byte[] dst = new byte[outSize * outSize * 4];
        ReadOnlySpan<byte> src = source.Pixels;
        int stride = source.Stride;
        bool isRgba64 = source.PixelFormat == ImagePixelFormat.Rgba64;

        for (int dy = 0; dy < outSize; dy++)
        {
            float sy = y0 + ((dy + 0.5f) / outSize) * side;
            int iy0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
            int iy1 = Math.Clamp(iy0 + 1, 0, srcH - 1);
            float fy = Math.Clamp(sy - iy0, 0f, 1f);

            for (int dx = 0; dx < outSize; dx++)
            {
                float sx = x0 + ((dx + 0.5f) / outSize) * side;
                int ix0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1);
                int ix1 = Math.Clamp(ix0 + 1, 0, srcW - 1);
                float fx = Math.Clamp(sx - ix0, 0f, 1f);

                SampleBgra(src, stride, ix0, iy0, isRgba64, out float b00, out float g00, out float r00);
                SampleBgra(src, stride, ix1, iy0, isRgba64, out float b10, out float g10, out float r10);
                SampleBgra(src, stride, ix0, iy1, isRgba64, out float b01, out float g01, out float r01);
                SampleBgra(src, stride, ix1, iy1, isRgba64, out float b11, out float g11, out float r11);

                float b = Lerp2(b00, b10, b01, b11, fx, fy);
                float g = Lerp2(g00, g10, g01, g11, fx, fy);
                float r = Lerp2(r00, r10, r01, r11, fx, fy);

                int off = (dy * outSize + dx) * 4;
                dst[off] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
                dst[off + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                dst[off + 2] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                dst[off + 3] = 255;
            }
        }

        return new DecodedImage(dst, outSize, outSize, outSize * 4, ImagePixelFormat.Bgra32, outSize, outSize,
            isDownscaled: true, isRaw: false);
    }

    private static float Lerp2(float c00, float c10, float c01, float c11, float fx, float fy)
    {
        float top = c00 + (c10 - c00) * fx;
        float bot = c01 + (c11 - c01) * fx;
        return top + (bot - top) * fy;
    }

    private static void SampleBgra(ReadOnlySpan<byte> px, int stride, int x, int y, bool isRgba64,
        out float b, out float g, out float r)
    {
        if (isRgba64)
        {
            int off = y * stride + x * 8;
            if (off + 6 > px.Length) { b = g = r = 0f; return; }
            r = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off, 2)) / 257f;
            g = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 2, 2)) / 257f;
            b = BinaryPrimitives.ReadUInt16LittleEndian(px.Slice(off + 4, 2)) / 257f;
        }
        else
        {
            int off = y * stride + x * 4;
            if (off + 3 > px.Length) { b = g = r = 0f; return; }
            b = px[off];
            g = px[off + 1];
            r = px[off + 2];
        }
    }
}
