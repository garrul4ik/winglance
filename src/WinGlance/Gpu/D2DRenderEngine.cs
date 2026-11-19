using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinGlance.Engine;
using WinGlance.Gpu.Overlays;
using WinGlance.Model;
using WinGlance.Vision;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DColorSpace = Vortice.Direct2D1.ColorSpace;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace WinGlance.Gpu;

public sealed class MultiUpSlotState
{
    public SlideItem? Item { get; set; }
    public int FilteredIndex { get; set; } = -1;
    public CullingMetadata Culling { get; set; }
    public AestheticScoreResult? Aesthetic { get; set; }
    public BurstBreadcrumbInfo? BurstInfo { get; set; }
    public bool IsHero => BurstInfo?.IsCurrentHero == true;
}

public sealed class RenderHudState
{
    public bool HasPlaylist { get; set; }
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public SlideItem? PrimaryItem { get; set; }
    public CullingMetadata PrimaryCulling { get; set; }
    public SlideItem? SecondaryItem { get; set; }
    public int SecondaryIndex { get; set; }
    public CullingMetadata SecondaryCulling { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public CullingFilter ActiveFilter { get; set; } = CullingFilter.All;
    public bool AutoAdvanceEnabled { get; set; } = true;

    public MultiUpMode MultiUpMode { get; set; } = MultiUpMode.Single;
    public bool IsSplitCompare
    {
        get => MultiUpMode != MultiUpMode.Single;
        set => MultiUpMode = value ? MultiUpMode.TwoUp : MultiUpMode.Single;
    }
    public int ActiveMultiUpSlot { get; set; }
    public bool IsSoloLoupeActive { get; set; }
    public MultiUpSlotState[] MultiUpSlots { get; } = { new(), new(), new(), new() };

    public bool IsSlideshowPlaying { get; set; }
    public int SlideshowSeconds { get; set; } = 3;
    public bool ShowHistogram { get; set; }
    public HistogramData? Histogram { get; set; }
    public HistogramData? SecondaryHistogram { get; set; }
    public bool ShowClipping { get; set; }
    public float ClippingPulseAlpha { get; set; } = 0.85f;
    public bool ShowFalseColor { get; set; }
    public bool ShowFocusPeaking { get; set; }
    public bool ShowExifDossier { get; set; }
    public float ExifHoverProgress { get; set; }
    public bool ShowCheatsheet { get; set; }
    public bool IsCheatsheetExpanded { get; set; }
    public bool IsLoadingFullRaw { get; set; }

    public AestheticScoreResult? PrimaryAesthetic { get; set; }
    public BurstBreadcrumbInfo? PrimaryBurstInfo { get; set; }

    // Camera Profile / Film Simulation (1-UP Fuji/Nikon/Sony RAW)
    public CameraProfile ActiveCameraProfile { get; set; } = CameraProfile.AsShot;
    public CameraBrand ActiveCameraBrand { get; set; } = CameraBrand.Unsupported;

    // Face Verification Dock (A)
    public bool ShowFaceDock { get; set; } = true;
    public IReadOnlyList<FaceCropTile> FaceTiles { get; set; } = Array.Empty<FaceCropTile>();
    public int? ActiveZoomedFaceIndex { get; set; }
    public int HoveredFaceTileIndex { get; set; } = -1;

    // Filmstrip Scrubber (G)
    public float FilmstripOpacity { get; set; }
    public bool IsFilmstripPinned { get; set; }
    public float FilmstripScrollX { get; set; }
    public IReadOnlyList<SlideItem> PlaylistItems { get; set; } = Array.Empty<SlideItem>();
    public Func<string, CullingMetadata>? GetCullingForItem { get; set; }
    public Func<int, BurstBreadcrumbInfo?>? GetBurstForIndex { get; set; }
    public int HoveredFilmstripIndex { get; set; } = -1;

    // Pan, Zoom & Loupe state
    public bool IsCustomZoom { get; set; }
    public float PixelZoomScale { get; set; } = 1.0f; // 1.0 = exact 1:1 physical sensor pixel
    public Vector2 PanNormCenter { get; set; } = new(0.5f, 0.5f);
    public bool IsMiniMapCollapsed { get; set; }
    public int CursorLoupeMagnification { get; set; } // 0, 2, 4
    public Vector2 CursorDip { get; set; }
    public bool IsCursorInsideClient { get; set; }

    // Animated HUD & AI Eye Reticle
    public float HudOpacity { get; set; } = 1.0f;
    public string? ToastText { get; set; }
    public float ToastOpacity { get; set; }
    public NormalizedPoint? EyeReticlePoint { get; set; }
    public NormalizedRect? EyeFaceRect { get; set; }
    public float EyeReticleOpacity { get; set; }
    public int EyeReticleSlot { get; set; }
}

public sealed class D2DRenderEngine : IDisposable
{
    private const int MaxMicroThumbGpuEntries = 160;

    private sealed class GpuPhotoSlot : IDisposable
    {
        public DecodedImage? Source;
        public ID2D1Bitmap1? Bitmap;
        public ID2D1ColorContext? CustomColorContext;
        public ColorManagement? ColorManagedEffect;
        public ColorMatrix? ProfileMatrixEffect;
        public CameraProfile ActiveProfile = CameraProfile.AsShot;
        public CameraBrand ActiveBrand = CameraBrand.Unsupported;
        public EdgeDetection? EdgeEffect;
        public ColorMatrix? PeakingMatrixEffect;
        public DecodedImage? FalseColorSource;
        public ID2D1Bitmap1? FalseColorBitmap;
        public DecodedImage? ClippingSource;
        public ID2D1Bitmap1? ClippingBitmap;

