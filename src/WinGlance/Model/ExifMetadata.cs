using System;
using System.Collections.Generic;

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
    int? Orientation = null
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
            return $"{speed:0.0}s";
        }
    }

    public string? FormattedAperture
    {
        get
        {
            if (Aperture is not { } f || f <= 0) return null;
            return Math.Abs(f - Math.Floor(f)) < 0.001 ? $"ƒ/{f:0}" : $"ƒ/{f:0.0}";
        }
    }

    public string? FormattedIso => Iso is > 0 ? $"ISO {Iso}" : null;

    public string? FormattedFocal
    {
        get
        {
            if (FocalLength is not { } fl || fl <= 0) return null;
            return Math.Abs(fl - Math.Floor(fl)) < 0.001 ? $"{(int)fl}mm" : $"{fl:0.0}mm";
        }
    }

    public string? FormattedBias
    {
        get
        {
            if (ExposureBias is not { } b || Math.Abs(b) <= 0.01) return null;
            return $"{b:+0.0;-0.0} EV";
        }
    }

    public string? FormattedDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateTimeOriginal)) return null;
            if (DateTime.TryParseExact(DateTimeOriginal, "yyyy:MM:dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            {
                return dt.ToString("g");
            }
            return DateTimeOriginal;
        }
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
}
