using System;
using System.IO;
using Vortice.Mathematics;
using WinGlance.Model;

namespace WinGlance.Engine;

/// <summary>
/// Real-time GPU camera picture-control / film simulation engine for Fujifilm, Nikon, and Sony RAW files.
/// Builds Direct2D ColorMatrix 5x4 transforms matching upstream Glance CameraProfileEngine tonal &amp; color curves.
/// </summary>
public static class CameraProfileEngine
{
    private const float LumR = 0.2126f;
    private const float LumG = 0.7152f;
    private const float LumB = 0.0722f;

    public static readonly Matrix5x4 IdentityMatrix = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f,
        0f, 0f, 0f, 0f);

    /// <summary>
    /// Detects camera brand from EXIF CameraMake or falls back to RAW file extension.
    /// </summary>
    public static CameraBrand DetectBrand(SlideItem item, ExifMetadata? exif = null)
    {
        if (!string.IsNullOrWhiteSpace(exif?.CameraMake))
        {
            string lower = exif.CameraMake.ToLowerInvariant();
            if (lower.Contains("fuji")) return CameraBrand.Fujifilm;
            if (lower.Contains("nikon")) return CameraBrand.Nikon;
            if (lower.Contains("sony")) return CameraBrand.Sony;
            return CameraBrand.Unsupported;
        }

        string ext = Path.GetExtension(item.RawPath ?? item.Path).ToLowerInvariant();
        return ext switch
        {
            ".raf" => CameraBrand.Fujifilm,
            ".nef" or ".nrw" => CameraBrand.Nikon,
            ".arw" or ".srf" or ".sr2" => CameraBrand.Sony,
            _ => CameraBrand.Unsupported
        };
    }

    /// <summary>
    /// Validates whether in-camera profiles are enabled for the given item (Fujifilm / Nikon / Sony RAWs only).
    /// </summary>
    public static (bool Supported, CameraBrand Brand, string? Reason) IsProfileSupported(SlideItem item, ExifMetadata? exif = null)
    {
        bool isRaw = item.RawPath != null || SupportedFormats.IsRaw(item.Path);
        if (!isRaw)
        {
            return (false, CameraBrand.Unsupported, "Profiles for RAW Files Only");
        }

        var brand = DetectBrand(item, exif);
        if (!brand.IsSupported())
        {
            return (false, brand, "Profiles for Fuji, Nikon & Sony RAWs");
        }

        return (true, brand, null);
    }

    /// <summary>
    /// Returns the combined 5x4 color matrix for Direct2D ColorMatrix effect (row-vector convention: [R, G, B, A, 1] * M).
    /// </summary>
    public static Matrix5x4 ComputeColorMatrix(CameraProfile profile, CameraBrand brand)
    {
        if (profile == CameraProfile.AsShot)
        {
            return IdentityMatrix;
        }

        (float sat, float contrast, float bright, float gainR, float gainG, float gainB) = profile switch
        {
            CameraProfile.Standard => (1.04f, 1.03f, 0.00f, 1.00f, 1.00f, 1.00f),

            CameraProfile.Vivid => brand switch
            {
                CameraBrand.Fujifilm => (1.42f, 1.18f, 0.00f, 1.01f, 1.00f, 1.01f),
                CameraBrand.Sony => (1.38f, 1.15f, 0.00f, 1.01f, 1.00f, 1.00f),
                _ => (1.34f, 1.15f, 0.00f, 1.00f, 1.00f, 1.01f)
            },

            CameraProfile.Neutral => brand == CameraBrand.Sony
                ? (0.88f, 0.94f, 0.02f, 1.00f, 1.00f, 1.00f)
                : (0.90f, 0.94f, 0.02f, 1.00f, 1.00f, 1.00f),

            CameraProfile.Landscape => brand switch
            {
                // Classic Chrome: muted saturation, crisp contrast, subtle cool slate split
                CameraBrand.Fujifilm => (0.88f, 1.14f, -0.01f, 0.97f, 1.00f, 1.03f),
                // Sony FL: film matte look
                CameraBrand.Sony => (0.92f, 1.12f, 0.01f, 0.98f, 1.00f, 1.02f),
                // Nikon Landscape: punchy foliage & sky
                _ => (1.28f, 1.12f, 0.00f, 1.00f, 1.02f, 1.02f)
            },

            CameraProfile.Portrait => brand == CameraBrand.Sony
                ? (1.00f, 0.98f, 0.03f, 1.02f, 1.00f, 0.99f)
                : (1.04f, 0.98f, 0.03f, 1.02f, 1.00f, 0.99f),

            CameraProfile.Monochrome => (0.00f, 1.22f, -0.02f, 1.00f, 1.00f, 1.00f),

            CameraProfile.Film => brand switch
            {
                // Classic Neg: warm midtones, rich film contrast
                CameraBrand.Fujifilm => (1.08f, 1.10f, 0.03f, 1.04f, 1.01f, 0.93f),
                // Sony IN: analog instant soft palette
                CameraBrand.Sony => (0.95f, 1.10f, 0.03f, 1.03f, 1.00f, 0.94f),
                // Nikon Flat / Creative warm
                _ => (0.92f, 0.96f, 0.03f, 1.02f, 1.00f, 0.96f)
            },

            _ => (1.00f, 1.00f, 0.00f, 1.00f, 1.00f, 1.00f)
        };

        float invSat = 1.0f - sat;
        float sLr = invSat * LumR;
        float sLg = invSat * LumG;
        float sLb = invSat * LumB;

        float m11 = gainR * contrast * (sLr + sat);
        float m21 = gainR * contrast * sLg;
        float m31 = gainR * contrast * sLb;

        float m12 = gainG * contrast * sLr;
        float m22 = gainG * contrast * (sLg + sat);
        float m32 = gainG * contrast * sLb;

        float m13 = gainB * contrast * sLr;
        float m23 = gainB * contrast * sLg;
        float m33 = gainB * contrast * (sLb + sat);

        float baseOffset = 0.5f * (1.0f - contrast) + bright;

        return new Matrix5x4(
            m11, m12, m13, 0.0f,
            m21, m22, m23, 0.0f,
            m31, m32, m33, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f,
            gainR * baseOffset, gainG * baseOffset, gainB * baseOffset, 0.0f);
    }

    /// <summary>
    /// Evaluates a 5x4 color matrix against a normalized linear/sRGB pixel triplet (useful for deterministic verification).
    /// </summary>
    public static (float R, float G, float B) ApplyMatrixToRgb(in Matrix5x4 m, float r, float g, float b)
    {
        float nr = Math.Clamp(r * m.M11 + g * m.M21 + b * m.M31 + m.M51, 0f, 1f);
        float ng = Math.Clamp(r * m.M12 + g * m.M22 + b * m.M32 + m.M52, 0f, 1f);
        float nb = Math.Clamp(r * m.M13 + g * m.M23 + b * m.M33 + m.M53, 0f, 1f);
        return (nr, ng, nb);
    }
}
