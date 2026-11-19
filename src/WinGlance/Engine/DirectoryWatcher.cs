using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace WinGlance.Engine;

/// <summary>
/// Watches the active photo folder for external additions/deletions/renames while suppressing
/// WinGlance's own .xmp sidecar writes, in-place EXIF rotations, and temporary files.
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    private readonly string _directoryPath;
    private readonly SynchronizationContext? _syncContext;
    private readonly ConcurrentDictionary<string, long> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _timerLock = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;

    public event Action<string>? DirectoryChanged;

    public DirectoryWatcher(string directoryPath, SynchronizationContext? syncContext = null)
    {
        _directoryPath = Path.GetFullPath(directoryPath);
        _syncContext = syncContext;
    }

    public void Start()
    {
        if (_disposed || !Directory.Exists(_directoryPath)) return;
        Stop();

        _watcher = new FileSystemWatcher(_directoryPath)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
        };

        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamedEvent;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileEvent;
            _watcher.Deleted -= OnFileEvent;
            _watcher.Renamed -= OnRenamedEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    public void SuppressPath(string path, int ttlMs = 2500)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = Path.GetFullPath(path);
        _suppressedPaths[full] = Environment.TickCount64 + Math.Max(100, ttlMs);
    }

    public bool ShouldProcessPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        string name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) return false;

        if (name.StartsWith('.') || name.StartsWith('~')) return false;
        if (name.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!SupportedFormats.IsSupported(fullPath)) return false;

        long now = Environment.TickCount64;
        string normalized = Path.GetFullPath(fullPath);
        if (_suppressedPaths.TryGetValue(normalized, out long expiresAt))
        {
            if (now <= expiresAt)
            {
                return false;
            }
            _suppressedPaths.TryRemove(normalized, out _);
        }

        // Opportunistic cleanup of stale suppression records
        if (_suppressedPaths.Count > 64)
        {
            foreach (var kv in _suppressedPaths)
            {
                if (now > kv.Value) _suppressedPaths.TryRemove(kv.Key, out _);
            }
        }

        return true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldProcessPath(e.FullPath))
        {
            ScheduleDebounce();
        }
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        if (ShouldProcessPath(e.FullPath) || ShouldProcessPath(e.OldFullPath))
        {
            ScheduleDebounce();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            if (!_disposed && Directory.Exists(_directoryPath))
            {
                Start();
            }
        }
        catch { }
    }

    private void ScheduleDebounce()
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            if (_disposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => RaiseDebounced(), null, 350, Timeout.Infinite);
        }
    }

    private void RaiseDebounced()
    {
        if (_disposed) return;

        if (_syncContext != null)
        {
            _syncContext.Post(_ =>
            {
                if (!_disposed) DirectoryChanged?.Invoke(_directoryPath);
            }, null);
        }
        else
        {
            DirectoryChanged?.Invoke(_directoryPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
