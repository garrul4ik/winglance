using System;
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
using WinGlance.Model;
using WinGlance.Vision;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DColorSpace = Vortice.Direct2D1.ColorSpace;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace WinGlance.Gpu;

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

    public CullingFilter ActiveFilter { get; set; } = CullingFilter.All;
    public bool IsSplitCompare { get; set; }
    public bool IsSlideshowPlaying { get; set; }
    public int SlideshowSeconds { get; set; } = 3;
    public bool ShowHistogram { get; set; }
    public HistogramData? Histogram { get; set; }
    public bool ShowClipping { get; set; }
    public float ClippingPulseAlpha { get; set; } = 0.85f;
    public bool ShowFocusPeaking { get; set; }
    public bool ShowCheatsheet { get; set; }
    public bool IsLoadingFullRaw { get; set; }

    // Pan & Zoom state (shared across split panes)
    public bool IsCustomZoom { get; set; }
    public float PixelZoomScale { get; set; } = 1.0f; // 1.0 = exact 1:1 physical sensor pixel
    public Vector2 PanNormCenter { get; set; } = new(0.5f, 0.5f);

    // Animated HUD & AI Eye Reticle
    public float HudOpacity { get; set; } = 1.0f;
    public string? ToastText { get; set; }
    public float ToastOpacity { get; set; }
    public NormalizedPoint? EyeReticlePoint { get; set; }
    public NormalizedRect? EyeFaceRect { get; set; }
    public float EyeReticleOpacity { get; set; }
}

public sealed class D2DRenderEngine : IDisposable
{
    private sealed class GpuPhotoSlot : IDisposable
    {
        public DecodedImage? Source;
        public ID2D1Bitmap1? Bitmap;
        public ID2D1ColorContext? CustomColorContext;
        public ColorManagement? ColorManagedEffect;
        public EdgeDetection? EdgeEffect;
        public ColorMatrix? PeakingMatrixEffect;

        public void Dispose()
        {
            PeakingMatrixEffect?.Dispose();
            EdgeEffect?.Dispose();
            ColorManagedEffect?.Dispose();
            CustomColorContext?.Dispose();
            Bitmap?.Dispose();
            PeakingMatrixEffect = null;
            EdgeEffect = null;
            ColorManagedEffect = null;
            CustomColorContext = null;
            Bitmap = null;
            Source = null;
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

    private readonly GpuPhotoSlot _primarySlot = new();
    private readonly GpuPhotoSlot _secondarySlot = new();
    private DecodedImage? _clippingSource;
    private ID2D1Bitmap1? _clippingBitmap;

    // Typography formats
    private IDWriteTextFormat? _fontHero;
    private IDWriteTextFormat? _fontToast;
    private IDWriteTextFormat? _fontUiBold;
    private IDWriteTextFormat? _fontUiRegular;
    private IDWriteTextFormat? _fontMonoSmall;
    private IDWriteTextFormat? _fontCheatHeader;

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

        _fontHero?.Dispose();
        _fontToast?.Dispose();
        _fontUiBold?.Dispose();
        _fontUiRegular?.Dispose();
        _fontMonoSmall?.Dispose();
        _fontCheatHeader?.Dispose();

        _fontHero = _dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.SemiBold, FontStyle.Normal, 28.0f);
        _fontHero.TextAlignment = TextAlignment.Center;

        _fontToast = _dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.SemiBold, FontStyle.Normal, 20.0f);
        _fontToast.TextAlignment = TextAlignment.Center;
        _fontToast.ParagraphAlignment = ParagraphAlignment.Center;

        _fontUiBold = _dwrite.CreateTextFormat("Segoe UI Variable Text", FontWeight.SemiBold, FontStyle.Normal, 13.0f);
        _fontUiBold.ParagraphAlignment = ParagraphAlignment.Center;
        _fontUiBold.WordWrapping = WordWrapping.NoWrap;

        _fontUiRegular = _dwrite.CreateTextFormat("Segoe UI Variable Text", FontWeight.Normal, FontStyle.Normal, 12.0f);
        _fontUiRegular.ParagraphAlignment = ParagraphAlignment.Center;
        _fontUiRegular.WordWrapping = WordWrapping.NoWrap;

        _fontMonoSmall = _dwrite.CreateTextFormat("Cascadia Mono", FontWeight.SemiBold, FontStyle.Normal, 11.0f);
        _fontMonoSmall.ParagraphAlignment = ParagraphAlignment.Center;
        _fontMonoSmall.WordWrapping = WordWrapping.NoWrap;

