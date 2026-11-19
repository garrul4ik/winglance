using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

/// <summary>
/// Virtualized floating Filmstrip Scrubber (G or bottom-edge hover):
/// draws only visible micro-thumbnail cells with burst brackets, Hero crowns, Pick/Reject indicators, stars, and color labels.
/// </summary>
public sealed class FilmstripOverlay : IOverlayRenderer
{
    public const float CellWidth = 68f;
    public const float CellHeight = 56f;
    public const float CellGap = 6f;
    public const float CellStride = CellWidth + CellGap; // 74f
    public const float SidePadding = 12f;

    public static float ComputeMaxScrollX(float cardWidth, int totalCount)
    {
        if (totalCount <= 0) return 0f;
        float contentWidth = SidePadding * 2f + totalCount * CellWidth + (totalCount - 1) * CellGap;
        float viewportW = MathF.Max(1f, cardWidth - SidePadding * 2f);
        return MathF.Max(0f, contentWidth - SidePadding * 2f - viewportW);
    }

    public static float ComputeCenteredScrollX(int index, float cardWidth, int totalCount)
    {
        if (totalCount <= 0) return 0f;
        float viewportW = MathF.Max(1f, cardWidth - SidePadding * 2f);
        float centerX = index * CellStride + CellWidth * 0.5f;
        float maxScroll = ComputeMaxScrollX(cardWidth, totalCount);
        return Math.Clamp(centerX - viewportW * 0.5f, 0f, maxScroll);
    }

