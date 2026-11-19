using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed class SlidePipeline
{
    private readonly ConcurrentDictionary<string, DecodedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<DecodedImage>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DecodedImage> _fullResCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<DecodedImage>> _inFlightFullResTasks = new(StringComparer.OrdinalIgnoreCase);

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
        _inFlightFullResTasks.Clear();

        _items = items;
        _currentIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1));
        _maxPixelSize = maxPixelSize;

        if (_items.Count > 0)
        {
            PreloadWindow(_currentIndex, isForward: true);
        }
    }

    /// <summary>
    /// Updates the active playlist items and index while preserving cached ID-keyed decodes
    /// that remain inside the preload window (prevents cache desync on delete/restore/watcher updates).
    /// </summary>
    public void UpdateItems(IReadOnlyList<SlideItem> items, int newCurrentIndex)
    {
        _items = items;
        _currentIndex = Math.Clamp(newCurrentIndex, 0, Math.Max(0, items.Count - 1));

        if (_items.Count == 0)
        {
            _preloadCts?.Cancel();
            _cache.Clear();
            _inFlightTasks.Clear();
            _fullResCache.Clear();
            _inFlightFullResTasks.Clear();
            return;
        }

        PreloadWindow(_currentIndex, isForward: true);
    }

    public void Invalidate(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        Invalidate(_items[index].Id);
    }

    public void Invalidate(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        _cache.TryRemove(itemId, out _);
        _fullResCache.TryRemove(itemId, out _);
        _inFlightTasks.TryRemove(itemId, out _);
        _inFlightFullResTasks.TryRemove(itemId, out _);
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

    public bool TryGetCachedScreenImage(int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DecodedImage? image)
    {
        if (index < 0 || index >= _items.Count)
        {
            image = null;
            return false;
        }
        return _cache.TryGetValue(_items[index].Id, out image);
    }

    public bool TryGetCachedScreenImage(string itemId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DecodedImage? image)
    {
        return _cache.TryGetValue(itemId, out image);
    }

    public async Task<DecodedImage> GetImageAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of bounds.");
        }

        var item = _items[index];
        if (_cache.TryGetValue(item.Id, out var cached))
        {
            return cached;
        }

        if (_inFlightTasks.TryGetValue(item.Id, out var existingTask))
        {
            return await existingTask;
        }

        var bounding = _maxPixelSize;
        var task = Task.Run(() => FastImageDecoder.Decode(item.Path, bounding), ct);
        _inFlightTasks[item.Id] = task;

        try
        {
            var decoded = await task;
            _cache[item.Id] = decoded;
            return decoded;
        }
        finally
        {
            _inFlightTasks.TryRemove(item.Id, out _);
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
            _cache.TryGetValue(item.Id, out var screenCached) &&
            !screenCached.IsDownscaled &&
            (!screenCached.IsRaw || screenCached.IsFullSensorRaw))
        {
            return screenCached;
        }

        if (_fullResCache.TryGetValue(item.Id, out var fullCached))
        {
            return fullCached;
        }

        if (_inFlightFullResTasks.TryGetValue(item.Id, out var existingTask))
        {
            return await existingTask;
        }

        var targetPath = item.RawPath ?? item.Path;
        var task = Task.Run(() => FastImageDecoder.Decode(targetPath, maxBoundingPixelSize: 0), ct);
        _inFlightFullResTasks[item.Id] = task;

        try
        {
            var decoded = await task;
            if (_fullResCache.Count >= 2)
            {
                foreach (var key in _fullResCache.Keys)
                {
                    if (!string.Equals(key, item.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _fullResCache.TryRemove(key, out _);
                        break;
                    }
                }
            }

            _fullResCache[item.Id] = decoded;
            return decoded;
        }
        finally
        {
            _inFlightFullResTasks.TryRemove(item.Id, out _);
        }
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

        var idsToKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsToPreload = new List<(int Index, SlideItem Item)>();

        if (isForward)
        {
            for (int r = 1; r <= _cacheRadius; r++)
            {
                var fwd = centerIndex + r;
                if (fwd < _items.Count) itemsToPreload.Add((fwd, _items[fwd]));
                var bwd = centerIndex - r;
                if (bwd >= 0) itemsToPreload.Add((bwd, _items[bwd]));
            }
        }
        else
        {
            for (int r = 1; r <= _cacheRadius; r++)
            {
                var bwd = centerIndex - r;
                if (bwd >= 0) itemsToPreload.Add((bwd, _items[bwd]));
                var fwd = centerIndex + r;
                if (fwd < _items.Count) itemsToPreload.Add((fwd, _items[fwd]));
            }
        }

        if (centerIndex >= 0 && centerIndex < _items.Count)
        {
            idsToKeep.Add(_items[centerIndex].Id);
        }
        foreach (var entry in itemsToPreload)
        {
            idsToKeep.Add(entry.Item.Id);
        }

        foreach (var key in _cache.Keys)
        {
            if (!idsToKeep.Contains(key))
            {
                _cache.TryRemove(key, out _);
            }
        }

        foreach (var key in _fullResCache.Keys)
        {
            if (!idsToKeep.Contains(key))
            {
                _fullResCache.TryRemove(key, out _);
            }
        }

        _ = Task.Run(async () =>
        {
            foreach (var (idx, item) in itemsToPreload)
            {
                if (ct.IsCancellationRequested) break;
                if (_cache.ContainsKey(item.Id)) continue;

                try
                {
                    await GetImageAsyncByItem(item, ct);
                }
                catch { }
            }
        }, ct);
    }

    private async Task GetImageAsyncByItem(SlideItem item, CancellationToken ct)
    {
        if (_cache.ContainsKey(item.Id)) return;
        if (_inFlightTasks.TryGetValue(item.Id, out var existing))
        {
            await existing;
            return;
        }

        var bounding = _maxPixelSize;
        var task = Task.Run(() => FastImageDecoder.Decode(item.Path, bounding), ct);
        _inFlightTasks[item.Id] = task;

        try
        {
            var decoded = await task;
            _cache[item.Id] = decoded;
        }
        finally
        {
            _inFlightTasks.TryRemove(item.Id, out _);
        }
    }
}