        public void Dispose()
        {
            PeakingMatrixEffect?.Dispose();
            EdgeEffect?.Dispose();
            ProfileMatrixEffect?.Dispose();
            ColorManagedEffect?.Dispose();
            CustomColorContext?.Dispose();
            Bitmap?.Dispose();
            FalseColorBitmap?.Dispose();
            ClippingBitmap?.Dispose();

            PeakingMatrixEffect = null;
            EdgeEffect = null;
            ProfileMatrixEffect = null;
            ColorManagedEffect = null;
            CustomColorContext = null;
            Bitmap = null;
            Source = null;
            FalseColorBitmap = null;
            FalseColorSource = null;
            ClippingBitmap = null;
            ClippingSource = null;
        }
    }

    private IntPtr _hwnd;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;
    private float _dpi = 96.0f;
    private bool _isFp16SwapChain;

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory6? _d2dFactory;
    private ID2D1Device5? _d2dDevice;
    private ID2D1DeviceContext5? _dc;
    private IDWriteFactory? _dwrite;

    private ID2D1Bitmap1? _backBufferBitmap;
    private ID2D1Bitmap1? _sceneBitmap;
    private GaussianBlur? _hudBlurEffect;
    private ID2D1ColorContext? _srgbColorContext;
    private ID2D1ColorContext? _scRgbColorContext;

    private readonly GpuPhotoSlot[] _slots = { new(), new(), new(), new() };

    private IReadOnlyList<FaceCropTile> _faceTilesSource = Array.Empty<FaceCropTile>();
    private readonly List<ID2D1Bitmap1?> _faceCropBitmaps = new();

    private MicroThumbnailEngine? _microThumbEngine;
    private readonly Dictionary<string, (ID2D1Bitmap1 Bitmap, LinkedListNode<string> Node)> _microThumbGpuCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _microThumbGpuLru = new();

    private TypographySet? _fonts;

    private readonly WelcomeOverlay _welcomeOverlay = new();
    private readonly SplitPaneBadgeOverlay _splitPaneBadgeOverlay = new();
    private readonly TopHudOverlay _topHudOverlay = new();
    private readonly BottomHudOverlay _bottomHudOverlay = new();
    private readonly FilmstripOverlay _filmstripOverlay = new();
    private readonly FaceDockOverlay _faceDockOverlay = new();
    private readonly HistogramOverlay _histogramOverlay = new();
    private readonly FalseColorLegendOverlay _falseColorLegendOverlay = new();
    private readonly ExifDossierOverlay _exifDossierOverlay = new();
    private readonly NavigatorMiniMapOverlay _miniMapOverlay = new();
    private readonly CursorLoupeOverlay _cursorLoupeOverlay = new();
    private readonly ToastOverlay _toastOverlay = new();
    private readonly CheatsheetOverlay _cheatsheetOverlay = new();

    public bool IsHdrFp16Active => _isFp16SwapChain;
    public float DpiScale => _dpi / 96.0f;
    public float DipWidth => _pixelWidth / DpiScale;
    public float DipHeight => _pixelHeight / DpiScale;

    public void Initialize(IntPtr hwnd, int width, int height, float dpi)
    {
        _hwnd = hwnd;
        _pixelWidth = Math.Max(1, width);
        _pixelHeight = Math.Max(1, height);
        _dpi = dpi > 0 ? dpi : 96.0f;

        CreateDeviceResources();
        CreateTypography();
        CreateWindowSizeResources();
    }

    public void SetMicroThumbnailEngine(MicroThumbnailEngine? engine) => _microThumbEngine = engine;

