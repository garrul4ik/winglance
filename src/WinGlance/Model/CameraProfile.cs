using System;

namespace WinGlance.Model;

/// <summary>
/// Supported camera manufacturers for real-time in-camera film/picture control simulations.
/// </summary>
public enum CameraBrand
{
    Unsupported = 0,
    Fujifilm = 1,
    Nikon = 2,
    Sony = 3
}

/// <summary>
/// Non-destructive camera color simulation profile stored in Adobe XMP crs:CameraProfile.
/// </summary>
public enum CameraProfile
{
    AsShot = 0,
    Standard = 1,
    Vivid = 2,
    Neutral = 3,
    Landscape = 4,
    Portrait = 5,
    Monochrome = 6,
    Film = 7
}

public static class CameraBrandExtensions
{
    public static bool IsSupported(this CameraBrand brand) =>
        brand is CameraBrand.Fujifilm or CameraBrand.Nikon or CameraBrand.Sony;
}

public static class CameraProfileExtensions
{
    private static readonly CameraProfile[] AllProfiles =
    {
        CameraProfile.AsShot,
        CameraProfile.Standard,
        CameraProfile.Vivid,
        CameraProfile.Neutral,
        CameraProfile.Landscape,
        CameraProfile.Portrait,
        CameraProfile.Monochrome,
        CameraProfile.Film
    };

    public static CameraProfile Next(this CameraProfile profile)
    {
        int idx = Array.IndexOf(AllProfiles, profile);
        return idx < 0 ? CameraProfile.Standard : AllProfiles[(idx + 1) % AllProfiles.Length];
    }

    public static string GetDisplayName(this CameraProfile profile, CameraBrand brand = CameraBrand.Unsupported)
    {
        return brand switch
        {
            CameraBrand.Fujifilm => profile switch
            {
                CameraProfile.AsShot => "As Shot (Camera)",
                CameraProfile.Standard => "Provia / Standard",
                CameraProfile.Vivid => "Velvia / Vivid",
                CameraProfile.Neutral => "Astia / Soft",
                CameraProfile.Landscape => "Classic Chrome",
                CameraProfile.Portrait => "PRO Neg. Hi",
                CameraProfile.Monochrome => "Acros (B&W)",
                CameraProfile.Film => "Classic Negative",
                _ => "As Shot (Camera)"
            },
            CameraBrand.Nikon => profile switch
            {
                CameraProfile.AsShot => "As Shot (Camera)",
                CameraProfile.Standard => "Nikon Standard",
                CameraProfile.Vivid => "Nikon Vivid",
                CameraProfile.Neutral => "Nikon Neutral",
                CameraProfile.Landscape => "Nikon Landscape",
                CameraProfile.Portrait => "Nikon Portrait",
                CameraProfile.Monochrome => "Nikon Monochrome",
                CameraProfile.Film => "Nikon Flat / Creative",
                _ => "As Shot (Camera)"
            },
            CameraBrand.Sony => profile switch
            {
                CameraProfile.AsShot => "As Shot (Camera)",
                CameraProfile.Standard => "Sony ST (Standard)",
                CameraProfile.Vivid => "Sony VV (Vivid)",
                CameraProfile.Neutral => "Sony NT (Neutral)",
                CameraProfile.Landscape => "Sony FL (Film/Matte)",
                CameraProfile.Portrait => "Sony PT (Portrait)",
                CameraProfile.Monochrome => "Sony BW (Monochrome)",
                CameraProfile.Film => "Sony IN (Analog Soft)",
                _ => "As Shot (Camera)"
            },
            _ => profile switch
            {
                CameraProfile.AsShot => "As Shot (Camera)",
                CameraProfile.Standard => "Standard",
                CameraProfile.Vivid => "Vivid",
                CameraProfile.Neutral => "Neutral",
                CameraProfile.Landscape => "Landscape",
                CameraProfile.Portrait => "Portrait",
                CameraProfile.Monochrome => "Monochrome (B&W)",
                CameraProfile.Film => "Analog Film",
                _ => "As Shot (Camera)"
            }
        };
    }

    public static string GetXmpName(this CameraProfile profile, CameraBrand brand = CameraBrand.Unsupported)
    {
        return brand switch
        {
            CameraBrand.Fujifilm => profile switch
            {
                CameraProfile.AsShot or CameraProfile.Standard => "Camera PROVIA/Standard",
                CameraProfile.Vivid => "Camera Velvia/Vivid",
                CameraProfile.Neutral => "Camera ASTIA/Soft",
                CameraProfile.Landscape => "Camera CLASSIC CHROME",
                CameraProfile.Portrait => "Camera PRO Neg. Hi",
                CameraProfile.Monochrome => "Camera ACROS",
                CameraProfile.Film => "Camera Classic Neg.",
                _ => "Camera PROVIA/Standard"
            },
            CameraBrand.Nikon => profile switch
            {
                CameraProfile.AsShot or CameraProfile.Standard => "Camera Standard",
                CameraProfile.Vivid => "Camera Vivid",
                CameraProfile.Neutral => "Camera Neutral",
                CameraProfile.Landscape => "Camera Landscape",
                CameraProfile.Portrait => "Camera Portrait",
                CameraProfile.Monochrome => "Camera Monochrome",
                CameraProfile.Film => "Camera Flat",
                _ => "Camera Standard"
            },
            CameraBrand.Sony => profile switch
            {
                CameraProfile.AsShot => "Camera Standard",
                CameraProfile.Standard => "Camera ST",
                CameraProfile.Vivid => "Camera VV",
                CameraProfile.Neutral => "Camera NT",
                CameraProfile.Landscape => "Camera FL",
                CameraProfile.Portrait => "Camera PT",
                CameraProfile.Monochrome => "Camera BW",
                CameraProfile.Film => "Camera IN",
                _ => "Camera Standard"
            },
            _ => profile switch
            {
                CameraProfile.AsShot => "Adobe Standard",
                CameraProfile.Standard => "Camera Standard",
                CameraProfile.Vivid => "Camera Vivid",
                CameraProfile.Neutral => "Camera Neutral",
                CameraProfile.Landscape => "Camera Landscape",
                CameraProfile.Portrait => "Camera Portrait",
                CameraProfile.Monochrome => "Camera Monochrome",
                CameraProfile.Film => "Camera Film",
                _ => "Adobe Standard"
            }
        };
    }

    public static CameraProfile? ParseXmp(string? xmpString)
    {
        if (string.IsNullOrWhiteSpace(xmpString)) return null;
        string lower = xmpString.Trim().ToLowerInvariant();

        if (lower.Contains("velvia") || lower.Contains("vivid") || lower.Contains("camera vv")) return CameraProfile.Vivid;
        if (lower.Contains("astia") || lower.Contains("neutral") || lower.Contains("camera nt")) return CameraProfile.Neutral;
        if (lower.Contains("classic chrome") || lower.Contains("landscape") || lower.Contains("camera fl")) return CameraProfile.Landscape;
        if (lower.Contains("pro neg") || lower.Contains("portrait") || lower.Contains("camera pt")) return CameraProfile.Portrait;
        if (lower.Contains("acros") || lower.Contains("mono") || lower.Contains("black") || lower.Contains("camera bw")) return CameraProfile.Monochrome;
        if (lower.Contains("classic neg") || lower.Contains("film") || lower.Contains("eterna") || lower.Contains("camera in") || lower.Contains("flat")) return CameraProfile.Film;
        if (lower.Contains("provia") || lower.Contains("standard") || lower.Contains("camera st")) return CameraProfile.Standard;
        return null;
    }
}
