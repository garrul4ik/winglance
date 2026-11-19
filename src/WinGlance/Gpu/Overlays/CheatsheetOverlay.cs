using System;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinGlance.Vision;

namespace WinGlance.Gpu.Overlays;

public sealed class CheatsheetOverlay : IOverlayRenderer
{
    private static readonly (string Key, string Desc)[] CompactLeft =
    {
        ("← / → / Space", "Next / Previous photo"),
        ("[ / ]  /  Ctrl+←/→", "Previous / Next Scene Leap"),
        ("Enter  /  + −", "Slideshow Play/Pause & Speed"),
        ("P  /  X  /  U", "Flag Pick / Reject / Unflag"),
        ("B  /  Shift+P", "Burst Hero Leap / Burst Knockout"),
        ("1 – 5  /  0", "Star Rating (1–5) / Clear"),
        ("6 / 7 / 8 / 9", "Red / Yellow / Green / Blue label"),
        ("C", "Toggle Auto-Advance (ON/OFF)"),
        ("\\  /  Shift+\\", "Smart Filters / Picks Only"),
        ("Ctrl+Z / Ctrl+Shift+Z", "Undo / Redo (100 steps)"),
        ("E / Shift+E / Del", "Export ./Selects / Recycle Bin")
    };

    private static readonly (string Key, string Desc)[] CompactRight =
    {
        ("Hold Z  /  Z", "Spring-Loaded / 100% 1:1 Zoom"),
        ("Shift+Z  /  M-Click", "AI Auto Face & Eye Jump (DirectML)"),
        ("M  /  Click", "Optical Reticle Loupe (2× / 4×)"),
        ("Shift + F", "GPU Focus Peaking (Sobel Shader)"),
        ("O  /  Shift+O", "Clipping Blinkies / ARRI False Color"),
        ("H", "Live RGB + Rec.709 Histogram"),
        ("I", "EXIF Technical Dossier (Hover/Copy)"),
        ("S  /  Tab", "1-UP / 2-UP / 4-UP & Switch Slot"),
        ("T / D / V", "Theme / RAW Film Profile (Fuji/Nikon/Sony)"),
        ("A  /  G  /  F", "Face Dock / Filmstrip / Fullscreen"),
        ("R / L  /  Shift+Del", "Rotate CW/CCW / Purge Rejects")
    };

    private static readonly (string Title, (string Key, string Desc)[] Rows)[] ExpandedCategories =
    {
        ("CULLING & BURST AI", new[]
        {
            ("P  /  X  /  U", "Flag Pick / Reject / Unflag"),
            ("B  /  Shift+P", "Burst Hero Leap / Burst Knockout"),
            ("1 – 5 / 0", "Rating (or 1/2 King of Hill in 2-UP)"),
            ("6 / 7 / 8 / 9", "Red / Yellow / Green / Blue label"),
            ("C", "Toggle Auto-Advance (ON/OFF)"),
            ("\\  /  Shift+\\", "Cycle Populated Filters / Picks Only"),
            ("Ctrl+Z / Ctrl+Shift+Z", "Multi-level Undo / Redo (100 steps)"),
            ("S  /  Tab", "Cycle 1-UP / 2-UP / 4-UP & Switch Slot")
        }),
        ("ZOOM, FACES & LOUPES", new[]
        {
            ("Hold Z  /  Z", "Spring-loaded / 100% Zoom (4-UP Solo)"),
            ("Shift+Z / M-Click", "AI Auto Face & Eye Jump (DirectML)"),
            ("A", "Toggle Face Verification Dock (ON/OFF)"),
            ("M  /  Click", "Floating 2× / 4× Optical Reticle Loupe"),
            ("Shift + F", "Real-time GPU Focus Peaking overlay"),
            ("Wheel / Ctrl+Wheel", "Scroll / Cursor Zoom & Filmstrip"),
            ("Mini-Map Drag", "120Hz 1:1 viewport pan navigator"),
            ("G  /  Bottom Hover", "Toggle Filmstrip Scrubber overlay")
        }),
        ("EXPOSURE, PROFILES & THEME", new[]
        {
            ("H", "Live RGB + Luminance Histogram"),
            ("O", "Exposure Clipping Blinkies (Highlight/Shadow)"),
            ("Shift + O", "ARRI False Color 7-band IRE HUD"),
            ("V", "RAW Film Profiles (Fuji / Nikon / Sony)"),
            ("I", "EXIF Technical Dossier (Hover/Click copy)"),
            ("T  /  D", "Toggle Frosted Dark / Light glass theme"),
            ("F  /  Space", "Toggle Fullscreen / Expand Cheatsheet"),
            ("Esc", "Exit Solo/Cheatsheet/Zoom/Multi-Up/App")
        }),
        ("SCENES, NAV & WORKFLOW", new[]
        {
            ("→ / Space / J / N", "Next photo / Previous (← / K)"),
            ("[ / ]  /  Ctrl+←/→", "Previous / Next Scene Leap"),
            ("Home / End", "Jump to First / Last photo"),
            ("Enter  /  + −", "Slideshow Play/Pause & Speed (1–30s)"),
            ("R  /  L", "Lossless EXIF + XMP Rotate CW / CCW"),
            ("E  /  Shift+E", "Export Shot / All Picks to ./Selects"),
            ("Ctrl + Enter", "Open active RAW/photo in External Editor"),
            ("Del / Shift+Del", "Recycle Bin / Purge Rejects (Undoable)")
        })
    };

