using System;
using System.Windows;
using System.Windows.Media;
using WinGlance.Engine;

namespace WinGlance.UI;

public sealed class HistogramControl : FrameworkElement
{
    private HistogramData _data = HistogramData.Empty;

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 0.8);
    private static readonly Pen RedStroke = new(new SolidColorBrush(Color.FromArgb(220, 255, 68, 68)), 1.2);
    private static readonly Brush RedFill = new SolidColorBrush(Color.FromArgb(40, 255, 68, 68));
    private static readonly Pen GreenStroke = new(new SolidColorBrush(Color.FromArgb(220, 68, 255, 68)), 1.2);
    private static readonly Brush GreenFill = new SolidColorBrush(Color.FromArgb(40, 68, 255, 68));
    private static readonly Pen BlueStroke = new(new SolidColorBrush(Color.FromArgb(220, 68, 136, 255)), 1.2);
    private static readonly Brush BlueFill = new SolidColorBrush(Color.FromArgb(40, 68, 136, 255));
    private static readonly Pen LumStroke = new(new SolidColorBrush(Color.FromArgb(240, 230, 230, 230)), 1.4);

    static HistogramControl()
    {
        GridPen.Freeze();
        RedStroke.Freeze();
        RedFill.Freeze();
        GreenStroke.Freeze();
        GreenFill.Freeze();
        BlueStroke.Freeze();
        BlueFill.Freeze();
        LumStroke.Freeze();
    }

    public void Update(HistogramData data)
    {
        _data = data;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;

        // 1. Grid lines (25%, 50%, 75% vertical + 50% horizontal)
        for (double f = 0.25; f < 1.0; f += 0.25)
        {
            var x = Math.Floor(w * f) + 0.5;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
        }
        var midY = Math.Floor(h * 0.5) + 0.5;
        dc.DrawLine(GridPen, new Point(0, midY), new Point(w, midY));

        // 2. Outlier-suppressed peak determination (ignoring 0-1 and 254-255)
        double peak = 1.0;
        for (int i = 2; i < 254; i++)
        {
            if (_data.Red[i] > peak) peak = _data.Red[i];
            if (_data.Green[i] > peak) peak = _data.Green[i];
            if (_data.Blue[i] > peak) peak = _data.Blue[i];
            if (_data.Luminance[i] > peak) peak = _data.Luminance[i];
        }
        if (peak <= 1.0) peak = Math.Max(1.0, _data.MaxCount);

        var sqrtPeak = Math.Sqrt(peak);
        var usableH = Math.Max(1.0, h - 4.0);

        // 3. Binomial smoothing filter [0.25, 0.50, 0.25]
        double[] Smooth(int[] bins)
        {
            var res = new double[256];
            for (int i = 0; i < 256; i++)
            {
                var left = i > 0 ? bins[i - 1] : bins[0];
                var center = bins[i];
                var right = i < 255 ? bins[i + 1] : bins[255];
                res[i] = 0.25 * left + 0.50 * center + 0.25 * right;
            }
            return res;
        }

        var rSmooth = Smooth(_data.Red);
        var gSmooth = Smooth(_data.Green);
        var bSmooth = Smooth(_data.Blue);
        var lSmooth = Smooth(_data.Luminance);

        // 4. Draw curves: Red, Green, Blue, Luminance
        DrawCurve(dc, rSmooth, sqrtPeak, w, h, usableH, RedFill, RedStroke);
        DrawCurve(dc, gSmooth, sqrtPeak, w, h, usableH, GreenFill, GreenStroke);
        DrawCurve(dc, bSmooth, sqrtPeak, w, h, usableH, BlueFill, BlueStroke);
        DrawCurve(dc, lSmooth, sqrtPeak, w, h, usableH, null, LumStroke);

        // 5. Highlight / Shadow clipping warning dots
        if (_data.Luminance[0] > 0.05 * peak)
        {
            // Shadow clipping dot (Blue) at bottom-left
            dc.DrawEllipse(Brushes.DodgerBlue, null, new Point(5, h - 5), 3, 3);
        }
        if (_data.Luminance[255] > 0.05 * peak)
        {
            // Highlight clipping dot (Red) at bottom-right
            dc.DrawEllipse(Brushes.Crimson, null, new Point(w - 5, h - 5), 3, 3);
        }
    }

    private static void DrawCurve(
        DrawingContext dc,
        double[] bins,
        double sqrtPeak,
        double w,
        double h,
        double usableH,
        Brush? fill,
        Pen stroke)
    {
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            var startY = h - Math.Min(usableH, (Math.Sqrt(bins[0]) / sqrtPeak) * usableH);
            ctx.BeginFigure(new Point(0, h), isFilled: fill != null, isClosed: fill != null);
            ctx.LineTo(new Point(0, startY), isStroked: true, isSmoothJoin: true);

            for (int i = 1; i < 256; i++)
            {
                var x = (i / 255.0) * w;
                var val = Math.Sqrt(bins[i]) / sqrtPeak;
                var y = h - Math.Min(usableH, val * usableH);
                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }

            if (fill != null)
            {
                ctx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            }
        }
        geom.Freeze();

        if (fill != null) dc.DrawGeometry(fill, null, geom);
        dc.DrawGeometry(null, stroke, geom);
    }
}
