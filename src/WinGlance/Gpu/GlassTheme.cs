using System;
using Vortice.Mathematics;
using WinGlance.Model;

namespace WinGlance.Gpu;

public enum ThemeMode
{
    Dark,
    Light
}

public readonly record struct Rgba(byte R, byte G, byte B, float A = 1.0f)
{
    public Color4 ToColor4(bool isFp16, float alphaMultiplier = 1.0f)
    {
        float a = Math.Clamp(A * alphaMultiplier, 0f, 1f);
        float rf = R / 255.0f;
        float gf = G / 255.0f;
        float bf = B / 255.0f;

        if (isFp16)
        {
            return new Color4(SrgbToLinear(rf), SrgbToLinear(gf), SrgbToLinear(bf), a);
        }
        return new Color4(rf, gf, bf, a);
    }

    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}

public sealed class GlassTheme
{
    public ThemeMode Mode { get; init; }
    public Rgba CanvasBackground { get; init; }
    public Rgba CardTint { get; init; }
    public Rgba HeavyCardTint { get; init; }
    public Rgba CardBorder { get; init; }
    public Rgba ModalBackdrop { get; init; }
    public Rgba Divider { get; init; }
    public Rgba PrimaryText { get; init; }
    public Rgba SecondaryText { get; init; }
    public Rgba MutedText { get; init; }
    public Rgba AccentBlue { get; init; }
    public Rgba KeycapText { get; init; }
    public Rgba KeycapBackground { get; init; }
    public Rgba KeycapBorder { get; init; }
    public Rgba WellBackground { get; init; }
    public Rgba PickAccentBorder { get; init; }
    public Rgba PickText { get; init; }
    public Rgba RejectAccentBorder { get; init; }
    public Rgba RejectText { get; init; }
    public Rgba GoldStarText { get; init; }
    public Rgba CompareBadgeText { get; init; }
    public Rgba HistogramGrid { get; init; }
    public Rgba HistogramLumaFill { get; init; }
    public Rgba HistogramLumaStroke { get; init; }
    public Rgba EmeraldAccent { get; init; }
    public Rgba CyanIndicator { get; init; }

    public static GlassTheme ForMode(ThemeMode mode) => mode == ThemeMode.Light ? Light : Dark;

    public static readonly GlassTheme Dark = new()
    {
        Mode = ThemeMode.Dark,
        CanvasBackground = new(0x0A, 0x0A, 0x0C, 1.0f),
        CardTint = new(0x10, 0x11, 0x15, 0.62f),
        HeavyCardTint = new(0x10, 0x11, 0x15, 0.80f),
        CardBorder = new(255, 255, 255, 0.16f),
        ModalBackdrop = new(0, 0, 0, 0.45f),
        Divider = new(255, 255, 255, 0.22f),
        PrimaryText = new(246, 247, 250, 0.96f),
        SecondaryText = new(185, 192, 206, 0.88f),
        MutedText = new(150, 160, 178, 0.88f),
        AccentBlue = new(0x64, 0xB5, 0xF6, 0.95f),
        KeycapText = new(0x80, 0xD8, 0xFF, 0.96f),
        KeycapBackground = new(255, 255, 255, 0.08f),
        KeycapBorder = new(255, 255, 255, 0.18f),
        WellBackground = new(0, 0, 0, 0.34f),
        PickAccentBorder = new(0x4C, 0xAF, 0x50, 0.55f),
        PickText = new(0x81, 0xC7, 0x84, 0.98f),
        RejectAccentBorder = new(0xEF, 0x53, 0x50, 0.55f),
        RejectText = new(0xEF, 0x9A, 0x9A, 0.98f),
        GoldStarText = new(0xFF, 0xD5, 0x4F, 0.98f),
        CompareBadgeText = new(0x90, 0xCA, 0xF9, 0.95f),
        HistogramGrid = new(255, 255, 255, 0.07f),
        HistogramLumaFill = new(255, 255, 255, 0.12f),
        HistogramLumaStroke = new(255, 255, 255, 0.72f),
        EmeraldAccent = new(0x33, 0xE0, 0x94, 0.95f),
        CyanIndicator = new(0x38, 0xD9, 0xFF, 0.96f)
    };

    public static readonly GlassTheme Light = new()
    {
        Mode = ThemeMode.Light,
        CanvasBackground = new(0xEC, 0xEE, 0xF4, 1.0f),
        CardTint = new(246, 248, 252, 0.76f),
        HeavyCardTint = new(250, 251, 255, 0.90f),
        CardBorder = new(18, 22, 32, 0.18f),
        ModalBackdrop = new(20, 24, 34, 0.28f),
        Divider = new(20, 24, 34, 0.16f),
        PrimaryText = new(18, 20, 26, 0.96f),
        SecondaryText = new(66, 74, 90, 0.92f),
        MutedText = new(96, 106, 124, 0.90f),
        AccentBlue = new(0x0D, 0x6E, 0xFD, 0.95f),
        KeycapText = new(0x00, 0x56, 0xB8, 0.98f),
        KeycapBackground = new(16, 22, 34, 0.07f),
        KeycapBorder = new(16, 22, 34, 0.16f),
        WellBackground = new(20, 26, 40, 0.07f),
        PickAccentBorder = new(0x2E, 0x7D, 0x32, 0.60f),
        PickText = new(0x1B, 0x5E, 0x20, 0.98f),
        RejectAccentBorder = new(0xC6, 0x28, 0x28, 0.60f),
        RejectText = new(0xB7, 0x1C, 0x1C, 0.98f),
        GoldStarText = new(0xB8, 0x6E, 0x00, 0.98f),
        CompareBadgeText = new(0x0D, 0x47, 0xA1, 0.95f),
        HistogramGrid = new(0, 0, 0, 0.08f),
        HistogramLumaFill = new(24, 30, 42, 0.12f),
        HistogramLumaStroke = new(24, 30, 42, 0.76f),
        EmeraldAccent = new(0x0F, 0x9D, 0x58, 0.95f),
        CyanIndicator = new(0x00, 0x7A, 0xCC, 0.96f)
    };

    public static Rgba ColorForLabel(ColorLabel label, float alpha = 0.95f) => label switch
    {
        ColorLabel.Red => new(0xEF, 0x53, 0x50, alpha),
        ColorLabel.Yellow => new(0xFF, 0xCA, 0x28, alpha),
        ColorLabel.Green => new(0x66, 0xBB, 0x6A, alpha),
        ColorLabel.Blue => new(0x42, 0xA5, 0xF5, alpha),
        _ => new(255, 255, 255, alpha)
    };
}
