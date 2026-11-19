using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

public sealed class ExifDossierOverlay : IOverlayRenderer
{
    public const float CompactWidth = 316f;
    public const float ExpandedWidth = 376f;
    public const float CompactHeight = 166f;
    public const float ExpandedHeight = 358f;

    public static RawRectF ComputeBounds(float dipW, float dipH, float hoverProgress)
    {
        float t = Math.Clamp(hoverProgress, 0f, 1f);
        float w = Math.Min(dipW - 40f, CompactWidth + (ExpandedWidth - CompactWidth) * t);
        float maxH = MathF.Max(CompactHeight, dipH - 160f);
        float h = MathF.Min(maxH, CompactHeight + (ExpandedHeight - CompactHeight) * t);
        return new RawRectF(dipW - w - 20f, 64f, dipW - 20f, 64f + h);
    }

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || !state.ShowExifDossier || state.PrimaryItem == null) return;

        float t = Math.Clamp(state.ExifHoverProgress, 0f, 1f);
        var bounds = ComputeBounds(ctx.DipWidth, ctx.DipHeight, t);
        var card = new RoundedRectangle { Rect = bounds, RadiusX = 14f, RadiusY = 14f };
        float w = bounds.Right - bounds.Left;

        ctx.BeginFrostedCard(card, 0.97f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var primaryBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var secondaryBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
        using var mutedBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.MutedText));
        using var accentBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.AccentBlue));
        using var wellBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.WellBackground));
        using var divBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.Divider));

        var img = ctx.PrimaryImage;
        var exif = img?.Exif;

        // 1. Header
        ctx.Dc.DrawText("EXIF DOSSIER", ctx.Fonts.MonoTiny,
            new Rect(bounds.Left + 14f, bounds.Top + 10f, 110f, 18f), accentBrush);

        string dateText = exif?.FormattedDate ?? "";
        if (!string.IsNullOrEmpty(dateText))
        {
            float dateW = ctx.MeasureWidth(dateText, ctx.Fonts.MonoTiny);
            ctx.Dc.DrawText(dateText, ctx.Fonts.MonoTiny,
                new Rect(bounds.Right - dateW - 14f, bounds.Top + 10f, dateW + 4f, 18f), mutedBrush);
        }

        // 2. Recessed 4-Column Exposure Well
        var wellRect = OverlayRenderContext.MakeRounded(bounds.Left + 12f, bounds.Top + 34f, w - 24f, 44f, 8f);
        ctx.Dc.FillRoundedRectangle(wellRect, wellBrush);

        float colW = (w - 24f) * 0.25f;
        DrawExposureMetric(ctx, wellRect.Rect.Left + 0 * colW, wellRect.Rect.Top, colW, "SHUTTER", exif?.FormattedShutter ?? "—", mutedBrush, primaryBrush);
        DrawExposureMetric(ctx, wellRect.Rect.Left + 1 * colW, wellRect.Rect.Top, colW, "APERTURE", exif?.FormattedAperture ?? "—", mutedBrush, primaryBrush);
        DrawExposureMetric(ctx, wellRect.Rect.Left + 2 * colW, wellRect.Rect.Top, colW, "ISO", exif?.Iso is > 0 ? exif.Iso.Value.ToString() : "—", mutedBrush, primaryBrush);
        DrawExposureMetric(ctx, wellRect.Rect.Left + 3 * colW, wellRect.Rect.Top, colW, "FOCAL", exif?.FormattedFocal ?? "—", mutedBrush, primaryBrush);

        // 3. Camera, Lens & Dimensions Summary
        string camText = FormatCameraLine(exif) ?? state.PrimaryItem.DisplayName;
        string lensText = !string.IsNullOrWhiteSpace(exif?.LensModel)
            ? exif.LensModel!
            : (exif?.LensSpecification ?? "Lens data unavailable");

        int origW = img?.OriginalWidth ?? 0;
        int origH = img?.OriginalHeight ?? 0;
        string resText = origW > 0 && origH > 0
            ? $"{origW}×{origH} ({ExifMetadata.FormatMegapixels(origW, origH)})  ·  {OverlayRenderContext.FormatBytes(state.PrimaryItem.TotalStackSize)}"
            : OverlayRenderContext.FormatBytes(state.PrimaryItem.TotalStackSize);

        ctx.Dc.DrawText(camText, ctx.Fonts.UiBold, new Rect(bounds.Left + 14f, bounds.Top + 86f, w - 28f, 20f), primaryBrush);
        ctx.Dc.DrawText(lensText, ctx.Fonts.UiRegular, new Rect(bounds.Left + 14f, bounds.Top + 106f, w - 28f, 18f), secondaryBrush);
        ctx.Dc.DrawText(resText, ctx.Fonts.MonoTiny, new Rect(bounds.Left + 14f, bounds.Top + 126f, w - 28f, 16f), mutedBrush);

        if (t < 0.15f)
        {
            ctx.Dc.DrawText("Hover to unfold  ·  Click card to copy EXIF", ctx.Fonts.MonoTiny,
                new Rect(bounds.Left + 14f, bounds.Top + 144f, w - 28f, 16f), mutedBrush);
        }

        // 4. Expanded Photographer Dossier (revealed smoothly on hover)
        if (t > 0.04f)
        {
            ctx.Dc.PushAxisAlignedClip(bounds, AntialiasMode.PerPrimitive);

            float expDivY = bounds.Top + 158f;
            ctx.Dc.DrawLine(new Vector2(bounds.Left + 14f, expDivY), new Vector2(bounds.Right - 14f, expDivY), divBrush, 0.75f);

            using var expPrimary = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText, t));
            using var expMuted = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.MutedText, t));
            using var expAccent = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.AccentBlue, t));

            float halfW = (w - 36f) * 0.5f;
            float leftColX = bounds.Left + 14f;
            float rightColX = leftColX + halfW + 10f;
            float rowStartY = bounds.Top + 166f;
            const float rowStep = 31f;

            DrawDetailRow(ctx, leftColX, rowStartY + 0 * rowStep, halfW, "PROGRAM", exif?.FormattedExposureProgram ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, leftColX, rowStartY + 1 * rowStep, halfW, "METERING", exif?.FormattedMeteringMode ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, leftColX, rowStartY + 2 * rowStep, halfW, "EXP BIAS", exif?.FormattedBiasDetailed ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, leftColX, rowStartY + 3 * rowStep, halfW, "WHITE BAL", exif?.FormattedWhiteBalance ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, leftColX, rowStartY + 4 * rowStep, halfW, "OPTICS", exif?.FormattedFocalAndCrop ?? "—", expMuted, expPrimary);

            string colorName = exif?.FormattedColorSpace(img?.ColorProfileName) ?? (img?.ColorProfileName ?? "sRGB");
            string lensSpecText = exif?.LensSpecification
                ?? (exif?.MaxAperture is > 0 ? $"Max ƒ/{exif.MaxAperture.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}" : "—");
            string hardwareText = exif?.ShutterCount is > 0
                ? $"{exif.ShutterCount.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} actuations"
                : (!string.IsNullOrWhiteSpace(exif?.BodySerialNumber)
                    ? $"S/N {exif.BodySerialNumber}"
                    : (!string.IsNullOrWhiteSpace(exif?.LensSerialNumber) ? $"Lens {exif.LensSerialNumber}" : "—"));

            DrawDetailRow(ctx, rightColX, rowStartY + 0 * rowStep, halfW, "COLOR SPACE", colorName, expMuted, expPrimary);
            DrawDetailRow(ctx, rightColX, rowStartY + 1 * rowStep, halfW, "FLASH", exif?.FormattedFlash ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, rightColX, rowStartY + 2 * rowStep, halfW, "LENS SPEC", lensSpecText, expMuted, expPrimary);
            DrawDetailRow(ctx, rightColX, rowStartY + 3 * rowStep, halfW, "SOFTWARE", exif?.Software ?? "—", expMuted, expPrimary);
            DrawDetailRow(ctx, rightColX, rowStartY + 4 * rowStep, halfW, "HARDWARE", hardwareText, expMuted, expPrimary);

            ctx.Dc.DrawText("Click card to copy full EXIF summary", ctx.Fonts.MonoTiny,
                new Rect(bounds.Left + 14f, bounds.Bottom - 24f, w - 28f, 16f), expAccent);

            ctx.Dc.PopAxisAlignedClip();
        }

        ctx.EndFrostedCard(card, 0.97f);
    }

    private static void DrawExposureMetric(
        in OverlayRenderContext ctx,
        float x,
        float y,
        float w,
        string label,
        string value,
        ID2D1SolidColorBrush labelBrush,
        ID2D1SolidColorBrush valueBrush)
    {
        ctx.Dc.DrawText(label, ctx.Fonts.MonoTiny, new Rect(x + 4f, y + 4f, w - 8f, 14f), labelBrush);
        ctx.Dc.DrawText(value, ctx.Fonts.UiBold, new Rect(x + 4f, y + 18f, w - 8f, 22f), valueBrush);
    }

    private static void DrawDetailRow(
        in OverlayRenderContext ctx,
        float x,
        float y,
        float w,
        string title,
        string value,
        ID2D1SolidColorBrush titleBrush,
        ID2D1SolidColorBrush valueBrush)
    {
        ctx.Dc.DrawText(title, ctx.Fonts.MonoTiny, new Rect(x, y, w, 13f), titleBrush);
        ctx.Dc.DrawText(value, ctx.Fonts.UiRegular, new Rect(x, y + 12f, w, 17f), valueBrush);
    }

    private static string? FormatCameraLine(ExifMetadata? exif)
    {
        if (exif == null) return null;
        if (!string.IsNullOrWhiteSpace(exif.CameraModel))
        {
            if (!string.IsNullOrWhiteSpace(exif.CameraMake) &&
                !exif.CameraModel.Contains(exif.CameraMake, StringComparison.OrdinalIgnoreCase))
            {
                return $"{exif.CameraMake} {exif.CameraModel}";
            }
            return exif.CameraModel;
        }
        return exif.CameraMake;
    }
}
