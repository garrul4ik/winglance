import Foundation

public enum TrashManager {
    /// Moves a file to the macOS Trash asynchronously off the main thread.
    public static func moveToTrash(url: URL) async throws -> URL {
        return try await Task.detached(priority: .userInitiated) {
            var resultingURL: NSURL?
            try FileManager.default.trashItem(at: url, resultingItemURL: &resultingURL)
            guard let trashedURL = resultingURL as? URL else {
                return url
            }
            return trashedURL
        }.value
    }
}