        _fontCheatHeader = _dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.Bold, FontStyle.Normal, 15.0f);
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

            // Prefer 16-bit floating-point linear scRGB Flip-Model SwapChain (Wide Color Gamut & HDR/EDR)
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

    public void SetPrimaryImage(DecodedImage? image)
    {
        if (ReferenceEquals(_primarySlot.Source, image)) return;
        UploadToSlot(_primarySlot, image);
    }

    public void SetSecondaryImage(DecodedImage? image)
    {
        if (ReferenceEquals(_secondarySlot.Source, image)) return;
        UploadToSlot(_secondarySlot, image);
    }

    public void SetClippingMask(DecodedImage? mask)
    {
        if (ReferenceEquals(_clippingSource, mask)) return;
        _clippingBitmap?.Dispose();
        _clippingBitmap = null;
        _clippingSource = mask;

        if (mask != null && _dc != null)
        {
            _clippingBitmap = UploadBitmap8BitPremultiplied(_dc, mask);
        }
    }

    private unsafe void UploadToSlot(GpuPhotoSlot slot, DecodedImage? image)
    {
        slot.Dispose();
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
                // Map edge intensity into crisp neon emerald-cyan highlights with noise-floor threshold (-0.22)
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
    }

    private static unsafe ID2D1Bitmap1 UploadBitmap8BitPremultiplied(ID2D1DeviceContext5 dc, DecodedImage img)
    {
        var bp8 = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
            96.0f,
            96.0f,
            BitmapOptions.None
        );

        // Quick scan to check if any alpha < 255 requires premultiplication
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

        if (drawW > paneW)
        {
            imgLeft = Math.Clamp(imgLeft, paneDip.Right - drawW, paneDip.Left);
        }
        else
        {
            imgLeft = paneCenterX - drawW * 0.5f;
        }

        if (drawH > paneH)
        {
            imgTop = Math.Clamp(imgTop, paneDip.Bottom - drawH, paneDip.Top);
        }
        else
        {
            imgTop = paneCenterY - drawH * 0.5f;
        }

        return new RawRectF(imgLeft, imgTop, imgLeft + drawW, imgTop + drawH);
    }

    public void Render(RenderHudState state)
    {
        if (_dc == null || _swapChain == null || _backBufferBitmap == null || _sceneBitmap == null) return;

        float dipW = DipWidth;
        float dipH = DipHeight;

        _dc.BeginDraw();

        // =========================================================================
        // PASS 1: Render Obsidian Canvas + Color-Managed Photos + Shaders into _sceneBitmap
        // =========================================================================
        _dc.Target = _sceneBitmap;
        _dc.Transform = Matrix3x2.Identity;
        _dc.Clear(UiColor(0x0A, 0x0A, 0x0C));

        if (state.HasPlaylist && _primarySlot.Source != null && _primarySlot.Bitmap != null)
        {
            if (state.IsSplitCompare && _secondarySlot.Source != null && _secondarySlot.Bitmap != null)
            {
                float halfW = dipW * 0.5f;
                var leftPane = new RawRectF(0, 0, halfW - 1f, dipH);
                var rightPane = new RawRectF(halfW + 1f, 0, dipW, dipH);

                DrawPhotoInPane(_primarySlot, leftPane, state, isPrimaryPane: true);
                DrawPhotoInPane(_secondarySlot, rightPane, state, isPrimaryPane: false);

                using var divBrush = _dc.CreateSolidColorBrush(UiColor(255, 255, 255, 0.22f));
                _dc.DrawLine(new Vector2(halfW, 0), new Vector2(halfW, dipH), divBrush, 1.5f);
            }
            else
            {
                var fullPane = new RawRectF(0, 0, dipW, dipH);
                DrawPhotoInPane(_primarySlot, fullPane, state, isPrimaryPane: true);
            }
        }

        // =========================================================================
        // PASS 2: Composite _sceneBitmap + Live Frosted-Glass Vibrancy HUD onto SwapChain
        // =========================================================================
        _dc.Target = _backBufferBitmap;
        _dc.Transform = Matrix3x2.Identity;
        _dc.DrawBitmap(_sceneBitmap, 1.0f, BitmapInterpolationMode.NearestNeighbor);

        if (!state.HasPlaylist)
        {
            DrawWelcomeCard(dipW, dipH);
        }
        else
        {
            if (state.HudOpacity > 0.01f)
            {
                DrawTopHud(dipW, state);
                DrawBottomHud(dipW, dipH, state);
                if (state.IsSplitCompare && state.SecondaryItem != null)
                {
                    DrawSecondaryPaneBadge(dipW, state);
                }
            }

            if (state.ShowHistogram && state.Histogram != null)
            {
                DrawHistogramCard(dipW, dipH, state.Histogram);
            }

            if (state.ToastOpacity > 0.01f && !string.IsNullOrEmpty(state.ToastText))
            {
                DrawToastCard(dipW, dipH, state.ToastText!, state.ToastOpacity);
            }
        }

        if (state.ShowCheatsheet)
        {
            DrawCheatsheetModal(dipW, dipH);
        }

        var endRes = _dc.EndDraw();
        var presentRes = _swapChain.Present(1, PresentFlags.None);

        if (endRes.Failure || presentRes.Failure)
        {
            RecreateAllAfterDeviceReset();
        }
    }

    private void DrawPhotoInPane(GpuPhotoSlot slot, RawRectF paneDip, RenderHudState state, bool isPrimaryPane)
    {
        if (_dc == null || slot.Source == null || slot.Bitmap == null || slot.ColorManagedEffect == null) return;

        _dc.PushAxisAlignedClip(paneDip, AntialiasMode.Aliased);

        var dest = ComputePhotoDestDipRect(slot.Source, paneDip, state, out float physScale);
        float destW = dest.Right - dest.Left;
        float destH = dest.Bottom - dest.Top;
        float dpiScale = DpiScale;

        bool needsColorManagement = slot.ColorManagedEffect != null && slot.CustomColorContext != null;
        // Direct DrawBitmap bypasses D2D intermediate effect tiling limits (>4096) and provides maximum hardware throughput for sRGB
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

            var imgTransform = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(dest.Left, dest.Top);
            _dc.Transform = imgTransform;
            _dc.DrawImage(slot.ColorManagedEffect!, interp, CompositeMode.SourceOver);
            _dc.Transform = Matrix3x2.Identity;
        }

        // GPU Focus Peaking (Shift + F)
        if (state.ShowFocusPeaking && slot.PeakingMatrixEffect != null)
        {
            float scaleX = (destW * dpiScale) / slot.Source.Width;
            float scaleY = (destH * dpiScale) / slot.Source.Height;
            var imgTransform = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(dest.Left, dest.Top);
            _dc.Transform = imgTransform;
            _dc.DrawImage(slot.PeakingMatrixEffect, D2DInterpolationMode.Linear, CompositeMode.SourceOver);
            _dc.Transform = Matrix3x2.Identity;
        }

        // Exposure Clipping Blinkies (O) on primary pane
        if (isPrimaryPane && state.ShowClipping && _clippingBitmap != null)
        {
            var interpMode = physScale >= 1.95f
                ? BitmapInterpolationMode.NearestNeighbor
                : BitmapInterpolationMode.Linear;
            _dc.DrawBitmap(
                _clippingBitmap,
                dest,
                state.ClippingPulseAlpha,
                interpMode,
                null
            );
        }

        // AI Auto Eye Reticle (Shift + Z)
        if (isPrimaryPane && state.EyeReticleOpacity > 0.01f && state.EyeReticlePoint is { } eyePt)
        {
            float ex = dest.Left + eyePt.X * destW;
            float ey = dest.Top + eyePt.Y * destH;
            float alpha = state.EyeReticleOpacity;

            using var outerGlow = _dc.CreateSolidColorBrush(UiColor(0x00, 0xE5, 0xFF, 0.30f * alpha));
            using var ringBrush = _dc.CreateSolidColorBrush(UiColor(0x40, 0xEC, 0xFF, 0.92f * alpha));

            float r = 22f + (1f - alpha) * 8f;
            _dc.DrawEllipse(new Ellipse(new Vector2(ex, ey), r + 2f, r + 2f), outerGlow, 4.0f);
            _dc.DrawEllipse(new Ellipse(new Vector2(ex, ey), r, r), ringBrush, 1.6f);

            // Subtle crosshairs outside iris
            _dc.DrawLine(new Vector2(ex - r - 7f, ey), new Vector2(ex - r + 3f, ey), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex + r - 3f, ey), new Vector2(ex + r + 7f, ey), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex, ey - r - 7f), new Vector2(ex, ey - r + 3f), ringBrush, 1.4f);
            _dc.DrawLine(new Vector2(ex, ey + r - 3f), new Vector2(ex, ey + r + 7f), ringBrush, 1.4f);
        }

        // Rejected photo subtle dim indicator border when unflagged toast fades
        if ((isPrimaryPane ? state.PrimaryCulling.Pick : state.SecondaryCulling.Pick) == PickStatus.Reject)
        {
            using var rejDim = _dc.CreateSolidColorBrush(UiColor(0x00, 0x00, 0x00, 0.28f));
            _dc.FillRectangle(dest, rejDim);
        }

        _dc.PopAxisAlignedClip();
    }

    private void BeginFrostedCard(RoundedRectangle card, float opacity, float darkTintAlpha = 0.62f)
    {
        if (_dc == null || _d2dFactory == null || _hudBlurEffect == null) return;

        using var geo = _d2dFactory.CreateRoundedRectangleGeometry(card);
        var layerParams = new LayerParameters1
        {
            ContentBounds = card.Rect,
            GeometricMask = geo,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions1.None
        };

        _dc.PushLayer(ref layerParams, null);
        _dc.DrawImage(_hudBlurEffect, D2DInterpolationMode.Linear, CompositeMode.SourceCopy);

        using var tintBrush = _dc.CreateSolidColorBrush(UiColor(0x10, 0x11, 0x15, darkTintAlpha));
        _dc.FillRectangle(card.Rect, tintBrush);
    }

    private void EndFrostedCard(RoundedRectangle card, float opacity, Color4? accentBorder = null)
    {
        if (_dc == null) return;
        _dc.PopLayer();

        var borderCol = accentBorder ?? UiColor(255, 255, 255, 0.16f * opacity);
        using var borderBrush = _dc.CreateSolidColorBrush(borderCol);
        _dc.DrawRoundedRectangle(card, borderBrush, 1.0f);
    }

    private void DrawTopHud(float dipW, RenderHudState state)
    {
        if (_dc == null || _fontUiBold == null || _fontMonoSmall == null) return;
        float alpha = state.HudOpacity;

        // Left status pill
        string counter = $"{state.CurrentIndex + 1} / {state.TotalCount}";
        string extra = "";
        if (state.ActiveFilter.Type != CullingFilterType.All) extra += $"  ·  {state.ActiveFilter.Title}";
        if (state.IsSlideshowPlaying) extra += $"  ·  ▶ {state.SlideshowSeconds}s";
        if (state.ShowFocusPeaking) extra += "  ·  PEAK";
        if (state.ShowClipping) extra += "  ·  BLINK";
        if (_isFp16SwapChain) extra += "  ·  HDR16";
        if (state.IsLoadingFullRaw) extra += "  ·  16-BIT DEMOSAIC…";

        string leftText = counter + extra;
        float leftW = Math.Clamp(MeasureWidth(leftText, _fontUiBold) + 30f, 94f, dipW * 0.62f);
        var leftCard = MakeRounded(20f, 18f, leftW, 34f, 11f);

        BeginFrostedCard(leftCard, alpha);
        using (var textBrush = _dc.CreateSolidColorBrush(UiColor(245, 246, 250, 0.95f)))
        {
            _dc.DrawText(leftText, _fontUiBold, new Rect(leftCard.Rect.Left + 14f, leftCard.Rect.Top, leftW - 28f, 34f), textBrush);
        }
        EndFrostedCard(leftCard, alpha);

        // Right culling status pill (if Pick/Reject/Rating/Color set)
        var cull = state.PrimaryCulling;
        string cullText = BuildCullingSummary(cull);
        if (!string.IsNullOrEmpty(cullText) || cull.Color != ColorLabel.None)
        {
            float dotExtra = cull.Color != ColorLabel.None ? 20f : 0f;
            float rightW = Math.Max(56f, MeasureWidth(cullText, _fontUiBold) + 28f + dotExtra);
            var rightCard = MakeRounded(dipW - rightW - 20f, 18f, rightW, 34f, 11f);

            Color4? accent = cull.Pick switch
            {
                PickStatus.Pick => UiColor(0x4C, 0xAF, 0x50, 0.55f * alpha),
                PickStatus.Reject => UiColor(0xEF, 0x53, 0x50, 0.55f * alpha),
                _ => null
            };

            BeginFrostedCard(rightCard, alpha);
            float tx = rightCard.Rect.Left + 14f;

            if (cull.Color != ColorLabel.None)
            {
                using var dotBrush = _dc.CreateSolidColorBrush(ColorForLabel(cull.Color, 0.95f));
                _dc.FillEllipse(new Ellipse(new Vector2(tx + 5f, rightCard.Rect.Top + 17f), 5.5f, 5.5f), dotBrush);
                tx += 18f;
            }

            if (!string.IsNullOrEmpty(cullText))
            {
                var fg = cull.Pick == PickStatus.Pick
                    ? UiColor(0x81, 0xC7, 0x84, 0.98f)
                    : cull.Pick == PickStatus.Reject
                        ? UiColor(0xEF, 0x9A, 0x9A, 0.98f)
                        : UiColor(0xFF, 0xD5, 0x4F, 0.98f);
                using var fgBrush = _dc.CreateSolidColorBrush(fg);
                _dc.DrawText(cullText, _fontUiBold, new Rect(tx, rightCard.Rect.Top, rightW - 20f, 34f), fgBrush);
            }

            EndFrostedCard(rightCard, alpha, accent);
        }
    }

    private void DrawSecondaryPaneBadge(float dipW, RenderHudState state)
    {
        if (_dc == null || _fontUiBold == null || state.SecondaryItem == null) return;
        float alpha = state.HudOpacity;

        string secCull = BuildCullingSummary(state.SecondaryCulling);
        string label = $"COMPARE #{state.SecondaryIndex + 1}  ·  {state.SecondaryItem.DisplayName}";
        if (!string.IsNullOrEmpty(secCull)) label += $"  ·  {secCull}";

        float w = Math.Clamp(MeasureWidth(label, _fontUiBold) + 28f, 160f, dipW * 0.44f);
        var card = MakeRounded(dipW * 0.5f + 16f, 18f, w, 34f, 11f);

        BeginFrostedCard(card, alpha);
        using (var brush = _dc.CreateSolidColorBrush(UiColor(0x90, 0xCA, 0xF9, 0.95f)))
        {
            _dc.DrawText(label, _fontUiBold, new Rect(card.Rect.Left + 14f, card.Rect.Top, w - 28f, 34f), brush);
        }
        EndFrostedCard(card, alpha);
    }

    private void DrawBottomHud(float dipW, float dipH, RenderHudState state)
    {
        if (_dc == null || _fontUiBold == null || _fontUiRegular == null || state.PrimaryItem == null) return;
        var img = _primarySlot.Source;
        float alpha = state.HudOpacity;

        string zoomTag = !state.IsCustomZoom
            ? "FIT"
            : MathF.Abs(state.PixelZoomScale - 1.0f) < 0.03f
                ? "100% 1:1"
                : $"{(int)MathF.Round(state.PixelZoomScale * 100f)}%";

        string formatBadge = img == null
            ? ""
            : img.IsFullSensorRaw
                ? "RAW 16-bit"
                : img.IsRaw
                    ? "RAW Preview"
                    : img.BitsPerChannel == 16 ? "16-bit" : "8-bit";

        string colorBadge = img?.ColorProfileName ?? "sRGB";
        string dims = img != null ? $"{img.OriginalWidth}×{img.OriginalHeight}" : "";
        string sizeStr = FormatBytes(state.PrimaryItem.TotalStackSize);

        string line1 = $"{state.PrimaryItem.DisplayName}   ·   {dims}   ·   {sizeStr}   ·   {formatBadge}   ·   {colorBadge}   ·   {zoomTag}";

        string line2 = "";
        if (img?.Exif != null)
        {
            var cam = img.Exif.CameraAndLensSummary;
            var exp = img.Exif.ExposureSummary;
            if (!string.IsNullOrEmpty(cam) && !string.IsNullOrEmpty(exp)) line2 = $"{cam}   ·   {exp}";
            else line2 = !string.IsNullOrEmpty(cam) ? cam : exp;
        }

        bool hasLine2 = !string.IsNullOrWhiteSpace(line2);
        float w1 = MeasureWidth(line1, _fontUiBold);
        float w2 = hasLine2 ? MeasureWidth(line2, _fontUiRegular) : 0f;
        float cardW = Math.Clamp(MathF.Max(w1, w2) + 40f, 280f, dipW - 40f);
        float cardH = hasLine2 ? 54f : 36f;

        var card = MakeRounded((dipW - cardW) * 0.5f, dipH - cardH - 18f, cardW, cardH, 13f);
        BeginFrostedCard(card, alpha);

        using var primaryText = _dc.CreateSolidColorBrush(UiColor(246, 247, 250, 0.96f));
        if (hasLine2)
        {
            _dc.DrawText(line1, _fontUiBold, new Rect(card.Rect.Left + 20f, card.Rect.Top + 4f, cardW - 40f, 24f), primaryText);
            using var subText = _dc.CreateSolidColorBrush(UiColor(185, 192, 206, 0.88f));
            _dc.DrawText(line2, _fontUiRegular, new Rect(card.Rect.Left + 20f, card.Rect.Top + 26f, cardW - 40f, 22f), subText);
        }
        else
        {
            _dc.DrawText(line1, _fontUiBold, new Rect(card.Rect.Left + 20f, card.Rect.Top, cardW - 40f, cardH), primaryText);
        }

        EndFrostedCard(card, alpha);
    }

    private void DrawHistogramCard(float dipW, float dipH, HistogramData hist)
    {
        if (_dc == null || _d2dFactory == null || _fontMonoSmall == null) return;

        const float cardW = 264f;
        const float cardH = 122f;
        var card = MakeRounded(dipW - cardW - 20f, dipH - cardH - 84f, cardW, cardH, 12f);

        BeginFrostedCard(card, 0.96f, darkTintAlpha: 0.72f);

        using (var titleBrush = _dc.CreateSolidColorBrush(UiColor(190, 198, 212, 0.80f)))
        {
            _dc.DrawText("RGB / LUMA HISTOGRAM", _fontMonoSmall, new Rect(card.Rect.Left + 12f, card.Rect.Top + 4f, cardW - 24f, 18f), titleBrush);
        }

        float plotLeft = card.Rect.Left + 10f;
        float plotRight = card.Rect.Right - 10f;
        float plotTop = card.Rect.Top + 24f;
        float plotBottom = card.Rect.Bottom - 10f;
        float plotW = plotRight - plotLeft;
        float plotH = plotBottom - plotTop;

        // Subtle 25% / 50% / 75% vertical grid lines
        using (var gridBrush = _dc.CreateSolidColorBrush(UiColor(255, 255, 255, 0.07f)))
        {
            for (int g = 1; g <= 3; g++)
            {
                float gx = plotLeft + plotW * (g * 0.25f);
                _dc.DrawLine(new Vector2(gx, plotTop), new Vector2(gx, plotBottom), gridBrush, 1.0f);
            }
        }

        // Ignore extreme 0 and 255 spikes when normalizing peak so midtones stay readable
        int normPeak = 1;
        for (int i = 2; i < 254; i++)
        {
            if (hist.Red[i] > normPeak) normPeak = hist.Red[i];
            if (hist.Green[i] > normPeak) normPeak = hist.Green[i];
            if (hist.Blue[i] > normPeak) normPeak = hist.Blue[i];
        }
        if (normPeak <= 1) normPeak = Math.Max(1, hist.MaxCount);

        DrawHistogramChannel(hist.Red, normPeak, plotLeft, plotBottom, plotW, plotH, UiColor(0xFF, 0x45, 0x55, 0.36f), UiColor(0xFF, 0x5C, 0x6C, 0.88f));
        DrawHistogramChannel(hist.Green, normPeak, plotLeft, plotBottom, plotW, plotH, UiColor(0x38, 0xE5, 0x7A, 0.34f), UiColor(0x52, 0xF2, 0x8E, 0.85f));
        DrawHistogramChannel(hist.Blue, normPeak, plotLeft, plotBottom, plotW, plotH, UiColor(0x3A, 0x9E, 0xFF, 0.36f), UiColor(0x58, 0xB2, 0xFF, 0.88f));
        DrawHistogramChannel(hist.Luminance, normPeak, plotLeft, plotBottom, plotW, plotH, UiColor(255, 255, 255, 0.12f), UiColor(255, 255, 255, 0.72f));

        EndFrostedCard(card, 0.96f);
    }

    private void DrawHistogramChannel(
        int[] bins,
        int peak,
        float left,
        float bottom,
        float width,
        float height,
        Color4 fillColor,
        Color4 strokeColor)
    {
        if (_dc == null || _d2dFactory == null) return;

        using var path = _d2dFactory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(left, bottom), FigureBegin.Filled);
            for (int i = 0; i < 256; i += 2)
            {
                int val = (bins[i] + bins[Math.Min(255, i + 1)]) >> 1;
                float norm = Math.Clamp((float)val / peak, 0f, 1f);
                // Mild gamma lift (sqrt) so shadow/midtone distributions remain clear
                float y = bottom - MathF.Pow(norm, 0.72f) * height;
                float x = left + (i / 255.0f) * width;
                sink.AddLine(new Vector2(x, y));
            }
            sink.AddLine(new Vector2(left + width, bottom));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        using var fill = _dc.CreateSolidColorBrush(fillColor);
        using var stroke = _dc.CreateSolidColorBrush(strokeColor);
        _dc.FillGeometry(path, fill);
        _dc.DrawGeometry(path, stroke, 1.15f);
    }

    private void DrawToastCard(float dipW, float dipH, string text, float opacity)
    {
        if (_dc == null || _fontToast == null) return;

        float w = Math.Clamp(MeasureWidth(text, _fontToast) + 40f, 130f, dipW - 60f);
        const float h = 44f;
        float topY = Math.Min(62f, dipH * 0.15f);
        var card = MakeRounded((dipW - w) * 0.5f, topY, w, h, 14f);

        BeginFrostedCard(card, opacity, darkTintAlpha: 0.76f);
        using (var brush = _dc.CreateSolidColorBrush(UiColor(255, 255, 255, 0.98f)))
        {
            _dc.DrawText(text, _fontToast, new Rect(card.Rect.Left + 12f, card.Rect.Top, w - 24f, h), brush);
        }
        EndFrostedCard(card, opacity);
    }

    private void DrawWelcomeCard(float dipW, float dipH)
    {
        if (_dc == null || _fontHero == null || _fontUiBold == null || _fontUiRegular == null || _fontMonoSmall == null) return;

        float w = Math.Min(540f, dipW - 48f);
        const float h = 230f;
        var card = MakeRounded((dipW - w) * 0.5f, (dipH - h) * 0.5f, w, h, 20f);

        BeginFrostedCard(card, 1.0f, darkTintAlpha: 0.78f);

        using var white = _dc.CreateSolidColorBrush(UiColor(248, 250, 255, 0.98f));
        using var muted = _dc.CreateSolidColorBrush(UiColor(175, 184, 200, 0.90f));
        using var accent = _dc.CreateSolidColorBrush(UiColor(0x64, 0xB5, 0xF6, 0.95f));

        _dc.DrawText("WinGlance 1.2", _fontHero, new Rect(card.Rect.Left, card.Rect.Top + 28f, w, 40f), white);

        string sub = "Drop a folder or RAW / photo anywhere, or press Ctrl+O to browse";
        float subW = MeasureWidth(sub, _fontUiBold);
        _dc.DrawText(sub, _fontUiBold, new Rect(card.Rect.Left + (w - subW) * 0.5f, card.Rect.Top + 78f, subW + 10f, 28f), muted);

        string stack = "Direct3D 11  ·  Direct2D 1.3 HDR16  ·  ICC WCG  ·  16-bit LibRaw  ·  DirectML AI";
        float stackW = MeasureWidth(stack, _fontMonoSmall);
        _dc.DrawText(stack, _fontMonoSmall, new Rect(card.Rect.Left + (w - stackW) * 0.5f, card.Rect.Top + 122f, stackW + 10f, 24f), accent);

        string hint = "Press ? for shortcuts   ·   Esc to exit   ·   Windows port of Glance by @mortalezz";
        float hintW = MeasureWidth(hint, _fontUiRegular);
        _dc.DrawText(hint, _fontUiRegular, new Rect(card.Rect.Left + (w - hintW) * 0.5f, card.Rect.Top + 174f, hintW + 10f, 24f), muted);

        EndFrostedCard(card, 1.0f);
    }

    private void DrawCheatsheetModal(float dipW, float dipH)
    {
        if (_dc == null || _fontCheatHeader == null || _fontUiBold == null || _fontUiRegular == null || _fontMonoSmall == null) return;

        using (var backdrop = _dc.CreateSolidColorBrush(UiColor(0, 0, 0, 0.45f)))
        {
            _dc.FillRectangle(new RawRectF(0, 0, dipW, dipH), backdrop);
        }

        float w = Math.Min(720f, dipW - 40f);
        float h = Math.Min(470f, dipH - 40f);
        var card = MakeRounded((dipW - w) * 0.5f, (dipH - h) * 0.5f, w, h, 18f);

        BeginFrostedCard(card, 0.99f, darkTintAlpha: 0.84f);

        using var headerBrush = _dc.CreateSolidColorBrush(UiColor(250, 251, 255, 0.98f));
        using var keyBrush = _dc.CreateSolidColorBrush(UiColor(0x80, 0xD8, 0xFF, 0.96f));
        using var descBrush = _dc.CreateSolidColorBrush(UiColor(210, 216, 228, 0.92f));
        using var footerBrush = _dc.CreateSolidColorBrush(UiColor(150, 160, 178, 0.88f));

        _dc.DrawText("WinGlance 1.2  —  Pro Culling, GPU & AI Vision Shortcuts", _fontCheatHeader,
            new Rect(card.Rect.Left + 26f, card.Rect.Top + 20f, w - 52f, 28f), headerBrush);

        (string Key, string Desc)[] leftCol =
        {
            ("← / → / Wheel", "Previous / Next photo"),
            ("Home / End", "First / Last photo"),
            ("Space / [ ]", "Slideshow Play/Pause & Speed"),
            ("F", "Toggle Fullscreen / Windowed"),
            ("P / X / U", "Flag Pick / Reject / Unflag"),
            ("1 – 5 / 0", "Star Rating (1–5) / Clear"),
            ("6 / 7 / 8 / 9", "Red / Yellow / Green / Blue label"),
            ("Ctrl + F", "Cycle Culling Filter (All/Picks/…)")
        };

        (string Key, string Desc)[] rightCol =
        {
            ("Z / R-Click", "100% 1:1 Sensor Zoom (16-bit RAW)"),
            ("Shift+Z / M-Click", "AI Auto Face & Eye Jump (DirectML)"),
            ("Shift + F", "GPU Focus Peaking (Sobel Shader)"),
            ("O", "Exposure Clipping Blinkies"),
            ("H", "Live RGB + Rec.709 Histogram"),
            ("S  /  Tab", "2-Up Split Compare / Swap Panes"),
            ("R  /  L", "Lossless EXIF + XMP Rotate CW/CCW"),
            ("Ctrl+E / Shift+Del", "Export Picks / Purge Rejects")
        };

        float startY = card.Rect.Top + 62f;
        float rowH = 38f;
        float colW = (w - 64f) * 0.5f;

        for (int i = 0; i < leftCol.Length; i++)
        {
            float y = startY + i * rowH;
            _dc.DrawText(leftCol[i].Key, _fontMonoSmall, new Rect(card.Rect.Left + 26f, y, 130f, rowH), keyBrush);
            _dc.DrawText(leftCol[i].Desc, _fontUiRegular, new Rect(card.Rect.Left + 158f, y, colW - 132f, rowH), descBrush);
        }

        float rightX = card.Rect.Left + 38f + colW;
        for (int i = 0; i < rightCol.Length; i++)
        {
            float y = startY + i * rowH;
            _dc.DrawText(rightCol[i].Key, _fontMonoSmall, new Rect(rightX, y, 142f, rowH), keyBrush);
            _dc.DrawText(rightCol[i].Desc, _fontUiRegular, new Rect(rightX + 144f, y, colW - 144f, rowH), descBrush);
        }

        string credit = $"AI Engine: {EyeFocusDetector.ActiveBackend}  ·  SwapChain: {(_isFp16SwapChain ? "FP16 Linear scRGB HDR/WCG" : "BGRA8 sRGB")}  ·  Windows port of Glance for macOS by @mortalezz (github.com/mortalezz/glance)";
        _dc.DrawText(credit, _fontUiRegular, new Rect(card.Rect.Left + 26f, card.Rect.Bottom - 38f, w - 52f, 24f), footerBrush);

        EndFrostedCard(card, 0.99f);
    }

    private float MeasureWidth(string text, IDWriteTextFormat fmt)
    {
        if (_dwrite == null || string.IsNullOrEmpty(text)) return 0f;
        using var layout = _dwrite.CreateTextLayout(text, fmt, 4096f, 64f);
        return layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    private static RoundedRectangle MakeRounded(float x, float y, float w, float h, float r) =>
        new() { Rect = new RawRectF(x, y, x + w, y + h), RadiusX = r, RadiusY = r };

    private static string BuildCullingSummary(CullingMetadata c)
    {
        string flag = c.Pick switch
        {
            PickStatus.Pick => "✓ PICK",
            PickStatus.Reject => "✕ REJECT",
            _ => ""
        };
        string stars = c.Rating > 0 ? new string('★', c.Rating) : "";
        if (!string.IsNullOrEmpty(flag) && !string.IsNullOrEmpty(stars)) return $"{flag}  {stars}";
        return !string.IsNullOrEmpty(flag) ? flag : stars;
    }

    private Color4 ColorForLabel(ColorLabel label, float alpha) => label switch
    {
        ColorLabel.Red => UiColor(0xEF, 0x53, 0x50, alpha),
        ColorLabel.Yellow => UiColor(0xFF, 0xCA, 0x28, alpha),
        ColorLabel.Green => UiColor(0x66, 0xBB, 0x6A, alpha),
        ColorLabel.Blue => UiColor(0x42, 0xA5, 0xF5, alpha),
        _ => UiColor(255, 255, 255, alpha)
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }

    private Color4 UiColor(byte r, byte g, byte b, float a = 1.0f)
    {
        float rf = r / 255.0f;
        float gf = g / 255.0f;
        float bf = b / 255.0f;

        if (_isFp16SwapChain)
        {
            return new Color4(SrgbToLinear(rf), SrgbToLinear(gf), SrgbToLinear(bf), a);
        }
        return new Color4(rf, gf, bf, a);
    }

    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private void RecreateAllAfterDeviceReset()
    {
        var prim = _primarySlot.Source;
        var sec = _secondarySlot.Source;
        var clip = _clippingSource;

        _primarySlot.Dispose();
        _secondarySlot.Dispose();
        _clippingBitmap?.Dispose();
        _clippingBitmap = null;
        _clippingSource = null;

        CreateDeviceResources();
        CreateWindowSizeResources();

        if (prim != null) UploadToSlot(_primarySlot, prim);
        if (sec != null) UploadToSlot(_secondarySlot, sec);
        if (clip != null) SetClippingMask(clip);
    }

    private void DisposeDeviceResources()
    {
        _primarySlot.Dispose();
        _secondarySlot.Dispose();
        _clippingBitmap?.Dispose();
        _clippingBitmap = null;

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
        _fontHero?.Dispose();
        _fontToast?.Dispose();
        _fontUiBold?.Dispose();
        _fontUiRegular?.Dispose();
        _fontMonoSmall?.Dispose();
        _fontCheatHeader?.Dispose();
        _dwrite?.Dispose();
    }
}