    private void CreateDeviceResources()
    {
        DisposeDeviceResources();

        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var flags = DeviceCreationFlags.BgraSupport;
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _d3dDevice, out _d3dContext).Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out _d3dDevice, out _d3dContext).CheckError();
        }

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory6>(D2DFactoryType.SingleThreaded);
        using var dxgiDevice = _d3dDevice!.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.EnableMultithreadedOptimizations);
        _dc.UnitMode = UnitMode.Dips;

        _srgbColorContext = _dc.CreateColorContext(D2DColorSpace.Srgb, null, 0);
        _scRgbColorContext = _dc.CreateColorContext(D2DColorSpace.ScRgb, null, 0);

        _hudBlurEffect = new GaussianBlur(_dc)
        {
            StandardDeviation = 18.0f,
            Optimization = GaussianBlurOptimization.Speed,
            BorderMode = BorderMode.Hard
        };
    }

    private void CreateTypography()
    {
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        _fonts?.Dispose();
        _fonts = new TypographySet(_dwrite);
    }

    private void CreateWindowSizeResources()
    {
        if (_dc == null || _d3dDevice == null) return;

        _dc.Target = null;
        _hudBlurEffect?.SetInput(0, null, true);
        _backBufferBitmap?.Dispose();
        _backBufferBitmap = null;
        _sceneBitmap?.Dispose();
        _sceneBitmap = null;

        if (_swapChain == null)
        {
            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var dxgiFactory = adapter.GetParent<IDXGIFactory2>();

            var fp16Desc = new SwapChainDescription1
            {
                Width = (uint)_pixelWidth,
                Height = (uint)_pixelHeight,
                Format = Format.R16G16B16A16_Float,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };

            try
            {
                _swapChain = dxgiFactory.CreateSwapChainForHwnd(_d3dDevice, _hwnd, fp16Desc);
                _isFp16SwapChain = true;
                using var sc3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
                sc3?.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            }
            catch
            {
                var sdrDesc = fp16Desc with { Format = Format.B8G8R8A8_UNorm };
                _swapChain = dxgiFactory.CreateSwapChainForHwnd(_d3dDevice, _hwnd, sdrDesc);
                _isFp16SwapChain = false;
            }

            dxgiFactory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }
        else
        {
            var fmt = _isFp16SwapChain ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
            var hr = _swapChain.ResizeBuffers(2, (uint)_pixelWidth, (uint)_pixelHeight, fmt, SwapChainFlags.None);
            if (hr.Failure)
            {
                RecreateAllAfterDeviceReset();
                return;
            }
        }

        var targetFormat = _isFp16SwapChain ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        var bpBackBuffer = new BitmapProperties1(
            new PixelFormat(targetFormat, AlphaMode.Ignore),
            _dpi,
            _dpi,
            BitmapOptions.Target | BitmapOptions.CannotDraw
        );

        using (var surface = _swapChain.GetBuffer<IDXGISurface>(0))
        {
            _backBufferBitmap = _dc.CreateBitmapFromDxgiSurface(surface, bpBackBuffer);
        }

        var bpScene = new BitmapProperties1(
            new PixelFormat(targetFormat, AlphaMode.Premultiplied),
            _dpi,
            _dpi,
            BitmapOptions.Target
        );

        _sceneBitmap = _dc.CreateBitmap(new SizeI(_pixelWidth, _pixelHeight), IntPtr.Zero, 0, bpScene);
        _dc.SetDpi(_dpi, _dpi);
        _hudBlurEffect?.SetInput(0, _sceneBitmap, true);
    }

    public void Resize(int pixelWidth, int pixelHeight, float dpi)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        if (pixelWidth == _pixelWidth && pixelHeight == _pixelHeight && MathF.Abs(dpi - _dpi) < 0.1f) return;

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _dpi = dpi > 0 ? dpi : _dpi;
        CreateWindowSizeResources();
    }

    public void SetPrimaryImage(DecodedImage? image) => SetSlotImage(0, image);
    public void SetSecondaryImage(DecodedImage? image) => SetSlotImage(1, image);
    public void SetClippingMask(DecodedImage? mask) => SetSlotClippingMask(0, mask);
    public void SetPrimaryFalseColor(DecodedImage? fcImage) => SetSlotFalseColor(0, fcImage);
    public void SetSecondaryFalseColor(DecodedImage? fcImage) => SetSlotFalseColor(1, fcImage);

    public void SetSlotImage(int slotIndex, DecodedImage? image)
    {
        if ((uint)slotIndex >= (uint)_slots.Length) return;
        var slot = _slots[slotIndex];
        if (ReferenceEquals(slot.Source, image)) return;
        slot.ActiveProfile = CameraProfile.AsShot;
        slot.ActiveBrand = CameraBrand.Unsupported;
        UploadToSlot(slot, image);
    }

    public void SetSlotCameraProfile(int slotIndex, CameraProfile profile, CameraBrand brand)
    {
        if ((uint)slotIndex >= (uint)_slots.Length) return;
        var slot = _slots[slotIndex];
        slot.ActiveProfile = profile;
        slot.ActiveBrand = brand;
        if (slot.ProfileMatrixEffect != null)
        {
            slot.ProfileMatrixEffect.Matrix = CameraProfileEngine.ComputeColorMatrix(profile, brand);
        }
    }

    public void SetSlotClippingMask(int slotIndex, DecodedImage? mask)
    {
        if ((uint)slotIndex >= (uint)_slots.Length) return;
        var slot = _slots[slotIndex];
        if (ReferenceEquals(slot.ClippingSource, mask)) return;
        slot.ClippingBitmap?.Dispose();
        slot.ClippingBitmap = null;
        slot.ClippingSource = mask;

        if (mask != null && _dc != null)
        {
            slot.ClippingBitmap = UploadBitmap8BitPremultiplied(_dc, mask);
        }
    }

    public void SetSlotFalseColor(int slotIndex, DecodedImage? fcImage)
    {
        if ((uint)slotIndex >= (uint)_slots.Length) return;
        var slot = _slots[slotIndex];
        if (ReferenceEquals(slot.FalseColorSource, fcImage)) return;
        slot.FalseColorBitmap?.Dispose();
        slot.FalseColorBitmap = null;
        slot.FalseColorSource = fcImage;

        if (fcImage != null && _dc != null)
        {
            slot.FalseColorBitmap = UploadBitmap8BitPremultiplied(_dc, fcImage);
        }
    }

    public void SetFaceCropImages(IReadOnlyList<FaceCropTile> tiles)
    {
        if (ReferenceEquals(_faceTilesSource, tiles)) return;
        DisposeFaceCropBitmaps();
        _faceTilesSource = tiles;

        if (_dc == null || tiles.Count == 0) return;
        foreach (var tile in tiles)
        {
            _faceCropBitmaps.Add(UploadBitmap8BitPremultiplied(_dc, tile.CropImage));
        }
    }

    public void ClearMicroThumbGpuCache()
    {
        foreach (var kv in _microThumbGpuCache.Values)
        {
            kv.Bitmap.Dispose();
        }
        _microThumbGpuCache.Clear();
        _microThumbGpuLru.Clear();
    }

    private ID2D1Bitmap1? ResolveMicroThumbBitmap(string itemId)
    {
        if (_dc == null || string.IsNullOrEmpty(itemId)) return null;

        if (_microThumbGpuCache.TryGetValue(itemId, out var existing))
        {
            _microThumbGpuLru.Remove(existing.Node);
            _microThumbGpuLru.AddFirst(existing.Node);
            return existing.Bitmap;
        }

        if (_microThumbEngine == null || !_microThumbEngine.TryGetThumbnail(itemId, out var thumb) || thumb == null)
            return null;

        ID2D1Bitmap1 bmp = thumb.PixelFormat == ImagePixelFormat.Rgba64
            ? Convert16To8AndUpload(_dc, thumb)
            : UploadBitmap8BitPremultiplied(_dc, thumb);

        if (_microThumbGpuCache.Count >= MaxMicroThumbGpuEntries && _microThumbGpuLru.Last is { } oldest)
        {
            if (_microThumbGpuCache.TryGetValue(oldest.Value, out var victim))
            {
                victim.Bitmap.Dispose();
                _microThumbGpuCache.Remove(oldest.Value);
            }
            _microThumbGpuLru.RemoveLast();
        }

        var node = _microThumbGpuLru.AddFirst(itemId);
        _microThumbGpuCache[itemId] = (bmp, node);
        return bmp;
    }

    private unsafe void UploadToSlot(GpuPhotoSlot slot, DecodedImage? image)
    {
        var savedClip = slot.ClippingSource;
        var savedFc = slot.FalseColorSource;
        var savedProfile = slot.ActiveProfile;
        var savedBrand = slot.ActiveBrand;
        slot.Dispose();
        slot.ActiveProfile = savedProfile;
        slot.ActiveBrand = savedBrand;

        if (image == null || _dc == null) return;

        slot.Source = image;

        if (image.PixelFormat == ImagePixelFormat.Rgba64)
        {
            try
            {
                var bp16 = new BitmapProperties1(
                    new PixelFormat(Format.R16G16B16A16_UNorm, AlphaMode.Premultiplied),
                    96.0f,
                    96.0f,
                    BitmapOptions.None
                );
                fixed (byte* ptr = image.Pixels)
                {
                    slot.Bitmap = _dc.CreateBitmap(
                        new SizeI(image.Width, image.Height),
                        (IntPtr)ptr,
                        (uint)image.Stride,
                        bp16
                    );
                }
            }
            catch
            {
                slot.Bitmap = Convert16To8AndUpload(_dc, image);
            }
        }
        else
        {
            slot.Bitmap = UploadBitmap8BitPremultiplied(_dc, image);
        }

        // Hardware ICC / Wide-Gamut Color Management shader
        var cm = new ColorManagement(_dc);
        cm.SetInput(0, slot.Bitmap, true);

        if (image.IccProfile is { Length: > 128 })
        {
            try
            {
                slot.CustomColorContext = _dc.CreateColorContext(D2DColorSpace.Custom, image.IccProfile, (uint)image.IccProfile.Length);
            }
            catch
            {
                slot.CustomColorContext = null;
            }
        }

        cm.SourceColorContext = slot.CustomColorContext ?? _srgbColorContext;
        cm.DestinationColorContext = _isFp16SwapChain ? _scRgbColorContext : _srgbColorContext;
        cm.AlphaMode = ColorManagementAlphaMode.Premultiplied;
        slot.ColorManagedEffect = cm;

        // Hardware Camera Profile / Film Simulation ColorMatrix shader
        bool canUseColorMgmt = slot.CustomColorContext != null && image.Width <= 4096 && image.Height <= 4096;
        var profMat = new ColorMatrix(_dc)
        {
            AlphaMode = ColorMatrixAlphaMode.Premultiplied,
            Matrix = CameraProfileEngine.ComputeColorMatrix(slot.ActiveProfile, slot.ActiveBrand)
        };
        if (canUseColorMgmt)
            profMat.SetInputEffect(0, cm);
        else
            profMat.SetInput(0, slot.Bitmap, true);
        slot.ProfileMatrixEffect = profMat;

        // GPU Focus Peaking Sobel + Neon Emerald shader graph
        var edge = new EdgeDetection(_dc)
        {
            Strength = 0.60f,
            BlurRadius = 0.0f,
            Mode = EdgeDetectionMode.Sobel,
            OverlayEdges = false,
            AlphaMode = AlphaMode.Premultiplied
        };
        edge.SetInputEffect(0, cm);

        float peakScale = _isFp16SwapChain ? 1.0f : 1.35f;
        var matrix = new ColorMatrix(_dc)
        {
            AlphaMode = ColorMatrixAlphaMode.Premultiplied,
            Matrix = new Matrix5x4(
                0.0f, 0.95f * peakScale, 0.45f * peakScale, 1.1f,
                0.0f, 1.25f * peakScale, 0.60f * peakScale, 1.4f,
                0.0f, 0.55f * peakScale, 0.30f * peakScale, 0.7f,
                0.0f, 0.0f,              0.0f,              0.0f,
                0.0f, -0.22f * peakScale, -0.12f * peakScale, -0.26f
            )
        };
        matrix.SetInputEffect(0, edge);

        slot.EdgeEffect = edge;
        slot.PeakingMatrixEffect = matrix;

        if (savedClip != null)
        {
            slot.ClippingSource = savedClip;
            slot.ClippingBitmap = UploadBitmap8BitPremultiplied(_dc, savedClip);
        }
        if (savedFc != null)
        {
            slot.FalseColorSource = savedFc;
            slot.FalseColorBitmap = UploadBitmap8BitPremultiplied(_dc, savedFc);
        }
    }

    private static unsafe ID2D1Bitmap1 UploadBitmap8BitPremultiplied(ID2D1DeviceContext5 dc, DecodedImage img)
    {
        var bp8 = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
            96.0f,
            96.0f,
            BitmapOptions.None
        );

        bool needsPremul = false;
        fixed (byte* p = img.Pixels)
        {
            int len = img.Height * img.Stride;
            for (int i = 3; i < len; i += 4)
            {
                if (p[i] != 255)
                {
                    needsPremul = true;
                    break;
                }
            }

            if (!needsPremul)
            {
                return dc.CreateBitmap(new SizeI(img.Width, img.Height), (IntPtr)p, (uint)img.Stride, bp8);
            }
        }

        var premul = new byte[img.Pixels.Length];
        fixed (byte* pSrc = img.Pixels)
        fixed (byte* pDst = premul)
        {
            int total = img.Pixels.Length;
            for (int i = 0; i + 3 < total; i += 4)
            {
                uint a = pSrc[i + 3];
                if (a == 255)
                {
                    *(uint*)(pDst + i) = *(uint*)(pSrc + i);
                }
                else if (a > 0)
                {
                    pDst[i] = (byte)((pSrc[i] * a + 127) / 255);
                    pDst[i + 1] = (byte)((pSrc[i + 1] * a + 127) / 255);
                    pDst[i + 2] = (byte)((pSrc[i + 2] * a + 127) / 255);
                    pDst[i + 3] = (byte)a;
                }
            }

            return dc.CreateBitmap(new SizeI(img.Width, img.Height), (IntPtr)pDst, (uint)img.Stride, bp8);
        }
    }

    private static unsafe ID2D1Bitmap1 Convert16To8AndUpload(ID2D1DeviceContext5 dc, DecodedImage img)
    {
        int stride8 = img.Width * 4;
        var bgra = new byte[img.Height * stride8];
        fixed (byte* pSrc = img.Pixels)
        fixed (byte* pDst = bgra)
        {
            for (int y = 0; y < img.Height; y++)
            {
                ushort* sRow = (ushort*)(pSrc + (long)y * img.Stride);
                byte* dRow = pDst + y * stride8;
                for (int x = 0; x < img.Width; x++)
                {
                    dRow[x * 4 + 0] = (byte)(sRow[x * 4 + 2] >> 8);
                    dRow[x * 4 + 1] = (byte)(sRow[x * 4 + 1] >> 8);
                    dRow[x * 4 + 2] = (byte)(sRow[x * 4 + 0] >> 8);
                    dRow[x * 4 + 3] = 255;
                }
            }

            var bp8 = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                96.0f, 96.0f, BitmapOptions.None);
            return dc.CreateBitmap(new SizeI(img.Width, img.Height), (IntPtr)pDst, (uint)stride8, bp8);
        }
    }

    public RawRectF ComputePhotoDestDipRect(DecodedImage img, RawRectF paneDip, RenderHudState state, out float effectivePhysicalScale)
    {
        float paneW = MathF.Max(1f, paneDip.Right - paneDip.Left);
        float paneH = MathF.Max(1f, paneDip.Bottom - paneDip.Top);
        float dpiScale = DpiScale;

        int origW = Math.Max(1, img.OriginalWidth);
        int origH = Math.Max(1, img.OriginalHeight);

        float fitDipScale = MathF.Min(paneW / origW, paneH / origH);
        float fitPhysScale = fitDipScale * dpiScale;

        float targetPhysScale = state.IsCustomZoom ? state.PixelZoomScale : fitPhysScale;
        effectivePhysicalScale = targetPhysScale;

        float drawW = origW * (targetPhysScale / dpiScale);
        float drawH = origH * (targetPhysScale / dpiScale);

        float paneCenterX = (paneDip.Left + paneDip.Right) * 0.5f;
        float paneCenterY = (paneDip.Top + paneDip.Bottom) * 0.5f;

        if (!state.IsCustomZoom || (drawW <= paneW + 0.5f && drawH <= paneH + 0.5f))
        {
            float left = paneCenterX - drawW * 0.5f;
            float top = paneCenterY - drawH * 0.5f;
            return new RawRectF(left, top, left + drawW, top + drawH);
        }

        float normX = Math.Clamp(state.PanNormCenter.X, 0f, 1f);
        float normY = Math.Clamp(state.PanNormCenter.Y, 0f, 1f);

        float imgLeft = paneCenterX - normX * drawW;
        float imgTop = paneCenterY - normY * drawH;

        imgLeft = drawW > paneW
            ? Math.Clamp(imgLeft, paneDip.Right - drawW, paneDip.Left)
            : paneCenterX - drawW * 0.5f;

        imgTop = drawH > paneH
            ? Math.Clamp(imgTop, paneDip.Bottom - drawH, paneDip.Top)
            : paneCenterY - drawH * 0.5f;

        return new RawRectF(imgLeft, imgTop, imgLeft + drawW, imgTop + drawH);
    }

    public void Render(RenderHudState state)
    {
        if (_dc == null || _swapChain == null || _backBufferBitmap == null || _sceneBitmap == null ||
            _d2dFactory == null || _dwrite == null || _hudBlurEffect == null || _fonts == null)
        {
            return;
        }

        float dipW = DipWidth;
        float dipH = DipHeight;
        var theme = GlassTheme.ForMode(state.Theme);

        var paneRects = BottomLayoutMetrics.ComputeMultiUpPaneRects(state.MultiUpMode, dipW, dipH, state.IsSoloLoupeActive);
        var slotDests = new RawRectF[_slots.Length];
        var slotBitmaps = new ID2D1Bitmap1?[_slots.Length];
        var slotImages = new DecodedImage?[_slots.Length];
        for (int i = 0; i < _slots.Length; i++)
        {
            slotBitmaps[i] = _slots[i].Bitmap;
            slotImages[i] = _slots[i].Source;
        }

        _dc.BeginDraw();

        // PASS 1: Render Canvas + Color-Managed Photos + Camera Profiles + Shaders into _sceneBitmap
        _dc.Target = _sceneBitmap;
        _dc.Transform = Matrix3x2.Identity;
        _dc.Clear(theme.CanvasBackground.ToColor4(_isFp16SwapChain));

        if (state.HasPlaylist)
        {
            if (state.IsSoloLoupeActive)
            {
                int soloIdx = Math.Clamp(state.ActiveMultiUpSlot, 0, _slots.Length - 1);
                if (_slots[soloIdx].Source != null && _slots[soloIdx].Bitmap != null)
                {
                    slotDests[soloIdx] = DrawPhotoInPane(_slots[soloIdx], paneRects[0], state, soloIdx, roundedCell: false);
                }
            }
            else if (state.MultiUpMode == MultiUpMode.Single)
            {
                if (_slots[0].Source != null && _slots[0].Bitmap != null)
                {
                    slotDests[0] = DrawPhotoInPane(_slots[0], paneRects[0], state, 0, roundedCell: false);
                }
            }
            else
            {
                int visibleCount = Math.Min((int)state.MultiUpMode, paneRects.Length);
                for (int s = 0; s < visibleCount; s++)
                {
                    var pane = paneRects[s];
                    if (_slots[s].Source != null && _slots[s].Bitmap != null)
                    {
                        slotDests[s] = DrawPhotoInPane(_slots[s], pane, state, s, roundedCell: true);
                    }

                    // Multi-Up glowing active border vs muted inactive cell frame
                    var cellRect = OverlayRenderContext.MakeRounded(
                        pane.Left + 0.5f, pane.Top + 0.5f,
                        MathF.Max(1f, pane.Right - pane.Left - 1f),
                        MathF.Max(1f, pane.Bottom - pane.Top - 1f), 10f);

                    bool isActive = s == state.ActiveMultiUpSlot;
                    if (isActive)
                    {
                        Color4 glowCol = GetSlotAccentRaw(s, 0.30f).ToColor4(_isFp16SwapChain);
                        Color4 rimCol = GetSlotAccentRaw(s, 0.95f).ToColor4(_isFp16SwapChain);
                        using var glowBrush = _dc.CreateSolidColorBrush(glowCol);
                        using var rimBrush = _dc.CreateSolidColorBrush(rimCol);
                        _dc.DrawRoundedRectangle(cellRect, glowBrush, 5.0f);
                        _dc.DrawRoundedRectangle(cellRect, rimBrush, 2.2f);
                    }
                    else
                    {
                        using var idleBrush = _dc.CreateSolidColorBrush(theme.CardBorder.ToColor4(_isFp16SwapChain, 0.55f));
                        _dc.DrawRoundedRectangle(cellRect, idleBrush, 1.0f);
                    }
                }
            }
        }

        // PASS 2: Composite _sceneBitmap + Live Frosted-Glass Vibrancy HUD Overlays onto SwapChain
        _dc.Target = _backBufferBitmap;
        _dc.Transform = Matrix3x2.Identity;
        _dc.DrawBitmap(_sceneBitmap, 1.0f, BitmapInterpolationMode.NearestNeighbor);

        int effPrimarySlot = state.IsSoloLoupeActive ? Math.Clamp(state.ActiveMultiUpSlot, 0, _slots.Length - 1) : 0;
        RawRectF primaryPaneDip = state.IsSoloLoupeActive ? paneRects[0] : paneRects[0];
        RawRectF secondaryPaneDip = (!state.IsSoloLoupeActive && paneRects.Length > 1) ? paneRects[1] : default;

        var overlayCtx = new OverlayRenderContext
        {
            Dc = _dc,
            Factory = _d2dFactory,
            DWrite = _dwrite,
            BlurEffect = _hudBlurEffect,
            Theme = theme,
            Fonts = _fonts,
            IsFp16 = _isFp16SwapChain,
            DipWidth = dipW,
            DipHeight = dipH,
            DpiScale = DpiScale,
            PrimaryImage = _slots[effPrimarySlot].Source,
            SecondaryImage = state.IsSoloLoupeActive ? null : _slots[1].Source,
            PrimaryBitmap = _slots[effPrimarySlot].Bitmap,
            SecondaryBitmap = state.IsSoloLoupeActive ? null : _slots[1].Bitmap,
            PrimaryPaneDip = primaryPaneDip,
            SecondaryPaneDip = secondaryPaneDip,
            PrimaryDestDip = slotDests[effPrimarySlot],
            SecondaryDestDip = state.IsSoloLoupeActive ? default : slotDests[1],
            MultiUpPanesDip = paneRects,
            SlotBitmaps = slotBitmaps,
            SlotImages = slotImages,
            SlotDestsDip = slotDests,
            FaceCropBitmaps = _faceCropBitmaps,
            ResolveMicroThumbBitmap = ResolveMicroThumbBitmap
        };

        if (state.ShowCheatsheet)
        {
            _cheatsheetOverlay.Render(overlayCtx, state);
            _toastOverlay.Render(overlayCtx, state);
        }
        else
        {
            _welcomeOverlay.Render(overlayCtx, state);
            _splitPaneBadgeOverlay.Render(overlayCtx, state);
            _topHudOverlay.Render(overlayCtx, state);
            _bottomHudOverlay.Render(overlayCtx, state);
            _filmstripOverlay.Render(overlayCtx, state);
            _faceDockOverlay.Render(overlayCtx, state);
            _histogramOverlay.Render(overlayCtx, state);
            _falseColorLegendOverlay.Render(overlayCtx, state);
            _exifDossierOverlay.Render(overlayCtx, state);
            _miniMapOverlay.Render(overlayCtx, state);
            _cursorLoupeOverlay.Render(overlayCtx, state);
            _toastOverlay.Render(overlayCtx, state);
        }

        var endRes = _dc.EndDraw();
        var presentRes = _swapChain.Present(1, PresentFlags.None);

        if (endRes.Failure || presentRes.Failure)
        {
            RecreateAllAfterDeviceReset();
        }
    }

    private static Rgba GetSlotAccentRaw(int slotIndex, float alpha) => slotIndex switch
    {
        0 => new Rgba(20, 184, 166, alpha),  // Teal
        1 => new Rgba(245, 158, 11, alpha),  // Amber
        2 => new Rgba(168, 85, 247, alpha),  // Purple
        _ => new Rgba(16, 185, 129, alpha)   // Emerald
    };

    private RawRectF DrawPhotoInPane(
        GpuPhotoSlot slot,
        RawRectF paneDip,
        RenderHudState state,
        int slotIdx,
        bool roundedCell)
    {
        if (_dc == null || _d2dFactory == null || slot.Source == null || slot.Bitmap == null)
        {
            return default;
        }

        ID2D1RoundedRectangleGeometry? cellGeo = null;
        if (roundedCell)
        {
            var rounded = OverlayRenderContext.MakeRounded(
                paneDip.Left, paneDip.Top,
                MathF.Max(1f, paneDip.Right - paneDip.Left),
                MathF.Max(1f, paneDip.Bottom - paneDip.Top), 10f);
            cellGeo = _d2dFactory.CreateRoundedRectangleGeometry(rounded);
            var lp = new LayerParameters1
            {
                ContentBounds = paneDip,
                GeometricMask = cellGeo,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1.0f,
                LayerOptions = LayerOptions1.None
            };
            _dc.PushLayer(ref lp, null);

            // Subtle dark backing card inside Multi-Up cell
            using var cellBg = _dc.CreateSolidColorBrush(new Rgba(14, 15, 19, 1.0f).ToColor4(_isFp16SwapChain));
            _dc.FillRectangle(paneDip, cellBg);
        }
        else
        {
            _dc.PushAxisAlignedClip(paneDip, AntialiasMode.Aliased);
        }

        var dest = ComputePhotoDestDipRect(slot.Source, paneDip, state, out float physScale);
        float destW = dest.Right - dest.Left;
        float destH = dest.Bottom - dest.Top;
        float dpiScale = DpiScale;

        if (state.ShowFalseColor && slot.FalseColorBitmap != null)
        {
            var interpMode = physScale >= 1.95f
                ? BitmapInterpolationMode.NearestNeighbor
                : BitmapInterpolationMode.Linear;
            _dc.DrawBitmap(slot.FalseColorBitmap, dest, 1.0f, interpMode, null);
        }
        else if (slot.ActiveProfile != CameraProfile.AsShot && slot.ProfileMatrixEffect != null)
        {
            float scaleX = (destW * dpiScale) / slot.Source.Width;
            float scaleY = (destH * dpiScale) / slot.Source.Height;
            var interp = physScale >= 1.95f
                ? D2DInterpolationMode.NearestNeighbor
                : D2DInterpolationMode.HighQualityCubic;

            _dc.Transform = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(dest.Left, dest.Top);
            _dc.DrawImage(slot.ProfileMatrixEffect, interp, CompositeMode.SourceOver);
            _dc.Transform = Matrix3x2.Identity;
        }
        else
        {
            bool needsColorManagement = slot.ColorManagedEffect != null && slot.CustomColorContext != null;
            bool useDirectBitmap = !needsColorManagement || slot.Source.Width > 4096 || slot.Source.Height > 4096;

            if (useDirectBitmap)
            {
                var interpMode = physScale >= 1.95f
                    ? BitmapInterpolationMode.NearestNeighbor
                    : BitmapInterpolationMode.Linear;
                _dc.DrawBitmap(slot.Bitmap, dest, 1.0f, interpMode, null);
            }
            else
            {
                float scaleX = (destW * dpiScale) / slot.Source.Width;
                float scaleY = (destH * dpiScale) / slot.Source.Height;
                var interp = physScale >= 1.95f
                    ? D2DInterpolationMode.NearestNeighbor
                    : D2DInterpolationMode.HighQualityCubic;

                _dc.Transform = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(dest.Left, dest.Top);
                _dc.DrawImage(slot.ColorManagedEffect!, interp, CompositeMode.SourceOver);
                _dc.Transform = Matrix3x2.Identity;
            }
        }

        // GPU Focus Peaking (Shift + F) across all visible slots
        if (state.ShowFocusPeaking && slot.PeakingMatrixEffect != null)
        {
            float scaleX = (destW * dpiScale) / slot.Source.Width;
            float scaleY = (destH * dpiScale) / slot.Source.Height;
            _dc.Transform = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(dest.Left, dest.Top);
            _dc.DrawImage(slot.PeakingMatrixEffect, D2DInterpolationMode.Linear, CompositeMode.SourceOver);
            _dc.Transform = Matrix3x2.Identity;
        }

        // Exposure Clipping Blinkies (O) across visible slots
        if (state.ShowClipping && !state.ShowFalseColor && slot.ClippingBitmap != null)
        {
            var interpMode = physScale >= 1.95f
                ? BitmapInterpolationMode.NearestNeighbor
                : BitmapInterpolationMode.Linear;
            _dc.DrawBitmap(slot.ClippingBitmap, dest, state.ClippingPulseAlpha, interpMode, null);
        }

        // AI Auto Eye Reticle (Shift + Z / Face Dock click)
        if (slotIdx == state.EyeReticleSlot && state.EyeReticleOpacity > 0.01f && state.EyeReticlePoint is { } eyePt)
        {
            float ex = dest.Left + eyePt.X * destW;
            float ey = dest.Top + eyePt.Y * destH;
            float alpha = state.EyeReticleOpacity;

            using var outerGlow = _dc.CreateSolidColorBrush(new Rgba(0x00, 0xE5, 0xFF, 0.30f * alpha).ToColor4(_isFp16SwapChain));
            using var ringBrush = _dc.CreateSolidColorBrush(new Rgba(0x40, 0xEC, 0xFF, 0.92f * alpha).ToColor4(_isFp16SwapChain));

            float r = 22f + (1f - alpha) * 8f;
            _dc.DrawEllipse(new Ellipse(new Vector2(ex, ey), r + 2f, r + 2f), outerGlow, 4.0f);
            _dc.DrawEllipse(new Ellipse(new Vector2(ex, ey), r, r), ringBrush, 1.6f);

            _dc.DrawLine(new Vector2(ex - r - 7f, ey), new Vector2(ex - r + 3f, ey), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex + r - 3f, ey), new Vector2(ex + r + 7f, ey), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex, ey - r - 7f), new Vector2(ex, ey - r + 3f), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex, ey + r - 3f), new Vector2(ex, ey + r + 7f), ringBrush, 1.4f);
        }

        PickStatus slotPick = state.MultiUpMode == MultiUpMode.Single
            ? state.PrimaryCulling.Pick
            : state.MultiUpSlots[slotIdx].Culling.Pick;

        if (slotPick == PickStatus.Reject)
        {
            using var rejDim = _dc.CreateSolidColorBrush(new Rgba(0x00, 0x00, 0x00, 0.32f).ToColor4(_isFp16SwapChain));
            _dc.FillRectangle(dest, rejDim);
        }

        if (roundedCell)
        {
            _dc.PopLayer();
            cellGeo?.Dispose();
        }
        else
        {
            _dc.PopAxisAlignedClip();
        }

        return dest;
    }

    private void DisposeFaceCropBitmaps()
    {
        foreach (var b in _faceCropBitmaps)
        {
            b?.Dispose();
        }
        _faceCropBitmaps.Clear();
    }

    private void RecreateAllAfterDeviceReset()
    {
        var savedSources = new (DecodedImage? Image, DecodedImage? Clip, DecodedImage? Fc, CameraProfile Prof, CameraBrand Brand)[_slots.Length];
        for (int i = 0; i < _slots.Length; i++)
        {
            savedSources[i] = (_slots[i].Source, _slots[i].ClippingSource, _slots[i].FalseColorSource, _slots[i].ActiveProfile, _slots[i].ActiveBrand);
            _slots[i].Dispose();
        }

        var savedFaces = _faceTilesSource;
        _faceTilesSource = Array.Empty<FaceCropTile>();
        DisposeFaceCropBitmaps();
        ClearMicroThumbGpuCache();

        CreateDeviceResources();
        CreateWindowSizeResources();

        for (int i = 0; i < _slots.Length; i++)
        {
            if (savedSources[i].Image != null)
            {
                _slots[i].ActiveProfile = savedSources[i].Prof;
                _slots[i].ActiveBrand = savedSources[i].Brand;
                UploadToSlot(_slots[i], savedSources[i].Image);
            }
            if (savedSources[i].Clip != null) SetSlotClippingMask(i, savedSources[i].Clip);
            if (savedSources[i].Fc != null) SetSlotFalseColor(i, savedSources[i].Fc);
        }

        if (savedFaces.Count > 0)
        {
            SetFaceCropImages(savedFaces);
        }
    }

    private void DisposeDeviceResources()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Dispose();
        }
        DisposeFaceCropBitmaps();
        ClearMicroThumbGpuCache();

        _hudBlurEffect?.Dispose();
        _hudBlurEffect = null;
        _backBufferBitmap?.Dispose();
        _backBufferBitmap = null;
        _sceneBitmap?.Dispose();
        _sceneBitmap = null;
        _srgbColorContext?.Dispose();
        _srgbColorContext = null;
        _scRgbColorContext?.Dispose();
        _scRgbColorContext = null;

        _swapChain?.Dispose();
        _swapChain = null;
        _dc?.Dispose();
        _dc = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;
        _d3dContext?.Dispose();
        _d3dContext = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    public void Dispose()
    {
        DisposeDeviceResources();
        _fonts?.Dispose();
        _fonts = null;
        _dwrite?.Dispose();
        _dwrite = null;
    }
}
