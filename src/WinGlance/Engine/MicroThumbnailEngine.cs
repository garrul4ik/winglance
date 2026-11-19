using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

/// <summary>
/// Asynchronous 160px micro-thumbnail engine with radial priority queuing and a 300-entry LRU buffer
/// for zero-stutter Filmstrip Scrubber navigation.
/// </summary>
public sealed class MicroThumbnailEngine : IDisposable
{
    public const int ThumbnailMaxSize = 160;
    public const int MaxCacheEntries = 300;
    public const int WorkerCount = 2;

    private readonly object _lock = new();
    private readonly Dictionary<string, (DecodedImage Thumb, LinkedListNode<string> Node)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly List<SlideItem> _pendingQueue = new();
    private readonly HashSet<string> _queuedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlightIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private bool _disposed;

    public event Action<string>? ThumbnailReady;

    public MicroThumbnailEngine()
    {
        _workers = new Task[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            _workers[i] = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    public bool TryGetThumbnail(string itemId, out DecodedImage? thumb)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(itemId, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                thumb = entry.Thumb;
                return true;
            }
        }
        thumb = null;
        return false;
    }

    /// <summary>
    /// Enqueues missing thumbnails in an expanding window centered around <paramref name="centerIndex"/>.
    /// </summary>
    public void RequestWindow(IReadOnlyList<SlideItem> items, int centerIndex, int radius = 24)
    {
        if (_disposed || items.Count == 0) return;

        int clampedCenter = Math.Clamp(centerIndex, 0, items.Count - 1);
        int start = Math.Max(0, clampedCenter - radius);
        int end = Math.Min(items.Count - 1, clampedCenter + radius);

        var ordered = new List<SlideItem>(end - start + 1);
        ordered.Add(items[clampedCenter]);
        for (int dist = 1; dist <= radius; dist++)
        {
            int right = clampedCenter + dist;
            if (right <= end) ordered.Add(items[right]);
            int left = clampedCenter - dist;
            if (left >= start) ordered.Add(items[left]);
        }

        int enqueued = 0;
        lock (_lock)
        {
            // Keep fresher priority around new viewport center by inserting front-most items cleanly
            _pendingQueue.Clear();
            _queuedIds.Clear();

            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var it = ordered[i];
                if (_cache.ContainsKey(it.Id) || _inFlightIds.Contains(it.Id) || !_queuedIds.Add(it.Id))
                    continue;

                _pendingQueue.Add(it);
                enqueued++;
            }
        }

        if (enqueued > 0)
        {
            try { _signal.Release(Math.Min(enqueued, WorkerCount)); } catch { }
        }
    }

    public void Invalidate(string itemId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(itemId, out var existing))
            {
                _lru.Remove(existing.Node);
                _cache.Remove(itemId);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _pendingQueue.Clear();
            _queuedIds.Clear();
            _cache.Clear();
            _lru.Clear();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!ct.IsCancellationRequested)
            {
                SlideItem? next = null;
                lock (_lock)
                {
                    if (_pendingQueue.Count == 0) break;
                    int lastIdx = _pendingQueue.Count - 1; // Closest to centerIndex was pushed last
                    next = _pendingQueue[lastIdx];
                    _pendingQueue.RemoveAt(lastIdx);
                    _queuedIds.Remove(next.Id);

                    if (_cache.ContainsKey(next.Id) || !_inFlightIds.Add(next.Id))
                    {
                        next = null;
                    }
                }

                if (next == null) continue;

                try
                {
                    var thumb = FastImageDecoder.Decode(next.Path, maxBoundingPixelSize: ThumbnailMaxSize);
                    if (thumb != null && !ct.IsCancellationRequested)
                    {
                        PutCache(next.Id, thumb);
                        ThumbnailReady?.Invoke(next.Id);
                    }
                }
                catch
                {
                    // Skip unreadable / deleted item cleanly
                }
                finally
                {
                    lock (_lock)
                    {
                        _inFlightIds.Remove(next.Id);
                    }
                }
            }
        }
    }

    private void PutCache(string itemId, DecodedImage thumb)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(itemId, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                _cache[itemId] = (thumb, existing.Node);
                return;
            }

            if (_cache.Count >= MaxCacheEntries && _lru.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lru.RemoveLast();
            }

            var node = _lru.AddFirst(itemId);
            _cache[itemId] = (thumb, node);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _signal.Release(WorkerCount); } catch { }
        try { Task.WaitAll(_workers, 250); } catch { }
        _signal.Dispose();
        _cts.Dispose();
        Clear();
    }
}
