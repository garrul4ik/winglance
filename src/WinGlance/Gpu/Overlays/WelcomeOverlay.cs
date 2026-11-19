using System;
using Vortice.Mathematics;

namespace WinGlance.Gpu.Overlays;

public sealed class WelcomeOverlay : IOverlayRenderer
{
    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (state.HasPlaylist) return;

        float w = Math.Min(540f, ctx.DipWidth - 48f);
        const float h = 230f;
        var card = OverlayRenderContext.MakeRounded((ctx.DipWidth - w) * 0.5f, (ctx.DipHeight - h) * 0.5f, w, h, 20f);

        ctx.BeginFrostedCard(card, 1.0f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var primary = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var muted = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
        using var accent = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.AccentBlue));

        ctx.Dc.DrawText("WinGlance 1.7", ctx.Fonts.Hero, new Rect(card.Rect.Left, card.Rect.Top + 28f, w, 40f), primary);

        string sub = "Drop a folder or RAW / photo anywhere, or press Ctrl+O to browse";
        float subW = ctx.MeasureWidth(sub, ctx.Fonts.UiBold);
        ctx.Dc.DrawText(sub, ctx.Fonts.UiBold, new Rect(card.Rect.Left + (w - subW) * 0.5f, card.Rect.Top + 78f, subW + 10f, 28f), muted);

        string stack = "Direct3D 11  ·  Direct2D 1.3 HDR16  ·  ICC WCG  ·  16-bit LibRaw  ·  DirectML AI";
        float stackW = ctx.MeasureWidth(stack, ctx.Fonts.MonoSmall);
        ctx.Dc.DrawText(stack, ctx.Fonts.MonoSmall, new Rect(card.Rect.Left + (w - stackW) * 0.5f, card.Rect.Top + 122f, stackW + 10f, 24f), accent);

        string hint = "Press ? for shortcuts   ·   T for Light/Dark theme   ·   Esc to exit";
        float hintW = ctx.MeasureWidth(hint, ctx.Fonts.UiRegular);
        ctx.Dc.DrawText(hint, ctx.Fonts.UiRegular, new Rect(card.Rect.Left + (w - hintW) * 0.5f, card.Rect.Top + 174f, hintW + 10f, 24f), muted);

        ctx.EndFrostedCard(card, 1.0f);
    }
}
