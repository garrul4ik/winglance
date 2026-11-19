using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WinGlance.Gpu.Overlays;

public sealed class NavigatorMiniMapOverlay : IOverlayRenderer
{
    public const float ExpandedWidth = 192f;
    public const float ExpandedHeight = 156f;
    public const float CollapsedWidth = 148f;
    public const float CollapsedHeight = 30f;
    public const float HeaderHeight = 26f;

    public static RawRectF ComputeCardBounds(float dipW, float dipH, bool collapsed, float filmstripOpacity = 0f)
    {
        float w = collapsed ? CollapsedWidth : ExpandedWidth;
        float h = collapsed ? CollapsedHeight : ExpandedHeight;
        float lift = Math.Clamp(filmstripOpacity, 0f, 1f) * BottomLayoutMetrics.FilmstripLiftOffset;
        float y = MathF.Max(64f, dipH - h - 84f - lift);
        return new RawRectF(20f, y, 20f + w, y + h);
    }

    public static RawRectF ComputeHeaderBounds(RawRectF card) =>
        new(card.Left, card.Top, card.Right, card.Top + HeaderHeight);

    public static RawRectF ComputeImageRect(RawRectF card, int imgW, int imgH)
    {
        float wellLeft = card.Left + 8f;
        float wellTop = card.Top + HeaderHeight + 2f;
        float wellRight = card.Right - 8f;
        float wellBottom = card.Bottom - 8f;

        float wellW = MathF.Max(1f, wellRight - wellLeft);
        float wellH = MathF.Max(1f, wellBottom - wellTop);
        if (imgW <= 0 || imgH <= 0)
        {
            return new RawRectF(wellLeft, wellTop, wellRight, wellBottom);
        }

        float scale = MathF.Min(wellW / imgW, wellH / imgH);
        float drawW = imgW * scale;
        float drawH = imgH * scale;
        float left = wellLeft + (wellW - drawW) * 0.5f;
        float top = wellTop + (wellH - drawH) * 0.5f;
        return new RawRectF(left, top, left + drawW, top + drawH);
    }

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || !state.IsCustomZoom || ctx.PrimaryImage == null || ctx.PrimaryBitmap == null)
        {
            return;
        }

        var cardBounds = ComputeCardBounds(ctx.DipWidth, ctx.DipHeight, state.IsMiniMapCollapsed, state.FilmstripOpacity);
        var card = new RoundedRectangle { Rect = cardBounds, RadiusX = 11f, RadiusY = 11f };

        ctx.BeginFrostedCard(card, 0.96f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var titleBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var mutedBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.MutedText));
        using var emeraldStroke = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.EmeraldAccent));
        using var emeraldFill = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.EmeraldAccent with { A = 0.16f }));
        using var wellBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.WellBackground));

        int zoomPct = (int)MathF.Round(state.PixelZoomScale * 100f);
        string glyph = state.IsMiniMapCollapsed ? "▸" : "▾";
        ctx.Dc.DrawText($"NAVIGATOR · {zoomPct}%", ctx.Fonts.MonoTiny,
            new Rect(cardBounds.Left + 10f, cardBounds.Top + 4f, (cardBounds.Right - cardBounds.Left) - 34f, 20f), titleBrush);
        ctx.Dc.DrawText(glyph, ctx.Fonts.UiBold,
            new Rect(cardBounds.Right - 24f, cardBounds.Top + 3f, 16f, 20f), mutedBrush);

        if (!state.IsMiniMapCollapsed)
        {
            var wellRect = OverlayRenderContext.MakeRounded(
                cardBounds.Left + 8f,
                cardBounds.Top + HeaderHeight + 2f,
                (cardBounds.Right - cardBounds.Left) - 16f,
                (cardBounds.Bottom - cardBounds.Top) - HeaderHeight - 10f,
                6f);
            ctx.Dc.FillRoundedRectangle(wellRect, wellBrush);

            var imgRect = ComputeImageRect(cardBounds, ctx.PrimaryImage.OriginalWidth, ctx.PrimaryImage.OriginalHeight);
            ctx.Dc.DrawBitmap(ctx.PrimaryBitmap, imgRect, 1.0f, BitmapInterpolationMode.Linear, null);

            // Compute visible normalized viewport rectangle from pane vs dest rects
            var pane = ctx.PrimaryPaneDip;
            var dest = ctx.PrimaryDestDip;
            float destW = MathF.Max(1f, dest.Right - dest.Left);
            float destH = MathF.Max(1f, dest.Bottom - dest.Top);

            float u0 = Math.Clamp((pane.Left - dest.Left) / destW, 0f, 1f);
            float u1 = Math.Clamp((pane.Right - dest.Left) / destW, 0f, 1f);
            float v0 = Math.Clamp((pane.Top - dest.Top) / destH, 0f, 1f);
            float v1 = Math.Clamp((pane.Bottom - dest.Top) / destH, 0f, 1f);

            float thumbW = imgRect.Right - imgRect.Left;
            float thumbH = imgRect.Bottom - imgRect.Top;

            float boxW = MathF.Max(6f, (u1 - u0) * thumbW);
            float boxH = MathF.Max(6f, (v1 - v0) * thumbH);
            float boxL = Math.Clamp(imgRect.Left + u0 * thumbW, imgRect.Left, imgRect.Right - boxW);
            float boxT = Math.Clamp(imgRect.Top + v0 * thumbH, imgRect.Top, imgRect.Bottom - boxH);

            var vpRounded = OverlayRenderContext.MakeRounded(boxL, boxT, boxW, boxH, 2f);
            ctx.Dc.FillRoundedRectangle(vpRounded, emeraldFill);
            ctx.Dc.DrawRoundedRectangle(vpRounded, emeraldStroke, 1.5f);

            ctx.Dc.FillEllipse(new Ellipse(new Vector2(boxL + boxW * 0.5f, boxT + boxH * 0.5f), 1.6f, 1.6f), emeraldStroke);
        }

        ctx.EndFrostedCard(card, 0.96f);
    }
}
