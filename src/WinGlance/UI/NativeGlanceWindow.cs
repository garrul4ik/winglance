using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice;
using WinGlance.Engine;
using WinGlance.Gpu;
using WinGlance.Model;
using WinGlance.Vision;

namespace WinGlance.UI;

internal sealed class Win32SynchronizationContext : SynchronizationContext
{
    private readonly IntPtr _hwnd;
    private readonly int _uiThreadId;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    public const uint WM_APP_SYNC = 0x8001;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    public Win32SynchronizationContext(IntPtr hwnd, int uiThreadId)
    {
        _hwnd = hwnd;
        _uiThreadId = uiThreadId;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        PostMessageW(_hwnd, WM_APP_SYNC, 0, 0);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            d(state);
            return;
        }
        using var ev = new ManualResetEventSlim(false);
        Post(_ =>
        {
            try { d(state); }
            finally { ev.Set(); }
        }, null);
        ev.Wait();
    }

    public void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            item.Callback(item.State);
        }
    }
}

public sealed class NativeGlanceWindow : IDisposable
{
    private static NativeGlanceWindow? s_instance;

    private IntPtr _hwnd;
    private Win32SynchronizationContext? _syncCtx;
    private readonly D2DRenderEngine _renderer = new();
    private readonly SlidePipeline _pipeline = new();
    private readonly RenderHudState _hud = new();

    private string _currentDirectory = "";
    private List<SlideItem> _allItems = new();
    private List<SlideItem> _filteredItems = new();
    private Dictionary<string, CullingMetadata> _cullingMap = new(StringComparer.OrdinalIgnoreCase);

    private DecodedImage? _primaryDecoded;
    private DecodedImage? _secondaryDecoded;
    private int _loadGeneration;

    // Eye Jump cycling state
    private string? _cachedEyeImageId;
    private IReadOnlyList<EyeJumpStop> _eyeStops = Array.Empty<EyeJumpStop>();
    private int _eyeStopCursor = -1;
    private long _lastZKeyPressMs;

    // Window & Input state
    private bool _isFullscreen = true;
    private RECT _savedWindowRect = new() { Left = 120, Top = 80, Right = 1560, Bottom = 960 };
    private bool _isDraggingPan;
    private POINT _lastMouseClient;
    private bool _cursorHidden;

    // Animation & Idle timers
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastActivityMs;
    private long _toastShownAtMs;
    private long _eyeReticleShownAtMs;
    private long _lastSlideshowAdvanceMs;
    private bool _needsRedraw = true;

    private static readonly CullingFilter[] FilterCycle =
    {
        CullingFilter.All,
        CullingFilter.PicksOnly,
        CullingFilter.HideRejects,
        CullingFilter.RejectsOnly,
        CullingFilter.UnratedOnly,
        new CullingFilter(CullingFilterType.MinRating, 3),
        new CullingFilter(CullingFilterType.MinRating, 5)
    };
    private int _filterCycleIndex;

    public unsafe void Run(string? initialPath)
    {
        s_instance = this;
        var hInstance = GetModuleHandleW(null);
        var iconHandle = LoadEmbeddedAppIcon(hInstance);

        const string className = "WinGlanceNativeWndClass";
        fixed (char* pClassName = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_DBLCLKS,
                lpfnWndProc = &WndProcStatic,
                hInstance = hInstance,
                hIcon = iconHandle,
                hIconSm = iconHandle,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszClassName = pClassName
            };
            RegisterClassExW(ref wc);

            var monRect = GetCursorMonitorBounds();
            _savedWindowRect = new RECT
            {
                Left = monRect.Left + (monRect.Width - Math.Min(1440, monRect.Width - 80)) / 2,
                Top = monRect.Top + (monRect.Height - Math.Min(900, monRect.Height - 80)) / 2,
                Right = monRect.Left + (monRect.Width + Math.Min(1440, monRect.Width - 80)) / 2,
                Top_Unused = 0,
                Bottom = monRect.Top + (monRect.Height + Math.Min(900, monRect.Height - 80)) / 2
            };

            fixed (char* pTitle = "WinGlance")
            {
                _hwnd = CreateWindowExW(
                    WS_EX_ACCEPTFILES,
                    pClassName,
                    pTitle,
                    WS_POPUP | WS_VISIBLE,
                    monRect.Left,
                    monRect.Top,
                    monRect.Width,
                    monRect.Height,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero
                );
            }
        }

        ApplyWin11WindowAttributes(_hwnd);
        DragAcceptFiles(_hwnd, true);

        _syncCtx = new Win32SynchronizationContext(_hwnd, Environment.CurrentManagedThreadId);
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        GetClientRect(_hwnd, out var rc);
        float dpi = GetWindowDpi(_hwnd);
        _renderer.Initialize(_hwnd, Math.Max(1, rc.Width), Math.Max(1, rc.Height), dpi);

