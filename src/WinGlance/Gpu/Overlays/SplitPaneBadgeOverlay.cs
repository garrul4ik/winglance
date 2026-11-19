using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

/// <summary>
/// Frosted glass per-pane overlay badges rendered in 2-UP and 4-UP comparison modes:
/// slot number accent, filename, Pick/Reject/Star culling summary, AI score, Hero badge, and 2-UP King-of-the-Hill hint.
/// </summary>
public sealed class SplitPaneBadgeOverlay : IOverlayRenderer
{
    public static Color4 GetSlotAccentColor(in OverlayRenderContext ctx, int slotIndex, float alpha = 1f) => slotIndex switch
    {
        0 => ctx.UiColor(20, 184, 166, alpha),  // Teal
        1 => ctx.UiColor(245, 158, 11, alpha),  // Amber
        2 => ctx.UiColor(168, 85, 247, alpha),  // Purple
        _ => ctx.UiColor(16, 185, 129, alpha)   // Emerald
    };

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.MultiUpMode == MultiUpMode.Single || state.IsSoloLoupeActive || state.HudOpacity <= 0.01f)
            return;

        int slotCount = Math.Min((int)state.MultiUpMode, ctx.MultiUpPanesDip.Length);
        if (slotCount < 2) return;

        float alpha = state.HudOpacity;

        // Determine pairwise winner in 2-UP via AestheticScoreResult.CompareMatchup
        int twoUpBestSlot = -1;
        if (state.MultiUpMode == MultiUpMode.TwoUp &&
            state.MultiUpSlots[0].Aesthetic is { } s0 &&
            state.MultiUpSlots[1].Aesthetic is { } s1)
        {
            int cmp = AestheticScoreResult.CompareMatchup(s0, s1);
            if (cmp > 0) twoUpBestSlot = 0;
            else if (cmp < 0) twoUpBestSlot = 1;
        }

        for (int i = 0; i < slotCount; i++)
        {
            var slot = state.MultiUpSlots[i];
            if (slot.Item == null) continue;

            var pane = ctx.MultiUpPanesDip[i];
            bool isActive = i == state.ActiveMultiUpSlot;
            float paneW = pane.Right - pane.Left;
            if (paneW < 140f) continue;

            float topOffset = i < 2 ? 62f : 14f;
            float badgeY = pane.Top + topOffset;
            float badgeX = pane.Left + 14f;

            string slotTag = $"[{i + 1}]";
            string name = slot.Item.DisplayName;
            string cullSummary = OverlayRenderContext.BuildCullingSummary(slot.Culling);
            string aiText = slot.Aesthetic is { } ai
                ? (i == twoUpBestSlot ? $"👑 BEST {ai.FormattedScore}" : ai.FormattedBadge)
                : "";
            string heroTag = slot.IsHero ? "👑" : "";

            string label = $"{slotTag}  {name}";
            if (!string.IsNullOrEmpty(cullSummary)) label += $"  ·  {cullSummary}";
            if (!string.IsNullOrEmpty(heroTag)) label += $"  ·  {heroTag}";
            if (!string.IsNullOrEmpty(aiText)) label += $"  ·  {aiText}";

            float dotExtra = slot.Culling.Color != ColorLabel.None ? 16f : 0f;
            float maxBadgeW = MathF.Max(120f, paneW - 28f);
            float badgeW = MathF.Min(maxBadgeW, ctx.MeasureWidth(label, ctx.Fonts.UiBold) + 28f + dotExtra);
            var card = OverlayRenderContext.MakeRounded(badgeX, badgeY, badgeW, 30f, 10f);

            Color4? borderAccent = isActive
                ? GetSlotAccentColor(ctx, i, alpha * 0.92f)
                : slot.Culling.Pick switch
                {
                    PickStatus.Pick => ctx.ThemeColor(ctx.Theme.PickAccentBorder, alpha),
                    PickStatus.Reject => ctx.ThemeColor(ctx.Theme.RejectAccentBorder, alpha),
                    _ => null
                };

            ctx.BeginFrostedCard(card, alpha);
            float tx = card.Rect.Left + 12f;

            if (slot.Culling.Color != ColorLabel.None)
            {
                using var dotBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(GlassTheme.ColorForLabel(slot.Culling.Color)));
                ctx.Dc.FillEllipse(new Ellipse(new Vector2(tx + 4f, card.Rect.Top + 15f), 4.5f, 4.5f), dotBrush);
                tx += 15f;
            }

            Color4 fg = i == twoUpBestSlot || slot.IsHero
                ? (state.Theme == ThemeMode.Light ? ctx.UiColor(179, 110, 0) : ctx.UiColor(255, 212, 77))
                : slot.Culling.Pick == PickStatus.Pick
                    ? ctx.ThemeColor(ctx.Theme.PickText)
                    : slot.Culling.Pick == PickStatus.Reject
                        ? ctx.ThemeColor(ctx.Theme.RejectText)
                        : isActive
                            ? GetSlotAccentColor(ctx, i)
                            : ctx.ThemeColor(ctx.Theme.PrimaryText);

            using (var textBrush = ctx.Dc.CreateSolidColorBrush(fg))
            {
                ctx.Dc.DrawText(label, ctx.Fonts.UiBold,
                    new Rect(tx, card.Rect.Top, MathF.Max(20f, card.Rect.Right - tx - 10f), 30f), textBrush);
            }
            ctx.EndFrostedCard(card, alpha, borderAccent);
        }

        // Subtle King-of-the-Hill control pill centered near top in 2-UP when filmstrip/dock are clear
        if (state.MultiUpMode == MultiUpMode.TwoUp && state.MultiUpSlots[0].Item != null && state.MultiUpSlots[1].Item != null)
        {
            const string hint = "King of the Hill: [1] Left Wins  ·  [2] Right Wins  ·  [Tab] Switch Slot";
            float hw = ctx.MeasureWidth(hint, ctx.Fonts.MonoSmall) + 24f;
            if (hw < ctx.DipWidth - 40f)
            {
                var hintCard = OverlayRenderContext.MakeRounded((ctx.DipWidth - hw) * 0.5f, 18f, hw, 30f, 10f);
                ctx.BeginFrostedCard(hintCard, alpha * 0.92f);
                using (var hb = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText)))
                {
                    ctx.Dc.DrawText(hint, ctx.Fonts.MonoSmall,
                        new Rect(hintCard.Rect.Left + 12f, hintCard.Rect.Top, hw - 24f, 30f), hb);
                }
                ctx.EndFrostedCard(hintCard, alpha * 0.92f);
            }
        }
    }
}
