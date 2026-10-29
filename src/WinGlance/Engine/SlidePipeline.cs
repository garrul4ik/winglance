using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed class SlidePipeline
{
    private readonly ConcurrentDictionary<int, DecodedImage> _cache = new();
    private readonly ConcurrentDictionary<int, Task<DecodedImage>> _inFlightTasks = new();
    private readonly ConcurrentDictionary<int, DecodedImage> _fullResCache = new();

    private IReadOnlyList<SlideItem> _items = Array.Empty<SlideItem>();
    private int _currentIndex;
    private int _maxPixelSize = 3840;
    private CancellationTokenSource? _preloadCts;
    private readonly int _cacheRadius = 2; // [curr-2, curr-1, curr, curr+1, curr+2]

    public int CurrentIndex => _currentIndex;
    public int Count => _items.Count;

    public void SetPlaylist(IReadOnlyList<SlideItem> items, int initialIndex, int maxPixelSize)
    {
        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = new CancellationTokenSource();

        _cache.Clear();
        _inFlightTasks.Clear();
        _fullResCache.Clear();

        _items = items;
        _currentIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1));
        _maxPixelSize = maxPixelSize;

        if (_items.Count > 0)
        {
            PreloadWindow(_currentIndex, isForward: true);
        }
    }

    public void Invalidate(int index)
    {
        _cache.TryRemove(index, out _);
        _fullResCache.TryRemove(index, out _);
        _inFlightTasks.TryRemove(index, out _);
    }

    public void UpdateBoundingSize(int newSize)
    {
        if (_maxPixelSize == newSize || newSize <= 0) return;
        _maxPixelSize = newSize;
        if (_items.Count > 0)
        {
            PreloadWindow(_currentIndex, isForward: true);
        }
    }

    public async Task<DecodedImage> GetImageAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of bounds.");
        }

        if (_cache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        if (_inFlightTasks.TryGetValue(index, out var existingTask))
        {
            return await existingTask;
        }

        var item = _items[index];
        var bounding = _maxPixelSize;

        var task = Task.Run(() => FastImageDecoder.Decode(item.Path, bounding), ct);
        _inFlightTasks[index] = task;

        try
        {
            var decoded = await task;
            _cache[index] = decoded;
            return decoded;
        }
        finally
        {
            _inFlightTasks.TryRemove(index, out _);
        }
    }

    public async Task<DecodedImage> GetFullResolutionImageAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of bounds.");
        }

        var item = _items[index];

        // Reuse screen-bounded image only if not downscaled AND not a RAW preview awaiting 16-bit sensor demosaic
        if (!item.IsStacked &&
            _cache.TryGetValue(index, out var screenCached) &&
            !screenCached.IsDownscaled &&
            (!screenCached.IsRaw || screenCached.IsFullSensorRaw))
        {
            return screenCached;
        }

        if (_fullResCache.TryGetValue(index, out var fullCached))
        {
            return fullCached;
        }

        var targetPath = item.RawPath ?? item.Path;
        var decoded = await Task.Run(() => FastImageDecoder.Decode(targetPath, maxBoundingPixelSize: 0), ct);

        if (_fullResCache.Count >= 2)
        {
            _fullResCache.Clear();
        }

        _fullResCache[index] = decoded;
        return decoded;
    }

    public void NotifyCurrentIndexChanged(int newIndex, bool isForward)
    {
        _currentIndex = Math.Clamp(newIndex, 0, Math.Max(0, _items.Count - 1));
        if (_items.Count > 0)
        {
            PreloadWindow(_currentIndex, isForward);
        }
    }

    private void PreloadWindow(int centerIndex, bool isForward)
    {
        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = new CancellationTokenSource();
        var ct = _preloadCts.Token;

        var indicesToKeep = new HashSet<int>();
        var indicesToPreload = new List<int>();

        if (isForward)
        {
            for (int r = 1; r <= _cacheRadius; r++)
            {
                var fwd = centerIndex + r;
                if (fwd < _items.Count) indicesToPreload.Add(fwd);
                var bwd = centerIndex - r;
                if (bwd >= 0) indicesToPreload.Add(bwd);
            }
        }
        else
        {
            for (int r = 1; r <= _cacheRadius; r++)
            {
                var bwd = centerIndex - r;
                if (bwd >= 0) indicesToPreload.Add(bwd);
                var fwd = centerIndex + r;
                if (fwd < _items.Count) indicesToPreload.Add(fwd);
            }
        }

        indicesToKeep.Add(centerIndex);
        foreach (var idx in indicesToPreload) indicesToKeep.Add(idx);

        foreach (var key in _cache.Keys)
        {
            if (!indicesToKeep.Contains(key))
            {
                _cache.TryRemove(key, out _);
            }
        }

        _ = Task.Run(async () =>
        {
            foreach (var idx in indicesToPreload)
            {
                if (ct.IsCancellationRequested) break;
                if (_cache.ContainsKey(idx)) continue;

                try
                {
                    await GetImageAsync(idx, ct);
                }
                catch { }
            }
        }, ct);
    }
}
