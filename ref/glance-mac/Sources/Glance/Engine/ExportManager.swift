import Foundation

public struct ExportProgress: Sendable {
    public let current: Int
    public let total: Int
    public let filename: String
    public let destinationURL: URL
}

public struct ExportSummary: Sendable {
    public let exportedCount: Int
    public let totalFilesWritten: Int
    public let destinationFolder: URL
    public let errors: [String]
}

/// High-performance background export engine that copies selected photos, companion RAWs/JPEGs,
/// and Adobe XMP sidecars into a designated export directory (e.g. `./Picks`).
public actor ExportManager {
    public static let shared = ExportManager()

    public init() {}

    /// Exports the provided items and all associated companion files (RAW + JPEG stacks, XMP sidecars)
    /// to the specified target directory.
    ///
    /// - Parameters:
    ///   - items: The `SlideItem` array representing the chosen shots.
    ///   - destinationDirectory: The folder where photos should be exported (created if needed).
    ///   - onProgress: Async progress reporting callback invoked on each completed item.
    /// - Returns: An `ExportSummary` detailing results and any errors.
    public func export(
        items: [SlideItem],
        destinationDirectory: URL,
        onProgress: (@Sendable (ExportProgress) -> Void)? = nil
    ) throws -> ExportSummary {
        let fm = FileManager.default

        // Ensure destination folder exists
        if !fm.fileExists(atPath: destinationDirectory.path) {
            try fm.createDirectory(at: destinationDirectory, withIntermediateDirectories: true, attributes: nil)
        }

        var exportedCount = 0
        var totalFilesWritten = 0
        var errors: [String] = []

        let total = items.count

        for (index, item) in items.enumerated() {
            var itemSuccess = false

            // Collect all unique file URLs belonging to this slide (primary, raw, companion, and sidecars)
            var filesToCopy = Set<URL>()
            for fileURL in item.allURLs {
                filesToCopy.insert(fileURL)
                if let sidecar = XmpSidecarManager.existingSidecarURL(for: fileURL) {
                    filesToCopy.insert(sidecar)
                }
            }

            // Also check for target sidecar naming of primary URL
            if let primarySidecar = XmpSidecarManager.existingSidecarURL(for: item.url) {
                filesToCopy.insert(primarySidecar)
            }

            for sourceURL in filesToCopy {
                guard fm.fileExists(atPath: sourceURL.path) else { continue }

                let destURL = destinationDirectory.appendingPathComponent(sourceURL.lastPathComponent)

                do {
                    // If file already exists in destination, remove it to overwrite cleanly
                    if fm.fileExists(atPath: destURL.path) {
                        try fm.removeItem(at: destURL)
                    }

                    try fm.copyItem(at: sourceURL, to: destURL)
                    totalFilesWritten += 1
                    itemSuccess = true
                } catch {
                    errors.append("Failed to copy \(sourceURL.lastPathComponent): \(error.localizedDescription)")
                }
            }

            if itemSuccess {
                exportedCount += 1
            }

            onProgress?(ExportProgress(
                current: index + 1,
                total: total,
                filename: item.displayName,
                destinationURL: destinationDirectory
            ))
        }

        return ExportSummary(
            exportedCount: exportedCount,
            totalFilesWritten: totalFilesWritten,
            destinationFolder: destinationDirectory,
            errors: errors
        )
    }
}