        _lastActivityMs = _clock.ElapsedMilliseconds;
        SetTimer(_hwnd, 1, 16, IntPtr.Zero); // 60Hz lightweight animation/idle tick (renders only when dirty)

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            _ = OpenPathAsync(initialPath);
        }

        RenderNow();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        KillTimer(_hwnd, 1);
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            ShowToast("Scanning…");
            RenderNow();

            var scan = await DirectoryScanner.ScanAsync(path);
            _currentDirectory = scan.DirectoryPath;
            _allItems = scan.Items.ToList();
            _cullingMap = await XmpSidecarManager.LoadAllSidecarsAsync(_allItems);

            _filterCycleIndex = 0;
            _hud.ActiveFilter = CullingFilter.All;
            RebuildFilteredPlaylist(preferredId: scan.Items.Count > 0 ? scan.Items[scan.InitialIndex].Id : null);

            if (_filteredItems.Count == 0)
            {
                ShowToast("No supported photos in folder");
            }
            else
            {
                ShowToast($"{_filteredItems.Count} photos");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Open failed: {ex.Message}");
        }
    }

    private void RebuildFilteredPlaylist(string? preferredId)
    {
        _filteredItems = _allItems
            .Where(it => _hud.ActiveFilter.Matches(GetCulling(it.Id)))
            .ToList();

        _hud.HasPlaylist = _filteredItems.Count > 0;
        _hud.TotalCount = _filteredItems.Count;

        if (_filteredItems.Count == 0)
        {
            _primaryDecoded = null;
            _secondaryDecoded = null;
            _renderer.SetPrimaryImage(null);
            _renderer.SetSecondaryImage(null);
            _renderer.SetClippingMask(null);
            _hud.PrimaryItem = null;
            _hud.SecondaryItem = null;
            RequestRedraw();
            return;
        }

        int newIdx = 0;
        if (!string.IsNullOrEmpty(preferredId))
        {
            int found = _filteredItems.FindIndex(x => string.Equals(x.Id, preferredId, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) newIdx = found;
            else newIdx = Math.Clamp(_hud.CurrentIndex, 0, _filteredItems.Count - 1);
        }

        int maxScreenPx = GetMaxScreenPixelBound();
        _pipeline.SetPlaylist(_filteredItems, newIdx, maxScreenPx);
        _hud.CurrentIndex = newIdx;
        _hud.SecondaryIndex = Math.Min(_filteredItems.Count - 1, newIdx + 1);
        ResetZoomState();
        _ = LoadCurrentSlideAsync(isForward: true);
    }

    private async Task LoadCurrentSlideAsync(bool isForward)
    {
        if (_filteredItems.Count == 0) return;

        int idx = Math.Clamp(_hud.CurrentIndex, 0, _filteredItems.Count - 1);
        _hud.CurrentIndex = idx;
        _pipeline.NotifyCurrentIndexChanged(idx, isForward);

        int gen = ++_loadGeneration;
        var primaryItem = _filteredItems[idx];
        _hud.PrimaryItem = primaryItem;
        _hud.PrimaryCulling = GetCulling(primaryItem.Id);
        _hud.IsLoadingFullRaw = false;

        try
        {
            var decoded = await _pipeline.GetImageAsync(idx);
            if (gen != _loadGeneration) return;

            _primaryDecoded = decoded;
            _renderer.SetPrimaryImage(decoded);
            UpdateDerivedOverlays(decoded);

            if (_hud.IsSplitCompare && _filteredItems.Count > 1)
            {
                int secIdx = _hud.SecondaryIndex;
                if (secIdx == idx || secIdx < 0 || secIdx >= _filteredItems.Count)
                {
                    secIdx = (idx + 1) % _filteredItems.Count;
                    _hud.SecondaryIndex = secIdx;
                }
                var secItem = _filteredItems[secIdx];
                _hud.SecondaryItem = secItem;
                _hud.SecondaryCulling = GetCulling(secItem.Id);

                var secDecoded = await _pipeline.GetImageAsync(secIdx);
                if (gen != _loadGeneration) return;
                _secondaryDecoded = secDecoded;
                _renderer.SetSecondaryImage(secDecoded);
            }
            else
            {
                _hud.SecondaryItem = null;
                _secondaryDecoded = null;
                _renderer.SetSecondaryImage(null);
            }

            RequestRedraw();

            // If user remained in custom zoom across photos, upgrade to full-res / 16-bit RAW automatically
            if (_hud.IsCustomZoom)
            {
                _ = UpgradeToFullResolutionAsync(gen, idx);
            }
        }
        catch (Exception ex)
        {
            if (gen == _loadGeneration)
            {
                ShowToast($"Decode error: {Path.GetFileName(primaryItem.Path)} ({ex.Message})");
            }
        }
    }

    private async Task UpgradeToFullResolutionAsync(int expectedGen, int index)
    {
        if (index < 0 || index >= _filteredItems.Count || _primaryDecoded == null) return;
        var item = _filteredItems[index];

        bool needsUpgrade = _primaryDecoded.IsDownscaled || (item.RawPath != null && !_primaryDecoded.IsFullSensorRaw);
        if (!needsUpgrade) return;

        if (item.RawPath != null)
        {
            _hud.IsLoadingFullRaw = true;
            RequestRedraw();
        }

        try
        {
            var full = await _pipeline.GetFullResolutionImageAsync(index);
            if (expectedGen != _loadGeneration || _hud.CurrentIndex != index) return;

            _primaryDecoded = full;
            _renderer.SetPrimaryImage(full);
            UpdateDerivedOverlays(full);

            if (_hud.IsSplitCompare && _hud.SecondaryItem != null && _hud.SecondaryIndex >= 0 && _hud.SecondaryIndex < _filteredItems.Count)
            {
                var fullSec = await _pipeline.GetFullResolutionImageAsync(_hud.SecondaryIndex);
                if (expectedGen == _loadGeneration)
                {
                    _secondaryDecoded = fullSec;
                    _renderer.SetSecondaryImage(fullSec);
                }
            }
        }
        catch
        {
            // Keep screen-bounded preview if full-res unpack fails
        }
        finally
        {
            if (expectedGen == _loadGeneration)
            {
                _hud.IsLoadingFullRaw = false;
                RequestRedraw();
            }
        }
    }

    private void UpdateDerivedOverlays(DecodedImage img)
    {
        if (_hud.ShowHistogram)
        {
            _hud.Histogram = HistogramEngine.Compute(img);
        }
        if (_hud.ShowClipping)
        {
            _renderer.SetClippingMask(ClippingEngine.GenerateClippingMask(img));
        }
        else
        {
            _renderer.SetClippingMask(null);
        }
    }

    private void Navigate(int delta)
    {
        if (_filteredItems.Count == 0) return;
        int next = Math.Clamp(_hud.CurrentIndex + delta, 0, _filteredItems.Count - 1);
        if (next == _hud.CurrentIndex) return;

        int prevIdx = _hud.CurrentIndex;
        _hud.CurrentIndex = next;
        if (_hud.IsSplitCompare)
        {
            _hud.SecondaryIndex = prevIdx;
        }
        else
        {
            ResetZoomState();
        }

        _eyeStops = Array.Empty<EyeJumpStop>();
        _cachedEyeImageId = null;
        _hud.EyeReticleOpacity = 0f;

        _ = LoadCurrentSlideAsync(isForward: delta >= 0);
    }

    private void ResetZoomState()
    {
        _hud.IsCustomZoom = false;
        _hud.PixelZoomScale = 1.0f;
        _hud.PanNormCenter = new Vector2(0.5f, 0.5f);
    }

    private void ToggleOneToOneZoom(Vector2? anchorDip = null)
    {
        if (_primaryDecoded == null || _hud.PrimaryItem == null) return;

        if (_hud.IsCustomZoom)
        {
            ResetZoomState();
            ShowToast("Fit to Screen");
            if (_primaryDecoded.IsFullSensorRaw && _pipeline.TryGetCachedScreenImage(_hud.CurrentIndex, out var screenImg))
            {
                _primaryDecoded = screenImg;
                _renderer.SetPrimaryImage(screenImg);
                UpdateDerivedOverlays(screenImg);
            }
            RequestRedraw();
            return;
        }

        Vector2 norm = new(0.5f, 0.5f);
        if (anchorDip is { } pt)
        {
            norm = ClientDipToImageNorm(pt);
        }

        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = 1.0f;
        _hud.PanNormCenter = norm;
        ShowToast(_hud.PrimaryItem.RawPath != null ? "100% 1:1 · 16-bit RAW" : "100% 1:1");
        RequestRedraw();

        _ = UpgradeToFullResolutionAsync(_loadGeneration, _hud.CurrentIndex);
    }

    private async Task TriggerAiEyeJumpAsync()
    {
        if (_primaryDecoded == null || _hud.PrimaryItem == null) return;

        int expectedGen = _loadGeneration;
        string itemId = _hud.PrimaryItem.Id;

        if (_cachedEyeImageId != itemId || _eyeStops.Count == 0)
        {
            ShowToast("Detecting Eyes…");
            RenderNow();

            var srcImg = _primaryDecoded;
            var stops = await Task.Run(() => EyeFocusDetector.BuildJumpSequence(srcImg));
            if (expectedGen != _loadGeneration) return;

            _cachedEyeImageId = itemId;
            _eyeStops = stops;
            _eyeStopCursor = -1;
        }

        if (_eyeStops.Count == 0)
        {
            ShowToast("No face or subject detected");
            return;
        }

        _eyeStopCursor = (_eyeStopCursor + 1) % _eyeStops.Count;
        var stop = _eyeStops[_eyeStopCursor];

        // Choose at least 100% 1:1 sensor pixels, or zoom further on small web-res portraits so eye fills loupe
        var pane = GetPrimaryPaneDip();
        _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, new RenderHudState(), out float fitPhysScale);
        float targetScale = Math.Clamp(MathF.Max(1.0f, fitPhysScale * 2.4f), 1.0f, 4.0f);

        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = targetScale;
        _hud.PanNormCenter = new Vector2(stop.Point.X, stop.Point.Y);
        _hud.EyeReticlePoint = stop.Point;
        _hud.EyeFaceRect = stop.FaceBox;
        _hud.EyeReticleOpacity = 1.0f;
        _eyeReticleShownAtMs = _clock.ElapsedMilliseconds;

        ShowToast($"👁 {stop.Label} · {stop.Backend}");
        RequestRedraw();

        _ = UpgradeToFullResolutionAsync(_loadGeneration, _hud.CurrentIndex);
    }

    private void ApplyWheelZoom(int wheelDelta, Vector2 cursorDip)
    {
        if (_primaryDecoded == null) return;

        var pane = GetPrimaryPaneDip();
        var oldRect = _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, _hud, out float curPhysScale);

        float factor = wheelDelta > 0 ? 1.25f : (1.0f / 1.25f);
        var fitProbe = new RenderHudState();
        _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, fitProbe, out float fitPhysScale);

        float nextScale = Math.Clamp(curPhysScale * factor, fitPhysScale, 12.0f);
        if (nextScale <= fitPhysScale * 1.02f && wheelDelta < 0)
        {
            ResetZoomState();
            if (_primaryDecoded.IsFullSensorRaw && _pipeline.TryGetCachedScreenImage(_hud.CurrentIndex, out var screenImg))
            {
                _primaryDecoded = screenImg;
                _renderer.SetPrimaryImage(screenImg);
                UpdateDerivedOverlays(screenImg);
            }
            RequestRedraw();
            return;
        }

        // Keep image point under cursor anchored after scale change
        float oldW = MathF.Max(1f, oldRect.Right - oldRect.Left);
        float oldH = MathF.Max(1f, oldRect.Bottom - oldRect.Top);
        float u = Math.Clamp((cursorDip.X - oldRect.Left) / oldW, 0f, 1f);
        float v = Math.Clamp((cursorDip.Y - oldRect.Top) / oldH, 0f, 1f);

        float newDrawW = _primaryDecoded.OriginalWidth * (nextScale / _renderer.DpiScale);
        float newDrawH = _primaryDecoded.OriginalHeight * (nextScale / _renderer.DpiScale);
        float paneCenterX = (pane.Left + pane.Right) * 0.5f;
        float paneCenterY = (pane.Top + pane.Bottom) * 0.5f;

        float newNormX = u + (paneCenterX - cursorDip.X) / MathF.Max(1f, newDrawW);
        float newNormY = v + (paneCenterY - cursorDip.Y) / MathF.Max(1f, newDrawH);

        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = nextScale;
        _hud.PanNormCenter = new Vector2(Math.Clamp(newNormX, 0f, 1f), Math.Clamp(newNormY, 0f, 1f));
        RequestRedraw();

        if (nextScale >= 0.9f)
        {
            _ = UpgradeToFullResolutionAsync(_loadGeneration, _hud.CurrentIndex);
        }
    }

    private Vector2 ClientDipToImageNorm(Vector2 dip)
    {
        if (_primaryDecoded == null) return new Vector2(0.5f, 0.5f);
        var pane = GetPrimaryPaneDip();
        var rect = _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, _hud, out _);
        float w = MathF.Max(1f, rect.Right - rect.Left);
        float h = MathF.Max(1f, rect.Bottom - rect.Top);
        return new Vector2(
            Math.Clamp((dip.X - rect.Left) / w, 0f, 1f),
            Math.Clamp((dip.Y - rect.Top) / h, 0f, 1f)
        );
    }

    private RawRectF GetPrimaryPaneDip()
    {
        float w = _renderer.DipWidth;
        float h = _renderer.DipHeight;
        return _hud.IsSplitCompare ? new RawRectF(0, 0, w * 0.5f - 1f, h) : new RawRectF(0, 0, w, h);
    }

    #region Culling, Rotation, Export & Trash Actions

    private CullingMetadata GetCulling(string id) =>
        _cullingMap.TryGetValue(id, out var m) ? m : new CullingMetadata();

    private void UpdateCurrentCulling(Func<CullingMetadata, CullingMetadata> mutator, string feedback)
    {
        if (_hud.PrimaryItem == null) return;
        var item = _hud.PrimaryItem;
        var updated = mutator(GetCulling(item.Id));
        _cullingMap[item.Id] = updated;
        _hud.PrimaryCulling = updated;

        var saveTarget = item.RawPath ?? item.Path;
        Task.Run(() => XmpSidecarManager.Save(updated, saveTarget));

        ShowToast(feedback);

        if (!_hud.ActiveFilter.Matches(updated))
        {
            string? nextNeighborId = null;
            if (_hud.CurrentIndex + 1 < _filteredItems.Count) nextNeighborId = _filteredItems[_hud.CurrentIndex + 1].Id;
            else if (_hud.CurrentIndex - 1 >= 0) nextNeighborId = _filteredItems[_hud.CurrentIndex - 1].Id;
            RebuildFilteredPlaylist(nextNeighborId);
        }
        else
        {
            RequestRedraw();
        }
    }

    private void RotateCurrent(RotationDirection dir)
    {
        if (_hud.PrimaryItem == null) return;
        var item = _hud.PrimaryItem;
        int idx = _hud.CurrentIndex;

        int newOrient = LosslessRotationEngine.Rotate(item, dir);
        var cull = GetCulling(item.Id) with { Orientation = newOrient };
        _cullingMap[item.Id] = cull;
        _hud.PrimaryCulling = cull;

        _pipeline.Invalidate(idx);
        _cachedEyeImageId = null;
        ShowToast(dir == RotationDirection.Clockwise ? "↻ Rotated 90° CW" : "↺ Rotated 90° CCW");
        _ = LoadCurrentSlideAsync(isForward: true);
    }

    private async Task ExportPicksAsync()
    {
        if (_allItems.Count == 0 || string.IsNullOrEmpty(_currentDirectory)) return;
        ShowToast("Exporting Picks…");
        RenderNow();

        int count = await ExportManager.ExportPicksAsync(_allItems, _cullingMap, _currentDirectory);
        ShowToast(count > 0 ? $"✓ Exported {count} files to /Picks" : "No Picks (P) flagged yet");
    }

    private async Task MoveCurrentToTrashAsync()
    {
        if (_hud.PrimaryItem == null) return;
        var item = _hud.PrimaryItem;
        string? nextId = _hud.CurrentIndex + 1 < _filteredItems.Count
            ? _filteredItems[_hud.CurrentIndex + 1].Id
            : (_hud.CurrentIndex - 1 >= 0 ? _filteredItems[_hud.CurrentIndex - 1].Id : null);

        foreach (var p in item.AllPaths)
        {
            var sidecar = XmpSidecarManager.ExistingSidecarPath(p);
            await TrashManager.MoveToTrashAsync(p);
            if (sidecar != null) await TrashManager.MoveToTrashAsync(sidecar);
        }

        _allItems.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        _cullingMap.Remove(item.Id);
        ShowToast($"Moved {item.DisplayName} to Recycle Bin");
        RebuildFilteredPlaylist(nextId);
    }

    private async Task PurgeAllRejectsAsync()
    {
        var rejects = _allItems.Where(it => GetCulling(it.Id).Pick == PickStatus.Reject).ToList();
        if (rejects.Count == 0)
        {
            ShowToast("No Rejected (X) photos to purge");
            return;
        }

        if (!ConfirmPurgeRejects(_hwnd, rejects.Count)) return;

        foreach (var item in rejects)
        {
            foreach (var p in item.AllPaths)
            {
                var sidecar = XmpSidecarManager.ExistingSidecarPath(p);
                await TrashManager.MoveToTrashAsync(p);
                if (sidecar != null) await TrashManager.MoveToTrashAsync(sidecar);
            }
            _allItems.Remove(item);
            _cullingMap.Remove(item.Id);
        }

        ShowToast($"Purged {rejects.Count} rejects to Recycle Bin");
        RebuildFilteredPlaylist(_hud.PrimaryItem?.Id);
    }

    private static unsafe bool ConfirmPurgeRejects(IntPtr hwnd, int count)
    {
        string prompt = $"Move {count} rejected photo(s) (including paired RAW/JPEG & XMP) to the Windows Recycle Bin?";
        fixed (char* pText = prompt)
        fixed (char* pCap = "WinGlance — Purge Rejects")
        {
            return MessageBoxW(hwnd, pText, pCap, MB_YESNO | MB_ICONWARNING) == IDYES;
        }
    }

    private unsafe void BrowseAndOpenFile()
    {
        const int maxChars = 1024;
        char* fileBuf = stackalloc char[maxChars];
        fileBuf[0] = '\0';

        const string filter = "Photos & Camera RAW\0*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.avif;*.jxl;*.tif;*.tiff;*.bmp;*.arw;*.cr2;*.cr3;*.nef;*.nrw;*.dng;*.raf;*.orf;*.rw2;*.pef;*.srw;*.3fr;*.fff;*.iiq;*.x3f\0All Files (*.*)\0*.*\0\0";

        fixed (char* pFilter = filter)
        fixed (char* pTitle = "Open Photo or Camera RAW Folder")
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = _hwnd,
                lpstrFilter = pFilter,
                lpstrFile = fileBuf,
                nMaxFile = maxChars,
                lpstrTitle = pTitle,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR
            };

            if (GetOpenFileNameW(ref ofn))
            {
                string selected = new string(fileBuf);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    _ = OpenPathAsync(selected);
                }
            }
        }
    }

    #endregion

    #region Keyboard, Mouse & Window Mode Handlers

    private void OnKeyDown(int vk)
    {
        WakeHud();

        bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

        if (_hud.ShowCheatsheet && vk is not VK_ESCAPE and not VK_F1 and not VK_OEM_2)
        {
            _hud.ShowCheatsheet = false;
            RequestRedraw();
            return;
        }

        if (ctrl)
        {
            switch (vk)
            {
                case 'O':
                    BrowseAndOpenFile();
                    return;
                case 'E':
                    _ = ExportPicksAsync();
                    return;
                case 'F':
                    _filterCycleIndex = (_filterCycleIndex + 1) % FilterCycle.Length;
                    _hud.ActiveFilter = FilterCycle[_filterCycleIndex];
                    ShowToast($"Filter: {_hud.ActiveFilter.Title}");
                    RebuildFilteredPlaylist(_hud.PrimaryItem?.Id);
                    return;
            }
        }

        switch (vk)
        {
            case VK_ESCAPE:
                if (_hud.ShowCheatsheet) { _hud.ShowCheatsheet = false; RequestRedraw(); }
                else if (_hud.IsCustomZoom) { ResetZoomState(); RequestRedraw(); }
                else if (_hud.IsSplitCompare) { _hud.IsSplitCompare = false; _ = LoadCurrentSlideAsync(true); }
                else DestroyWindow(_hwnd);
                break;

            case VK_F1:
            case VK_OEM_2: // '?' / '/'
                _hud.ShowCheatsheet = !_hud.ShowCheatsheet;
                RequestRedraw();
                break;

            case VK_RIGHT:
            case VK_DOWN:
            case VK_NEXT:
            case 'J':
                Navigate(+1);
                break;

            case VK_LEFT:
            case VK_UP:
            case VK_PRIOR:
            case 'K':
                Navigate(-1);
                break;

            case VK_HOME:
                if (_filteredItems.Count > 0) { _hud.CurrentIndex = 0; _ = LoadCurrentSlideAsync(true); }
                break;

            case VK_END:
                if (_filteredItems.Count > 0) { _hud.CurrentIndex = _filteredItems.Count - 1; _ = LoadCurrentSlideAsync(false); }
                break;

            case VK_SPACE:
                if (_filteredItems.Count > 1)
                {
                    _hud.IsSlideshowPlaying = !_hud.IsSlideshowPlaying;
                    _lastSlideshowAdvanceMs = _clock.ElapsedMilliseconds;
                    ShowToast(_hud.IsSlideshowPlaying ? $"▶ Slideshow ({_hud.SlideshowSeconds}s)" : "⏸ Paused");
                    RequestRedraw();
                }
                break;

            case 'F':
                if (shift)
                {
                    _hud.ShowFocusPeaking = !_hud.ShowFocusPeaking;
                    ShowToast(_hud.ShowFocusPeaking ? "GPU Focus Peaking ON" : "Focus Peaking OFF");
                    RequestRedraw();
                }
                else
                {
                    ToggleFullscreenMode();
                }
                break;

            case VK_F11:
                ToggleFullscreenMode();
                break;

            case 'Z':
                {
                    long now = _clock.ElapsedMilliseconds;
                    bool isDoubleTap = (now - _lastZKeyPressMs) <= 320;
                    _lastZKeyPressMs = now;

                    if (shift || isDoubleTap)
                    {
                        _ = TriggerAiEyeJumpAsync();
                    }
                    else
                    {
                        GetCursorPos(out var screenPt);
                        ScreenToClient(_hwnd, ref screenPt);
                        float dpiScale = _renderer.DpiScale;
                        var dip = new Vector2(screenPt.X / dpiScale, screenPt.Y / dpiScale);
                        ToggleOneToOneZoom(dip);
                    }
                    break;
                }

            case 'O':
                _hud.ShowClipping = !_hud.ShowClipping;
                if (_primaryDecoded != null) UpdateDerivedOverlays(_primaryDecoded);
                ShowToast(_hud.ShowClipping ? "Clipping Blinkies ON" : "Clipping Blinkies OFF");
                RequestRedraw();
                break;

            case 'H':
                _hud.ShowHistogram = !_hud.ShowHistogram;
                if (_hud.ShowHistogram && _primaryDecoded != null)
                {
                    _hud.Histogram = HistogramEngine.Compute(_primaryDecoded);
                }
                RequestRedraw();
                break;

            case 'S':
                if (_filteredItems.Count > 1)
                {
                    _hud.IsSplitCompare = !_hud.IsSplitCompare;
                    if (_hud.IsSplitCompare && (_hud.SecondaryIndex == _hud.CurrentIndex || _hud.SecondaryIndex >= _filteredItems.Count))
                    {
                        _hud.SecondaryIndex = (_hud.CurrentIndex + 1) % _filteredItems.Count;
                    }
                    ShowToast(_hud.IsSplitCompare ? "2-Up Split Compare" : "Single View");
                    _ = LoadCurrentSlideAsync(true);
                }
                break;

            case VK_TAB:
                if (_hud.IsSplitCompare && _filteredItems.Count > 1)
                {
                    (_hud.CurrentIndex, _hud.SecondaryIndex) = (_hud.SecondaryIndex, _hud.CurrentIndex);
                    _ = LoadCurrentSlideAsync(true);
                }
                break;

            case 'P':
                UpdateCurrentCulling(
                    m => m with { Pick = m.Pick == PickStatus.Pick ? PickStatus.Unflagged : PickStatus.Pick },
                    GetCulling(_hud.PrimaryItem?.Id ?? "").Pick == PickStatus.Pick ? "Unflagged" : "✓ Pick"
                );
                break;

            case 'X':
                UpdateCurrentCulling(
                    m => m with { Pick = m.Pick == PickStatus.Reject ? PickStatus.Unflagged : PickStatus.Reject },
                    GetCulling(_hud.PrimaryItem?.Id ?? "").Pick == PickStatus.Reject ? "Unflagged" : "✕ Reject"
                );
                break;

            case 'U':
                UpdateCurrentCulling(m => m with { Pick = PickStatus.Unflagged }, "Unflagged");
                break;

            case >= '0' and <= '5':
                {
                    int stars = vk - '0';
                    int cur = GetCulling(_hud.PrimaryItem?.Id ?? "").Rating;
                    int nextStars = (stars == cur) ? 0 : stars;
                    UpdateCurrentCulling(
                        m => m with { Rating = nextStars },
                        nextStars > 0 ? new string('★', nextStars) : "Rating Cleared"
                    );
                    break;
                }

            case '6':
            case '7':
            case '8':
            case '9':
                {
                    var label = (ColorLabel)(vk - '0');
                    var cur = GetCulling(_hud.PrimaryItem?.Id ?? "").Color;
                    var next = cur == label ? ColorLabel.None : label;
                    UpdateCurrentCulling(
                        m => m with { Color = next },
                        next == ColorLabel.None ? "Label Cleared" : $"Label: {next.GetTitle()}"
                    );
                    break;
                }

            case 'R':
                RotateCurrent(RotationDirection.Clockwise);
                break;

            case 'L':
                RotateCurrent(RotationDirection.CounterClockwise);
                break;

            case VK_OEM_6: // ']'
                if (_hud.IsSlideshowPlaying)
                {
                    _hud.SlideshowSeconds = Math.Clamp(_hud.SlideshowSeconds + 1, 1, 15);
                    ShowToast($"Slideshow: {_hud.SlideshowSeconds}s");
                }
                else
                {
                    RotateCurrent(RotationDirection.Clockwise);
                }
                break;

            case VK_OEM_4: // '['
                if (_hud.IsSlideshowPlaying)
                {
                    _hud.SlideshowSeconds = Math.Clamp(_hud.SlideshowSeconds - 1, 1, 15);
                    ShowToast($"Slideshow: {_hud.SlideshowSeconds}s");
                }
                else
                {
                    RotateCurrent(RotationDirection.CounterClockwise);
                }
                break;

            case VK_DELETE:
            case VK_BACK:
                if (shift) _ = PurgeAllRejectsAsync();
                else _ = MoveCurrentToTrashAsync();
                break;
        }
    }

    private void ToggleFullscreenMode()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            GetWindowRect(_hwnd, out _savedWindowRect);
            var mon = GetWindowMonitorBounds(_hwnd);
            SetWindowLongPtrW(_hwnd, GWL_STYLE, unchecked((IntPtr)(int)(WS_POPUP | WS_VISIBLE)));
            SetWindowPos(_hwnd, IntPtr.Zero, mon.Left, mon.Top, mon.Width, mon.Height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        else
        {
            SetWindowLongPtrW(_hwnd, GWL_STYLE, unchecked((IntPtr)(int)(WS_OVERLAPPEDWINDOW | WS_VISIBLE)));
            ApplyWin11WindowAttributes(_hwnd);
            SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                _savedWindowRect.Left,
                _savedWindowRect.Top,
                Math.Max(640, _savedWindowRect.Width),
                Math.Max(480, _savedWindowRect.Height),
                SWP_FRAMECHANGED | SWP_SHOWWINDOW
            );
            RestoreCursorVisibility();
        }
        RequestRedraw();
    }

    private void OnTimerTick()
    {
        long now = _clock.ElapsedMilliseconds;
        bool animDirty = false;

        // 1. Slideshow auto-advance
        if (_hud.IsSlideshowPlaying && _filteredItems.Count > 1)
        {
            if (now - _lastSlideshowAdvanceMs >= _hud.SlideshowSeconds * 1000L)
            {
                _lastSlideshowAdvanceMs = now;
                if (_hud.CurrentIndex + 1 < _filteredItems.Count) Navigate(+1);
                else { _hud.CurrentIndex = 0; _ = LoadCurrentSlideAsync(true); }
                animDirty = true;
            }
        }

        // 2. Toast fade-out (visible 850ms, smooth fade 300ms)
        if (_hud.ToastOpacity > 0f)
        {
            long dt = now - _toastShownAtMs;
            float target = dt < 850 ? 1.0f : Math.Clamp(1.0f - (dt - 850) / 300.0f, 0f, 1f);
            if (MathF.Abs(target - _hud.ToastOpacity) > 0.005f)
            {
                _hud.ToastOpacity = target;
                animDirty = true;
            }
        }

        // 3. AI Eye Reticle fade-out (visible 1100ms, smooth fade 550ms)
        if (_hud.EyeReticleOpacity > 0f)
        {
            long dt = now - _eyeReticleShownAtMs;
            float target = dt < 1100 ? 1.0f : Math.Clamp(1.0f - (dt - 1100) / 550.0f, 0f, 1f);
            if (MathF.Abs(target - _hud.EyeReticleOpacity) > 0.005f)
            {
                _hud.EyeReticleOpacity = target;
                animDirty = true;
            }
        }

        // 4. Clipping blinkies gentle pulse
        if (_hud.ShowClipping && _primaryDecoded != null)
        {
            _hud.ClippingPulseAlpha = 0.65f + 0.30f * (0.5f + 0.5f * MathF.Sin(now * 0.0065f));
            animDirty = true;
        }

        // 5. Auto-hide HUD & cursor after 3.0s idle in fullscreen
        long idleMs = now - _lastActivityMs;
        float targetHud = (idleMs > 3000 && _hud.HasPlaylist && !_hud.ShowCheatsheet) ? 0.0f : 1.0f;
        if (MathF.Abs(targetHud - _hud.HudOpacity) > 0.01f)
        {
            _hud.HudOpacity += (targetHud - _hud.HudOpacity) * 0.22f;
            if (_hud.HudOpacity < 0.015f) _hud.HudOpacity = 0f;
            if (_hud.HudOpacity > 0.985f) _hud.HudOpacity = 1f;
            animDirty = true;
        }

        if (_isFullscreen && idleMs > 3000 && _hud.HasPlaylist && !_hud.ShowCheatsheet && !_isDraggingPan)
        {
            if (!_cursorHidden)
            {
                ShowCursor(false);
                _cursorHidden = true;
            }
        }

        if (animDirty || _needsRedraw)
        {
            RenderNow();
        }
    }

    private void WakeHud()
    {
        _lastActivityMs = _clock.ElapsedMilliseconds;
        RestoreCursorVisibility();
        if (_hud.HudOpacity < 1.0f)
        {
            _hud.HudOpacity = 1.0f;
            RequestRedraw();
        }
    }

    private void RestoreCursorVisibility()
    {
        if (_cursorHidden)
        {
            ShowCursor(true);
            _cursorHidden = false;
        }
    }

    private void ShowToast(string text)
    {
        _hud.ToastText = text;
        _hud.ToastOpacity = 1.0f;
        _toastShownAtMs = _clock.ElapsedMilliseconds;
        RequestRedraw();
    }

    private void RequestRedraw() => _needsRedraw = true;

    private void RenderNow()
    {
        _needsRedraw = false;
        _renderer.Render(_hud);
    }

    private int GetMaxScreenPixelBound()
    {
        var mon = GetWindowMonitorBounds(_hwnd);
        return Math.Clamp(Math.Max(mon.Width, mon.Height), 1920, 5120);
    }

    #endregion

    #region Win32 WndProc & Native Interop

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe nint WndProcStatic(IntPtr hwnd, uint msg, nint wParam, nint lParam)
    {
        var inst = s_instance;
        if (inst == null) return DefWindowProcW(hwnd, msg, wParam, lParam);
        return inst.WndProc(hwnd, msg, wParam, lParam);
    }

    private unsafe nint WndProc(IntPtr hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32SynchronizationContext.WM_APP_SYNC:
                _syncCtx?.Drain();
                return 0;

            case WM_ERASEBKGND:
                return 1;

            case WM_PAINT:
                BeginPaint(hwnd, out var ps);
                RenderNow();
                EndPaint(hwnd, ref ps);
                return 0;

            case WM_SIZE:
                {
                    int w = (ushort)(lParam & 0xFFFF);
                    int h = (ushort)((lParam >> 16) & 0xFFFF);
                    if (w > 0 && h > 0)
                    {
                        _renderer.Resize(w, h, GetWindowDpi(hwnd));
                        _pipeline.UpdateBoundingSize(Math.Clamp(Math.Max(w, h), 1920, 5120));
                        RenderNow();
                    }
                    return 0;
                }

            case WM_DPICHANGED:
                {
                    uint newDpi = (ushort)(wParam & 0xFFFF);
                    RECT* suggested = (RECT*)lParam;
                    SetWindowPos(hwnd, IntPtr.Zero, suggested->Left, suggested->Top, suggested->Width, suggested->Height, SWP_NOZORDER | SWP_NOACTIVATE);
                    GetClientRect(hwnd, out var rc);
                    _renderer.Resize(Math.Max(1, rc.Width), Math.Max(1, rc.Height), newDpi);
                    RenderNow();
                    return 0;
                }

            case WM_TIMER:
                OnTimerTick();
                return 0;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                OnKeyDown((int)wParam);
                return 0;

            case WM_LBUTTONDOWN:
                {
                    WakeHud();
                    var pt = UnpackPoint(lParam);
                    if (_hud.IsCustomZoom && _primaryDecoded != null)
                    {
                        _isDraggingPan = true;
                        _lastMouseClient = pt;
                        SetCapture(hwnd);
                    }
                    return 0;
                }

            case WM_LBUTTONUP:
                if (_isDraggingPan)
                {
                    _isDraggingPan = false;
                    ReleaseCapture();
                }
                return 0;

            case WM_LBUTTONDBLCLK:
                {
                    WakeHud();
                    var pt = UnpackPoint(lParam);
                    float s = _renderer.DpiScale;
                    ToggleOneToOneZoom(new Vector2(pt.X / s, pt.Y / s));
                    return 0;
                }

            case WM_MOUSEMOVE:
                {
                    var pt = UnpackPoint(lParam);
                    if (pt.X != _lastMouseClient.X || pt.Y != _lastMouseClient.Y)
                    {
                        WakeHud();
                    }

                    if (_isDraggingPan && _hud.IsCustomZoom && _primaryDecoded != null)
                    {
                        int dxPx = pt.X - _lastMouseClient.X;
                        int dyPx = pt.Y - _lastMouseClient.Y;
                        _lastMouseClient = pt;

                        float s = _renderer.DpiScale;
                        float drawW = MathF.Max(1f, _primaryDecoded.OriginalWidth * (_hud.PixelZoomScale / s));
                        float drawH = MathF.Max(1f, _primaryDecoded.OriginalHeight * (_hud.PixelZoomScale / s));

                        _hud.PanNormCenter = new Vector2(
                            Math.Clamp(_hud.PanNormCenter.X - (dxPx / s) / drawW, 0f, 1f),
                            Math.Clamp(_hud.PanNormCenter.Y - (dyPx / s) / drawH, 0f, 1f)
                        );
                        RenderNow();
                    }
                    else
                    {
                        _lastMouseClient = pt;
                    }
                    return 0;
                }

            case WM_MOUSEWHEEL:
                {
                    WakeHud();
                    short delta = (short)((wParam >> 16) & 0xFFFF);
                    bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

                    if (ctrl || _hud.IsCustomZoom)
                    {
                        var screenPt = UnpackPoint(lParam);
                        ScreenToClient(hwnd, ref screenPt);
                        float s = _renderer.DpiScale;
                        ApplyWheelZoom(delta, new Vector2(screenPt.X / s, screenPt.Y / s));
                    }
                    else
                    {
                        Navigate(delta < 0 ? +1 : -1);
                    }
                    return 0;
                }

            case WM_DROPFILES:
                {
                    IntPtr hDrop = (IntPtr)wParam;
                    uint chars = DragQueryFileW(hDrop, 0, null, 0);
                    if (chars > 0 && chars < 2048)
                    {
                        char* buf = stackalloc char[(int)chars + 2];
                        if (DragQueryFileW(hDrop, 0, buf, chars + 1) > 0)
                        {
                            string dropped = new string(buf);
                            _ = OpenPathAsync(dropped);
                        }
                    }
                    DragFinish(hDrop);
                    return 0;
                }

            case WM_DESTROY:
                RestoreCursorVisibility();
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static POINT UnpackPoint(nint lParam) => new()
    {
        X = (short)(lParam & 0xFFFF),
        Y = (short)((lParam >> 16) & 0xFFFF)
    };

    private static IntPtr LoadEmbeddedAppIcon(IntPtr hInstance)
    {
        // .NET ApplicationIcon embeds main multi-res icon at IDI_APPLICATION (32512) or ordinal 1
        var hIcon = LoadIconW(hInstance, (IntPtr)32512);
        if (hIcon == IntPtr.Zero) hIcon = LoadIconW(hInstance, (IntPtr)1);
        if (hIcon == IntPtr.Zero) hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512);
        return hIcon;
    }

    private static void ApplyWin11WindowAttributes(IntPtr hwnd)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
        }
        catch { }
    }

    private static float GetWindowDpi(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi : 96.0f;
        }
        catch { return 96.0f; }
    }

    private static unsafe RECT GetCursorMonitorBounds()
    {
        GetCursorPos(out var pt);
        IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(hMon, ref mi)) return mi.rcMonitor;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private static unsafe RECT GetWindowMonitorBounds(IntPtr hwnd)
    {
        IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(hMon, ref mi)) return mi.rcMonitor;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    public void Dispose()
    {
        _renderer.Dispose();
    }

    private const uint CS_DBLCLKS = 0x0008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_EX_ACCEPTFILES = 0x00000010;
    private const int GWL_STYLE = -16;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int IDC_ARROW = 32512;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_DPICHANGED = 0x02E0;

    private const int VK_BACK = 0x08;
    private const int VK_TAB = 0x09;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    private const int VK_END = 0x23;
    private const int VK_HOME = 0x24;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_DELETE = 0x2E;
    private const int VK_F1 = 0x70;
    private const int VK_F11 = 0x7A;
    private const int VK_OEM_2 = 0xBF;
    private const int VK_OEM_4 = 0xDB;
    private const int VK_OEM_6 = 0xDD;

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const int IDYES = 6;

    private const uint OFN_NOCHANGEDIR = 0x00000008;
    private const uint OFN_PATHMUSTEXIST = 0x00000800;
    private const uint OFN_FILEMUSTEXIST = 0x00001000;
    private const uint OFN_EXPLORER = 0x00080000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int Top_Unused { set { } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OPENFILENAMEW
    {
        public uint lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public char* lpstrFilter;
        public char* lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public char* lpstrFile;
        public uint nMaxFile;
        public char* lpstrFileTitle;
        public uint nMaxFileTitle;
        public char* lpstrInitialDir;
        public char* lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData;
        public IntPtr lpfnHook;
        public char* lpTemplateName;
        public IntPtr pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static unsafe extern IntPtr GetModuleHandleW(char* lpModuleName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static unsafe extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static unsafe extern IntPtr CreateWindowExW(
        uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DefWindowProcW(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static unsafe extern int MessageBoxW(IntPtr hWnd, char* lpText, char* lpCaption, uint uType);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static unsafe extern uint DragQueryFileW(IntPtr hDrop, uint iFile, char* lpszFile, uint cch);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static unsafe extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    #endregion
}