    public static int HitTestIndex(Vector2 dip, float dipW, float dipH, float scrollX, int totalCount)
    {
        if (totalCount <= 0) return -1;
        var card = BottomLayoutMetrics.ComputeFilmstripBounds(dipW, dipH);
        if (dip.X < card.Left + SidePadding || dip.X > card.Right - SidePadding ||
            dip.Y < card.Top || dip.Y > card.Bottom)
        {
            return -1;
        }

        float relX = dip.X - (card.Left + SidePadding) + scrollX;
        if (relX < 0f) return -1;

        int idx = (int)(relX / CellStride);
        if (idx < 0 || idx >= totalCount) return -1;

        float inCell = relX - idx * CellStride;
        return inCell <= CellWidth ? idx : -1;
    }

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.FilmstripOpacity <= 0.01f || state.PlaylistItems.Count == 0)
            return;

        float alpha = Math.Clamp(state.FilmstripOpacity, 0f, 1f) * Math.Clamp(state.HudOpacity + 0.35f, 0f, 1f);
        var bounds = BottomLayoutMetrics.ComputeFilmstripBounds(ctx.DipWidth, ctx.DipHeight);
        var card = new RoundedRectangle { Rect = bounds, RadiusX = 16f, RadiusY = 16f };

        ctx.BeginFrostedCard(card, alpha);

        float innerLeft = bounds.Left + SidePadding;
        float innerRight = bounds.Right - SidePadding;
        float viewportW = MathF.Max(1f, innerRight - innerLeft);
        float cellTop = bounds.Top + 12f;

        int total = state.PlaylistItems.Count;
        float scrollX = Math.Clamp(state.FilmstripScrollX, 0f, ComputeMaxScrollX(bounds.Right - bounds.Left, total));

        int firstVisible = Math.Max(0, (int)MathF.Floor(scrollX / CellStride) - 1);
        int lastVisible = Math.Min(total - 1, (int)MathF.Ceiling((scrollX + viewportW) / CellStride) + 1);

        var innerClip = new RawRectF(innerLeft, bounds.Top + 4f, innerRight, bounds.Bottom - 4f);
        ctx.Dc.PushAxisAlignedClip(innerClip, AntialiasMode.Aliased);

        using var cellBgBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBackground));
        using var rejOverlayBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(0, 0, 0, 0.56f));
        using var rejTextBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.RejectText));
        using var pickDotBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PickAccentBorder));
        using var starBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.GoldStarText));
        using var heroBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 212, 77));
        using var burstBracketBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(20, 184, 166, 0.80f));
        using var inactiveBorderBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBorder));

        int activeSlotsCount = state.MultiUpMode == MultiUpMode.Single ? 1 : (int)state.MultiUpMode;

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            float cellX = innerLeft + i * CellStride - scrollX;
            var cellRounded = OverlayRenderContext.MakeRounded(cellX, cellTop, CellWidth, CellHeight, 7f);

            ctx.Dc.FillRoundedRectangle(cellRounded, cellBgBrush);

            var item = state.PlaylistItems[i];
            if (ctx.ResolveMicroThumbBitmap?.Invoke(item.Id) is { } thumbBmp)
            {
                DrawAspectFillBitmap(ctx, thumbBmp, cellRounded);
            }

            var meta = state.GetCullingForItem?.Invoke(item.Id) ?? default;
            var burst = state.GetBurstForIndex?.Invoke(i);

            // Reject darken + X mark
            if (meta.Pick == PickStatus.Reject)
            {
                ctx.Dc.FillRoundedRectangle(cellRounded, rejOverlayBrush);
                float xw = ctx.MeasureWidth("✕", ctx.Fonts.UiBold);
                ctx.Dc.DrawText("✕", ctx.Fonts.UiBold,
                    new Rect(cellX + (CellWidth - xw) * 0.5f, cellTop, xw + 4f, CellHeight), rejTextBrush);
            }
            else if (meta.Pick == PickStatus.Pick)
            {
                ctx.Dc.FillEllipse(new Ellipse(new Vector2(cellX + CellWidth - 8f, cellTop + 8f), 4f, 4f), pickDotBrush);
            }

            // Hero crown badge
            if (burst?.IsCurrentHero == true)
            {
                ctx.Dc.DrawText("👑", ctx.Fonts.MonoTiny,
                    new Rect(cellX + 3f, cellTop + 2f, 20f, 14f), heroBrush);
            }

            // Color label dot
            if (meta.Color != ColorLabel.None)
            {
                using var lblBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(GlassTheme.ColorForLabel(meta.Color)));
                ctx.Dc.FillEllipse(new Ellipse(new Vector2(cellX + 8f, cellTop + CellHeight - 8f), 3.6f, 3.6f), lblBrush);
            }

            // Rating stars indicator at bottom right
            if (meta.Rating > 0)
            {
                string starsStr = new string('★', meta.Rating);
                float sw = ctx.MeasureWidth(starsStr, ctx.Fonts.MonoTiny);
                ctx.Dc.DrawText(starsStr, ctx.Fonts.MonoTiny,
                    new Rect(MathF.Max(cellX + 12f, cellX + CellWidth - sw - 4f), cellTop + CellHeight - 15f, sw + 4f, 13f), starBrush);
            }

            // Burst grouping underline bracket
            if (burst != null && burst.BurstCount > 1)
            {
                float bx0 = burst.CurrentBurstIndex == 0 ? cellX + 4f : cellX - CellGap * 0.5f;
                float bx1 = burst.CurrentBurstIndex == burst.BurstCount - 1 ? cellX + CellWidth - 4f : cellX + CellWidth + CellGap * 0.5f;
                ctx.Dc.DrawLine(new Vector2(bx0, cellTop + CellHeight + 6f), new Vector2(bx1, cellTop + CellHeight + 6f), burstBracketBrush, 2.5f);
            }

            // Multi-up slot highlight or single current selection rim
            int matchedSlot = -1;
            if (state.MultiUpMode == MultiUpMode.Single)
            {
                if (i == state.CurrentIndex) matchedSlot = 0;
            }
            else
            {
                for (int s = 0; s < activeSlotsCount; s++)
                {
                    if (state.MultiUpSlots[s].FilteredIndex == i)
                    {
                        matchedSlot = s;
                        break;
                    }
                }
            }

            if (matchedSlot >= 0)
            {
                Color4 rim = state.MultiUpMode == MultiUpMode.Single
                    ? ctx.ThemeColor(ctx.Theme.AccentBlue)
                    : SplitPaneBadgeOverlay.GetSlotAccentColor(ctx, matchedSlot);
                using var rimBrush = ctx.Dc.CreateSolidColorBrush(rim);
                ctx.Dc.DrawRoundedRectangle(cellRounded, rimBrush, 2.4f);
            }
            else if (i == state.HoveredFilmstripIndex)
            {
                using var hoverBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText, 0.7f));
                ctx.Dc.DrawRoundedRectangle(cellRounded, hoverBrush, 1.4f);
            }
            else
            {
                ctx.Dc.DrawRoundedRectangle(cellRounded, inactiveBorderBrush, 0.75f);
            }
        }

        ctx.Dc.PopAxisAlignedClip();
        ctx.EndFrostedCard(card, alpha);
    }

    private static void DrawAspectFillBitmap(in OverlayRenderContext ctx, ID2D1Bitmap1 bmp, RoundedRectangle target)
    {
        var bmpSize = bmp.Size;
        float bw = MathF.Max(1f, bmpSize.Width);
        float bh = MathF.Max(1f, bmpSize.Height);
        float dstW = target.Rect.Right - target.Rect.Left;
        float dstH = target.Rect.Bottom - target.Rect.Top;

        float srcAspect = bw / bh;
        float dstAspect = dstW / dstH;
        RawRectF srcRect;
        if (srcAspect > dstAspect)
        {
            float cropW = bh * dstAspect;
            float x0 = (bw - cropW) * 0.5f;
            srcRect = new RawRectF(x0, 0f, x0 + cropW, bh);
        }
        else
        {
            float cropH = bw / dstAspect;
            float y0 = (bh - cropH) * 0.5f;
            srcRect = new RawRectF(0f, y0, bw, y0 + cropH);
        }

        using var geo = ctx.Factory.CreateRoundedRectangleGeometry(target);
        var lp = new LayerParameters1
        {
            ContentBounds = target.Rect,
            GeometricMask = geo,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1.0f,
            LayerOptions = LayerOptions1.None
        };
        ctx.Dc.PushLayer(ref lp, null);
        ctx.Dc.DrawBitmap(bmp, target.Rect, 1.0f, BitmapInterpolationMode.Linear, srcRect);
        ctx.Dc.PopLayer();
    }
}
