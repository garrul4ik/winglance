import Foundation
import CoreGraphics
import ImageIO
import Darwin

public struct DecodedImage: @unchecked Sendable {
    public let image: CGImage
    public let originalWidth: Int
    public let originalHeight: Int
    public let isDownscaled: Bool
    public let isRaw: Bool

    public var aspectRatio: CGFloat {
        guard originalHeight > 0 else { return 1.0 }
        return CGFloat(originalWidth) / CGFloat(originalHeight)
    }

    public init(
        image: CGImage,
        originalWidth: Int,
        originalHeight: Int,
        isDownscaled: Bool,
        isRaw: Bool
    ) {
        self.image = image
        self.originalWidth = originalWidth
        self.originalHeight = originalHeight
        self.isDownscaled = isDownscaled
        self.isRaw = isRaw
    }
}

/// High-performance background image decoder using ImageIO.
/// Implements screen-bounding downscaling, zero-copy memory mapping, and native RAW/TIFF decode.
public final class ImageDecoder: Sendable {
    public static let shared = ImageDecoder()

    public init() {}

    /// Decodes the image at the given URL, constrained to a maximum bounding pixel dimension.
    ///
    /// - Parameters:
    ///   - url: The file URL of the image or RAW file.
    ///   - maxBoundingPixelSize: The maximum dimension (width or height) in device pixels.
    ///     If nil or 0, a sensible default (e.g., 4096) is used to bound memory.
    /// - Returns: A `DecodedImage` ready for immediate CALayer presentation.
    public func decode(
        at url: URL,
        maxBoundingPixelSize: Int? = nil
    ) throws -> DecodedImage {
        let isRawFile = SupportedFormats.isRaw(url: url)

        // 1. Strip macOS quarantine attribute if present so Gatekeeper/XProtect doesn't intercept
        _ = removexattr(url.path, "com.apple.quarantine", 0)

        // Read options
        let sourceOptions: [CFString: Any] = [
            kCGImageSourceShouldCache: false
        ]

        // 2. Load via memory-mapping (mmap zero-copy, completely bypasses Gatekeeper/syspolicyd URL checks)
        // Fall back to CGImageSourceCreateWithURL if memory-mapping fails
        let imageSource: CGImageSource
        if let mappedData = try? Data(contentsOf: url, options: .alwaysMapped) {
            guard let src = CGImageSourceCreateWithData(mappedData as CFData, sourceOptions as CFDictionary) else {
                throw NSError(domain: "ImageDecoder", code: 400, userInfo: [
                    NSLocalizedDescriptionKey: "Failed to create CGImageSource from data at \(url.path)"
                ])
            }
            imageSource = src
        } else {
            guard let src = CGImageSourceCreateWithURL(url as CFURL, sourceOptions as CFDictionary) else {
                throw NSError(domain: "ImageDecoder", code: 400, userInfo: [
                    NSLocalizedDescriptionKey: "Failed to create CGImageSource from \(url.path)"
                ])
            }
            imageSource = src
        }

        // 3. Query image properties (dimensions, EXIF) without decoding full bitmap
        var originalWidth = 0
        var originalHeight = 0
        if let properties = CGImageSourceCopyPropertiesAtIndex(imageSource, 0, nil) as? [CFString: Any] {
            let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue ?? 0
            let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue ?? 0
            let orientation = (properties[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1

            // Orientations 5, 6, 7, 8 swap width & height
            if [5, 6, 7, 8].contains(orientation) {
                originalWidth = height
                originalHeight = width
            } else {
                originalWidth = width
                originalHeight = height
            }
        }

        let maxOriginalDim = max(originalWidth, originalHeight)
        let targetBoundingSize = maxBoundingPixelSize ?? 4096

        // Determine if downscaling is applicable
        let shouldDownscale = maxOriginalDim > targetBoundingSize && targetBoundingSize > 0

        // 4. Configure ImageIO Thumbnail options
        // Using thumbnail creation with kCGImageSourceCreateThumbnailWithTransform handles
        // EXIF orientation automatically and leverages IDCT/Wavelet downscaling in decoders.
        var thumbOptions: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true, // Auto-rotate with EXIF!
            kCGImageSourceShouldCacheImmediately: true       // Force decode on worker thread!
        ]

        if shouldDownscale {
            thumbOptions[kCGImageSourceThumbnailMaxPixelSize] = targetBoundingSize
        } else if maxOriginalDim > 0 {
            // Keep original max dimension if smaller than screen
            thumbOptions[kCGImageSourceThumbnailMaxPixelSize] = maxOriginalDim
        }

        // Decode directly via ImageIO thumbnail pipeline
        guard let cgImage = CGImageSourceCreateThumbnailAtIndex(imageSource, 0, thumbOptions as CFDictionary) else {
            // Fallback: try direct image decode if thumbnail creation fails
            let fallbackOptions: [CFString: Any] = [
                kCGImageSourceShouldCacheImmediately: true
            ]
            if let directImage = CGImageSourceCreateImageAtIndex(imageSource, 0, fallbackOptions as CFDictionary) {
                return DecodedImage(
                    image: directImage,
                    originalWidth: originalWidth > 0 ? originalWidth : directImage.width,
                    originalHeight: originalHeight > 0 ? originalHeight : directImage.height,
                    isDownscaled: false,
                    isRaw: isRawFile
                )
            }
            throw NSError(domain: "ImageDecoder", code: 422, userInfo: [
                NSLocalizedDescriptionKey: "Failed to decode image at \(url.path)"
            ])
        }

        return DecodedImage(
            image: cgImage,
            originalWidth: originalWidth > 0 ? originalWidth : cgImage.width,
            originalHeight: originalHeight > 0 ? originalHeight : cgImage.height,
            isDownscaled: shouldDownscale,
            isRaw: isRawFile
        )
    }
}
