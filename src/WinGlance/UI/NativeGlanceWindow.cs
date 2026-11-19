using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using WinGlance.Gpu.Overlays;
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

    private static readonly int[] SlideshowSteps = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 15, 20, 30 };

    private IntPtr _hwnd;
    private Win32SynchronizationContext? _syncCtx;
    private readonly D2DRenderEngine _renderer = new();
    private readonly SlidePipeline _pipeline = new();
    private readonly CullingService _cullingService = new();
    private readonly SceneDetectionEngine _sceneEngine = new();
    private readonly AestheticsEngine _aestheticsEngine = new();
    private readonly FaceInspectionEngine _faceEngine = new();
    private readonly MicroThumbnailEngine _microThumbEngine = new();
    private readonly RenderHudState _hud = new();
    private AppSettings _settings = new();

    private string _currentDirectory = "";
    private DirectoryWatcher? _watcher;

    private DecodedImage? _primaryDecoded;
    private DecodedImage? _secondaryDecoded;
    private readonly DecodedImage?[] _slotDecoded = new DecodedImage?[4];
    private readonly int[] _multiUpIndices = { -1, -1, -1, -1 };
    private int _multiUpCount = 1;
    private CullingFilter? _filterBeforeMultiUp;
    private readonly Dictionary<int, BurstBreadcrumbInfo?> _burstCacheByIndex = new();
    private int _lastRequestedThumbCenter = -1;

    private bool _filmstripPinned;
    private bool _filmstripHoverOpen;
    private long _filmstripDismissAtMs;
    private float _filmstripScrollTargetX;
    private float _filmstripScrollVelocity;

    private int _loadGeneration;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _sceneJumpCts;

    // Eye Jump cycling state
    private string? _cachedEyeImageId;
    private IReadOnlyList<EyeJumpStop> _eyeStops = Array.Empty<EyeJumpStop>();
    private int _eyeStopCursor = -1;
    private long _lastZKeyPressMs;

    // Spring-Loaded Z hold & glide state
    private bool _isZHeld;
    private bool _wasZoomedOnZDown;
    private bool _zTriggeredEyeJump;
    private long _zKeyDownMs;
    private POINT _zLastMouseClient;

    // Window & Input state
    private bool _isFullscreen = true;
    private RECT _savedWindowRect = new() { Left = 120, Top = 80, Right = 1560, Bottom = 960 };
    private bool _isDraggingPan;
    private bool _isDraggingMiniMap;
    private bool _isHoveringExif;
    private bool _trackingMouseLeave;
    private POINT _lastMouseClient;
    private bool _cursorHidden;

    // Animation & Idle timers
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastActivityMs;
    private long _toastShownAtMs;
    private long _eyeReticleShownAtMs;
    private long _lastSlideshowAdvanceMs;
    private long _pendingAutoAdvanceAtMs;
    private bool _needsRedraw = true;

    public unsafe void Run(string? initialPath)
    {
        s_instance = this;
        _settings = SettingsStore.Load();
        _hud.Theme = SettingsStore.ResolveTheme(_settings.Theme);
        _cullingService.AutoAdvanceEnabled = _settings.AutoAdvance;
        _hud.AutoAdvanceEnabled = _settings.AutoAdvance;
        _renderer.SetMicroThumbnailEngine(_microThumbEngine);
        _microThumbEngine.ThumbnailReady += _ => _syncCtx?.Post(_ => RequestRedraw(), null);
        _hud.GetCullingForItem = id => _cullingService.GetMetadata(id);
        _hud.GetBurstForIndex = idx => _burstCacheByIndex.TryGetValue(idx, out var b) ? b : null;

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

        ApplyWin11WindowAttributes(_hwnd, _hud.Theme == ThemeMode.Dark);
        DragAcceptFiles(_hwnd, true);

        _syncCtx = new Win32SynchronizationContext(_hwnd, Environment.CurrentManagedThreadId);
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        GetClientRect(_hwnd, out var rc);
        float dpi = GetWindowDpi(_hwnd);
        _renderer.Initialize(_hwnd, Math.Max(1, rc.Width), Math.Max(1, rc.Height), dpi);

        _lastActivityMs = _clock.ElapsedMilliseconds;
        SetTimer(_hwnd, 1, 16, IntPtr.Zero);

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
            _pendingAutoAdvanceAtMs = 0;
            StopSlideshowIfRunning();
            _analysisCts?.Cancel();
            _sceneJumpCts?.Cancel();
            _sceneEngine.ClearCache();
            _aestheticsEngine.ClearCache();
            _faceEngine.ClearCache();
            _microThumbEngine.Clear();
            _renderer.ClearMicroThumbGpuCache();
            _burstCacheByIndex.Clear();
            _lastRequestedThumbCenter = -1;
            _hud.PrimaryAesthetic = null;
            _hud.PrimaryBurstInfo = null;
            _hud.ActiveCameraProfile = CameraProfile.AsShot;
            _hud.ActiveCameraBrand = CameraBrand.Unsupported;
            _hud.FaceTiles = Array.Empty<FaceCropTile>();
            _renderer.SetFaceCropImages(Array.Empty<FaceCropTile>());
            _hud.ActiveZoomedFaceIndex = null;
            _hud.MultiUpMode = MultiUpMode.Single;
            _hud.ActiveMultiUpSlot = 0;
            _hud.IsSoloLoupeActive = false;
            _filterBeforeMultiUp = null;
            ShowToast("Scanning…");
            RenderNow();

            var scan = await DirectoryScanner.ScanAsync(path);
            _currentDirectory = scan.DirectoryPath;
            var cullingMap = await XmpSidecarManager.LoadAllSidecarsAsync(scan.Items);

            _cullingService.SetFolderData(scan.Items, cullingMap, resetUndo: true);
            _hud.ActiveFilter = _cullingService.ActiveFilter;
            _hud.AutoAdvanceEnabled = _cullingService.AutoAdvanceEnabled;

            StartDirectoryWatcher(_currentDirectory);
            RebuildFilteredPlaylist(
                preferredId: scan.Items.Count > 0 ? scan.Items[scan.InitialIndex].Id : null,
                preserveCache: false);

            var filtered = _cullingService.FilteredItems;
            ShowToast(filtered.Count == 0
                ? "No supported photos in folder"
                : $"{filtered.Count} photos");
        }
        catch (Exception ex)
        {
            ShowToast($"Open failed: {ex.Message}");
        }
    }

    private void StartDirectoryWatcher(string directory)
    {
        if (_watcher != null)
        {
            _watcher.DirectoryChanged -= OnDirectoryWatcherChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        _watcher = new DirectoryWatcher(directory, _syncCtx);
        _watcher.DirectoryChanged += OnDirectoryWatcherChanged;
        _watcher.Start();
    }

    private async void OnDirectoryWatcherChanged(string directory)
    {
        if (!string.Equals(directory, _currentDirectory, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var scan = await DirectoryScanner.ScanAsync(_currentDirectory);
            if (!string.Equals(directory, _currentDirectory, StringComparison.OrdinalIgnoreCase)) return;

            var existingIds = new HashSet<string>(_cullingService.AllItems.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var addedItems = scan.Items.Where(x => !existingIds.Contains(x.Id)).ToList();

            var newSidecars = addedItems.Count > 0
                ? await XmpSidecarManager.LoadAllSidecarsAsync(addedItems)
                : new Dictionary<string, CullingMetadata>(StringComparer.OrdinalIgnoreCase);

            string? currentId = _hud.PrimaryItem?.Id;
            _cullingService.MergeDirectoryRescan(scan.Items, newSidecars);

            RebuildFilteredPlaylist(currentId, preserveCache: true);
        }
        catch
        {
            // Ignore transient IO locks while external writers finish saving files
        }
    }

    private void SuppressWatcherPath(string path) => _watcher?.SuppressPath(path);

    private void RebuildFilteredPlaylist(string? preferredId, bool preserveCache)
    {
        var filtered = _cullingService.FilteredItems;

        _hud.HasPlaylist = filtered.Count > 0;
        _hud.TotalCount = filtered.Count;
        _hud.PlaylistItems = filtered;
        _burstCacheByIndex.Clear();
        _lastRequestedThumbCenter = -1;

        if (filtered.Count == 0)
        {
            _primaryDecoded = null;
            _secondaryDecoded = null;
            _multiUpCount = 0;
            for (int s = 0; s < 4; s++)
            {
                _slotDecoded[s] = null;
                _multiUpIndices[s] = -1;
                _hud.MultiUpSlots[s] = new();
                _renderer.SetSlotImage(s, null);
                _renderer.SetSlotClippingMask(s, null);
                _renderer.SetSlotFalseColor(s, null);
            }
            _hud.Histogram = null;
            _hud.SecondaryHistogram = null;
            _hud.PrimaryItem = null;
            _hud.SecondaryItem = null;
            _hud.FaceTiles = Array.Empty<FaceCropTile>();
            _renderer.SetFaceCropImages(Array.Empty<FaceCropTile>());
            RequestRedraw();
            return;
        }

        int newIdx = 0;
        if (!string.IsNullOrEmpty(preferredId))
        {
            int found = -1;
            for (int i = 0; i < filtered.Count; i++)
            {
                if (string.Equals(filtered[i].Id, preferredId, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
            newIdx = found >= 0 ? found : Math.Clamp(_hud.CurrentIndex, 0, filtered.Count - 1);
        }

        if (preserveCache)
        {
            _pipeline.UpdateItems(filtered, newIdx);
        }
        else
        {
            int maxScreenPx = GetMaxScreenPixelBound();
            _pipeline.SetPlaylist(filtered, newIdx, maxScreenPx);
            ResetZoomState();
        }

        _hud.CurrentIndex = newIdx;
        _hud.SecondaryIndex = Math.Min(filtered.Count - 1, newIdx + 1);
        if (_filmstripPinned || _filmstripHoverOpen)
        {
            _microThumbEngine.RequestWindow(filtered, newIdx, 28);
        }
        _ = LoadCurrentSlideAsync(isForward: true);
    }

    private async Task LoadCurrentSlideAsync(bool isForward)
    {
        var filtered = _cullingService.FilteredItems;
        if (filtered.Count == 0) return;

        int idx = Math.Clamp(_hud.CurrentIndex, 0, filtered.Count - 1);
        _hud.CurrentIndex = idx;
        _pipeline.NotifyCurrentIndexChanged(idx, isForward);

        int gen = ++_loadGeneration;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var analysisCt = _analysisCts.Token;

        var primaryItem = filtered[idx];
        _hud.PrimaryItem = primaryItem;
        _hud.PrimaryCulling = _cullingService.GetMetadata(primaryItem.Id);
        _hud.PrimaryAesthetic = _aestheticsEngine.TryGetCachedScore(primaryItem.Id, out var cachedAi) ? cachedAi : null;
        _hud.PrimaryBurstInfo = null;
        _hud.FaceTiles = Array.Empty<FaceCropTile>();
        _renderer.SetFaceCropImages(Array.Empty<FaceCropTile>());
        _hud.ActiveZoomedFaceIndex = null;
        _hud.IsLoadingFullRaw = false;

        int targetSlots = _hud.MultiUpMode == MultiUpMode.Single ? 1 : (int)_hud.MultiUpMode;
        int visibleSlots = Math.Min(targetSlots, filtered.Count - idx);
        visibleSlots = Math.Max(1, visibleSlots);
        _multiUpCount = visibleSlots;
        if (_hud.ActiveMultiUpSlot >= _multiUpCount)
        {
            _hud.ActiveMultiUpSlot = 0;
        }

        try
        {
            for (int s = 0; s < 4; s++)
            {
                if (s >= visibleSlots)
                {
                    _multiUpIndices[s] = -1;
                    _slotDecoded[s] = null;
                    _hud.MultiUpSlots[s] = new();
                    _renderer.SetSlotImage(s, null);
                    _renderer.SetSlotClippingMask(s, null);
                    _renderer.SetSlotFalseColor(s, null);
                    continue;
                }

                int slotIdx = idx + s;
                var slotItem = filtered[slotIdx];
                var slotMeta = _cullingService.GetMetadata(slotItem.Id);
                _multiUpIndices[s] = slotIdx;

                var dec = await _pipeline.GetImageAsync(slotIdx);
                if (gen != _loadGeneration) return;

                _slotDecoded[s] = dec;
                _renderer.SetSlotImage(s, dec);

                if (s == 0)
                {
                    _primaryDecoded = dec;
                    ApplySlotProfileForCurrentMode(slotIdx: 0, slotItem, dec, slotMeta);
                }
                else
                {
                    _renderer.SetSlotCameraProfile(s, CameraProfile.AsShot, CameraBrand.Unsupported);
                }

                _hud.MultiUpSlots[s] = new MultiUpSlotState
                {
                    Item = slotItem,
                    FilteredIndex = slotIdx,
                    Culling = slotMeta,
                    Aesthetic = _aestheticsEngine.TryGetCachedScore(slotItem.Id, out var slotAi) ? slotAi : null
                };
            }

            if (visibleSlots > 1 && _multiUpIndices[1] >= 0)
            {
                _hud.SecondaryIndex = _multiUpIndices[1];
                _hud.SecondaryItem = filtered[_multiUpIndices[1]];
                _hud.SecondaryCulling = _cullingService.GetMetadata(_hud.SecondaryItem.Id);
                _secondaryDecoded = _slotDecoded[1];
            }
            else
            {
                _hud.SecondaryItem = null;
                _secondaryDecoded = null;
            }

            if (_hud.MultiUpMode != MultiUpMode.Single)
            {
                _hud.ActiveCameraProfile = CameraProfile.AsShot;
                _hud.ActiveCameraBrand = CameraBrand.Unsupported;
            }

            if (_hud.FilmstripOpacity > 0.05f || _filmstripPinned)
            {
                float cardW = BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight).Right
                            - BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight).Left;
                _filmstripScrollTargetX = FilmstripOverlay.ComputeCenteredScrollX(idx, cardW, filtered.Count);
                _microThumbEngine.RequestWindow(filtered, idx, 28);
            }

            UpdateDerivedOverlays();
            RequestRedraw();

            _ = AnalyzeCurrentSlideAsync(gen, idx, primaryItem, analysisCt);

            if (_hud.IsCustomZoom || _hud.CursorLoupeMagnification > 0)
            {
                int zoomSlot = GetZoomTargetSlotIndex();
                _ = UpgradeToFullResolutionAsync(gen, _multiUpIndices[zoomSlot]);
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

    private void ApplySlotProfileForCurrentMode(int slotIdx, SlideItem item, DecodedImage? decoded, CullingMetadata meta)
    {
        if (_hud.MultiUpMode != MultiUpMode.Single)
        {
            _renderer.SetSlotCameraProfile(slotIdx, CameraProfile.AsShot, CameraBrand.Unsupported);
            if (slotIdx == 0)
            {
                _hud.ActiveCameraProfile = CameraProfile.AsShot;
                _hud.ActiveCameraBrand = CameraBrand.Unsupported;
            }
            return;
        }

        var check = CameraProfileEngine.IsProfileSupported(item, decoded?.Exif);
        var effectiveProf = check.Supported ? (meta.CameraProfile ?? CameraProfile.AsShot) : CameraProfile.AsShot;
        _renderer.SetSlotCameraProfile(slotIdx, effectiveProf, check.Brand);
        _hud.ActiveCameraProfile = effectiveProf;
        _hud.ActiveCameraBrand = check.Brand;
    }

    private int GetZoomTargetSlotIndex() =>
        (_hud.MultiUpMode != MultiUpMode.Single && _hud.IsSoloLoupeActive)
            ? Math.Clamp(_hud.ActiveMultiUpSlot, 0, Math.Max(0, _multiUpCount - 1))
            : 0;

    private async Task UpgradeToFullResolutionAsync(int expectedGen, int index)
    {
        var filtered = _cullingService.FilteredItems;
        if (index < 0 || index >= filtered.Count) return;

        int targetSlot = -1;
        for (int s = 0; s < _multiUpCount; s++)
        {
            if (_multiUpIndices[s] == index) { targetSlot = s; break; }
        }
        if (targetSlot < 0 || _slotDecoded[targetSlot] == null) return;

        var item = filtered[index];
        var currentDec = _slotDecoded[targetSlot]!;
        bool needsUpgrade = currentDec.IsDownscaled || (item.RawPath != null && !currentDec.IsFullSensorRaw);
        if (!needsUpgrade) return;

        if (item.RawPath != null && _hud.IsCustomZoom)
        {
            _hud.IsLoadingFullRaw = true;
            RequestRedraw();
        }

        try
        {
            var full = await _pipeline.GetFullResolutionImageAsync(index);
            if (expectedGen != _loadGeneration) return;

            _slotDecoded[targetSlot] = full;
            _renderer.SetSlotImage(targetSlot, full);
            if (targetSlot == 0)
            {
                _primaryDecoded = full;
                ApplySlotProfileForCurrentMode(0, item, full, _cullingService.GetMetadata(item.Id));
            }
            else
            {
                _renderer.SetSlotCameraProfile(targetSlot, CameraProfile.AsShot, CameraBrand.Unsupported);
            }

            if (_hud.IsSplitCompare && !_hud.IsSoloLoupeActive)
            {
                for (int s = 0; s < _multiUpCount; s++)
                {
                    if (s == targetSlot) continue;
                    int otherIdx = _multiUpIndices[s];
                    if (otherIdx < 0 || otherIdx >= filtered.Count) continue;
                    var fullOther = await _pipeline.GetFullResolutionImageAsync(otherIdx);
                    if (expectedGen != _loadGeneration) return;
                    _slotDecoded[s] = fullOther;
                    _renderer.SetSlotImage(s, fullOther);
                    _renderer.SetSlotCameraProfile(s, CameraProfile.AsShot, CameraBrand.Unsupported);
                }
                _secondaryDecoded = _slotDecoded[1];
            }

            UpdateDerivedOverlays();
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

    private void UpdateDerivedOverlays()
    {
        int zoomSlot = GetZoomTargetSlotIndex();
        var primaryHistSrc = _slotDecoded[zoomSlot] ?? _primaryDecoded;
        if (primaryHistSrc == null)
        {
            _hud.Histogram = null;
            _hud.SecondaryHistogram = null;
            for (int s = 0; s < 4; s++)
            {
                _renderer.SetSlotClippingMask(s, null);
                _renderer.SetSlotFalseColor(s, null);
            }
            return;
        }

        if (_hud.ShowHistogram)
        {
            _hud.Histogram = HistogramEngine.Compute(primaryHistSrc);
            _hud.SecondaryHistogram = (_hud.MultiUpMode == MultiUpMode.TwoUp && !_hud.IsSoloLoupeActive && _slotDecoded[1] != null)
                ? HistogramEngine.Compute(_slotDecoded[1]!)
                : null;
        }
        else
        {
            _hud.SecondaryHistogram = null;
        }

        for (int s = 0; s < _multiUpCount; s++)
        {
            var slotImg = _slotDecoded[s];
            _renderer.SetSlotClippingMask(s, _hud.ShowClipping && !_hud.ShowFalseColor && slotImg != null
                ? ClippingEngine.GenerateClippingMask(slotImg)
                : null);

            _renderer.SetSlotFalseColor(s, _hud.ShowFalseColor && slotImg != null
                ? FalseColorEngine.GenerateFalseColorImage(slotImg)
                : null);
        }
    }

    private void Navigate(int delta)
    {
        _pendingAutoAdvanceAtMs = 0;
        var filtered = _cullingService.FilteredItems;
        if (filtered.Count == 0) return;

        int step = _hud.MultiUpMode == MultiUpMode.Single ? delta : delta * (int)_hud.MultiUpMode;
        int next = Math.Clamp(_hud.CurrentIndex + step, 0, filtered.Count - 1);
        if (next == _hud.CurrentIndex) return;

        _hud.CurrentIndex = next;
        if (_hud.MultiUpMode == MultiUpMode.Single || _hud.IsSoloLoupeActive)
        {
            _hud.IsSoloLoupeActive = false;
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
        _isZHeld = false;
        _isDraggingPan = false;
        _isDraggingMiniMap = false;
    }

    private void EnterOneToOneZoom(Vector2? anchorDip = null, bool silent = false)
    {
        if (_hud.MultiUpMode == MultiUpMode.FourUp && !_hud.IsSoloLoupeActive)
        {
            _hud.IsSoloLoupeActive = true;
        }

        int slot = GetZoomTargetSlotIndex();
        var dec = _slotDecoded[slot] ?? _primaryDecoded;
        var item = _hud.MultiUpSlots[slot].Item ?? _hud.PrimaryItem;
        if (dec == null || item == null) return;

        // Deconflict circular loupe when entering 1:1 zoom
        _hud.CursorLoupeMagnification = 0;

        Vector2 norm = anchorDip is { } pt ? ClientDipToImageNorm(pt, dec, GetActivePaneDip(slot)) : new Vector2(0.5f, 0.5f);
        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = 1.0f;
        _hud.PanNormCenter = norm;

        if (!silent)
        {
            ShowToast(item.RawPath != null ? "100% 1:1 · 16-bit RAW" : "100% 1:1");
        }
        RequestRedraw();

        int targetIdx = _multiUpIndices[slot] >= 0 ? _multiUpIndices[slot] : _hud.CurrentIndex;
        _ = UpgradeToFullResolutionAsync(_loadGeneration, targetIdx);
    }

    private void ExitZoomToFit(bool silent = false)
    {
        bool wasSolo = _hud.IsSoloLoupeActive;
        if (!_hud.IsCustomZoom && !wasSolo) return;

        _hud.ActiveZoomedFaceIndex = null;
        _hud.IsSoloLoupeActive = false;
        ResetZoomState();
        if (!silent)
        {
            ShowToast(wasSolo ? "Back to 4-UP" : "Fit to Screen");
        }

        for (int s = 0; s < _multiUpCount; s++)
        {
            var dec = _slotDecoded[s];
            int rawIdx = _multiUpIndices[s];
            if (dec != null && dec.IsFullSensorRaw && rawIdx >= 0 &&
                _pipeline.TryGetCachedScreenImage(rawIdx, out var screenImg) && screenImg != null)
            {
                _slotDecoded[s] = screenImg;
                _renderer.SetSlotImage(s, screenImg);
                if (s == 0)
                {
                    _primaryDecoded = screenImg;
                    var item0 = _hud.MultiUpSlots[0].Item ?? _hud.PrimaryItem;
                    if (item0 != null) ApplySlotProfileForCurrentMode(0, item0, screenImg, _cullingService.GetMetadata(item0.Id));
                }
            }
        }
        if (_slotDecoded[0] != null) UpdateDerivedOverlays();
        RequestRedraw();
    }

    private void ToggleOneToOneZoom(Vector2? anchorDip = null)
    {
        if (_primaryDecoded == null || _hud.PrimaryItem == null) return;

        if (_hud.IsCustomZoom)
        {
            ExitZoomToFit();
        }
        else
        {
            EnterOneToOneZoom(anchorDip);
        }
    }

    private async Task TriggerAiEyeJumpAsync()
    {
        int slot = GetZoomTargetSlotIndex();
        var dec = _slotDecoded[slot] ?? _primaryDecoded;
        var item = _hud.MultiUpSlots[slot].Item ?? _hud.PrimaryItem;
        if (dec == null || item == null) return;

        _hud.CursorLoupeMagnification = 0;
        int expectedGen = _loadGeneration;
        string itemId = item.Id;

        if (_cachedEyeImageId != itemId || _eyeStops.Count == 0)
        {
            ShowToast("Detecting Eyes…");
            RenderNow();

            var srcImg = dec;
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

        var pane = GetActivePaneDip(slot);
        _renderer.ComputePhotoDestDipRect(dec, pane, new RenderHudState(), out float fitPhysScale);
        float targetScale = Math.Clamp(MathF.Max(1.0f, fitPhysScale * 2.4f), 1.0f, 4.0f);

        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = targetScale;
        _hud.PanNormCenter = new Vector2(stop.Point.X, stop.Point.Y);
        _hud.EyeReticleSlot = slot;
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

        _hud.CursorLoupeMagnification = 0;
        var pane = GetPrimaryPaneDip();
        var oldRect = _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, _hud, out float curPhysScale);

        float factor = wheelDelta > 0 ? 1.25f : (1.0f / 1.25f);
        var fitProbe = new RenderHudState();
        _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, fitProbe, out float fitPhysScale);

        float nextScale = Math.Clamp(curPhysScale * factor, fitPhysScale, 12.0f);
        if (nextScale <= fitPhysScale * 1.02f && wheelDelta < 0)
        {
            ExitZoomToFit(silent: true);
            return;
        }

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

    private Vector2 ClientDipToImageNorm(Vector2 dip, DecodedImage? decoded = null, RawRectF? explicitPane = null)
    {
        var img = decoded ?? _primaryDecoded;
        if (img == null) return new Vector2(0.5f, 0.5f);
        var pane = explicitPane ?? GetPrimaryPaneDip();
        var rect = _renderer.ComputePhotoDestDipRect(img, pane, _hud, out _);
        float w = MathF.Max(1f, rect.Right - rect.Left);
        float h = MathF.Max(1f, rect.Bottom - rect.Top);
        return new Vector2(
            Math.Clamp((dip.X - rect.Left) / w, 0f, 1f),
            Math.Clamp((dip.Y - rect.Top) / h, 0f, 1f)
        );
    }

    private RawRectF GetActivePaneDip(int slotIndex)
    {
        var panes = BottomLayoutMetrics.ComputeMultiUpPaneRects(_hud.MultiUpMode, _renderer.DipWidth, _renderer.DipHeight, _hud.IsSoloLoupeActive);
        if (_hud.IsSoloLoupeActive || panes.Length == 1) return panes[0];
        int idx = Math.Clamp(slotIndex, 0, panes.Length - 1);
        return panes[idx];
    }

    private RawRectF GetPrimaryPaneDip() => GetActivePaneDip(0);

    private SlideItem? GetActiveSlotItem()
    {
        if (_hud.MultiUpMode == MultiUpMode.Single) return _hud.PrimaryItem;
        int slot = Math.Clamp(_hud.ActiveMultiUpSlot, 0, Math.Max(0, _multiUpCount - 1));
        return _hud.MultiUpSlots[slot].Item ?? _hud.PrimaryItem;
    }

    #region Culling, Rotation, Undo/Redo, Export & Trash Actions

    private void StopSlideshowIfRunning()
    {
        if (_hud.IsSlideshowPlaying)
        {
            _hud.IsSlideshowPlaying = false;
            RequestRedraw();
        }
    }

    private void HandleCullingOutcome(
        SlideItem item,
        string toast,
        bool shouldAutoAdvance,
        bool itemRemovedFromFilter)
    {
        ShowToast(toast);

        if (itemRemovedFromFilter)
        {
            var filtered = _cullingService.FilteredItems;
            string? nextNeighborId = null;
            int itemPos = -1;
            for (int i = 0; i < filtered.Count; i++)
            {
                if (string.Equals(filtered[i].Id, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    itemPos = i;
                    break;
                }
            }
            if (itemPos < 0) itemPos = _hud.CurrentIndex;

            if (itemPos + 1 < filtered.Count) nextNeighborId = filtered[itemPos + 1].Id;
            else if (itemPos - 1 >= 0) nextNeighborId = filtered[itemPos - 1].Id;

            _cullingService.RebuildFilteredItems();
            RebuildFilteredPlaylist(nextNeighborId, preserveCache: true);
            return;
        }

        var updatedMeta = _cullingService.GetMetadata(item.Id);
        if (_hud.PrimaryItem != null && string.Equals(_hud.PrimaryItem.Id, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            _hud.PrimaryCulling = updatedMeta;
        }
        if (_hud.SecondaryItem != null && string.Equals(_hud.SecondaryItem.Id, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            _hud.SecondaryCulling = updatedMeta;
        }
        for (int s = 0; s < _multiUpCount; s++)
        {
            if (_hud.MultiUpSlots[s].Item is { } si && string.Equals(si.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                _hud.MultiUpSlots[s].Culling = updatedMeta;
            }
        }
        RequestRedraw();

        if (shouldAutoAdvance && _hud.MultiUpMode == MultiUpMode.Single && _cullingService.FilteredItems.Count > 1 &&
            _hud.CurrentIndex + 1 < _cullingService.FilteredItems.Count)
        {
            _pendingAutoAdvanceAtMs = _clock.ElapsedMilliseconds + 150;
        }
    }

    private void RotateCurrent(RotationDirection dir)
    {
        var item = GetActiveSlotItem();
        if (item == null) return;
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        _cullingService.ApplyRotation(item, dir, SuppressWatcherPath);
        if (_hud.PrimaryItem != null && string.Equals(_hud.PrimaryItem.Id, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            _hud.PrimaryCulling = _cullingService.GetMetadata(item.Id);
        }

        _pipeline.Invalidate(item.Id);
        _microThumbEngine.Invalidate(item.Id);
        _cachedEyeImageId = null;
        ShowToast(dir == RotationDirection.Clockwise ? "↻ Rotated 90° CW" : "↺ Rotated 90° CCW");
        _ = LoadCurrentSlideAsync(isForward: true);
    }

    private async Task ExportSingleCurrentAsync()
    {
        if (_hud.PrimaryItem == null || string.IsNullOrEmpty(_currentDirectory)) return;
        StopSlideshowIfRunning();
        var item = _hud.PrimaryItem;

        var res = await ExportManager.ExportSingleAsync(item, _currentDirectory, SuppressWatcherPath);
        ShowToast(res.FilesWritten > 0
            ? $"✓ Exported {item.DisplayName} to ./{ExportManager.SelectsFolderName}"
            : "Export failed");
    }

    private async Task ExportPicksAsync()
    {
        if (_cullingService.AllItems.Count == 0 || string.IsNullOrEmpty(_currentDirectory)) return;
        StopSlideshowIfRunning();
        ShowToast("Exporting Picks…");
        RenderNow();

        var res = await ExportManager.ExportPicksAsync(
            _cullingService.AllItems,
            _cullingService.CullingMap,
            _currentDirectory,
            SuppressWatcherPath);

        if (res.ItemCount > 0)
        {
            ShowToast($"✓ Exported {res.ItemCount} Picks ({res.FilesWritten} files) to ./{ExportManager.SelectsFolderName}");
            ExportManager.RevealInExplorer(res.DestinationDirectory);
        }
        else
        {
            ShowToast("No Picks (P) flagged yet");
        }
    }

    private void OpenCurrentInExternalEditor()
    {
        if (_hud.PrimaryItem == null) return;
        bool launched = ExportManager.OpenInExternalEditor(_hud.PrimaryItem);
        ShowToast(launched ? "Opening in external editor…" : "Could not launch external editor");
    }

    private async Task MoveCurrentToTrashAsync()
    {
        var item = GetActiveSlotItem();
        if (item == null) return;
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        var filtered = _cullingService.FilteredItems;
        int itemPos = -1;
        for (int i = 0; i < filtered.Count; i++)
        {
            if (string.Equals(filtered[i].Id, item.Id, StringComparison.OrdinalIgnoreCase)) { itemPos = i; break; }
        }
        if (itemPos < 0) itemPos = _hud.CurrentIndex;

        string? nextId = itemPos + 1 < filtered.Count
            ? filtered[itemPos + 1].Id
            : (itemPos - 1 >= 0 ? filtered[itemPos - 1].Id : null);

        if (await _cullingService.TrashItemAsync(item, SuppressWatcherPath))
        {
            _pipeline.Invalidate(item.Id);
            _microThumbEngine.Invalidate(item.Id);
            _cachedEyeImageId = null;
            ShowToast($"Moved {item.DisplayName} to Recycle Bin");
            RebuildFilteredPlaylist(nextId, preserveCache: true);
        }
    }

    private async Task PurgeAllRejectsAsync()
    {
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        int rejectCount = _cullingService.AllItems.Count(it => _cullingService.GetMetadata(it.Id).Pick == PickStatus.Reject);
        if (rejectCount == 0)
        {
            ShowToast("No Rejected (X) photos to purge");
            return;
        }

        if (!ConfirmPurgeRejects(_hwnd, rejectCount)) return;

        int purged = await _cullingService.PurgeRejectsAsync(SuppressWatcherPath);
        _cachedEyeImageId = null;
        ShowToast($"Purged {purged} rejects to Recycle Bin");
        RebuildFilteredPlaylist(_hud.PrimaryItem?.Id, preserveCache: true);
    }

    private async Task PerformUndoRedoAsync(bool isUndo)
    {
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        var res = isUndo
            ? await _cullingService.UndoAsync(SuppressWatcherPath)
            : await _cullingService.RedoAsync(SuppressWatcherPath);

        if (!res.Applied)
        {
            ShowToast(res.Toast);
            return;
        }

        foreach (var id in res.InvalidatedItemIds)
        {
            _pipeline.Invalidate(id);
            _microThumbEngine.Invalidate(id);
        }
        _cachedEyeImageId = null;
        ShowToast(res.Toast);

        if (res.PlaylistChanged)
        {
            RebuildFilteredPlaylist(res.FocusItemId ?? _hud.PrimaryItem?.Id, preserveCache: true);
            return;
        }

        if (!string.IsNullOrEmpty(res.FocusItemId) &&
            !string.Equals(res.FocusItemId, _hud.PrimaryItem?.Id, StringComparison.OrdinalIgnoreCase))
        {
            var filtered = _cullingService.FilteredItems;
            for (int i = 0; i < filtered.Count; i++)
            {
                if (string.Equals(filtered[i].Id, res.FocusItemId, StringComparison.OrdinalIgnoreCase))
                {
                    _hud.CurrentIndex = i;
                    _ = LoadCurrentSlideAsync(isForward: true);
                    return;
                }
            }
        }

        if (res.InvalidatedItemIds.Count > 0)
        {
            _ = LoadCurrentSlideAsync(isForward: true);
        }
        else if (_hud.PrimaryItem != null)
        {
            var primMeta = _cullingService.GetMetadata(_hud.PrimaryItem.Id);
            _hud.PrimaryCulling = primMeta;
            if (_hud.MultiUpMode == MultiUpMode.Single)
            {
                ApplySlotProfileForCurrentMode(0, _hud.PrimaryItem, _primaryDecoded, primMeta);
            }
            for (int s = 0; s < _multiUpCount; s++)
            {
                if (_hud.MultiUpSlots[s].Item is { } si)
                {
                    _hud.MultiUpSlots[s].Culling = _cullingService.GetMetadata(si.Id);
                }
            }
            RequestRedraw();
        }
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

    private async Task AnalyzeCurrentSlideAsync(int expectedGen, int slideIndex, SlideItem primaryItem, CancellationToken ct)
    {
        try
        {
            var aiScore = await _aestheticsEngine.ScoreItemAsync(primaryItem, _pipeline, detectFaces: true, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;

            if (aiScore != null)
            {
                _hud.PrimaryAesthetic = aiScore;
                _hud.MultiUpSlots[0].Aesthetic = aiScore;
                RequestRedraw();
            }

            var filtered = _cullingService.FilteredItems;
            if (slideIndex < 0 || slideIndex >= filtered.Count) return;

            // Score additional Multi-Up slots asynchronously so 2-UP King-of-the-Hill / 4-UP badges light up
            if (_hud.MultiUpMode != MultiUpMode.Single)
            {
                for (int s = 1; s < _multiUpCount; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_hud.MultiUpSlots[s].Item is { } si)
                    {
                        var slotScore = await _aestheticsEngine.ScoreItemAsync(si, _pipeline, detectFaces: true, ct);
                        if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;
                        _hud.MultiUpSlots[s].Aesthetic = slotScore;
                        RequestRedraw();
                    }
                }
            }
            else if (_hud.ShowFaceDock && _slotDecoded[0] is { } dec0)
            {
                var tiles = await _faceEngine.GetFacesAsync(primaryItem, dec0, ct);
                if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;
                if (tiles.Count > 0)
                {
                    _hud.FaceTiles = tiles;
                    _renderer.SetFaceCropImages(tiles);
                    RequestRedraw();
                }
            }

            var burst = await _sceneEngine.ComputeBurstBoundsAsync(filtered, slideIndex, _pipeline, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;

            _burstCacheByIndex[slideIndex] = burst;
            if (burst == null) return;

            _hud.PrimaryBurstInfo = burst;
            _hud.MultiUpSlots[0].BurstInfo = burst;
            RequestRedraw();

            var (_, eval) = await EvaluateBurstSliceWithHeroAsync(filtered, burst, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration || eval == null) return;

            int globalHeroIdx = burst.GlobalStartIndex + eval.BurstHeroLocalIndex;
            for (int k = 0; k < burst.BurstCount && burst.GlobalStartIndex + k < filtered.Count; k++)
            {
                int gi = burst.GlobalStartIndex + k;
                var info = burst with
                {
                    CurrentBurstIndex = k,
                    GlobalHeroIndex = globalHeroIdx,
                    BurstHeroLocalIndex = eval.BurstHeroLocalIndex,
                    HeroScore = eval.HeroScore.OverallScore,
                    IsCurrentHero = gi == globalHeroIdx
                };
                _burstCacheByIndex[gi] = info;
                for (int s = 0; s < _multiUpCount; s++)
                {
                    if (_hud.MultiUpSlots[s].FilteredIndex == gi)
                    {
                        _hud.MultiUpSlots[s].BurstInfo = info;
                    }
                }
            }

            _hud.PrimaryBurstInfo = _burstCacheByIndex[slideIndex];
            RequestRedraw();
        }
        catch (OperationCanceledException) { }
    }

    private void CycleMultiUpMode()
    {
        StopSlideshowIfRunning();
        var filtered = _cullingService.FilteredItems;
        var nextMode = _hud.MultiUpMode.Next();
        if (nextMode != MultiUpMode.Single && _cullingService.AllItems.Count < 2)
        {
            ShowToast("Need at least 2 photos for Multi-Up");
            return;
        }

        string? keepId = GetActiveSlotItem()?.Id ?? _hud.PrimaryItem?.Id;
        _hud.IsSoloLoupeActive = false;
        _hud.ActiveMultiUpSlot = 0;
        ResetZoomState();

        if (nextMode == MultiUpMode.TwoUp)
        {
            _hud.MultiUpMode = MultiUpMode.TwoUp;
            if (_cullingService.ActiveFilter.Type is CullingFilterType.All or CullingFilterType.RejectsOnly)
            {
                _filterBeforeMultiUp = _cullingService.ActiveFilter;
                _cullingService.SetFilter(CullingFilter.HideRejects);
                _hud.ActiveFilter = _cullingService.ActiveFilter;
                ShowToast("2-UP Compare (Pairs · Hide Rejects)");
                RebuildFilteredPlaylist(keepId, preserveCache: true);
                return;
            }
            ShowToast("2-UP Compare (Pairs)");
            _ = LoadCurrentSlideAsync(isForward: true);
        }
        else if (nextMode == MultiUpMode.FourUp)
        {
            if (filtered.Count < 2)
            {
                CycleToSingleFromMultiUp(keepId);
                return;
            }
            _hud.MultiUpMode = MultiUpMode.FourUp;
            ShowToast("4-UP Quad Compare");
            _ = LoadCurrentSlideAsync(isForward: true);
        }
        else
        {
            CycleToSingleFromMultiUp(keepId);
        }
    }

    private void CycleToSingleFromMultiUp(string? keepId)
    {
        _hud.MultiUpMode = MultiUpMode.Single;
        ShowToast("Single Fullscreen");
        if (_filterBeforeMultiUp != null)
        {
            _cullingService.SetFilter(_filterBeforeMultiUp);
            _filterBeforeMultiUp = null;
            _hud.ActiveFilter = _cullingService.ActiveFilter;
            RebuildFilteredPlaylist(keepId, preserveCache: true);
            return;
        }
        _ = LoadCurrentSlideAsync(isForward: true);
    }

    private void HandleKingOfTheHill(int winnerSlot, int loserSlot)
    {
        var winner = _hud.MultiUpSlots[winnerSlot].Item;
        var loser = _hud.MultiUpSlots[loserSlot].Item;
        if (winner == null || loser == null) return;

        StopSlideshowIfRunning();
        _cullingService.ApplyKingOfTheHill(winner, loser, SuppressWatcherPath, out var toast);
        ShowToast(toast);

        _cullingService.RebuildFilteredItems();
        RebuildFilteredPlaylist(preferredId: winner.Id, preserveCache: true);
    }

    private void CycleCameraProfile()
    {
        if (_hud.MultiUpMode != MultiUpMode.Single)
        {
            ShowToast("🎨 Profiles Locked in Multi-Up");
            return;
        }
        if (_hud.PrimaryItem == null) return;

        var exif = _primaryDecoded?.Exif ?? FastImageDecoder.ReadExifMetadata(_hud.PrimaryItem.Path);
        var check = CameraProfileEngine.IsProfileSupported(_hud.PrimaryItem, exif);
        if (!check.Supported)
        {
            ShowToast($"🎨 {check.Reason ?? "Profiles Unavailable"}");
            return;
        }

        var meta = _cullingService.GetMetadata(_hud.PrimaryItem.Id);
        var nextProfile = (meta.CameraProfile ?? CameraProfile.AsShot).Next();
        _cullingService.ApplyCameraProfile(_hud.PrimaryItem, nextProfile, check.Brand, SuppressWatcherPath, out var toast);

        _hud.ActiveCameraProfile = nextProfile;
        _hud.ActiveCameraBrand = check.Brand;
        _renderer.SetSlotCameraProfile(0, nextProfile, check.Brand);
        ShowToast(toast);
        RequestRedraw();
    }

    private void ToggleFaceDock()
    {
        _hud.ShowFaceDock = !_hud.ShowFaceDock;
        if (_hud.ShowFaceDock)
        {
            ShowToast("👥 Face Inspection ON");
            if (_hud.MultiUpMode == MultiUpMode.Single && _hud.PrimaryItem != null && _primaryDecoded != null && _hud.FaceTiles.Count == 0)
            {
                _ = RefreshFaceDockAsync(_loadGeneration, _hud.PrimaryItem, _primaryDecoded);
            }
            else
            {
                RequestRedraw();
            }
        }
        else
        {
            ShowToast("Face Inspection OFF");
            RequestRedraw();
        }
    }

    private async Task RefreshFaceDockAsync(int expectedGen, SlideItem item, DecodedImage dec)
    {
        try
        {
            var tiles = await _faceEngine.GetFacesAsync(item, dec);
            if (expectedGen != _loadGeneration || !_hud.ShowFaceDock) return;
            _hud.FaceTiles = tiles;
            _renderer.SetFaceCropImages(tiles);
            RequestRedraw();
        }
        catch { }
    }

    private void HandleFaceTileClick(int tileIndex)
    {
        if (tileIndex < 0 || tileIndex >= _hud.FaceTiles.Count || _primaryDecoded == null) return;
        var tile = _hud.FaceTiles[tileIndex];

        if (_hud.IsCustomZoom && _hud.ActiveZoomedFaceIndex == tile.FaceIndex)
        {
            _hud.ActiveZoomedFaceIndex = null;
            ExitZoomToFit(silent: true);
            ShowToast("Eye Focus: Off");
            return;
        }

        _hud.CursorLoupeMagnification = 0;
        var pane = GetPrimaryPaneDip();
        _renderer.ComputePhotoDestDipRect(_primaryDecoded, pane, new RenderHudState(), out float fitPhysScale);
        float targetScale = Math.Clamp(MathF.Max(1.0f, fitPhysScale * 2.5f), 1.0f, 4.0f);

        _hud.ActiveZoomedFaceIndex = tile.FaceIndex;
        _hud.IsCustomZoom = true;
        _hud.PixelZoomScale = targetScale;
        _hud.PanNormCenter = new Vector2(tile.EyeCenter.X, tile.EyeCenter.Y);
        _hud.EyeReticleSlot = 0;
        _hud.EyeReticlePoint = tile.EyeCenter;
        _hud.EyeFaceRect = tile.FaceBox;
        _hud.EyeReticleOpacity = 1.0f;
        _eyeReticleShownAtMs = _clock.ElapsedMilliseconds;

        ShowToast($"👤 Face {tile.FaceIndex}/{_hud.FaceTiles.Count} ({(tile.IsSharp ? "Sharp" : "Soft")})");
        RequestRedraw();
        _ = UpgradeToFullResolutionAsync(_loadGeneration, _hud.CurrentIndex);
    }

    private void ToggleFilmstrip()
    {
        var filtered = _cullingService.FilteredItems;
        if (filtered.Count == 0) return;

        _filmstripPinned = !_filmstripPinned;
        _filmstripHoverOpen = false;
        _hud.IsFilmstripPinned = _filmstripPinned;

        if (_filmstripPinned)
        {
            var bounds = BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight);
            float centered = FilmstripOverlay.ComputeCenteredScrollX(_hud.CurrentIndex, bounds.Right - bounds.Left, filtered.Count);
            _hud.FilmstripScrollX = centered;
            _filmstripScrollTargetX = centered;
            _filmstripScrollVelocity = 0f;
            _microThumbEngine.RequestWindow(filtered, _hud.CurrentIndex, 28);
            ShowToast("🎞 Filmstrip Pinned");
        }
        else
        {
            ShowToast("Filmstrip Hidden");
        }
        RequestRedraw();
    }

    private void JumpToFilteredIndex(int targetIndex)
    {
        var filtered = _cullingService.FilteredItems;
        if (targetIndex < 0 || targetIndex >= filtered.Count) return;

        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;
        bool forward = targetIndex >= _hud.CurrentIndex;
        _hud.CurrentIndex = targetIndex;
        _hud.IsSoloLoupeActive = false;
        ResetZoomState();
        _ = LoadCurrentSlideAsync(forward);
    }

    private async Task<(List<SlideItem> Slice, BurstHeroEvaluation? Evaluation)> EvaluateBurstSliceWithHeroAsync(
        IReadOnlyList<SlideItem> filtered,
        BurstBreadcrumbInfo burst,
        CancellationToken ct)
    {
        int len = Math.Min(burst.BurstCount, filtered.Count - burst.GlobalStartIndex);
        var slice = new List<SlideItem>(Math.Max(0, len));
        for (int i = 0; i < len; i++)
        {
            slice.Add(filtered[burst.GlobalStartIndex + i]);
        }
        var eval = await _aestheticsEngine.EvaluateBurstHeroAsync(slice, _pipeline, ct);
        return (slice, eval);
    }

    private async Task JumpSceneAsync(int direction)
    {
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        var filtered = _cullingService.FilteredItems;
        if (filtered.Count <= 1) return;

        _sceneJumpCts?.Cancel();
        _sceneJumpCts?.Dispose();
        _sceneJumpCts = new CancellationTokenSource();
        var ct = _sceneJumpCts.Token;
        int expectedGen = _loadGeneration;
        int startIdx = _hud.CurrentIndex;

        ShowToast(direction > 0 ? "Scanning next scene…" : "Scanning previous scene…");
        try
        {
            int? targetIdx = await _sceneEngine.FindSceneBoundaryAsync(filtered, startIdx, direction, _pipeline, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;

            if (targetIdx.HasValue && targetIdx.Value != startIdx && targetIdx.Value >= 0 && targetIdx.Value < filtered.Count)
            {
                _hud.CurrentIndex = targetIdx.Value;
                ShowToast(direction > 0
                    ? $"⏭ Scene #{targetIdx.Value + 1} · {filtered[targetIdx.Value].DisplayName}"
                    : $"⏮ Scene #{targetIdx.Value + 1} · {filtered[targetIdx.Value].DisplayName}");
                _ = LoadCurrentSlideAsync(isForward: direction > 0);
            }
            else
            {
                ShowToast(direction > 0 ? "No next scene boundary" : "At first scene");
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task JumpToBurstHeroAsync()
    {
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        var filtered = _cullingService.FilteredItems;
        if (filtered.Count < 2)
        {
            ShowToast("Not in a burst sequence");
            return;
        }

        if (_hud.PrimaryBurstInfo is { GlobalHeroIndex: { } cachedHeroIdx } && cachedHeroIdx >= 0 && cachedHeroIdx < filtered.Count)
        {
            string scoreStr = _hud.PrimaryBurstInfo.HeroScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
            if (cachedHeroIdx == _hud.CurrentIndex)
            {
                ShowToast($"👑 Already on Burst Hero (AI {scoreStr})");
                return;
            }

            bool forward = cachedHeroIdx > _hud.CurrentIndex;
            _hud.CurrentIndex = cachedHeroIdx;
            ShowToast($"👑 Burst Hero #{cachedHeroIdx + 1} (AI {scoreStr})");
            _ = LoadCurrentSlideAsync(forward);
            return;
        }

        _sceneJumpCts?.Cancel();
        _sceneJumpCts?.Dispose();
        _sceneJumpCts = new CancellationTokenSource();
        var ct = _sceneJumpCts.Token;
        int expectedGen = _loadGeneration;
        int startIdx = _hud.CurrentIndex;

        ShowToast("Evaluating Burst Hero…");
        try
        {
            var burst = _hud.PrimaryBurstInfo ?? await _sceneEngine.ComputeBurstBoundsAsync(filtered, startIdx, _pipeline, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;

            if (burst == null)
            {
                ShowToast("Not in a burst sequence");
                return;
            }

            var (_, eval) = await EvaluateBurstSliceWithHeroAsync(filtered, burst, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration || eval == null) return;

            int heroIdx = burst.GlobalStartIndex + eval.BurstHeroLocalIndex;
            if (heroIdx == _hud.CurrentIndex)
            {
                _hud.PrimaryBurstInfo = burst with
                {
                    GlobalHeroIndex = heroIdx,
                    BurstHeroLocalIndex = eval.BurstHeroLocalIndex,
                    HeroScore = eval.HeroScore.OverallScore,
                    IsCurrentHero = true
                };
                ShowToast($"👑 Already on Burst Hero (AI {eval.HeroScore.FormattedScore})");
                RequestRedraw();
                return;
            }

            _hud.CurrentIndex = heroIdx;
            ShowToast($"👑 Burst Hero #{heroIdx + 1} (AI {eval.HeroScore.FormattedScore})");
            _ = LoadCurrentSlideAsync(heroIdx > startIdx);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PerformBurstKnockoutAsync()
    {
        StopSlideshowIfRunning();
        _pendingAutoAdvanceAtMs = 0;

        var filtered = _cullingService.FilteredItems;
        if (filtered.Count < 2 || _hud.PrimaryItem == null)
        {
            ShowToast("Not in a burst sequence");
            return;
        }

        _sceneJumpCts?.Cancel();
        _sceneJumpCts?.Dispose();
        _sceneJumpCts = new CancellationTokenSource();
        var ct = _sceneJumpCts.Token;
        int expectedGen = _loadGeneration;
        int startIdx = _hud.CurrentIndex;

        try
        {
            var burst = _hud.PrimaryBurstInfo ?? await _sceneEngine.ComputeBurstBoundsAsync(filtered, startIdx, _pipeline, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration) return;

            if (burst == null)
            {
                ShowToast("Not in a burst sequence");
                return;
            }

            var (burstSlice, eval) = await EvaluateBurstSliceWithHeroAsync(filtered, burst, ct);
            if (ct.IsCancellationRequested || expectedGen != _loadGeneration || eval == null || burstSlice.Count < 2)
            {
                ShowToast("Not in a burst sequence");
                return;
            }

            _cullingService.ApplyBurstKnockout(burstSlice, eval.HeroItem, SuppressWatcherPath,
                out var toast, out bool anyRemovedFromFilter);
            _cachedEyeImageId = null;

            int nextAfterBurstIdx = burst.GlobalStartIndex + burstSlice.Count;
            string? nextAfterBurstId = nextAfterBurstIdx < filtered.Count ? filtered[nextAfterBurstIdx].Id : null;
            ShowToast(toast);

            if (anyRemovedFromFilter)
            {
                _cullingService.RebuildFilteredItems();
                RebuildFilteredPlaylist(nextAfterBurstId ?? eval.HeroItem.Id, preserveCache: true);
                return;
            }

            if (nextAfterBurstIdx < filtered.Count)
            {
                _hud.CurrentIndex = nextAfterBurstIdx;
                _ = LoadCurrentSlideAsync(isForward: true);
            }
            else
            {
                int globalHeroIdx = burst.GlobalStartIndex + eval.BurstHeroLocalIndex;
                if (globalHeroIdx != _hud.CurrentIndex && globalHeroIdx < filtered.Count)
                {
                    _hud.CurrentIndex = globalHeroIdx;
                    _ = LoadCurrentSlideAsync(isForward: globalHeroIdx > startIdx);
                }
                else if (_hud.PrimaryItem != null)
                {
                    _hud.PrimaryCulling = _cullingService.GetMetadata(_hud.PrimaryItem.Id);
                    RequestRedraw();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Keyboard, Mouse & Hit-Test Handlers

    private void OnKeyDown(int vk, bool isRepeat)
    {
        WakeHud();

        bool ctrl = (GetKeyState(CommandMap.VK_CONTROL) & 0x8000) != 0;
        bool shift = (GetKeyState(CommandMap.VK_SHIFT) & 0x8000) != 0;
        bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;

        var cmd = CommandMap.Resolve(new KeyInput(vk, ctrl, shift, alt, isRepeat), _hud.ShowCheatsheet);
        if (cmd.HasValue)
        {
            ExecuteCommand(cmd.Value);
        }
    }

    private void OnKeyUp(int vk)
    {
        if (vk == 'Z' && _isZHeld)
        {
            _isZHeld = false;
            long holdDuration = _clock.ElapsedMilliseconds - _zKeyDownMs;
            if (!_zTriggeredEyeJump && _hud.IsCustomZoom && (_wasZoomedOnZDown || holdDuration > 250))
            {
                ExitZoomToFit(silent: holdDuration > 250);
            }
        }
    }

    private void ExecuteCommand(AppCommand cmd)
    {
        var filtered = _cullingService.FilteredItems;

        switch (cmd)
        {
            case AppCommand.CancelOrExit:
                if (_hud.ShowCheatsheet && _hud.IsCheatsheetExpanded) { _hud.IsCheatsheetExpanded = false; RequestRedraw(); }
                else if (_hud.ShowCheatsheet) { _hud.ShowCheatsheet = false; RequestRedraw(); }
                else if (_hud.IsSoloLoupeActive) { ExitZoomToFit(); }
                else if (_hud.CursorLoupeMagnification > 0) { _hud.CursorLoupeMagnification = 0; ShowToast("Loupe OFF"); RequestRedraw(); }
                else if (_hud.IsCustomZoom) { ExitZoomToFit(); }
                else if (_filmstripPinned || _filmstripHoverOpen) { _filmstripPinned = false; _filmstripHoverOpen = false; _hud.IsFilmstripPinned = false; RequestRedraw(); }
                else if (_hud.MultiUpMode != MultiUpMode.Single) { CycleToSingleFromMultiUp(GetActiveSlotItem()?.Id ?? _hud.PrimaryItem?.Id); }
                else DestroyWindow(_hwnd);
                break;

            case AppCommand.ToggleCheatsheet:
                _hud.ShowCheatsheet = !_hud.ShowCheatsheet;
                if (!_hud.ShowCheatsheet) _hud.IsCheatsheetExpanded = false;
                RequestRedraw();
                break;

            case AppCommand.ToggleCheatsheetExpanded:
                if (_hud.ShowCheatsheet)
                {
                    _hud.IsCheatsheetExpanded = !_hud.IsCheatsheetExpanded;
                    RequestRedraw();
                }
                break;

            case AppCommand.OpenFileDialog:
                BrowseAndOpenFile();
                break;

            case AppCommand.OpenExternalEditor:
                OpenCurrentInExternalEditor();
                break;

            case AppCommand.NextImage:
                StopSlideshowIfRunning();
                Navigate(+1);
                break;

            case AppCommand.PreviousImage:
                StopSlideshowIfRunning();
                Navigate(-1);
                break;

            case AppCommand.FirstImage:
                StopSlideshowIfRunning();
                if (filtered.Count > 0) { _hud.CurrentIndex = 0; _ = LoadCurrentSlideAsync(true); }
                break;

            case AppCommand.LastImage:
                StopSlideshowIfRunning();
                if (filtered.Count > 0) { _hud.CurrentIndex = filtered.Count - 1; _ = LoadCurrentSlideAsync(false); }
                break;

            case AppCommand.NextScene:
                _ = JumpSceneAsync(+1);
                break;

            case AppCommand.PreviousScene:
                _ = JumpSceneAsync(-1);
                break;

            case AppCommand.JumpToBurstHero:
                _ = JumpToBurstHeroAsync();
                break;

            case AppCommand.BurstKnockout:
                _ = PerformBurstKnockoutAsync();
                break;

            case AppCommand.ToggleSlideshow:
                if (filtered.Count > 1)
                {
                    _hud.IsSlideshowPlaying = !_hud.IsSlideshowPlaying;
                    _lastSlideshowAdvanceMs = _clock.ElapsedMilliseconds;
                    ShowToast(_hud.IsSlideshowPlaying ? $"▶ Slideshow ({_hud.SlideshowSeconds}s)" : "⏸ Paused");
                    RequestRedraw();
                }
                break;

            case AppCommand.SlideshowIntervalIncrease:
                if (_hud.IsSlideshowPlaying)
                {
                    int? nextStep = null;
                    for (int i = 0; i < SlideshowSteps.Length; i++)
                    {
                        if (SlideshowSteps[i] > _hud.SlideshowSeconds) { nextStep = SlideshowSteps[i]; break; }
                    }
                    if (nextStep.HasValue)
                    {
                        _hud.SlideshowSeconds = nextStep.Value;
                        _lastSlideshowAdvanceMs = _clock.ElapsedMilliseconds;
                    }
                    ShowToast($"Slideshow: {_hud.SlideshowSeconds}s");
                }
                break;

            case AppCommand.SlideshowIntervalDecrease:
                if (_hud.IsSlideshowPlaying)
                {
                    int? prevStep = null;
                    for (int i = SlideshowSteps.Length - 1; i >= 0; i--)
                    {
                        if (SlideshowSteps[i] < _hud.SlideshowSeconds) { prevStep = SlideshowSteps[i]; break; }
                    }
                    if (prevStep.HasValue)
                    {
                        _hud.SlideshowSeconds = prevStep.Value;
                        _lastSlideshowAdvanceMs = _clock.ElapsedMilliseconds;
                    }
                    ShowToast($"Slideshow: {_hud.SlideshowSeconds}s");
                }
                break;

            case AppCommand.ToggleFullscreen:
                ToggleFullscreenMode();
                break;

            case AppCommand.ToggleFocusPeaking:
                _hud.ShowFocusPeaking = !_hud.ShowFocusPeaking;
                ShowToast(_hud.ShowFocusPeaking ? "GPU Focus Peaking ON" : "Focus Peaking OFF");
                RequestRedraw();
                break;

            case AppCommand.ToggleZoom100:
                {
                    StopSlideshowIfRunning();
                    long now = _clock.ElapsedMilliseconds;
                    bool isDoubleTap = (now - _lastZKeyPressMs) <= 320;
                    _lastZKeyPressMs = now;

                    _isZHeld = true;
                    _zKeyDownMs = now;
                    _wasZoomedOnZDown = _hud.IsCustomZoom;
                    _zTriggeredEyeJump = false;

                    GetCursorPos(out var screenPt);
                    ScreenToClient(_hwnd, ref screenPt);
                    float dpiScale = _renderer.DpiScale;
                    var dip = new Vector2(screenPt.X / dpiScale, screenPt.Y / dpiScale);
                    _zLastMouseClient = screenPt;

                    if (isDoubleTap)
                    {
                        _zTriggeredEyeJump = true;
                        _ = TriggerAiEyeJumpAsync();
                    }
                    else if (!_hud.IsCustomZoom)
                    {
                        EnterOneToOneZoom(dip);
                    }
                    break;
                }

            case AppCommand.AiEyeJump:
                _ = TriggerAiEyeJumpAsync();
                break;

            case AppCommand.ToggleClipping:
                _hud.ShowClipping = !_hud.ShowClipping;
                if (_hud.ShowClipping) _hud.ShowFalseColor = false;
                UpdateDerivedOverlays();
                ShowToast(_hud.ShowClipping ? "Clipping Blinkies ON" : "Clipping Blinkies OFF");
                RequestRedraw();
                break;

            case AppCommand.ToggleFalseColor:
                _hud.ShowFalseColor = !_hud.ShowFalseColor;
                if (_hud.ShowFalseColor) _hud.ShowClipping = false;
                UpdateDerivedOverlays();
                ShowToast(_hud.ShowFalseColor ? "🌈 ARRI False Color ON" : "False Color OFF");
                RequestRedraw();
                break;

            case AppCommand.CycleCursorLoupe:
                if (_hud.HasPlaylist)
                {
                    int nextMag = _hud.CursorLoupeMagnification switch
                    {
                        0 => 2,
                        2 => 4,
                        _ => 0
                    };
                    if (nextMag > 0 && _hud.IsCustomZoom)
                    {
                        ExitZoomToFit(silent: true);
                    }
                    _hud.CursorLoupeMagnification = nextMag;
                    GetCursorPos(out var ptScreen);
                    ScreenToClient(_hwnd, ref ptScreen);
                    GetClientRect(_hwnd, out var cli);
                    if (ptScreen.X >= 0 && ptScreen.Y >= 0 && ptScreen.X < cli.Width && ptScreen.Y < cli.Height)
                    {
                        _hud.IsCursorInsideClient = true;
                        _hud.CursorDip = new Vector2(ptScreen.X / _renderer.DpiScale, ptScreen.Y / _renderer.DpiScale);
                    }
                    if (nextMag > 0)
                    {
                        _ = UpgradeToFullResolutionAsync(_loadGeneration, _hud.CurrentIndex);
                    }
                    ShowToast(nextMag > 0 ? $"🔬 Optical Loupe {nextMag}×" : "Loupe OFF");
                    RequestRedraw();
                }
                break;

            case AppCommand.ToggleExifDossier:
                _hud.ShowExifDossier = !_hud.ShowExifDossier;
                if (!_hud.ShowExifDossier)
                {
                    _hud.ExifHoverProgress = 0f;
                    _isHoveringExif = false;
                }
                ShowToast(_hud.ShowExifDossier ? "📷 EXIF Dossier ON" : "EXIF Dossier OFF");
                RequestRedraw();
                break;

            case AppCommand.ToggleTheme:
                _hud.Theme = _hud.Theme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
                _settings.Theme = _hud.Theme == ThemeMode.Dark ? ThemePreference.Dark : ThemePreference.Light;
                SettingsStore.Save(_settings);
                ApplyWin11WindowAttributes(_hwnd, _hud.Theme == ThemeMode.Dark);
                ShowToast(_hud.Theme == ThemeMode.Dark ? "🌙 Frosted Dark Theme" : "☀ Frosted Light Theme");
                RequestRedraw();
                break;

            case AppCommand.ToggleHistogram:
                _hud.ShowHistogram = !_hud.ShowHistogram;
                UpdateDerivedOverlays();
                RequestRedraw();
                break;

            case AppCommand.ToggleFaceDock:
                ToggleFaceDock();
                break;

            case AppCommand.ToggleFilmstrip:
                ToggleFilmstrip();
                break;

            case AppCommand.CycleCameraProfile:
                CycleCameraProfile();
                break;

            case AppCommand.ToggleSplitCompare:
                CycleMultiUpMode();
                break;

            case AppCommand.SwapSplitPanes:
                if (_hud.MultiUpMode != MultiUpMode.Single && _multiUpCount > 1)
                {
                    _hud.ActiveMultiUpSlot = (_hud.ActiveMultiUpSlot + 1) % _multiUpCount;
                    RequestRedraw();
                }
                break;

            case AppCommand.TogglePick when GetActiveSlotItem() != null:
                {
                    StopSlideshowIfRunning();
                    var item = GetActiveSlotItem()!;
                    _cullingService.ApplyPick(item, PickStatus.Pick, toggle: true, SuppressWatcherPath,
                        out var toast, out var autoAdv, out var removed);
                    HandleCullingOutcome(item, toast, autoAdv, removed);
                    break;
                }

            case AppCommand.ToggleReject when GetActiveSlotItem() != null:
                {
                    StopSlideshowIfRunning();
                    var item = GetActiveSlotItem()!;
                    _cullingService.ApplyPick(item, PickStatus.Reject, toggle: true, SuppressWatcherPath,
                        out var toast, out var autoAdv, out var removed);
                    HandleCullingOutcome(item, toast, autoAdv, removed);
                    break;
                }

            case AppCommand.Unflag when GetActiveSlotItem() != null:
                {
                    StopSlideshowIfRunning();
                    var item = GetActiveSlotItem()!;
                    _cullingService.ApplyPick(item, PickStatus.Unflagged, toggle: false, SuppressWatcherPath,
                        out var toast, out var autoAdv, out var removed);
                    HandleCullingOutcome(item, toast, autoAdv, removed);
                    break;
                }

            case AppCommand.SetRating0 or AppCommand.SetRating1 or AppCommand.SetRating2
                or AppCommand.SetRating3 or AppCommand.SetRating4 or AppCommand.SetRating5 when GetActiveSlotItem() != null:
                {
                    StopSlideshowIfRunning();
                    int stars = cmd - AppCommand.SetRating0;
                    if (_hud.MultiUpMode == MultiUpMode.TwoUp && !_hud.IsSoloLoupeActive &&
                        stars is 1 or 2 && _multiUpCount == 2 &&
                        _hud.MultiUpSlots[0].Item != null && _hud.MultiUpSlots[1].Item != null)
                    {
                        HandleKingOfTheHill(winnerSlot: stars - 1, loserSlot: 2 - stars);
                        break;
                    }
                    var item = GetActiveSlotItem()!;
                    _cullingService.ApplyRating(item, stars, SuppressWatcherPath,
                        out var toast, out var autoAdv, out var removed);
                    HandleCullingOutcome(item, toast, autoAdv, removed);
                    break;
                }

            case AppCommand.ToggleColorRed or AppCommand.ToggleColorYellow
                or AppCommand.ToggleColorGreen or AppCommand.ToggleColorBlue when GetActiveSlotItem() != null:
                {
                    StopSlideshowIfRunning();
                    var label = cmd switch
                    {
                        AppCommand.ToggleColorRed => ColorLabel.Red,
                        AppCommand.ToggleColorYellow => ColorLabel.Yellow,
                        AppCommand.ToggleColorGreen => ColorLabel.Green,
                        AppCommand.ToggleColorBlue => ColorLabel.Blue,
                        _ => ColorLabel.None
                    };
                    var item = GetActiveSlotItem()!;
                    _cullingService.ApplyColor(item, label, SuppressWatcherPath,
                        out var toast, out var autoAdv, out var removed);
                    HandleCullingOutcome(item, toast, autoAdv, removed);
                    break;
                }

            case AppCommand.ToggleAutoAdvance:
                _cullingService.AutoAdvanceEnabled = !_cullingService.AutoAdvanceEnabled;
                _hud.AutoAdvanceEnabled = _cullingService.AutoAdvanceEnabled;
                _settings.AutoAdvance = _cullingService.AutoAdvanceEnabled;
                SettingsStore.Save(_settings);
                ShowToast(_hud.AutoAdvanceEnabled ? "⚡ Auto-Advance: ON" : "Auto-Advance: OFF");
                RequestRedraw();
                break;

            case AppCommand.CycleFilter:
                {
                    StopSlideshowIfRunning();
                    string? keepId = _hud.PrimaryItem?.Id;
                    bool changed = _cullingService.CycleFilter(out var toast);
                    _hud.ActiveFilter = _cullingService.ActiveFilter;
                    ShowToast(toast);
                    if (changed) RebuildFilteredPlaylist(keepId, preserveCache: true);
                    break;
                }

            case AppCommand.TogglePicksOnlyFilter:
                {
                    StopSlideshowIfRunning();
                    string? keepId = _hud.PrimaryItem?.Id;
                    bool changed = _cullingService.TogglePicksOnlyFilter(out var toast);
                    _hud.ActiveFilter = _cullingService.ActiveFilter;
                    ShowToast(toast);
                    if (changed) RebuildFilteredPlaylist(keepId, preserveCache: true);
                    break;
                }

            case AppCommand.RotateCW:
                RotateCurrent(RotationDirection.Clockwise);
                break;

            case AppCommand.RotateCCW:
                RotateCurrent(RotationDirection.CounterClockwise);
                break;

            case AppCommand.ExportCurrent:
                _ = ExportSingleCurrentAsync();
                break;

            case AppCommand.BatchExportPicks:
                _ = ExportPicksAsync();
                break;

            case AppCommand.TrashCurrent:
                _ = MoveCurrentToTrashAsync();
                break;

            case AppCommand.PurgeRejects:
                _ = PurgeAllRejectsAsync();
                break;

            case AppCommand.Undo:
                _ = PerformUndoRedoAsync(isUndo: true);
                break;

            case AppCommand.Redo:
                _ = PerformUndoRedoAsync(isUndo: false);
                break;
        }
    }

    private bool IsDipInRect(Vector2 dip, RawRectF rect) =>
        dip.X >= rect.Left && dip.X <= rect.Right && dip.Y >= rect.Top && dip.Y <= rect.Bottom;

    private bool TryGetExifBounds(out RawRectF bounds)
    {
        if (!_hud.HasPlaylist || !_hud.ShowExifDossier || _hud.ShowCheatsheet || _hud.PrimaryItem == null)
        {
            bounds = default;
            return false;
        }
        bounds = ExifDossierOverlay.ComputeBounds(_renderer.DipWidth, _renderer.DipHeight, _hud.ExifHoverProgress);
        return true;
    }

    private bool TryGetMiniMapBounds(out RawRectF cardBounds)
    {
        int zoomSlot = GetZoomTargetSlotIndex();
        var dec = _slotDecoded[zoomSlot] ?? _primaryDecoded;
        if (!_hud.HasPlaylist || !_hud.IsCustomZoom || _hud.ShowCheatsheet || dec == null)
        {
            cardBounds = default;
            return false;
        }
        cardBounds = NavigatorMiniMapOverlay.ComputeCardBounds(_renderer.DipWidth, _renderer.DipHeight, _hud.IsMiniMapCollapsed, _hud.FilmstripOpacity);
        return true;
    }

    private void PanFromMiniMapDip(Vector2 dip, RawRectF cardBounds)
    {
        var dec = _slotDecoded[GetZoomTargetSlotIndex()] ?? _primaryDecoded;
        if (dec == null) return;
        var imgRect = NavigatorMiniMapOverlay.ComputeImageRect(cardBounds, dec.OriginalWidth, dec.OriginalHeight);
        float w = MathF.Max(1f, imgRect.Right - imgRect.Left);
        float h = MathF.Max(1f, imgRect.Bottom - imgRect.Top);
        _hud.PanNormCenter = new Vector2(
            Math.Clamp((dip.X - imgRect.Left) / w, 0f, 1f),
            Math.Clamp((dip.Y - imgRect.Top) / h, 0f, 1f));
    }

    private void CopyExifSummaryToClipboard()
    {
        if (_hud.PrimaryItem == null || _primaryDecoded == null) return;
        var exif = _primaryDecoded.Exif ?? new ExifMetadata();
        string text = exif.BuildClipboardSummary(
            _hud.PrimaryItem.DisplayName,
            _primaryDecoded.OriginalWidth,
            _primaryDecoded.OriginalHeight,
            _hud.PrimaryItem.TotalStackSize,
            _primaryDecoded.ColorProfileName);

        if (CopyTextToClipboard(_hwnd, text))
        {
            ShowToast("📋 EXIF copied to clipboard");
        }
    }

    private static unsafe bool CopyTextToClipboard(IntPtr hwnd, string text)
    {
        if (!OpenClipboard(hwnd)) return false;
        try
        {
            EmptyClipboard();
            int byteCount = (text.Length + 1) * sizeof(char);
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)byteCount);
            if (hGlobal == IntPtr.Zero) return false;

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                fixed (char* src = text)
                {
                    Buffer.MemoryCopy(src, (void*)ptr, byteCount, text.Length * sizeof(char));
                    ((char*)ptr)[text.Length] = '\0';
                }
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
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
            ApplyWin11WindowAttributes(_hwnd, _hud.Theme == ThemeMode.Dark);
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

        // 0. Pending Auto-Advance (150ms delay after rating/flagging)
        if (_pendingAutoAdvanceAtMs > 0 && now >= _pendingAutoAdvanceAtMs)
        {
            _pendingAutoAdvanceAtMs = 0;
            Navigate(+1);
            animDirty = true;
        }

        // 1. Slideshow auto-advance
        var filtered = _cullingService.FilteredItems;
        if (_hud.IsSlideshowPlaying && filtered.Count > 1)
        {
            if (now - _lastSlideshowAdvanceMs >= _hud.SlideshowSeconds * 1000L)
            {
                _lastSlideshowAdvanceMs = now;
                if (_hud.CurrentIndex + 1 < filtered.Count) Navigate(+1);
                else { _hud.CurrentIndex = 0; _ = LoadCurrentSlideAsync(true); }
                animDirty = true;
            }
        }

        // 2. EXIF dossier smooth hover expansion
        if (_hud.ShowExifDossier)
        {
            float targetExif = _isHoveringExif ? 1.0f : 0.0f;
            if (MathF.Abs(targetExif - _hud.ExifHoverProgress) > 0.008f)
            {
                _hud.ExifHoverProgress += (targetExif - _hud.ExifHoverProgress) * 0.28f;
                if (_hud.ExifHoverProgress < 0.01f) _hud.ExifHoverProgress = 0f;
                if (_hud.ExifHoverProgress > 0.99f) _hud.ExifHoverProgress = 1f;
                animDirty = true;
            }
        }

        // 3. Toast fade-out
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

        // 4. AI Eye Reticle fade-out
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

        // 5. Clipping blinkies gentle pulse
        if (_hud.ShowClipping && !_hud.ShowFalseColor && _primaryDecoded != null)
        {
            _hud.ClippingPulseAlpha = 0.65f + 0.30f * (0.5f + 0.5f * MathF.Sin(now * 0.0065f));
            animDirty = true;
        }

        // 6. Smooth Filmstrip fade + inertial scroll + hover auto-dismiss
        if (_filmstripHoverOpen && !_filmstripPinned && now >= _filmstripDismissAtMs)
        {
            _filmstripHoverOpen = false;
        }

        float targetFilm = (_hud.HasPlaylist && !_hud.ShowCheatsheet && (_filmstripPinned || _filmstripHoverOpen)) ? 1.0f : 0.0f;
        if (MathF.Abs(targetFilm - _hud.FilmstripOpacity) > 0.008f)
        {
            _hud.FilmstripOpacity += (targetFilm - _hud.FilmstripOpacity) * 0.28f;
            if (_hud.FilmstripOpacity < 0.015f) _hud.FilmstripOpacity = 0f;
            if (_hud.FilmstripOpacity > 0.985f) _hud.FilmstripOpacity = 1f;
            animDirty = true;
        }

        if (_hud.FilmstripOpacity > 0.01f && _hud.PlaylistItems.Count > 0)
        {
            var fBounds = BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight);
            float cardW = fBounds.Right - fBounds.Left;
            float maxScroll = FilmstripOverlay.ComputeMaxScrollX(cardW, _hud.PlaylistItems.Count);

            if (MathF.Abs(_filmstripScrollVelocity) > 0.35f)
            {
                _filmstripScrollTargetX = Math.Clamp(_filmstripScrollTargetX + _filmstripScrollVelocity, 0f, maxScroll);
                _filmstripScrollVelocity *= 0.84f;
                animDirty = true;
            }
            else
            {
                _filmstripScrollVelocity = 0f;
            }

            float scrollDiff = Math.Clamp(_filmstripScrollTargetX, 0f, maxScroll) - _hud.FilmstripScrollX;
            if (MathF.Abs(scrollDiff) > 0.4f)
            {
                _hud.FilmstripScrollX += scrollDiff * 0.30f;
                animDirty = true;
            }
            else if (MathF.Abs(scrollDiff) > 0.01f)
            {
                _hud.FilmstripScrollX = Math.Clamp(_filmstripScrollTargetX, 0f, maxScroll);
                animDirty = true;
            }

            float viewportW = MathF.Max(1f, cardW - FilmstripOverlay.SidePadding * 2f);
            int centerVisIdx = Math.Clamp((int)((_hud.FilmstripScrollX + viewportW * 0.5f) / FilmstripOverlay.CellStride),
                0, _hud.PlaylistItems.Count - 1);
            if (Math.Abs(centerVisIdx - _lastRequestedThumbCenter) >= 6)
            {
                _lastRequestedThumbCenter = centerVisIdx;
                _microThumbEngine.RequestWindow(_hud.PlaylistItems, centerVisIdx, 28);
            }
        }

        // 7. Auto-hide HUD & cursor after 3.0s idle in fullscreen
        long idleMs = now - _lastActivityMs;
        float targetHud = (idleMs > 3000 && _hud.HasPlaylist && !_hud.ShowCheatsheet && !_filmstripPinned && !_filmstripHoverOpen) ? 0.0f : 1.0f;
        if (MathF.Abs(targetHud - _hud.HudOpacity) > 0.01f)
        {
            _hud.HudOpacity += (targetHud - _hud.HudOpacity) * 0.22f;
            if (_hud.HudOpacity < 0.015f) _hud.HudOpacity = 0f;
            if (_hud.HudOpacity > 0.985f) _hud.HudOpacity = 1f;
            animDirty = true;
        }

        if (_isFullscreen && idleMs > 3000 && _hud.HasPlaylist && !_hud.ShowCheatsheet &&
            !_isDraggingPan && !_isDraggingMiniMap && _hud.CursorLoupeMagnification == 0 &&
            !_filmstripHoverOpen && _hud.HoveredFaceTileIndex < 0)
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

            case WM_SETTINGCHANGE:
                if (_settings.Theme == ThemePreference.System)
                {
                    var sysTheme = SettingsStore.DetectWindowsSystemTheme();
                    if (sysTheme != _hud.Theme)
                    {
                        _hud.Theme = sysTheme;
                        ApplyWin11WindowAttributes(_hwnd, sysTheme == ThemeMode.Dark);
                        RequestRedraw();
                    }
                }
                return 0;

            case WM_KILLFOCUS:
                _isZHeld = false;
                _isDraggingPan = false;
                _isDraggingMiniMap = false;
                return 0;

            case WM_SETCURSOR:
                if (((int)lParam & 0xFFFF) == HTCLIENT)
                {
                    float s = _renderer.DpiScale;
                    GetCursorPos(out var curScreen);
                    ScreenToClient(hwnd, ref curScreen);
                    var dip = new Vector2(curScreen.X / s, curScreen.Y / s);

                    if (_hud.ShowFaceDock && _hud.MultiUpMode == MultiUpMode.Single && _hud.FaceTiles.Count > 0 && !_hud.ShowCheatsheet &&
                        FaceDockOverlay.HitTestTile(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FaceTiles.Count, _hud.FilmstripOpacity) >= 0)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND));
                        return 1;
                    }

                    if (_hud.FilmstripOpacity > 0.2f && !_hud.ShowCheatsheet &&
                        FilmstripOverlay.HitTestIndex(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FilmstripScrollX, _hud.PlaylistItems.Count) >= 0)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND));
                        return 1;
                    }

                    if (TryGetExifBounds(out var exifBounds) && IsDipInRect(dip, exifBounds))
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND));
                        return 1;
                    }

                    if (TryGetMiniMapBounds(out var mapBounds) && IsDipInRect(dip, mapBounds))
                    {
                        var hdr = NavigatorMiniMapOverlay.ComputeHeaderBounds(mapBounds);
                        int idc = IsDipInRect(dip, hdr) || _hud.IsMiniMapCollapsed ? IDC_HAND : IDC_CROSS;
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)idc));
                        return 1;
                    }

                    if (_hud.CursorLoupeMagnification > 0 && !_hud.ShowCheatsheet && !_hud.IsCustomZoom && _hud.IsCursorInsideClient)
                    {
                        SetCursor(IntPtr.Zero);
                        return 1;
                    }

                    SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW));
                    return 1;
                }
                break;

            case WM_TIMER:
                OnTimerTick();
                return 0;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                {
                    bool isRepeat = ((long)lParam & (1L << 30)) != 0;
                    OnKeyDown((int)wParam, isRepeat);
                    return 0;
                }

            case WM_KEYUP:
            case WM_SYSKEYUP:
                OnKeyUp((int)wParam);
                return 0;

            case WM_LBUTTONDOWN:
                {
                    WakeHud();
                    var pt = UnpackPoint(lParam);
                    float s = _renderer.DpiScale;
                    var dip = new Vector2(pt.X / s, pt.Y / s);

                    // 0. Face Verification Dock tile click
                    if (_hud.HasPlaylist && _hud.ShowFaceDock && _hud.MultiUpMode == MultiUpMode.Single &&
                        _hud.FaceTiles.Count > 0 && !_hud.ShowCheatsheet)
                    {
                        int faceHit = FaceDockOverlay.HitTestTile(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FaceTiles.Count, _hud.FilmstripOpacity);
                        if (faceHit >= 0)
                        {
                            HandleFaceTileClick(faceHit);
                            return 0;
                        }
                    }

                    // 0b. Filmstrip Scrubber click
                    if (_hud.HasPlaylist && _hud.FilmstripOpacity > 0.2f && !_hud.ShowCheatsheet)
                    {
                        var fBounds = BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight);
                        if (IsDipInRect(dip, fBounds))
                        {
                            int thumbIdx = FilmstripOverlay.HitTestIndex(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FilmstripScrollX, _hud.PlaylistItems.Count);
                            if (thumbIdx >= 0)
                            {
                                JumpToFilteredIndex(thumbIdx);
                            }
                            return 0;
                        }
                    }

                    // 1. EXIF Dossier click -> Copy to clipboard
                    if (_hud.ShowFaceDock && _hud.MultiUpMode == MultiUpMode.Single && _hud.FaceTiles.Count > 0 && !_hud.ShowCheatsheet &&
                        FaceDockOverlay.HitTestTile(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FaceTiles.Count, _hud.FilmstripOpacity) >= 0)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND));
                        return 1;
                    }

                    if (_hud.FilmstripOpacity > 0.2f && !_hud.ShowCheatsheet &&
                        FilmstripOverlay.HitTestIndex(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FilmstripScrollX, _hud.PlaylistItems.Count) >= 0)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND));
                        return 1;
                    }

                    if (TryGetExifBounds(out var exifBounds) && IsDipInRect(dip, exifBounds))
                    {
                        CopyExifSummaryToClipboard();
                        return 0;
                    }

                    // 2. Navigator Mini-Map hit test (before zoom/pan)
                    if (TryGetMiniMapBounds(out var mapBounds) && IsDipInRect(dip, mapBounds))
                    {
                        var hdr = NavigatorMiniMapOverlay.ComputeHeaderBounds(mapBounds);
                        if (IsDipInRect(dip, hdr) || _hud.IsMiniMapCollapsed)
                        {
                            _hud.IsMiniMapCollapsed = !_hud.IsMiniMapCollapsed;
                            RequestRedraw();
                        }
                        else
                        {
                            _isDraggingMiniMap = true;
                            SetCapture(hwnd);
                            PanFromMiniMapDip(dip, mapBounds);
                            RenderNow();
                        }
                        return 0;
                    }

                    // 3. Floating Optical Loupe click -> toggle 2x / 4x
                    if (_hud.CursorLoupeMagnification > 0 && !_hud.ShowCheatsheet && !_hud.IsCustomZoom)
                    {
                        _hud.CursorLoupeMagnification = _hud.CursorLoupeMagnification == 2 ? 4 : 2;
                        _hud.CursorDip = dip;
                        ShowToast($"🔬 Optical Loupe {_hud.CursorLoupeMagnification}×");
                        RenderNow();
                        return 0;
                    }

                    // 4. Custom 1:1 pan drag
                    if (_hud.IsCustomZoom && _primaryDecoded != null)
                    {
                        _isDraggingPan = true;
                        _lastMouseClient = pt;
                        SetCapture(hwnd);
                    }
                    return 0;
                }

            case WM_LBUTTONUP:
                if (_isDraggingMiniMap)
                {
                    _isDraggingMiniMap = false;
                    ReleaseCapture();
                }
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
                    var dip = new Vector2(pt.X / s, pt.Y / s);

                    if ((TryGetExifBounds(out var exifB) && IsDipInRect(dip, exifB)) ||
                        (TryGetMiniMapBounds(out var mapB) && IsDipInRect(dip, mapB)))
                    {
                        return 0;
                    }

                    ToggleOneToOneZoom(dip);
                    return 0;
                }

            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                WakeHud();
                return 0;

            case WM_RBUTTONUP:
                {
                    WakeHud();
                    var pt = UnpackPoint(lParam);
                    float s = _renderer.DpiScale;
                    var dip = new Vector2(pt.X / s, pt.Y / s);

                    if (TryGetMiniMapBounds(out var mapB) && IsDipInRect(dip, mapB))
                    {
                        return 0;
                    }

                    bool shift = (GetKeyState(CommandMap.VK_SHIFT) & 0x8000) != 0;
                    if (shift)
                    {
                        _ = TriggerAiEyeJumpAsync();
                    }
                    else
                    {
                        ToggleOneToOneZoom(dip);
                    }
                    return 0;
                }

            case WM_MBUTTONUP:
                {
                    WakeHud();
                    _ = TriggerAiEyeJumpAsync();
                    return 0;
                }

            case WM_MOUSELEAVE:
                _trackingMouseLeave = false;
                _hud.IsCursorInsideClient = false;
                _isHoveringExif = false;
                _hud.HoveredFaceTileIndex = -1;
                _hud.HoveredFilmstripIndex = -1;
                if (_hud.CursorLoupeMagnification > 0 || _hud.FilmstripOpacity > 0f)
                {
                    RequestRedraw();
                }
                return 0;

            case WM_MOUSEMOVE:
                {
                    if (!_trackingMouseLeave)
                    {
                        var tme = new TRACKMOUSEEVENT
                        {
                            cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                            dwFlags = TME_LEAVE,
                            hwndTrack = hwnd
                        };
                        _trackingMouseLeave = TrackMouseEvent(ref tme);
                    }

                    var pt = UnpackPoint(lParam);
                    float s = _renderer.DpiScale;
                    var dip = new Vector2(pt.X / s, pt.Y / s);

                    _hud.IsCursorInsideClient = true;
                    _hud.CursorDip = dip;

                    if (pt.X != _lastMouseClient.X || pt.Y != _lastMouseClient.Y)
                    {
                        WakeHud();
                    }

                    // Multi-Up dynamic hover pane focus
                    if (!_hud.IsSoloLoupeActive && _hud.MultiUpMode != MultiUpMode.Single &&
                        !_isDraggingPan && !_isDraggingMiniMap && !_hud.ShowCheatsheet)
                    {
                        var panes = BottomLayoutMetrics.ComputeMultiUpPaneRects(_hud.MultiUpMode, _renderer.DipWidth, _renderer.DipHeight, false);
                        for (int k = 0; k < Math.Min(_multiUpCount, panes.Length); k++)
                        {
                            if (IsDipInRect(dip, panes[k]))
                            {
                                if (k != _hud.ActiveMultiUpSlot)
                                {
                                    _hud.ActiveMultiUpSlot = k;
                                    RequestRedraw();
                                }
                                break;
                            }
                        }
                    }

                    // Filmstrip bottom-edge hover pulse & cell highlight
                    if (_hud.HasPlaylist && !_hud.ShowCheatsheet)
                    {
                        long nowHover = _clock.ElapsedMilliseconds;
                        var fBounds = BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight);
                        if (dip.Y >= _renderer.DipHeight - 10f && !_filmstripPinned)
                        {
                            if (!_filmstripHoverOpen)
                            {
                                _filmstripHoverOpen = true;
                                _filmstripScrollTargetX = FilmstripOverlay.ComputeCenteredScrollX(_hud.CurrentIndex, fBounds.Right - fBounds.Left, _hud.PlaylistItems.Count);
                                _microThumbEngine.RequestWindow(_hud.PlaylistItems, _hud.CurrentIndex, 28);
                            }
                            _filmstripDismissAtMs = nowHover + 2200;
                        }
                        else if (_filmstripHoverOpen && !_filmstripPinned && IsDipInRect(dip, fBounds))
                        {
                            _filmstripDismissAtMs = nowHover + 1800;
                        }

                        int prevHoverThumb = _hud.HoveredFilmstripIndex;
                        _hud.HoveredFilmstripIndex = _hud.FilmstripOpacity > 0.2f
                            ? FilmstripOverlay.HitTestIndex(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FilmstripScrollX, _hud.PlaylistItems.Count)
                            : -1;
                        if (_hud.HoveredFilmstripIndex != prevHoverThumb) RequestRedraw();

                        int prevHoverFace = _hud.HoveredFaceTileIndex;
                        _hud.HoveredFaceTileIndex = (_hud.ShowFaceDock && _hud.MultiUpMode == MultiUpMode.Single && _hud.FaceTiles.Count > 0)
                            ? FaceDockOverlay.HitTestTile(dip, _renderer.DipWidth, _renderer.DipHeight, _hud.FaceTiles.Count, _hud.FilmstripOpacity)
                            : -1;
                        if (_hud.HoveredFaceTileIndex != prevHoverFace) RequestRedraw();
                    }

                    bool prevHoverExif = _isHoveringExif;
                    _isHoveringExif = TryGetExifBounds(out var exifB) && IsDipInRect(dip, exifB);

                    if (_isDraggingMiniMap && TryGetMiniMapBounds(out var mapBounds))
                    {
                        _lastMouseClient = pt;
                        PanFromMiniMapDip(dip, mapBounds);
                        RenderNow();
                        return 0;
                    }

                    if (_isDraggingPan && _hud.IsCustomZoom && (_slotDecoded[GetZoomTargetSlotIndex()] ?? _primaryDecoded) is { } panDec)
                    {
                        int dxPx = pt.X - _lastMouseClient.X;
                        int dyPx = pt.Y - _lastMouseClient.Y;
                        _lastMouseClient = pt;

                        float drawW = MathF.Max(1f, panDec.OriginalWidth * (_hud.PixelZoomScale / s));
                        float drawH = MathF.Max(1f, panDec.OriginalHeight * (_hud.PixelZoomScale / s));

                        _hud.PanNormCenter = new Vector2(
                            Math.Clamp(_hud.PanNormCenter.X - (dxPx / s) / drawW, 0f, 1f),
                            Math.Clamp(_hud.PanNormCenter.Y - (dyPx / s) / drawH, 0f, 1f)
                        );
                        RenderNow();
                        return 0;
                    }

                    if (_isZHeld && _hud.IsCustomZoom && (_slotDecoded[GetZoomTargetSlotIndex()] ?? _primaryDecoded) != null)
                    {
                        int dxPx = pt.X - _zLastMouseClient.X;
                        int dyPx = pt.Y - _zLastMouseClient.Y;
                        _zLastMouseClient = pt;
                        _lastMouseClient = pt;

                        var pane = GetActivePaneDip(GetZoomTargetSlotIndex());
                        float paneW = MathF.Max(1f, (pane.Right - pane.Left) * 0.85f);
                        float paneH = MathF.Max(1f, (pane.Bottom - pane.Top) * 0.85f);

                        _hud.PanNormCenter = new Vector2(
                            Math.Clamp(_hud.PanNormCenter.X + (dxPx / s) / paneW, 0f, 1f),
                            Math.Clamp(_hud.PanNormCenter.Y + (dyPx / s) / paneH, 0f, 1f)
                        );
                        RenderNow();
                        return 0;
                    }

                    _lastMouseClient = pt;

                    if (_hud.CursorLoupeMagnification > 0 && !_hud.IsCustomZoom && !_hud.ShowCheatsheet)
                    {
                        RenderNow();
                    }
                    else if (_isHoveringExif != prevHoverExif)
                    {
                        RequestRedraw();
                    }
                    return 0;
                }

            case WM_MOUSEWHEEL:
                {
                    WakeHud();
                    short delta = (short)((wParam >> 16) & 0xFFFF);
                    bool ctrl = (GetKeyState(CommandMap.VK_CONTROL) & 0x8000) != 0;
                    var screenPt = UnpackPoint(lParam);
                    ScreenToClient(hwnd, ref screenPt);
                    float s = _renderer.DpiScale;
                    var dip = new Vector2(screenPt.X / s, screenPt.Y / s);

                    if (_hud.FilmstripOpacity > 0.2f && !_hud.ShowCheatsheet &&
                        IsDipInRect(dip, BottomLayoutMetrics.ComputeFilmstripBounds(_renderer.DipWidth, _renderer.DipHeight)))
                    {
                        _filmstripScrollVelocity += -(delta / 120f) * 58f;
                        if (_filmstripHoverOpen && !_filmstripPinned)
                            _filmstripDismissAtMs = _clock.ElapsedMilliseconds + 2200;
                        RequestRedraw();
                        return 0;
                    }

                    if (ctrl || _hud.IsCustomZoom)
                    {
                        ApplyWheelZoom(delta, dip);
                    }
                    else
                    {
                        StopSlideshowIfRunning();
                        Navigate(delta < 0 ? +1 : -1);
                    }
                    return 0;
                }

            case WM_MOUSEHWHEEL:
                {
                    if (_hud.FilmstripOpacity > 0.1f && !_hud.ShowCheatsheet)
                    {
                        WakeHud();
                        short hDelta = (short)((wParam >> 16) & 0xFFFF);
                        _filmstripScrollVelocity += (hDelta / 120f) * 48f;
                        if (_filmstripHoverOpen && !_filmstripPinned)
                            _filmstripDismissAtMs = _clock.ElapsedMilliseconds + 2200;
                        RequestRedraw();
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
        var hIcon = LoadIconW(hInstance, (IntPtr)32512);
        if (hIcon == IntPtr.Zero) hIcon = LoadIconW(hInstance, (IntPtr)1);
        if (hIcon == IntPtr.Zero) hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512);
        return hIcon;
    }

    private static void ApplyWin11WindowAttributes(IntPtr hwnd, bool isDark)
    {
        try
        {
            int dark = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
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
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        _sceneJumpCts?.Cancel();
        _sceneJumpCts?.Dispose();
        _sceneJumpCts = null;
        _microThumbEngine.Dispose();
        if (_watcher != null)
        {
            _watcher.DirectoryChanged -= OnDirectoryWatcherChanged;
            _watcher.Dispose();
            _watcher = null;
        }
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
    private const int IDC_CROSS = 32515;
    private const int IDC_HAND = 32649;
    private const int HTCLIENT = 1;
    private const uint TME_LEAVE = 0x00000002;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_DPICHANGED = 0x02E0;

    private const int VK_MENU = 0x12;

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
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

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
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

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

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    #endregion
}
