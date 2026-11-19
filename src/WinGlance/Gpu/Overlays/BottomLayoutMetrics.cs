using System;
using Vortice;
using WinGlance.Model;

namespace WinGlance.Gpu.Overlays;

/// <summary>
/// Unified geometry calculator for bottom floating HUD panels and multi-up pane grids.
/// Ensures spatial separation between Bottom Telemetry HUD, Filmstrip Scrubber, Face Dock, and Mini-Map.
/// </summary>
public static class BottomLayoutMetrics
{
    public const float BottomHudClearance = 80f;
    public const float FilmstripHeight = 88f;
    public const float FilmstripLiftOffset = 98f; // 88f height + 10f breathing gap

    public const float FaceTileWidth = 80f;
    public const float FaceTileHeight = 92f;
    public const float FaceTileGap = 8f;
    public const float FaceDockHorizontalPad = 10f;
    public const float FaceDockVerticalPad = 6f;
    public const float FaceDockHeight = FaceTileHeight + FaceDockVerticalPad * 2f; // 104f

    public static RawRectF ComputeFilmstripBounds(float dipW, float dipH)
    {
        float w = Math.Clamp(dipW - 80f, 320f, 1140f);
        float left = (dipW - w) * 0.5f;
        float bottom = MathF.Max(FilmstripHeight + 64f, dipH - BottomHudClearance);
        float top = bottom - FilmstripHeight;
        return new RawRectF(left, top, left + w, bottom);
    }

    public static RawRectF ComputeFaceDockBounds(float dipW, float dipH, int tileCount, float filmstripOpacity)
    {
        int count = Math.Clamp(tileCount, 1, 8);
        float idealW = count * FaceTileWidth + (count - 1) * FaceTileGap + FaceDockHorizontalPad * 2f;
        float maxW = MathF.Max(FaceTileWidth + FaceDockHorizontalPad * 2f, dipW - 80f);
        float w = MathF.Min(maxW, idealW);
        float left = (dipW - w) * 0.5f;

        float lift = Math.Clamp(filmstripOpacity, 0f, 1f) * FilmstripLiftOffset;
        float bottom = MathF.Max(FaceDockHeight + 64f, dipH - BottomHudClearance - lift);
        float top = bottom - FaceDockHeight;
        return new RawRectF(left, top, left + w, bottom);
    }

    public static RawRectF[] ComputeMultiUpPaneRects(MultiUpMode mode, float dipW, float dipH, bool isSoloLoupe)
    {
        if (isSoloLoupe || mode == MultiUpMode.Single)
        {
            return new[] { new RawRectF(0f, 0f, MathF.Max(1f, dipW), MathF.Max(1f, dipH)) };
        }

        const float pad = 6f;
        if (mode == MultiUpMode.TwoUp)
        {
            float cellW = MathF.Max(1f, (dipW - pad * 3f) * 0.5f);
            float cellH = MathF.Max(1f, dipH - pad * 2f);
            return new[]
            {
                new RawRectF(pad, pad, pad + cellW, pad + cellH),
                new RawRectF(pad * 2f + cellW, pad, pad * 2f + cellW * 2f, pad + cellH)
            };
        }

        // 4-UP 2x2 quad grid
        float qW = MathF.Max(1f, (dipW - pad * 3f) * 0.5f);
        float qH = MathF.Max(1f, (dipH - pad * 3f) * 0.5f);
        return new[]
        {
            new RawRectF(pad, pad, pad + qW, pad + qH),
            new RawRectF(pad * 2f + qW, pad, pad * 2f + qW * 2f, pad + qH),
            new RawRectF(pad, pad * 2f + qH, pad + qW, pad * 2f + qH * 2f),
            new RawRectF(pad * 2f + qW, pad * 2f + qH, pad * 2f + qW * 2f, pad * 2f + qH * 2f)
        };
    }
}
