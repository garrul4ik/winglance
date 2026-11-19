using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

public sealed class TopHudOverlay : IOverlayRenderer
{
    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.HudOpacity <= 0.01f) return;

        float alpha = state.HudOpacity;

        // Left status pill
        string counter = $"{state.CurrentIndex + 1} / {state.TotalCount}";
        string extra = "";
        if (state.ActiveFilter.Type != CullingFilterType.All) extra += $"  ·  {state.ActiveFilter.Title}";
        if (state.AutoAdvanceEnabled) extra += "  ·  AUTO";
        if (state.MultiUpMode == MultiUpMode.TwoUp && !state.IsSoloLoupeActive) extra += "  ·  2-UP";
        else if (state.MultiUpMode == MultiUpMode.FourUp && !state.IsSoloLoupeActive) extra += "  ·  4-UP";
        if (state.IsSoloLoupeActive) extra += "  ·  SOLO 1:1";
        if (state.IsSlideshowPlaying) extra += $"  ·  ▶ {state.SlideshowSeconds}s";
        if (state.ShowFocusPeaking) extra += "  ·  PEAK";
        if (state.ShowClipping) extra += "  ·  BLINK";
        if (state.ShowFalseColor) extra += "  ·  IRE";
        if (state.CursorLoupeMagnification > 0) extra += $"  ·  LOUPE {state.CursorLoupeMagnification}×";
        if (state.ShowExifDossier) extra += "  ·  EXIF";
        if (state.Theme == ThemeMode.Light) extra += "  ·  LIGHT";
        if (ctx.IsFp16) extra += "  ·  HDR16";
        if (state.IsLoadingFullRaw) extra += "  ·  16-BIT DEMOSAIC…";

        string leftText = counter + extra;
        float leftW = Math.Clamp(ctx.MeasureWidth(leftText, ctx.Fonts.UiBold) + 30f, 94f, ctx.DipWidth * 0.62f);
        var leftCard = OverlayRenderContext.MakeRounded(20f, 18f, leftW, 34f, 11f);

        ctx.BeginFrostedCard(leftCard, alpha);
        using (var textBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText)))
        {
            ctx.Dc.DrawText(leftText, ctx.Fonts.UiBold, new Rect(leftCard.Rect.Left + 14f, leftCard.Rect.Top, leftW - 28f, 34f), textBrush);
        }
        ctx.EndFrostedCard(leftCard, alpha);

        // Precompute Right Culling Pill bounds so optional Burst / AI badges never overlap it
        var cull = state.PrimaryCulling;
        string cullText = OverlayRenderContext.BuildCullingSummary(cull);
        bool hasCullCard = !string.IsNullOrEmpty(cullText) || cull.Color != ColorLabel.None;
        float dotExtra = cull.Color != ColorLabel.None ? 20f : 0f;
        float rightW = hasCullCard ? Math.Max(56f, ctx.MeasureWidth(cullText, ctx.Fonts.UiBold) + 28f + dotExtra) : 0f;
        float rightCardLeft = hasCullCard ? ctx.DipWidth - rightW - 20f : ctx.DipWidth - 20f;

        // Phase 3 / 5.3: Burst Hero / Breadcrumb, AI Aesthetic & Camera Profile pills (1-UP or Solo Loupe)
        float nextPillX = leftCard.Rect.Right + 8f;
        float pillRightLimit = rightCardLeft - 8f;

        if ((state.MultiUpMode == MultiUpMode.Single || state.IsSoloLoupeActive) && state.PrimaryBurstInfo is { } burst)
        {
            string burstText = burst.FormattedBadge;
            float bw = ctx.MeasureWidth(burstText, ctx.Fonts.UiBold) + 26f;
            if (nextPillX + bw <= pillRightLimit)
            {
                var burstCard = OverlayRenderContext.MakeRounded(nextPillX, 18f, bw, 34f, 11f);
                var burstAccent = burst.IsCurrentHero
                    ? ctx.UiColor(242, 184, 41, alpha * 0.90f)
                    : (Color4?)null;
                var burstTextCol = burst.IsCurrentHero
                    ? (state.Theme == ThemeMode.Light ? ctx.UiColor(179, 110, 0) : ctx.UiColor(255, 212, 77))
                    : ctx.ThemeColor(ctx.Theme.AccentBlue);

                ctx.BeginFrostedCard(burstCard, alpha);
                using (var burstBrush = ctx.Dc.CreateSolidColorBrush(burstTextCol))
                {
                    ctx.Dc.DrawText(burstText, ctx.Fonts.UiBold,
                        new Rect(burstCard.Rect.Left + 13f, burstCard.Rect.Top, bw - 26f, 34f), burstBrush);
                }
                ctx.EndFrostedCard(burstCard, alpha, burstAccent);
                nextPillX += bw + 8f;
            }
        }

        if ((state.MultiUpMode == MultiUpMode.Single || state.IsSoloLoupeActive) && state.PrimaryAesthetic is { } aesthetic)
        {
            string aiText = aesthetic.FormattedBadge;
            float aw = ctx.MeasureWidth(aiText, ctx.Fonts.UiBold) + 24f;
            if (nextPillX + aw <= pillRightLimit)
            {
                var aiCard = OverlayRenderContext.MakeRounded(nextPillX, 18f, aw, 34f, 11f);
                var aiTextCol = aesthetic.OverallScore >= 7.5
                    ? ctx.ThemeColor(ctx.Theme.PickText)
                    : aesthetic.OverallScore >= 5.0
                        ? ctx.ThemeColor(ctx.Theme.AccentBlue)
                        : ctx.ThemeColor(ctx.Theme.MutedText);

                ctx.BeginFrostedCard(aiCard, alpha);
                using (var aiBrush = ctx.Dc.CreateSolidColorBrush(aiTextCol))
                {
                    ctx.Dc.DrawText(aiText, ctx.Fonts.UiBold,
                        new Rect(aiCard.Rect.Left + 12f, aiCard.Rect.Top, aw - 24f, 34f), aiBrush);
                }
                ctx.EndFrostedCard(aiCard, alpha);
                nextPillX += aw + 8f;
            }
        }

        if (state.MultiUpMode == MultiUpMode.Single && state.ActiveCameraProfile != CameraProfile.AsShot)
        {
            string profText = $"🎨 {state.ActiveCameraProfile.GetDisplayName(state.ActiveCameraBrand)}";
            float pw = ctx.MeasureWidth(profText, ctx.Fonts.UiBold) + 24f;
            if (nextPillX + pw <= pillRightLimit)
            {
                var profCard = OverlayRenderContext.MakeRounded(nextPillX, 18f, pw, 34f, 11f);
                var indigoText = state.Theme == ThemeMode.Light ? ctx.UiColor(79, 70, 229) : ctx.UiColor(165, 180, 252);
                var indigoBorder = ctx.UiColor(99, 102, 241, alpha * 0.85f);

                ctx.BeginFrostedCard(profCard, alpha);
                using (var profBrush = ctx.Dc.CreateSolidColorBrush(indigoText))
                {
                    ctx.Dc.DrawText(profText, ctx.Fonts.UiBold,
                        new Rect(profCard.Rect.Left + 12f, profCard.Rect.Top, pw - 24f, 34f), profBrush);
                }
                ctx.EndFrostedCard(profCard, alpha, indigoBorder);
            }
        }

        // Right culling status pill (if Pick/Reject/Rating/Color set)
        if (hasCullCard)
        {
            var rightCard = OverlayRenderContext.MakeRounded(rightCardLeft, 18f, rightW, 34f, 11f);

            Color4? accent = cull.Pick switch
            {
                PickStatus.Pick => ctx.ThemeColor(ctx.Theme.PickAccentBorder, alpha),
                PickStatus.Reject => ctx.ThemeColor(ctx.Theme.RejectAccentBorder, alpha),
                _ => null
            };

            ctx.BeginFrostedCard(rightCard, alpha);
            float tx = rightCard.Rect.Left + 14f;

            if (cull.Color != ColorLabel.None)
            {
                using var dotBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(GlassTheme.ColorForLabel(cull.Color)));
                ctx.Dc.FillEllipse(new Ellipse(new Vector2(tx + 5f, rightCard.Rect.Top + 17f), 5.5f, 5.5f), dotBrush);
                tx += 18f;
            }

            if (!string.IsNullOrEmpty(cullText))
            {
                var fg = cull.Pick == PickStatus.Pick
                    ? ctx.ThemeColor(ctx.Theme.PickText)
                    : cull.Pick == PickStatus.Reject
                        ? ctx.ThemeColor(ctx.Theme.RejectText)
                        : ctx.ThemeColor(ctx.Theme.GoldStarText);
                using var fgBrush = ctx.Dc.CreateSolidColorBrush(fg);
                ctx.Dc.DrawText(cullText, ctx.Fonts.UiBold, new Rect(tx, rightCard.Rect.Top, rightW - 20f, 34f), fgBrush);
            }

            ctx.EndFrostedCard(rightCard, alpha, accent);
        }
    }
}
