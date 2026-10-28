import Foundation
import Darwin

public struct ScanResult: Sendable {
    public let items: [SlideItem]
    public let initialIndex: Int
    public let directoryURL: URL

    public init(items: [SlideItem], initialIndex: Int, directoryURL: URL) {
        self.items = items
        self.initialIndex = initialIndex
        self.directoryURL = directoryURL
    }
}

/// Asynchronous directory scanner that gathers and natural-sorts sibling image files off the main thread.
public actor DirectoryScanner {
    public init() {}

    /// Scans the directory containing `targetURL` (or `targetURL` itself if it is a directory)
    /// and finds all supported images, returning them with natural alphanumeric sorting.
    public func scan(targetURL: URL) async throws -> ScanResult {
        return try await Task.detached(priority: .userInitiated) {
            let fileManager = FileManager.default
            var isDir: ObjCBool = false

            guard fileManager.fileExists(atPath: targetURL.path, isDirectory: &isDir) else {
                throw NSError(domain: "DirectoryScanner", code: 404, userInfo: [
                    NSLocalizedDescriptionKey: "File does not exist at path: \(targetURL.path)"
                ])
            }

            let directoryURL: URL
            let selectedFilename: String?

            if isDir.boolValue {
                directoryURL = targetURL
                selectedFilename = nil
            } else {
                directoryURL = targetURL.deletingLastPathComponent()
                selectedFilename = targetURL.lastPathComponent
            }

            let resourceKeys: [URLResourceKey] = [
                .fileSizeKey,
                .isRegularFileKey,
                .nameKey
            ]

            let contents = try fileManager.contentsOfDirectory(
                at: directoryURL,
                includingPropertiesForKeys: resourceKeys,
                options: [.skipsHiddenFiles]
            )

            // Filter for supported images and map to SlideItem
            var items: [SlideItem] = []
            for fileURL in contents {
                guard SupportedFormats.isSupported(url: fileURL) else { continue }

                // Strip quarantine and corrupt launch-services attributes in background
                _ = removexattr(fileURL.path, "com.apple.quarantine", 0)
                _ = removexattr(fileURL.path, "com.apple.LaunchServices.OpenWith", 0)

                var fileSize: Int64 = 0
                if let resourceValues = try? fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                   resourceValues.isRegularFile == true {
                    fileSize = Int64(resourceValues.fileSize ?? 0)
                }

                items.append(SlideItem(url: fileURL, fileSize: fileSize))
            }

            // Natural alphanumeric sort (e.g., photo1, photo2, photo10 instead of photo1, photo10, photo2)
            items.sort {
                $0.filename.localizedStandardCompare($1.filename) == .orderedAscending
            }

            // Find index of the clicked file
            var initialIndex = 0
            if let selected = selectedFilename {
                if let foundIndex = items.firstIndex(where: { $0.filename == selected }) {
                    initialIndex = foundIndex
                }
            }

            return ScanResult(items: items, initialIndex: initialIndex, directoryURL: directoryURL)
        }.value
    }
}
