using System;
using Vortice.Mathematics;

namespace WinGlance.Gpu.Overlays;

public sealed class ToastOverlay : IOverlayRenderer
{
    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.ToastOpacity <= 0.01f || string.IsNullOrEmpty(state.ToastText)) return;

        float w = Math.Clamp(ctx.MeasureWidth(state.ToastText, ctx.Fonts.Toast) + 40f, 130f, ctx.DipWidth - 60f);
        const float h = 44f;
        float topY = Math.Min(62f, ctx.DipHeight * 0.15f);
        var card = OverlayRenderContext.MakeRounded((ctx.DipWidth - w) * 0.5f, topY, w, h, 14f);

        ctx.BeginFrostedCard(card, state.ToastOpacity, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);
        using (var brush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText)))
        {
            ctx.Dc.DrawText(state.ToastText, ctx.Fonts.Toast, new Rect(card.Rect.Left + 12f, card.Rect.Top, w - 24f, h), brush);
        }
        ctx.EndFrostedCard(card, state.ToastOpacity);
    }
}
