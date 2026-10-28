import Foundation
import UniformTypeIdentifiers

/// Utilities for detecting supported image formats, including RAW formats supported by macOS ImageIO.
public enum SupportedFormats {
    /// Supported standard image file extensions.
    public static let standardExtensions: Set<String> = [
        "jpg", "jpeg", "png", "heic", "heif", "tiff", "tif",
        "gif", "webp", "bmp", "ico", "jp2"
    ]

    /// Supported RAW camera formats natively decoded by macOS ImageIO / Apple Camera RAW engine.
    public static let rawExtensions: Set<String> = [
        "cr2", "cr3", // Canon
        "nef", "nrw", // Nikon
        "arw", "srf", "sr2", // Sony
        "dng", // Adobe Digital Negative / Leica / Pentax / Apple ProRAW
        "raf", // Fujifilm
        "orf", "ori", // Olympus
        "rw2", // Panasonic
        "pef", "ptx", // Pentax
        "srw", // Samsung
        "raw", // General RAW
        "3fr", // Hasselblad
        "mef", // Mamiya
        "mos", // Leaf
        "erf", // Epson
        "kdc", "dcr" // Kodak
    ]

    public static let allExtensions: Set<String> = standardExtensions.union(rawExtensions)

    /// Checks if a given file URL has a supported image extension or conforms to UTType.image.
    public static func isSupported(url: URL) -> Bool {
        let ext = url.pathExtension.lowercased()
        if allExtensions.contains(ext) {
            return true
        }

        if let utType = UTType(filenameExtension: ext) {
            if utType.conforms(to: .image) || utType.conforms(to: .rawImage) {
                return true
            }
        }
        return false
    }

    /// Checks if the extension belongs to a RAW format.
    public static func isRaw(url: URL) -> Bool {
        let ext = url.pathExtension.lowercased()
        if rawExtensions.contains(ext) {
            return true
        }
        if let utType = UTType(filenameExtension: ext) {
            return utType.conforms(to: .rawImage)
        }
        return false
    }
}
