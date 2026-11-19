using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

public sealed class HistogramOverlay : IOverlayRenderer
{
    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || !state.ShowHistogram || state.Histogram == null) return;

        const float defaultCardW = 264f;
        const float cardH = 122f;
        float filmstripLift = Math.Clamp(state.FilmstripOpacity, 0f, 1f) * BottomLayoutMetrics.FilmstripLiftOffset;
        float topY = MathF.Max(64f, ctx.DipHeight - cardH - 84f - filmstripLift);

        if (state.MultiUpMode == MultiUpMode.TwoUp && !state.IsSoloLoupeActive && state.SecondaryHistogram != null)
        {
            float halfW = ctx.DipWidth * 0.5f;
            float splitCardW = MathF.Min(defaultCardW, MathF.Max(180f, halfW - 32f));

            float leftX = MathF.Max(14f, halfW - splitCardW - 14f);
            float rightX = ctx.DipWidth - splitCardW - 20f;

            DrawHistogramCard(ctx, state.Histogram, "LEFT · RGB / LUMA", leftX, topY, splitCardW, cardH);
            DrawHistogramCard(ctx, state.SecondaryHistogram, "RIGHT · RGB / LUMA", rightX, topY, splitCardW, cardH);
        }
        else
        {
            float cardX = ctx.DipWidth - defaultCardW - 20f;
            DrawHistogramCard(ctx, state.Histogram, "RGB / LUMA HISTOGRAM", cardX, topY, defaultCardW, cardH);
        }
    }

    private static void DrawHistogramCard(
        in OverlayRenderContext ctx,
        HistogramData hist,
        string title,
        float x,
        float y,
        float cardW,
        float cardH)
    {
        var card = OverlayRenderContext.MakeRounded(x, y, cardW, cardH, 12f);
        ctx.BeginFrostedCard(card, 0.96f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using (var titleBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText)))
        {
            ctx.Dc.DrawText(title, ctx.Fonts.MonoSmall, new Rect(card.Rect.Left + 12f, card.Rect.Top + 4f, cardW - 44f, 18f), titleBrush);
        }

        // Subtle shadow crush & highlight clip indicator dots in top-right of card
        DrawClippingIndicators(ctx, hist, card.Rect.Right - 14f, card.Rect.Top + 13f);

        float plotLeft = card.Rect.Left + 10f;
        float plotRight = card.Rect.Right - 10f;
        float plotTop = card.Rect.Top + 24f;
        float plotBottom = card.Rect.Bottom - 10f;
        float plotW = plotRight - plotLeft;
        float plotH = plotBottom - plotTop;

        using (var gridBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.HistogramGrid)))
        {
            for (int g = 1; g <= 3; g++)
            {
                float gx = plotLeft + plotW * (g * 0.25f);
                ctx.Dc.DrawLine(new Vector2(gx, plotTop), new Vector2(gx, plotBottom), gridBrush, 1.0f);
            }
        }

        int normPeak = 1;
        for (int i = 2; i < 254; i++)
        {
            if (hist.Red[i] > normPeak) normPeak = hist.Red[i];
            if (hist.Green[i] > normPeak) normPeak = hist.Green[i];
            if (hist.Blue[i] > normPeak) normPeak = hist.Blue[i];
        }
        if (normPeak <= 1) normPeak = Math.Max(1, hist.MaxCount);

        DrawChannel(ctx, hist.Red, normPeak, plotLeft, plotBottom, plotW, plotH,
            ctx.UiColor(0xFF, 0x45, 0x55, 0.36f), ctx.UiColor(0xFF, 0x5C, 0x6C, 0.88f));
        DrawChannel(ctx, hist.Green, normPeak, plotLeft, plotBottom, plotW, plotH,
            ctx.UiColor(0x38, 0xE5, 0x7A, 0.34f), ctx.UiColor(0x52, 0xF2, 0x8E, 0.85f));
        DrawChannel(ctx, hist.Blue, normPeak, plotLeft, plotBottom, plotW, plotH,
            ctx.UiColor(0x3A, 0x9E, 0xFF, 0.36f), ctx.UiColor(0x58, 0xB2, 0xFF, 0.88f));
        DrawChannel(ctx, hist.Luminance, normPeak, plotLeft, plotBottom, plotW, plotH,
            ctx.ThemeColor(ctx.Theme.HistogramLumaFill), ctx.ThemeColor(ctx.Theme.HistogramLumaStroke));

        ctx.EndFrostedCard(card, 0.96f);
    }

    private static void DrawClippingIndicators(in OverlayRenderContext ctx, HistogramData hist, float rightDotX, float cy)
    {
        long total = 0;
        for (int i = 0; i < 256; i++) total += hist.Luminance[i];
        if (total <= 0) return;

        bool shadowClip = (hist.Luminance[0] + hist.Luminance[1]) > total * 0.002;
        bool highlightClip = (hist.Luminance[254] + hist.Luminance[255]) > total * 0.002;

        if (highlightClip)
        {
            using var redDot = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(0xFF, 0x45, 0x55, 0.95f));
            ctx.Dc.FillEllipse(new Ellipse(new Vector2(rightDotX, cy), 3.5f, 3.5f), redDot);
        }
        if (shadowClip)
        {
            using var blueDot = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(0x3A, 0x9E, 0xFF, 0.95f));
            ctx.Dc.FillEllipse(new Ellipse(new Vector2(rightDotX - 11f, cy), 3.5f, 3.5f), blueDot);
        }
    }

    private static void DrawChannel(
        in OverlayRenderContext ctx,
        int[] bins,
        int peak,
        float left,
        float bottom,
        float width,
        float height,
        Color4 fillColor,
        Color4 strokeColor)
    {
        using var path = ctx.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(left, bottom), FigureBegin.Filled);
            for (int i = 0; i < 256; i += 2)
            {
                int val = (bins[i] + bins[Math.Min(255, i + 1)]) >> 1;
                float norm = Math.Clamp((float)val / peak, 0f, 1f);
                float y = bottom - MathF.Pow(norm, 0.72f) * height;
                float x = left + (i / 255.0f) * width;
                sink.AddLine(new Vector2(x, y));
            }
            sink.AddLine(new Vector2(left + width, bottom));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        using var fill = ctx.Dc.CreateSolidColorBrush(fillColor);
        using var stroke = ctx.Dc.CreateSolidColorBrush(strokeColor);
        ctx.Dc.FillGeometry(path, fill);
        ctx.Dc.DrawGeometry(path, stroke, 1.15f);
    }
}
