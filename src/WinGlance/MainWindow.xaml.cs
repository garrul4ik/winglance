using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance;

public partial class MainWindow : Window
{
    private readonly SlidePipeline _pipeline = new();
    private List<SlideItem> _allItems = new();
    private Dictionary<string, CullingMetadata> _cullingData = new(StringComparer.OrdinalIgnoreCase);

    // Filtering & Navigation
    private readonly List<CullingFilter> _filterOrder = new()
    {
        CullingFilter.All,
        CullingFilter.HideRejects,
        CullingFilter.PicksOnly,
        CullingFilter.RejectsOnly,
        CullingFilter.UnratedOnly,
        new CullingFilter(CullingFilterType.MinRating, 3),
        new CullingFilter(CullingFilterType.MinRating, 5)
    };
    private int _filterIndex = 0;
    private List<int> _filteredIndices = new();
    private int _currentFilteredPos = 0;

    private bool _autoAdvance = false;
    private bool _isSplitMode = false;
    private bool _isZoomed = false;
    private bool _isHistogramVisible = false;
    private bool _isClippingVisible = false;
    private bool _isExifVisible = false;

    // Slideshow
    private bool _isSlideshowActive = false;
    private double _slideshowInterval = 3.0;
    private DispatcherTimer? _slideshowTimer;

    // Mouse drag & pan
    private bool _isDragging = false;
    private Point _dragStartMouse;
    private Point _dragStartTranslate;

    // HUD inactivity timer
    private DispatcherTimer? _hudInactivityTimer;
    private DispatcherTimer? _feedbackFadeTimer;

    private string _currentDirectory = "";
    private DecodedImage? _currentDecodedImage;
    private DecodedImage? _rightDecodedImage;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;

