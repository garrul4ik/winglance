using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WinGlance.Gpu.Overlays;

public sealed class FalseColorLegendOverlay : IOverlayRenderer
{
    private readonly record struct LegendEntry(string Ire, string Label, byte R, byte G, byte B);

    private static readonly LegendEntry[] Entries =
    {
        new("100", "CLIP",     255,   0,  43),
        new(" 98", "ROLL",     255, 230,   0),
        new(" 80", "HIGH",     255, 122,   0),
        new(" 54", "SKIN",     255, 102, 178),
        new(" 40", "18% GRAY",   0, 230,  77),
        new("  3", "SHADOW",     0, 122, 255),
        new("  0", "CRUSH",    139,  31, 162)
    };

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || !state.ShowFalseColor) return;

        const float cardW = 152f;
        const float cardH = 222f;
        var card = OverlayRenderContext.MakeRounded(20f, 64f, cardW, cardH, 13f);

        ctx.BeginFrostedCard(card, 0.96f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var primaryBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var secondaryBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
        using var dividerBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.Divider));

        ctx.Dc.DrawText("ARRI FALSE COLOR", ctx.Fonts.MonoTiny,
            new Rect(card.Rect.Left + 10f, card.Rect.Top + 8f, cardW - 20f, 16f), primaryBrush);

        float divY = card.Rect.Top + 29f;
        ctx.Dc.DrawLine(new Vector2(card.Rect.Left + 10f, divY), new Vector2(card.Rect.Right - 10f, divY), dividerBrush, 0.75f);

        float y = card.Rect.Top + 36f;
        const float rowH = 25f;

        for (int i = 0; i < Entries.Length; i++)
        {
            var e = Entries[i];
            var swatch = OverlayRenderContext.MakeRounded(card.Rect.Left + 12f, y + 4f, 16f, 14f, 4f);
            using var swatchBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(e.R, e.G, e.B));
            ctx.Dc.FillRoundedRectangle(swatch, swatchBrush);

            ctx.Dc.DrawText(e.Ire, ctx.Fonts.MonoTiny, new Rect(card.Rect.Left + 34f, y, 28f, rowH), secondaryBrush);
            ctx.Dc.DrawText(e.Label, ctx.Fonts.MonoTiny, new Rect(card.Rect.Left + 66f, y, cardW - 76f, rowH), primaryBrush);

            y += rowH;
        }

        ctx.EndFrostedCard(card, 0.96f);
    }
}