    public void Render(in OverlayRenderContext ctx, RenderHudState state)
    {
        if (!state.ShowCheatsheet) return;

        using (var backdrop = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.ModalBackdrop)))
        {
            ctx.Dc.FillRectangle(new RawRectF(0, 0, ctx.DipWidth, ctx.DipHeight), backdrop);
        }

        if (state.IsCheatsheetExpanded)
        {
            RenderExpanded(ctx);
        }
        else
        {
            RenderCompact(ctx);
        }
    }

    private static void RenderCompact(in OverlayRenderContext ctx)
    {
        float w = Math.Min(820f, ctx.DipWidth - 40f);
        float h = Math.Min(525f, ctx.DipHeight - 40f);
        var card = OverlayRenderContext.MakeRounded((ctx.DipWidth - w) * 0.5f, (ctx.DipHeight - h) * 0.5f, w, h, 18f);

        ctx.BeginFrostedCard(card, 0.99f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var headerBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var keyBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapText));
        using var keyBgBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBackground));
        using var keyBorderBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBorder));
        using var descBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
        using var footerBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.MutedText));
        using var accentBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.AccentBlue));

        ctx.Dc.DrawText("WinGlance 1.7  —  Shortcuts & Pro Culling Guide", ctx.Fonts.CheatHeader,
            new Rect(card.Rect.Left + 26f, card.Rect.Top + 18f, w - 210f, 28f), headerBrush);

        ctx.Dc.DrawText("Press Space to expand", ctx.Fonts.MonoSmall,
            new Rect(card.Rect.Right - 190f, card.Rect.Top + 20f, 164f, 24f), accentBrush);

        float startY = card.Rect.Top + 56f;
        float rowH = 36f;
        float colW = (w - 68f) * 0.5f;

        DrawShortcutColumn(ctx, CompactLeft, card.Rect.Left + 26f, startY, colW, 148f, rowH,
            keyBgBrush, keyBorderBrush, keyBrush, descBrush);

        DrawShortcutColumn(ctx, CompactRight, card.Rect.Left + 42f + colW, startY, colW, 148f, rowH,
            keyBgBrush, keyBorderBrush, keyBrush, descBrush);

        string credit = $"AI Engine: {EyeFocusDetector.ActiveBackend}  ·  SwapChain: {(ctx.IsFp16 ? "FP16 Linear scRGB HDR/WCG" : "BGRA8 sRGB")}  ·  Press Space for full reference";
        ctx.Dc.DrawText(credit, ctx.Fonts.UiRegular, new Rect(card.Rect.Left + 26f, card.Rect.Bottom - 36f, w - 52f, 24f), footerBrush);

        ctx.EndFrostedCard(card, 0.99f);
    }

    private static void RenderExpanded(in OverlayRenderContext ctx)
    {
        float w = Math.Min(980f, ctx.DipWidth - 36f);
        float h = Math.Min(650f, ctx.DipHeight - 36f);
        var card = OverlayRenderContext.MakeRounded((ctx.DipWidth - w) * 0.5f, (ctx.DipHeight - h) * 0.5f, w, h, 20f);

        ctx.BeginFrostedCard(card, 0.99f, tintAlphaOverride: ctx.Theme.HeavyCardTint.A);

        using var headerBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.PrimaryText));
        using var catBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.AccentBlue));
        using var keyBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapText));
        using var keyBgBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBackground));
        using var keyBorderBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.KeycapBorder));
        using var descBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.SecondaryText));
        using var footerBrush = ctx.Dc.CreateSolidColorBrush(ctx.ThemeColor(ctx.Theme.MutedText));

        ctx.Dc.DrawText("WinGlance 1.7  —  Complete Reference Guide", ctx.Fonts.CheatHeader,
            new Rect(card.Rect.Left + 28f, card.Rect.Top + 18f, w - 260f, 28f), headerBrush);

        ctx.Dc.DrawText("Space / Esc to compact", ctx.Fonts.MonoSmall,
            new Rect(card.Rect.Right - 220f, card.Rect.Top + 20f, 192f, 24f), catBrush);

        float colW = (w - 80f) * 0.5f;
        float topGridY = card.Rect.Top + 56f;
        float quadrantH = (h - 108f) * 0.5f;
        float rowH = MathF.Min(28f, (quadrantH - 30f) / 8f);

        for (int c = 0; c < ExpandedCategories.Length; c++)
        {
            int gridCol = c % 2;
            int gridRow = c / 2;
            float qx = card.Rect.Left + 28f + gridCol * (colW + 24f);
            float qy = topGridY + gridRow * (quadrantH + 6f);

            var (title, rows) = ExpandedCategories[c];
            ctx.Dc.DrawText(title, ctx.Fonts.MonoSmall, new Rect(qx, qy, colW, 20f), catBrush);

            DrawShortcutColumn(ctx, rows, qx, qy + 24f, colW, 146f, rowH,
                keyBgBrush, keyBorderBrush, keyBrush, descBrush);
        }

        string credit = $"AI Engine: {EyeFocusDetector.ActiveBackend}  ·  SwapChain: {(ctx.IsFp16 ? "FP16 Linear scRGB HDR/WCG" : "BGRA8 sRGB")}  ·  Windows port of Glance for macOS by @mortalezz";
        ctx.Dc.DrawText(credit, ctx.Fonts.UiRegular, new Rect(card.Rect.Left + 28f, card.Rect.Bottom - 34f, w - 56f, 22f), footerBrush);

        ctx.EndFrostedCard(card, 0.99f);
    }

    private static void DrawShortcutColumn(
        in OverlayRenderContext ctx,
        (string Key, string Desc)[] rows,
        float x,
        float startY,
        float colW,
        float keyW,
        float rowH,
        ID2D1SolidColorBrush keyBgBrush,
        ID2D1SolidColorBrush keyBorderBrush,
        ID2D1SolidColorBrush keyBrush,
        ID2D1SolidColorBrush descBrush)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            float y = startY + i * rowH;
            var keyPill = OverlayRenderContext.MakeRounded(x, y + 3f, keyW - 8f, rowH - 8f, 6f);
            ctx.Dc.FillRoundedRectangle(keyPill, keyBgBrush);
            ctx.Dc.DrawRoundedRectangle(keyPill, keyBorderBrush, 0.75f);

            ctx.Dc.DrawText(rows[i].Key, ctx.Fonts.MonoTiny, new Rect(x + 6f, y, keyW - 20f, rowH), keyBrush);
            ctx.Dc.DrawText(rows[i].Desc, ctx.Fonts.UiRegular, new Rect(x + keyW + 4f, y, colW - keyW - 4f, rowH), descBrush);
        }
    }
}
