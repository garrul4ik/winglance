using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

/// <summary>
/// Bottom floating Face Verification Dock (A): renders YuNet cropped faces with eye-focus SHARP/SOFT badges.
/// Clicking a tile zooms 100% directly to that person's eye; lifts dynamically above Filmstrip when open.
/// </summary>
public sealed class FaceDockOverlay : IOverlayRenderer
{
    public static int HitTestTile(Vector2 dip, float dipW, float dipH, int tileCount, float filmstripOpacity)
    {
        if (tileCount <= 0) return -1;
        var card = BottomLayoutMetrics.ComputeFaceDockBounds(dipW, dipH, tileCount, filmstripOpacity);
        if (dip.X < card.Left || dip.X > card.Right || dip.Y < card.Top || dip.Y > card.Bottom)
            return -1;

        float x = dip.X - (card.Left + BottomLayoutMetrics.FaceDockHorizontalPad);
        float y = dip.Y - (card.Top + BottomLayoutMetrics.FaceDockVerticalPad);
        if (x < 0f || y < 0f || y > BottomLayoutMetrics.FaceTileHeight)
            return -1;

        float step = BottomLayoutMetrics.FaceTileWidth + BottomLayoutMetrics.FaceTileGap;
        int idx = (int)(x / step);
        if (idx < 0 || idx >= tileCount) return -1;

        float offsetInTile = x - idx * step;
        return offsetInTile <= BottomLayoutMetrics.FaceTileWidth ? idx : -1;
    }

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || !state.ShowFaceDock || state.MultiUpMode != MultiUpMode.Single ||
            state.FaceTiles.Count == 0 || state.HudOpacity <= 0.01f)
        {
            return;
        }

        float alpha = state.HudOpacity;
        var bounds = BottomLayoutMetrics.ComputeFaceDockBounds(ctx.DipWidth, ctx.DipHeight, state.FaceTiles.Count, state.FilmstripOpacity);
        var card = new RoundedRectangle { Rect = bounds, RadiusX = 14f, RadiusY = 14f };

        ctx.BeginFrostedCard(card, alpha);

        int count = state.FaceTiles.Count;
        float startX = bounds.Left + BottomLayoutMetrics.FaceDockHorizontalPad;
        float tileY = bounds.Top + BottomLayoutMetrics.FaceDockVerticalPad;

        for (int i = 0; i < count; i++)
        {
            var tile = state.FaceTiles[i];
            float tileX = startX + i * (BottomLayoutMetrics.FaceTileWidth + BottomLayoutMetrics.FaceTileGap);
            if (tileX + BottomLayoutMetrics.FaceTileWidth > bounds.Right - 4f) break;

            var tileRect = OverlayRenderContext.MakeRounded(
                tileX, tileY, BottomLayoutMetrics.FaceTileWidth, BottomLayoutMetrics.FaceTileHeight, 9f);

            bool isZoomed = state.ActiveZoomedFaceIndex == tile.FaceIndex;
            bool isHovered = state.HoveredFaceTileIndex == i;

            // Tile backing
            using (var bgBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBackground)))
            {
                ctx.Dc.FillRoundedRectangle(tileRect, bgBrush);
            }

            // Face bitmap crop (upper 74 DIPs)
            if (i < ctx.FaceCropBitmaps.Count && ctx.FaceCropBitmaps[i] is { } faceBmp)
            {
                var imgRect = new RawRectF(tileX + 1f, tileY + 1f, tileX + BottomLayoutMetrics.FaceTileWidth - 1f, tileY + 73f);
                using var clipGeo = ctx.Factory.CreateRoundedRectangleGeometry(
                    OverlayRenderContext.MakeRounded(imgRect.Left, imgRect.Top, imgRect.Right - imgRect.Left, imgRect.Bottom - imgRect.Top, 8f));
                var lp = new LayerParameters1
                {
                    ContentBounds = imgRect,
                    GeometricMask = clipGeo,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1.0f,
                    LayerOptions = LayerOptions1.None
                };
                ctx.Dc.PushLayer(ref lp, null);
                ctx.Dc.DrawBitmap(faceBmp, imgRect, 1.0f, BitmapInterpolationMode.Linear, null);
                ctx.Dc.PopLayer();
            }

            // Top-left index badge ("1", "2", ...)
            var idxPill = OverlayRenderContext.MakeRounded(tileX + 4f, tileY + 4f, 18f, 16f, 5f);
            string idxStr = tile.FaceIndex.ToString();
            float idxW = ctx.MeasureWidth(idxStr, ctx.Fonts.MonoTiny);
            using (var pillBg = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(12, 14, 18, 0.78f)))
            using (var idxText = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 255, 255)))
            {
                ctx.Dc.FillRoundedRectangle(idxPill, pillBg);
                ctx.Dc.DrawText(idxStr, ctx.Fonts.MonoTiny,
                    new Rect(idxPill.Rect.Left + (18f - idxW) * 0.5f, idxPill.Rect.Top, idxW + 4f, 16f), idxText);
            }

            // Bottom sharpness pill (SHARP green vs SOFT amber)
            var statusPill = OverlayRenderContext.MakeRounded(tileX + 3f, tileY + 74f, BottomLayoutMetrics.FaceTileWidth - 6f, 15f, 4f);
            Color4 statusBg = tile.IsSharp ? ctx.UiColor(16, 185, 129, 0.25f) : ctx.UiColor(245, 158, 11, 0.25f);
            Color4 statusFg = tile.IsSharp ? ctx.ThemeColor(ctx.Theme.PickText) : ctx.UiColor(251, 191, 36);
            string statusStr = tile.IsSharp ? "✓ SHARP" : "~ SOFT";
            float statusW = ctx.MeasureWidth(statusStr, ctx.Fonts.MonoTiny);
            float pillW = BottomLayoutMetrics.FaceTileWidth - 6f;

            using (var sbBg = ctx.Dc.CreateSolidColorBrush(statusBg))
            using (var sbFg = ctx.Dc.CreateSolidColorBrush(statusFg))
            {
                ctx.Dc.FillRoundedRectangle(statusPill, sbBg);
                ctx.Dc.DrawText(statusStr, ctx.Fonts.MonoTiny,
                    new Rect(statusPill.Rect.Left + MathF.Max(2f, (pillW - statusW) * 0.5f), statusPill.Rect.Top, statusW + 6f, 15f), sbFg);
            }

            // Active/Hover frame rim
            Color4 borderCol = isZoomed
                ? ctx.UiColor(0, 229, 255)
                : isHovered
                    ? ctx.ThemeColor(ctx.Theme.AccentBlue)
                    : ctx.ThemeColor(ctx.Theme.KeycapBorder);
            float borderW = isZoomed ? 2.2f : isHovered ? 1.5f : 0.9f;
            using (var borderBrush = ctx.Dc.CreateSolidColorBrush(borderCol))
            {
                ctx.Dc.DrawRoundedRectangle(tileRect, borderBrush, borderW);
            }
        }

        ctx.EndFrostedCard(card, alpha);
    }
}
