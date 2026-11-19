using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using WinGlance.Model;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace WinGlance.Gpu;

public sealed class TypographySet : IDisposable
{
    public IDWriteTextFormat Hero { get; }
    public IDWriteTextFormat Toast { get; }
    public IDWriteTextFormat UiBold { get; }
    public IDWriteTextFormat UiRegular { get; }
    public IDWriteTextFormat MonoSmall { get; }
    public IDWriteTextFormat MonoTiny { get; }
    public IDWriteTextFormat CheatHeader { get; }

    public TypographySet(IDWriteFactory dwrite)
    {
        Hero = dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.SemiBold, FontStyle.Normal, 28.0f);
        Hero.TextAlignment = TextAlignment.Center;

        Toast = dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.SemiBold, FontStyle.Normal, 20.0f);
        Toast.TextAlignment = TextAlignment.Center;
        Toast.ParagraphAlignment = ParagraphAlignment.Center;

        UiBold = dwrite.CreateTextFormat("Segoe UI Variable Text", FontWeight.SemiBold, FontStyle.Normal, 13.0f);
        UiBold.ParagraphAlignment = ParagraphAlignment.Center;
        UiBold.WordWrapping = WordWrapping.NoWrap;

        UiRegular = dwrite.CreateTextFormat("Segoe UI Variable Text", FontWeight.Normal, FontStyle.Normal, 12.0f);
        UiRegular.ParagraphAlignment = ParagraphAlignment.Center;
        UiRegular.WordWrapping = WordWrapping.NoWrap;

        MonoSmall = dwrite.CreateTextFormat("Cascadia Mono", FontWeight.SemiBold, FontStyle.Normal, 11.0f);
        MonoSmall.ParagraphAlignment = ParagraphAlignment.Center;
        MonoSmall.WordWrapping = WordWrapping.NoWrap;

        MonoTiny = dwrite.CreateTextFormat("Cascadia Mono", FontWeight.SemiBold, FontStyle.Normal, 9.5f);
        MonoTiny.ParagraphAlignment = ParagraphAlignment.Center;
        MonoTiny.WordWrapping = WordWrapping.NoWrap;

        CheatHeader = dwrite.CreateTextFormat("Segoe UI Variable Display", FontWeight.Bold, FontStyle.Normal, 15.0f);
    }

    public void Dispose()
    {
        Hero.Dispose();
        Toast.Dispose();
        UiBold.Dispose();
        UiRegular.Dispose();
        MonoSmall.Dispose();
        MonoTiny.Dispose();
        CheatHeader.Dispose();
    }
}

public readonly struct OverlayRenderContext
{
    public ID2D1DeviceContext5 Dc { get; init; }
    public ID2D1Factory6 Factory { get; init; }
    public IDWriteFactory DWrite { get; init; }
    public GaussianBlur BlurEffect { get; init; }
    public GlassTheme Theme { get; init; }
    public TypographySet Fonts { get; init; }
    public bool IsFp16 { get; init; }
    public float DipWidth { get; init; }
    public float DipHeight { get; init; }
    public float DpiScale { get; init; }
    public DecodedImage? PrimaryImage { get; init; }
    public DecodedImage? SecondaryImage { get; init; }
    public ID2D1Bitmap1? PrimaryBitmap { get; init; }
    public ID2D1Bitmap1? SecondaryBitmap { get; init; }
    public RawRectF PrimaryPaneDip { get; init; }
    public RawRectF SecondaryPaneDip { get; init; }
    public RawRectF PrimaryDestDip { get; init; }
    public RawRectF SecondaryDestDip { get; init; }
    public RawRectF[] MultiUpPanesDip { get; init; }
    public ID2D1Bitmap1?[] SlotBitmaps { get; init; }
    public DecodedImage?[] SlotImages { get; init; }
    public RawRectF[] SlotDestsDip { get; init; }
    public IReadOnlyList<ID2D1Bitmap1?> FaceCropBitmaps { get; init; }
    public Func<string, ID2D1Bitmap1?>? ResolveMicroThumbBitmap { get; init; }

    public Color4 UiColor(byte r, byte g, byte b, float a = 1.0f) =>
        new Rgba(r, g, b, a).ToColor4(IsFp16);

    public Color4 ThemeColor(Rgba c, float alphaMultiplier = 1.0f) =>
        c.ToColor4(IsFp16, alphaMultiplier);

    public void BeginFrostedCard(RoundedRectangle card, float opacity, float? tintAlphaOverride = null)
    {
        using var geo = Factory.CreateRoundedRectangleGeometry(card);
        var layerParams = new LayerParameters1
        {
            ContentBounds = card.Rect,
            GeometricMask = geo,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions1.None
        };

        Dc.PushLayer(ref layerParams, null);
        Dc.DrawImage(BlurEffect, D2DInterpolationMode.Linear, CompositeMode.SourceCopy);

        var tint = tintAlphaOverride.HasValue
            ? Theme.CardTint with { A = tintAlphaOverride.Value }
            : Theme.CardTint;
        using var tintBrush = Dc.CreateSolidColorBrush(ThemeColor(tint));
        Dc.FillRectangle(card.Rect, tintBrush);
    }

    public void EndFrostedCard(RoundedRectangle card, float opacity, Color4? accentBorder = null)
    {
        Dc.PopLayer();

        var borderCol = accentBorder ?? ThemeColor(Theme.CardBorder, opacity);
        using var borderBrush = Dc.CreateSolidColorBrush(borderCol);
        Dc.DrawRoundedRectangle(card, borderBrush, 1.0f);
    }

    public float MeasureWidth(string text, IDWriteTextFormat fmt)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        using var layout = DWrite.CreateTextLayout(text, fmt, 4096f, 64f);
        return layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    public static RoundedRectangle MakeRounded(float x, float y, float w, float h, float r) =>
        new() { Rect = new RawRectF(x, y, x + w, y + h), RadiusX = r, RadiusY = r };

    public static string BuildCullingSummary(CullingMetadata c)
    {
        string flag = c.Pick switch
        {
            PickStatus.Pick => "✓ PICK",
            PickStatus.Reject => "✕ REJECT",
            _ => ""
        };
        string stars = c.Rating > 0 ? new string('★', c.Rating) : "";
        if (!string.IsNullOrEmpty(flag) && !string.IsNullOrEmpty(stars)) return $"{flag}  {stars}";
        return !string.IsNullOrEmpty(flag) ? flag : stars;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}

public interface IOverlayRenderer
{
    void Render(in OverlayRenderContext ctx, RenderHudState state);
}
