using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

public sealed class CursorLoupeOverlay : IOverlayRenderer
{
    public const float TotalDiameter = 226f;
    public const float GlassDiameter = 210f;
    public const float BezelWidth = 8f;
    public const float BadgeWidth = 58f;
    public const float BadgeHeight = 22f;
    public const float BadgeGap = 8f;

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.HasPlaylist || state.CursorLoupeMagnification <= 0 || state.IsCustomZoom || !state.IsCursorInsideClient)
        {
            return;
        }

        float totalR = TotalDiameter * 0.5f;
        float glassR = GlassDiameter * 0.5f;

        // Keep loupe smoothly inside viewport edges while centered on cursor
        float cx = Math.Clamp(state.CursorDip.X, totalR + 6f, MathF.Max(totalR + 6f, ctx.DipWidth - totalR - 6f));
        float cy = Math.Clamp(state.CursorDip.Y, totalR + 6f, MathF.Max(totalR + 6f, ctx.DipHeight - totalR - BadgeGap - BadgeHeight - 6f));
        var center = new Vector2(cx, cy);

        // Select pane & bitmap under cursor across 1-UP / 2-UP / 4-UP
        int hitSlot = 0;
        if (!state.IsSoloLoupeActive && state.MultiUpMode != MultiUpMode.Single && ctx.MultiUpPanesDip.Length > 1)
        {
            for (int s = 0; s < ctx.MultiUpPanesDip.Length; s++)
            {
                var p = ctx.MultiUpPanesDip[s];
                if (state.CursorDip.X >= p.Left && state.CursorDip.X <= p.Right &&
                    state.CursorDip.Y >= p.Top && state.CursorDip.Y <= p.Bottom)
                {
                    hitSlot = s;
                    break;
                }
            }
        }
        else if (state.IsSoloLoupeActive)
        {
            hitSlot = Math.Clamp(state.ActiveMultiUpSlot, 0, ctx.SlotBitmaps.Length - 1);
        }

        var bitmap = (hitSlot < ctx.SlotBitmaps.Length && ctx.SlotBitmaps[hitSlot] != null)
            ? ctx.SlotBitmaps[hitSlot]
            : ctx.PrimaryBitmap;
        var destDip = (hitSlot < ctx.SlotDestsDip.Length && ctx.SlotBitmaps[hitSlot] != null)
            ? ctx.SlotDestsDip[hitSlot]
            : ctx.PrimaryDestDip;
        int mag = state.CursorLoupeMagnification;

        // 1. Soft drop shadow halo behind barrel
        using (var shadowBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(0, 0, 0, 0.42f)))
        {
            ctx.Dc.FillEllipse(new Ellipse(new Vector2(cx, cy + 5f), totalR + 2f, totalR + 2f), shadowBrush);
        }

        // 2. Machined dark anodized barrel background
        using (var barrelBrush = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(18, 20, 26, 0.98f)))
        {
            ctx.Dc.FillEllipse(new Ellipse(center, totalR, totalR), barrelBrush);
        }

        // 3. Engraved bezel radial tick marks (36 ticks every 10°)
        using (var minorTick = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 255, 255, 0.24f)))
        using (var majorTick = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 255, 255, 0.52f)))
        {
            float outerTickR = totalR - 1.5f;
            for (int deg = 0; deg < 360; deg += 10)
            {
                bool cardinal = deg % 90 == 0;
                float innerTickR = cardinal ? glassR + 1.5f : glassR + 3.8f;
                float rad = deg * (MathF.PI / 180f);
                float cos = MathF.Cos(rad);
                float sin = MathF.Sin(rad);
                ctx.Dc.DrawLine(
                    new Vector2(cx + innerTickR * cos, cy + innerTickR * sin),
                    new Vector2(cx + outerTickR * cos, cy + outerTickR * sin),
                    cardinal ? majorTick : minorTick,
                    cardinal ? 1.2f : 0.8f);
            }
        }

        // 4. Circular optical glass aperture (clipped)
        using (var glassGeo = ctx.Factory.CreateEllipseGeometry(new Ellipse(center, glassR, glassR)))
        {
            var glassRect = new RawRectF(cx - glassR, cy - glassR, cx + glassR, cy + glassR);
            var layerParams = new LayerParameters1
            {
                ContentBounds = glassRect,
                GeometricMask = glassGeo,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1.0f,
                LayerOptions = LayerOptions1.None
            };

            ctx.Dc.PushLayer(ref layerParams, null);

            using (var canvasBg = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.CanvasBackground)))
            {
                ctx.Dc.FillRectangle(glassRect, canvasBg);
            }

            if (bitmap != null)
            {
                float destW = MathF.Max(1f, destDip.Right - destDip.Left);
                float destH = MathF.Max(1f, destDip.Bottom - destDip.Top);

                float bmpW = bitmap.PixelSize.Width;
                float bmpH = bitmap.PixelSize.Height;

                // Cursor normalized coordinates relative to the displayed photo rect
                float u = (state.CursorDip.X - destDip.Left) / destW;
                float v = (state.CursorDip.Y - destDip.Top) / destH;

                if (u is >= 0f and <= 1f && v is >= 0f and <= 1f && bmpW > 0f && bmpH > 0f)
                {
                    // Sample src region sized so aperture renders at mag× display scale
                    float srcSpanW = (GlassDiameter / destW) * (bmpW / mag);
                    float srcSpanH = (GlassDiameter / destH) * (bmpH / mag);

                    float srcCenterX = u * bmpW;
                    float srcCenterY = v * bmpH;

                    float rawL = srcCenterX - srcSpanW * 0.5f;
                    float rawT = srcCenterY - srcSpanH * 0.5f;
                    float rawR = srcCenterX + srcSpanW * 0.5f;
                    float rawB = srcCenterY + srcSpanH * 0.5f;

                    float clampL = Math.Clamp(rawL, 0f, bmpW);
                    float clampT = Math.Clamp(rawT, 0f, bmpH);
                    float clampR = Math.Clamp(rawR, 0f, bmpW);
                    float clampB = Math.Clamp(rawB, 0f, bmpH);

                    if (clampR > clampL + 0.5f && clampB > clampT + 0.5f)
                    {
                        // Map clamped source rect proportionally into aperture so edges never stretch
                        float dstL = glassRect.Left + ((clampL - rawL) / MathF.Max(0.001f, srcSpanW)) * GlassDiameter;
                        float dstT = glassRect.Top + ((clampT - rawT) / MathF.Max(0.001f, srcSpanH)) * GlassDiameter;
                        float dstR = glassRect.Left + ((clampR - rawL) / MathF.Max(0.001f, srcSpanW)) * GlassDiameter;
                        float dstB = glassRect.Top + ((clampB - rawT) / MathF.Max(0.001f, srcSpanH)) * GlassDiameter;

                        float effectivePxRatio = (GlassDiameter * ctx.DpiScale) / MathF.Max(1f, srcSpanW);
                        var interp = effectivePxRatio >= 1.95f
                            ? BitmapInterpolationMode.NearestNeighbor
                            : BitmapInterpolationMode.Linear;

                        ctx.Dc.DrawBitmap(
                            bitmap,
                            new RawRectF(dstL, dstT, dstR, dstB),
                            1.0f,
                            interp,
                            new RawRectF(clampL, clampT, clampR, clampB));
                    }
                }
            }

            // Subtle optical vignette inside glass rim
            GradientStop[] stops =
            {
                new(0.65f, ctx.UiColor(0, 0, 0, 0.0f)),
                new(0.92f, ctx.UiColor(0, 0, 0, 0.14f)),
                new(1.00f, ctx.UiColor(0, 0, 0, 0.36f))
            };
            using (var stopCollection = ctx.Dc.CreateGradientStopCollection(stops))
            using (var vignetteBrush = ctx.Dc.CreateRadialGradientBrush(
                new RadialGradientBrushProperties
                {
                    Center = center,
                    GradientOriginOffset = Vector2.Zero,
                    RadiusX = glassR,
                    RadiusY = glassR
                },
                stopCollection))
            {
                ctx.Dc.FillEllipse(new Ellipse(center, glassR, glassR), vignetteBrush);
            }

            // High-contrast dual-contour viewfinder reticle
            DrawReticle(ctx, cx, cy);

            ctx.Dc.PopLayer();
        }

        // 5. Outer metallic chamfer, inner glass coating rim
        using (var outerRim = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 255, 255, 0.28f)))
        using (var innerRim = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.CyanIndicator with { A = 0.55f })))
        {
            ctx.Dc.DrawEllipse(new Ellipse(center, totalR - 0.5f, totalR - 0.5f), outerRim, 1.0f);
            ctx.Dc.DrawEllipse(new Ellipse(center, glassR, glassR), innerRim, 1.25f);
        }

        // 6. Detached floating multiplier capsule pill (8px below barrel)
        float pillX = cx - BadgeWidth * 0.5f;
        float pillY = cy + totalR + BadgeGap;
        var pillCard = OverlayRenderContext.MakeRounded(pillX, pillY, BadgeWidth, BadgeHeight, BadgeHeight * 0.5f);

        ctx.BeginFrostedCard(pillCard, 0.96f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);
        using (var dotBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.CyanIndicator)))
        using (var txtBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText)))
        {
            ctx.Dc.FillEllipse(new Ellipse(new Vector2(pillX + 13f, pillY + BadgeHeight * 0.5f), 3.2f, 3.2f), dotBrush);
            ctx.Dc.DrawText($"{mag}×", ctx.Fonts.MonoSmall,
                new Rect(pillX + 20f, pillY, BadgeWidth - 26f, BadgeHeight), txtBrush);
        }
        ctx.EndFrostedCard(pillCard, 0.96f);
    }

    private static void DrawReticle(in OverlayRenderContext ctx, float cx, float cy)
    {
        const float gap = 7f;
        const float arm = 15f;

        using var darkContour = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(0, 0, 0, 0.65f));
        using var brightContour = ctx.Dc.CreateSolidColorBrush(ctx.UiColor(255, 255, 255, 0.90f));

        // Shadow under-stroke
        ctx.Dc.DrawLine(new Vector2(cx - gap - arm, cy), new Vector2(cx - gap, cy), darkContour, 2.4f);
        ctx.Dc.DrawLine(new Vector2(cx + gap, cy), new Vector2(cx + gap + arm, cy), darkContour, 2.4f);
        ctx.Dc.DrawLine(new Vector2(cx, cy - gap - arm), new Vector2(cx, cy - gap), darkContour, 2.4f);
        ctx.Dc.DrawLine(new Vector2(cx, cy + gap), new Vector2(cx, cy + gap + arm), darkContour, 2.4f);

        // Crisp bright reticle
        ctx.Dc.DrawLine(new Vector2(cx - gap - arm, cy), new Vector2(cx - gap, cy), brightContour, 1.1f);
        ctx.Dc.DrawLine(new Vector2(cx + gap, cy), new Vector2(cx + gap + arm, cy), brightContour, 1.1f);
        ctx.Dc.DrawLine(new Vector2(cx, cy - gap - arm), new Vector2(cx, cy - gap), brightContour, 1.1f);
        ctx.Dc.DrawLine(new Vector2(cx, cy + gap), new Vector2(cx, cy + gap + arm), brightContour, 1.1f);

        ctx.Dc.FillEllipse(new Ellipse(new Vector2(cx, cy), 1.6f, 1.6f), brightContour);
    }
}
