using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinGlance.Model;

public sealed record ExifMetadata(
    string? CameraMake = null,
    string? CameraModel = null,
    string? LensModel = null,
    double? ShutterSpeed = null,
    double? Aperture = null,
    int? Iso = null,
    double? FocalLength = null,
    int? FocalLength35mm = null,
    double? ExposureBias = null,
    bool? FlashFired = null,
    string? DateTimeOriginal = null,
    int? Orientation = null,
    int? ExposureProgram = null,
    int? MeteringMode = null,
    int? WhiteBalance = null,
    int? FlashCode = null,
    int? ColorSpaceCode = null,
    string? LensMake = null,
    string? LensSpecification = null,
    double? MaxAperture = null,
    string? Software = null,
    string? BodySerialNumber = null,
    string? LensSerialNumber = null,
    int? ShutterCount = null,
    string? SubSecTimeOriginal = null
)
{
    public string? FormattedShutter
    {
        get
        {
            if (ShutterSpeed is not { } speed || speed <= 0) return null;
            if (speed < 0.8)
            {
                var recip = Math.Round(1.0 / speed);
                return $"1/{(int)recip}s";
            }
            if (Math.Abs(speed - Math.Floor(speed)) < 0.001)
            {
                return $"{(int)speed}s";
            }
            return $"{speed.ToString("0.0", CultureInfo.InvariantCulture)}s";
        }
    }

    public string? FormattedAperture
    {
        get
        {
            if (Aperture is not { } f || f <= 0) return null;
            return Math.Abs(f - Math.Floor(f)) < 0.001 ? $"ƒ/{f:0}" : $"ƒ/{f.ToString("0.0", CultureInfo.InvariantCulture)}";
        }
    }

    public string? FormattedIso => Iso is > 0 ? $"ISO {Iso}" : null;

    public string? FormattedFocal
    {
        get
        {
            if (FocalLength is not { } fl || fl <= 0) return null;
            return Math.Abs(fl - Math.Floor(fl)) < 0.001 ? $"{(int)fl}mm" : $"{fl.ToString("0.0", CultureInfo.InvariantCulture)}mm";
        }
    }

    public string? FormattedBias
    {
        get
        {
            if (ExposureBias is not { } b || Math.Abs(b) <= 0.01) return null;
            return $"{b.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} EV";
        }
    }

    public string? FormattedBiasDetailed
    {
        get
        {
            if (ExposureBias is not { } b) return null;
            if (Math.Abs(b) < 0.01) return "0.0 EV";
            return $"{b.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} EV";
        }
    }

    /// <summary>
    /// Parses DateTimeOriginal + optional SubSecTimeOriginal into an accurate capture timestamp for burst clustering.
    /// </summary>
    public DateTime? ParsedCaptureTime
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateTimeOriginal)) return null;

            string trimmed = DateTimeOriginal.Trim().Trim('\0');
            if (!DateTime.TryParseExact(trimmed, "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            {
                if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    return null;
            }

            if (!string.IsNullOrWhiteSpace(SubSecTimeOriginal))
            {
                Span<char> digits = stackalloc char[Math.Min(SubSecTimeOriginal.Length, 7)];
                int count = 0;
                foreach (char ch in SubSecTimeOriginal)
                {
                    if (ch >= '0' && ch <= '9')
                    {
                        digits[count++] = ch;
                        if (count == 7) break;
                    }
                    else if (count > 0)
                    {
                        break;
                    }
                }

                if (count > 0)
                {
                    // EXIF SubSecTimeOriginal represents fractional seconds after the decimal point (e.g. "5" -> 0.5s, "05" -> 0.05s).
                    long ticks = 0;
                    long unit = TimeSpan.TicksPerSecond / 10; // 10^6 ticks for first digit
                    for (int i = 0; i < count; i++)
                    {
                        ticks += (digits[i] - '0') * unit;
                        unit /= 10;
                    }
                    dt = dt.AddTicks(ticks);
                }
            }

            return dt;
        }
    }

    public string? FormattedDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateTimeOriginal)) return null;
            if (DateTime.TryParseExact(DateTimeOriginal, "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return DateTimeOriginal;
        }
    }

    public string? FormattedExposureProgram => ExposureProgram switch
    {
        1 => "Manual (M)",
        2 => "Program AE (P)",
        3 => "Aperture Priority (A)",
        4 => "Shutter Priority (S)",
        5 => "Creative Program",
        6 => "Action Program",
        7 => "Portrait Mode",
        8 => "Landscape Mode",
        _ => null
    };

    public string? FormattedMeteringMode => MeteringMode switch
    {
        1 => "Average",
        2 => "Center-Weighted",
        3 => "Spot",
        4 => "Multi-Spot",
        5 => "Matrix / Evaluative",
        6 => "Partial",
        255 => "Other",
        _ => null
    };

    public string? FormattedWhiteBalance => WhiteBalance switch
    {
        0 => "Auto",
        1 => "Manual",
        _ => null
    };

    public string? FormattedFlash
    {
        get
        {
            if (FlashCode is { } code)
            {
                bool fired = (code & 1) != 0;
                int mode = (code >> 3) & 0x3;
                if (fired)
                {
                    return mode == 1 ? "Fired (Forced)" : mode == 3 ? "Fired (Auto)" : "Fired";
                }
                return mode == 2 ? "Did not fire (Off)" : "Did not fire";
            }
            if (FlashFired is { } f) return f ? "Fired" : "Did not fire";
            return null;
        }
    }

    public string FormattedColorSpace(string? detectedProfileName)
    {
        if (!string.IsNullOrWhiteSpace(detectedProfileName) &&
            !string.Equals(detectedProfileName, "sRGB", StringComparison.OrdinalIgnoreCase))
        {
            return detectedProfileName;
        }

        return ColorSpaceCode switch
        {
            1 => "sRGB",
            2 => "Adobe RGB",
            0xFFFF => "Uncalibrated",
            _ => string.IsNullOrWhiteSpace(detectedProfileName) ? "sRGB" : detectedProfileName
        };
    }

    public string? FormattedFocalAndCrop
    {
        get
        {
            if (FormattedFocal is not { } fStr) return null;
            if (FocalLength is > 0 && FocalLength35mm is > 0)
            {
                double crop = FocalLength35mm.Value / FocalLength.Value;
                if (Math.Abs(crop - 1.0) < 0.08)
                {
                    return $"{fStr} (Full Frame)";
                }
                return $"{fStr} ({FocalLength35mm}mm eq. · {crop.ToString("0.0", CultureInfo.InvariantCulture)}×)";
            }
            return fStr;
        }
    }

    public static string FormatMegapixels(int width, int height)
    {
        if (width <= 0 || height <= 0) return "—";
        double mp = (width * (double)height) / 1_000_000.0;
        return $"{mp.ToString("0.0", CultureInfo.InvariantCulture)} MP";
    }

    public string ExposureSummary
    {
        get
        {
            var parts = new List<string>();
            if (FormattedShutter is { } s) parts.Add(s);
            if (FormattedAperture is { } a) parts.Add(a);
            if (FormattedIso is { } i) parts.Add(i);
            if (FormattedFocal is { } f) parts.Add(f);
            if (FormattedBias is { } b) parts.Add(b);
            return string.Join(" · ", parts);
        }
    }

    public string CameraAndLensSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(CameraModel))
            {
                if (!string.IsNullOrWhiteSpace(CameraMake) &&
                    !CameraModel.Contains(CameraMake, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add($"{CameraMake} {CameraModel}");
                }
                else
                {
                    parts.Add(CameraModel);
                }
            }
            else if (!string.IsNullOrWhiteSpace(CameraMake))
            {
                parts.Add(CameraMake);
            }

            if (!string.IsNullOrWhiteSpace(LensModel))
            {
                parts.Add(LensModel);
            }

            return string.Join(" · ", parts);
        }
    }

    public string BuildClipboardSummary(string filename, int origW, int origH, long totalBytes, string? colorProfileName)
    {
        var sb = new StringBuilder();
        sb.Append(filename);
        if (origW > 0 && origH > 0)
        {
            sb.Append($" ({origW}×{origH}, {FormatMegapixels(origW, origH)})");
        }
        sb.AppendLine();

        var camLens = CameraAndLensSummary;
        if (!string.IsNullOrEmpty(camLens)) sb.AppendLine($"Gear: {camLens}");

        var exp = ExposureSummary;
        if (!string.IsNullOrEmpty(exp)) sb.AppendLine($"Exposure: {exp}");

        var extra = new List<string>();
        if (FormattedExposureProgram is { } ep) extra.Add($"Program: {ep}");
        if (FormattedMeteringMode is { } mm) extra.Add($"Metering: {mm}");
        if (FormattedWhiteBalance is { } wb) extra.Add($"WB: {wb}");
        if (FormattedFlash is { } fl) extra.Add($"Flash: {fl}");
        extra.Add($"Color: {FormattedColorSpace(colorProfileName)}");
        if (ShutterCount is > 0) extra.Add($"Shutter: {ShutterCount.Value.ToString("N0", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(BodySerialNumber)) extra.Add($"Body S/N: {BodySerialNumber}");
        if (!string.IsNullOrWhiteSpace(LensSerialNumber)) extra.Add($"Lens S/N: {LensSerialNumber}");
        if (FormattedDate is { } dt) extra.Add($"Captured: {dt}");

        sb.Append(string.Join(" · ", extra));
        return sb.ToString();
    }
}