        _hudInactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.0)
        };
        _hudInactivityTimer.Tick += (s, e) => FadeOutHuds();

        _feedbackFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2)
        };
        _feedbackFadeTimer.Tick += (s, e) =>
        {
            _feedbackFadeTimer.Stop();
            CenterFeedbackHud.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200)));
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hudInactivityTimer?.Start();

        var args = Environment.GetCommandLineArgs();
        string? targetPath = null;
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            targetPath = args[1];
        }
        else
        {
            // Default to current directory if images exist, or Pictures directory
            var currentDir = Directory.GetCurrentDirectory();
            if (HasSupportedImages(currentDir))
            {
                targetPath = currentDir;
            }
            else
            {
                var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (Directory.Exists(pictures) && HasSupportedImages(pictures))
                {
                    targetPath = pictures;
                }
            }
        }

        if (targetPath != null)
        {
            await OpenTargetAsync(targetPath);
        }
        else
        {
            TxtFilename.Text = "Drop any image or folder here to start";
        }
    }

    private static bool HasSupportedImages(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any(SupportedFormats.IsSupported);
        }
        catch { return false; }
    }

    public async Task OpenTargetAsync(string targetPath)
    {
        try
        {
            var scan = await DirectoryScanner.ScanAsync(targetPath);
            if (scan.Items.Count == 0)
            {
                TxtFilename.Text = "No supported images found in directory.";
                return;
            }

            _currentDirectory = scan.DirectoryPath;
            _allItems = scan.Items.ToList();

            // Background load all sidecars
            _cullingData = await XmpSidecarManager.LoadAllSidecarsAsync(_allItems);

            var screenPixelSize = (int)Math.Max(SystemParameters.PrimaryScreenWidth * 1.5, SystemParameters.PrimaryScreenHeight * 1.5);
            _pipeline.SetPlaylist(_allItems, scan.InitialIndex, screenPixelSize);

            RebuildFilter(scan.InitialIndex);
            await DisplayCurrentSlideAsync(isForward: true);
        }
        catch (Exception ex)
        {
            TxtFilename.Text = $"Error: {ex.Message}";
        }
    }

    private void RebuildFilter(int targetRawIndex)
    {
        var currentFilter = _filterOrder[_filterIndex];
        _filteredIndices.Clear();

        for (int i = 0; i < _allItems.Count; i++)
        {
            var item = _allItems[i];
            _cullingData.TryGetValue(item.Id, out var meta);
            if (currentFilter.Matches(meta))
            {
                _filteredIndices.Add(i);
            }
        }

        if (_filteredIndices.Count == 0)
        {
            _currentFilteredPos = 0;
            return;
        }

        var matchPos = _filteredIndices.IndexOf(targetRawIndex);
        _currentFilteredPos = matchPos >= 0 ? matchPos : 0;
    }

    private int? CurrentRawIndex => (_filteredIndices.Count > 0 && _currentFilteredPos < _filteredIndices.Count)
        ? _filteredIndices[_currentFilteredPos]
        : null;

    private async Task DisplayCurrentSlideAsync(bool isForward)
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null)
        {
            MainImage.Source = null;
            TxtFilename.Text = "No files match active filter";
            TxtIndex.Text = "0 / 0";
            return;
        }

        var index = rawIdx.Value;
        var item = _allItems[index];
        _pipeline.NotifyCurrentIndexChanged(index, isForward);

        try
        {
            var decoded = _isZoomed
                ? await _pipeline.GetFullResolutionImageAsync(index)
                : await _pipeline.GetImageAsync(index);

            _currentDecodedImage = decoded;
            MainImage.Source = decoded.Image;

            // Reset pan/zoom if not zoomed
            if (!_isZoomed)
            {
                MainScaleTransform.ScaleX = 1.0;
                MainScaleTransform.ScaleY = 1.0;
                MainTranslateTransform.X = 0;
                MainTranslateTransform.Y = 0;
            }

            // Update 2-Up compare mode if active
            if (_isSplitMode)
            {
                var nextPos = (_currentFilteredPos + 1) % _filteredIndices.Count;
                var nextRawIdx = _filteredIndices[nextPos];
                var rightDecoded = await _pipeline.GetImageAsync(nextRawIdx);
                _rightDecodedImage = rightDecoded;
                RightImage.Source = rightDecoded.Image;
            }

            // Update Histogram if open
            if (_isHistogramVisible && decoded.Image != null)
            {
                var hist = HistogramEngine.Compute(decoded.Image);
                HistogramView.Update(hist);
            }

            // Update Exposure Clipping if active
            if (_isClippingVisible && decoded.Image != null)
            {
                var mask = ClippingEngine.GenerateClippingMask(decoded.Image);
                if (mask != null)
                {
                    ClippingOverlay.Source = mask;
                    ClippingOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    ClippingOverlay.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ClippingOverlay.Visibility = Visibility.Collapsed;
            }

            UpdateHud(item, decoded);
        }
        catch (Exception ex)
        {
            TxtFilename.Text = $"Failed to decode: {ex.Message}";
        }
    }

    private void UpdateHud(SlideItem item, DecodedImage decoded)
    {
        // 1. Top HUD
        TxtIndex.Text = $"{_currentFilteredPos + 1} / {_filteredIndices.Count} (Total {_allItems.Count})";
        BadgeRaw.Visibility = (decoded.IsRaw || item.IsStacked) ? Visibility.Visible : Visibility.Collapsed;

        var currentFilter = _filterOrder[_filterIndex];
        if (currentFilter.Type != CullingFilterType.All)
        {
            BadgeFilter.Visibility = Visibility.Visible;
            TxtFilter.Text = currentFilter.Title;
        }
        else
        {
            BadgeFilter.Visibility = Visibility.Collapsed;
        }

        BadgeZoom.Visibility = _isZoomed ? Visibility.Visible : Visibility.Collapsed;
        BadgeAuto.Visibility = _autoAdvance ? Visibility.Visible : Visibility.Collapsed;
        BadgeSlideshow.Visibility = _isSlideshowActive ? Visibility.Visible : Visibility.Collapsed;

        // 2. Bottom HUD
        TxtFilename.Text = item.DisplayName;

        var megapixel = (decoded.OriginalWidth * decoded.OriginalHeight) / 1_000_000.0;
        var mb = item.TotalStackSize / (1024.0 * 1024.0);
        TxtDimensions.Text = $"{megapixel:0.#}MP · {decoded.OriginalWidth} × {decoded.OriginalHeight}";
        TxtFileSize.Text = $"{mb:0.#} MB";

        var exif = decoded.Exif;
        if (exif != null && _isExifVisible)
        {
            var summary = $"{exif.ExposureSummary} · {exif.CameraAndLensSummary}".Trim(' ', '·');
            if (!string.IsNullOrWhiteSpace(summary))
            {
                TxtExif.Text = summary;
                TxtExif.Visibility = Visibility.Visible;
            }
            else
            {
                TxtExif.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TxtExif.Visibility = Visibility.Collapsed;
        }

        ShowHuds();
    }

    private void ShowFeedbackBadge(string text, Brush foreground, string? subtext = null)
    {
        TxtFeedbackRating.Text = text;
        TxtFeedbackRating.Foreground = foreground;
        TxtFeedbackRating.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(subtext))
        {
            TxtFeedbackStatus.Text = subtext;
            TxtFeedbackStatus.Foreground = foreground;
            TxtFeedbackStatus.Visibility = Visibility.Visible;
        }
        else
        {
            TxtFeedbackStatus.Visibility = Visibility.Collapsed;
        }

        CenterFeedbackHud.BeginAnimation(OpacityProperty, null);
        CenterFeedbackHud.Opacity = 1.0;

        _feedbackFadeTimer?.Stop();
        _feedbackFadeTimer?.Start();
    }

    #region Navigation & Actions

    private async Task NextSlideAsync()
    {
        if (_filteredIndices.Count == 0) return;
        _currentFilteredPos = (_currentFilteredPos + 1) % _filteredIndices.Count;
        await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task PreviousSlideAsync()
    {
        if (_filteredIndices.Count == 0) return;
        _currentFilteredPos = (_currentFilteredPos - 1 + _filteredIndices.Count) % _filteredIndices.Count;
        await DisplayCurrentSlideAsync(isForward: false);
    }

    private async Task FirstSlideAsync()
    {
        if (_filteredIndices.Count == 0) return;
        _currentFilteredPos = 0;
        await DisplayCurrentSlideAsync(isForward: false);
    }

    private async Task LastSlideAsync()
    {
        if (_filteredIndices.Count == 0) return;
        _currentFilteredPos = _filteredIndices.Count - 1;
        await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task SetRatingAsync(int stars)
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null) return;
        var item = _allItems[rawIdx.Value];

        var meta = _cullingData.GetValueOrDefault(item.Id, new CullingMetadata());
        meta = meta with { Rating = stars, Pick = (stars > 0 && meta.Pick == PickStatus.Reject) ? PickStatus.Unflagged : meta.Pick };
        _cullingData[item.Id] = meta;

        // Persist to Adobe XMP sidecar
        XmpSidecarManager.Save(meta, item.Path);

        if (stars > 0)
        {
            var starText = string.Join(" ", Enumerable.Repeat("★", stars));
            ShowFeedbackBadge(starText, (Brush)new BrushConverter().ConvertFrom("#FFD700")!);
        }
        else
        {
            ShowFeedbackBadge("Rating Cleared", Brushes.LightGray);
        }

        if (_autoAdvance) await NextSlideAsync();
        else await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task SetPickStatusAsync(PickStatus status)
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null) return;
        var item = _allItems[rawIdx.Value];

        var meta = _cullingData.GetValueOrDefault(item.Id, new CullingMetadata());
        meta = meta with { Pick = status };
        _cullingData[item.Id] = meta;

        XmpSidecarManager.Save(meta, item.Path);

        switch (status)
        {
            case PickStatus.Pick:
                ShowFeedbackBadge("🟢 PICK", (Brush)new BrushConverter().ConvertFrom("#4CAF50")!);
                break;
            case PickStatus.Reject:
                ShowFeedbackBadge("🔴 REJECT", (Brush)new BrushConverter().ConvertFrom("#E53935")!);
                break;
            case PickStatus.Unflagged:
                ShowFeedbackBadge("⚪ UNFLAGGED", Brushes.WhiteSmoke);
                break;
        }

        if (_autoAdvance) await NextSlideAsync();
        else await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task SetColorLabelAsync(ColorLabel color)
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null) return;
        var item = _allItems[rawIdx.Value];

        var meta = _cullingData.GetValueOrDefault(item.Id, new CullingMetadata());
        meta = meta with { Color = color };
        _cullingData[item.Id] = meta;

        XmpSidecarManager.Save(meta, item.Path);

        var hex = color.GetHexColor();
        var brush = color != ColorLabel.None ? (Brush)new BrushConverter().ConvertFrom(hex)! : Brushes.White;
        ShowFeedbackBadge($"● {color.GetTitle()}", brush);

        if (_autoAdvance) await NextSlideAsync();
        else await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task CycleFilterAsync()
    {
        _filterIndex = (_filterIndex + 1) % _filterOrder.Count;
        var targetRaw = CurrentRawIndex ?? 0;
        RebuildFilter(targetRaw);
        await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task Toggle100PercentZoomAsync(Point? clickLocation = null)
    {
        _isZoomed = !_isZoomed;
        BadgeZoom.Visibility = _isZoomed ? Visibility.Visible : Visibility.Collapsed;

        if (_isZoomed && _currentDecodedImage != null)
        {
            // Calculate 1:1 sensor scale factor
            var renderW = MainImage.ActualWidth;
            var origW = _currentDecodedImage.OriginalWidth;

            var scale = renderW > 0 ? (double)origW / renderW : 2.5;
            MainScaleTransform.ScaleX = scale;
            MainScaleTransform.ScaleY = scale;

            if (clickLocation.HasValue)
            {
                // Center zoom on mouse cursor
                var center = new Point(ViewportGrid.ActualWidth / 2, ViewportGrid.ActualHeight / 2);
                var mouse = clickLocation.Value;
                MainTranslateTransform.X = (center.X - mouse.X) * (scale - 1);
                MainTranslateTransform.Y = (center.Y - mouse.Y) * (scale - 1);
            }
        }
        else
        {
            MainScaleTransform.ScaleX = 1.0;
            MainScaleTransform.ScaleY = 1.0;
            MainTranslateTransform.X = 0;
            MainTranslateTransform.Y = 0;
        }

        var rawIdx = CurrentRawIndex;
        if (rawIdx.HasValue)
        {
            await DisplayCurrentSlideAsync(isForward: true);
        }
    }

    private async Task ToggleSplitCompareAsync()
    {
        _isSplitMode = !_isSplitMode;
        if (_isSplitMode)
        {
            ColRight.Width = new GridLength(1, GridUnitType.Star);
            SplitDivider.Visibility = Visibility.Visible;
            RightGrid.Visibility = Visibility.Visible;
        }
        else
        {
            ColRight.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
            RightGrid.Visibility = Visibility.Collapsed;
            RightImage.Source = null;
        }

        await DisplayCurrentSlideAsync(isForward: true);
    }

    private void ToggleHistogram()
    {
        _isHistogramVisible = !_isHistogramVisible;
        HistogramHud.Visibility = _isHistogramVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_isHistogramVisible && _currentDecodedImage?.Image != null)
        {
            HistogramView.Update(HistogramEngine.Compute(_currentDecodedImage.Image));
        }
    }

    private void ToggleClipping()
    {
        _isClippingVisible = !_isClippingVisible;
        if (_isClippingVisible)
        {
            var sb = (Storyboard)FindResource("PulseClipping");
            sb.Begin();
            if (_currentDecodedImage?.Image != null)
            {
                var mask = ClippingEngine.GenerateClippingMask(_currentDecodedImage.Image);
                ClippingOverlay.Source = mask;
                ClippingOverlay.Visibility = mask != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else
        {
            ClippingOverlay.Visibility = Visibility.Collapsed;
            var sb = (Storyboard)FindResource("PulseClipping");
            sb.Stop();
        }
    }

    private void ToggleExif()
    {
        _isExifVisible = !_isExifVisible;
        if (CurrentRawIndex.HasValue && _currentDecodedImage != null)
        {
            UpdateHud(_allItems[CurrentRawIndex.Value], _currentDecodedImage);
        }
    }

    private async Task RotateAsync(RotationDirection dir)
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null) return;
        var item = _allItems[rawIdx.Value];

        LosslessRotationEngine.Rotate(item, dir);
        ShowFeedbackBadge(dir == RotationDirection.Clockwise ? "↷ Rotate CW" : "↶ Rotate CCW", Brushes.LightSkyBlue);
        await DisplayCurrentSlideAsync(isForward: true);
    }

    private async Task ExportPicksAsync()
    {
        if (string.IsNullOrEmpty(_currentDirectory)) return;

        BadgeExport.Visibility = Visibility.Visible;
        TxtExport.Text = "Exporting Picks...";

        var count = await ExportManager.ExportPicksAsync(_allItems, _cullingData, _currentDirectory);
        BadgeExport.Visibility = Visibility.Collapsed;

        ShowFeedbackBadge($"📦 Exported {count} files to ./Picks", (Brush)new BrushConverter().ConvertFrom("#42A5F5")!);
    }

    private void ToggleSlideshow()
    {
        _isSlideshowActive = !_isSlideshowActive;
        BadgeSlideshow.Visibility = _isSlideshowActive ? Visibility.Visible : Visibility.Collapsed;

        if (_isSlideshowActive)
        {
            _slideshowTimer ??= new DispatcherTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(_slideshowInterval);
            _slideshowTimer.Tick += async (s, e) => await NextSlideAsync();
            _slideshowTimer.Start();
            TxtSlideshow.Text = $"SLIDESHOW {_slideshowInterval:0.#}s";
        }
        else
        {
            _slideshowTimer?.Stop();
        }
    }

    private void AdjustSlideshowSpeed(bool increase)
    {
        if (increase) _slideshowInterval = Math.Min(30.0, _slideshowInterval + 1.0);
        else _slideshowInterval = Math.Max(1.0, _slideshowInterval - 1.0);

        TxtSlideshow.Text = $"SLIDESHOW {_slideshowInterval:0.#}s";
        if (_slideshowTimer != null)
        {
            _slideshowTimer.Interval = TimeSpan.FromSeconds(_slideshowInterval);
        }
    }

    private async Task DeleteCurrentFileAsync()
    {
        var rawIdx = CurrentRawIndex;
        if (rawIdx == null) return;
        var item = _allItems[rawIdx.Value];

        foreach (var path in item.AllPaths)
        {
            await TrashManager.MoveToTrashAsync(path);
            var sidecar = XmpSidecarManager.ExistingSidecarPath(path);
            if (sidecar != null) await TrashManager.MoveToTrashAsync(sidecar);
        }

        _allItems.RemoveAt(rawIdx.Value);
        _cullingData.Remove(item.Id);

        RebuildFilter(Math.Min(rawIdx.Value, _allItems.Count - 1));
        ShowFeedbackBadge("🗑️ Moved to Recycle Bin", (Brush)new BrushConverter().ConvertFrom("#EF5350")!);
        await DisplayCurrentSlideAsync(isForward: true);
    }

    #endregion

    #region Input Handlers

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        ShowHuds();

        // Cheatsheet toggle
        if (e.Key == Key.OemQuestion || (e.Key == Key.D2 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            CheatsheetOverlay.Visibility = CheatsheetOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            e.Handled = true;
            return;
        }

        if (CheatsheetOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CheatsheetOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            // Navigation
            case Key.Right or Key.Down or Key.Space or Key.N:
                await NextSlideAsync();
                break;
            case Key.Left or Key.Up or Key.PageUp or Key.K:
                await PreviousSlideAsync();
                break;
            case Key.Home:
                await FirstSlideAsync();
                break;
            case Key.End:
                await LastSlideAsync();
                break;

            // Fullscreen & Window
            case Key.F:
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case Key.Escape or Key.Q:
                if (_isZoomed) await Toggle100PercentZoomAsync();
                else Close();
                break;

            // Culling - Stars
            case Key.D1 or Key.NumPad1: await SetRatingAsync(1); break;
            case Key.D2 or Key.NumPad2: await SetRatingAsync(2); break;
            case Key.D3 or Key.NumPad3: await SetRatingAsync(3); break;
            case Key.D4 or Key.NumPad4: await SetRatingAsync(4); break;
            case Key.D5 or Key.NumPad5: await SetRatingAsync(5); break;
            case Key.D0 or Key.NumPad0: await SetRatingAsync(0); break;

            // Culling - Flags
            case Key.P: await SetPickStatusAsync(PickStatus.Pick); break;
            case Key.X: await SetPickStatusAsync(PickStatus.Reject); break;
            case Key.U: await SetPickStatusAsync(PickStatus.Unflagged); break;

            // Culling - Color Labels
            case Key.D6 or Key.NumPad6: await SetColorLabelAsync(ColorLabel.Red); break;
            case Key.D7 or Key.NumPad7: await SetColorLabelAsync(ColorLabel.Yellow); break;
            case Key.D8 or Key.NumPad8: await SetColorLabelAsync(ColorLabel.Green); break;
            case Key.D9 or Key.NumPad9: await SetColorLabelAsync(ColorLabel.Blue); break;

            // Filter Cycle
            case Key.OemBackslash or Key.OemPipe:
                await CycleFilterAsync();
                break;

            // Auto-advance toggle
            case Key.C:
                _autoAdvance = !_autoAdvance;
                BadgeAuto.Visibility = _autoAdvance ? Visibility.Visible : Visibility.Collapsed;
                ShowFeedbackBadge(_autoAdvance ? "⚡ Auto-Advance ON" : "Auto-Advance OFF", Brushes.Gold);
                break;

            // 1:1 Zoom
            case Key.Z:
                await Toggle100PercentZoomAsync();
                break;

            // 2-Up Split Compare
            case Key.S or Key.Tab:
                await ToggleSplitCompareAsync();
                break;

            // Overlays: Histogram, Clipping, EXIF
            case Key.H:
                ToggleHistogram();
                break;
            case Key.O:
                ToggleClipping();
                break;
            case Key.I:
                ToggleExif();
                break;

            // Lossless Rotation
            case Key.R or Key.OemCloseBrackets:
                await RotateAsync(RotationDirection.Clockwise);
                break;
            case Key.L or Key.OemOpenBrackets:
                await RotateAsync(RotationDirection.CounterClockwise);
                break;

            // Export Picks
            case Key.E:
                await ExportPicksAsync();
                break;

            // Slideshow
            case Key.Return:
                ToggleSlideshow();
                break;
            case Key.OemPlus or Key.Add:
                AdjustSlideshowSpeed(increase: true);
                break;
            case Key.OemMinus or Key.Subtract:
                AdjustSlideshowSpeed(increase: false);
                break;

            // Delete
            case Key.Delete or Key.Back:
                await DeleteCurrentFileAsync();
                break;
        }

        e.Handled = true;
    }

    private async void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_isZoomed)
            {
                await Toggle100PercentZoomAsync(e.GetPosition(ViewportGrid));
            }
            else
            {
                _isDragging = true;
                _dragStartMouse = e.GetPosition(this);
                _dragStartTranslate = new Point(MainTranslateTransform.X, MainTranslateTransform.Y);
                ViewportGrid.CaptureMouse();
            }
        }
        else if (e.RightButton == MouseButtonState.Pressed && _isZoomed)
        {
            await Toggle100PercentZoomAsync();
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var curMouse = e.GetPosition(this);
            var dx = curMouse.X - _dragStartMouse.X;
            var dy = curMouse.Y - _dragStartMouse.Y;

            MainTranslateTransform.X = _dragStartTranslate.X + dx;
            MainTranslateTransform.Y = _dragStartTranslate.Y + dy;

            if (_isSplitMode)
            {
                RightTranslateTransform.X = MainTranslateTransform.X;
                RightTranslateTransform.Y = MainTranslateTransform.Y;
            }
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ViewportGrid.ReleaseMouseCapture();
        }
    }

    private async void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) await PreviousSlideAsync();
        else if (e.Delta < 0) await NextSlideAsync();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        ShowHuds();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        ShowHuds();
    }

    private void ShowHuds()
    {
        TopHud.BeginAnimation(OpacityProperty, null);
        BottomHud.BeginAnimation(OpacityProperty, null);
        TopHud.Opacity = 1.0;
        BottomHud.Opacity = 1.0;

        _hudInactivityTimer?.Stop();
        _hudInactivityTimer?.Start();
    }

    private void FadeOutHuds()
    {
        _hudInactivityTimer?.Stop();
        var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400));
        TopHud.BeginAnimation(OpacityProperty, fade);
        BottomHud.BeginAnimation(OpacityProperty, fade);
    }

    private void OnCheatsheetClose(object sender, MouseButtonEventArgs e)
    {
        CheatsheetOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                await OpenTargetAsync(files[0]);
            }
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var screenPixelSize = (int)Math.Max(ActualWidth * 1.5, ActualHeight * 1.5);
        _pipeline.UpdateBoundingSize(screenPixelSize);
    }

    #endregion
}
