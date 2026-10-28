using System;
using System.Collections.Generic;
using System.IO;

namespace WinGlance.Engine;

public static class SupportedFormats
{
    public static readonly HashSet<string> StandardExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".ico", ".gif"
    };

    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", // Canon
        ".nef", ".nrw", // Nikon
        ".arw", ".srf", ".sr2", // Sony
        ".dng", // Adobe Digital Negative / Apple ProRAW
        ".raf", // Fujifilm
        ".orf", ".ori", // Olympus / OM System
        ".rw2", // Panasonic Lumix
        ".pef", ".ptx", // Pentax
        ".srw", // Samsung
        ".raw", // Leica / General RAW
        ".3fr", // Hasselblad
        ".mef", // Mamiya
        ".mos", // Leaf
        ".erf", // Epson
        ".kdc", ".dcr" // Kodak
    };

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return StandardExtensions.Contains(ext) || RawExtensions.Contains(ext);
    }

    public static bool IsRaw(string path)
    {
        var ext = Path.GetExtension(path);
        return RawExtensions.Contains(ext);
    }
}
