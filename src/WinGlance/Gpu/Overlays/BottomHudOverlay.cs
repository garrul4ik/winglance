using System;
using Vortice.Mathematics;

namespace WinGlance.Gpu.Overlays;

public sealed class BottomHudOverlay : IOverlayRenderer
{
    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.HudOpacity <= 0.01f || state.PrimaryItem == null) return;

        var img = ctx.PrimaryImage;
        float alpha = state.HudOpacity;

        string zoomTag = !state.IsCustomZoom
            ? "FIT"
            : MathF.Abs(state.PixelZoomScale - 1.0f) < 0.03f
                ? "100% 1:1"
                : $"{(int)MathF.Round(state.PixelZoomScale * 100f)}%";

        string formatBadge = img == null
            ? ""
            : img.IsFullSensorRaw
                ? "RAW 16-bit"
                : img.IsRaw
                    ? "RAW Preview"
                    : img.BitsPerChannel == 16 ? "16-bit" : "8-bit";

        string colorBadge = img?.ColorProfileName ?? "sRGB";
        string dims = img != null ? $"{img.OriginalWidth}×{img.OriginalHeight}" : "";
        string sizeStr = OverlayRenderContext.FormatBytes(state.PrimaryItem.TotalStackSize);

        string line1 = $"{state.PrimaryItem.DisplayName}   ·   {dims}   ·   {sizeStr}   ·   {formatBadge}   ·   {colorBadge}   ·   {zoomTag}";

        string line2 = "";
        if (img?.Exif != null)
        {
            var cam = img.Exif.CameraAndLensSummary;
            var exp = img.Exif.ExposureSummary;
            if (!string.IsNullOrEmpty(cam) && !string.IsNullOrEmpty(exp)) line2 = $"{cam}   ·   {exp}";
            else line2 = !string.IsNullOrEmpty(cam) ? cam : exp;
        }

        bool hasLine2 = !string.IsNullOrWhiteSpace(line2);
        float w1 = ctx.MeasureWidth(line1, ctx.Fonts.UiBold);
        float w2 = hasLine2 ? ctx.MeasureWidth(line2, ctx.Fonts.UiRegular) : 0f;
        float cardW = Math.Clamp(MathF.Max(w1, w2) + 40f, 280f, ctx.DipWidth - 40f);
        float cardH = hasLine2 ? 54f : 36f;

        var card = OverlayRenderContext.MakeRounded((ctx.DipWidth - cardW) * 0.5f, ctx.DipHeight - cardH - 18f, cardW, cardH, 13f);
        ctx.BeginFrostedCard(card, alpha);

        using var primaryText = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        if (hasLine2)
        {
            ctx.Dc.DrawText(line1, ctx.Fonts.UiBold, new Rect(card.Rect.Left + 20f, card.Rect.Top + 4f, cardW - 40f, 24f), primaryText);
            using var subText = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
            ctx.Dc.DrawText(line2, ctx.Fonts.UiRegular, new Rect(card.Rect.Left + 20f, card.Rect.Top + 26f, cardW - 40f, 22f), subText);
        }
        else
        {
            ctx.Dc.DrawText(line1, ctx.Fonts.UiBold, new Rect(card.Rect.Left + 20f, card.Rect.Top, cardW - 40f, cardH), primaryText);
        }

        ctx.EndFrostedCard(card, alpha);
    }
}
