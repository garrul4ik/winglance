import Foundation
import UniformTypeIdentifiers

/// Represents a single slide item in the folder.
public struct SlideItem: Identifiable, Hashable, Sendable {
    public let id: String
    public let url: URL
    public let filename: String
    public let fileSize: Int64

    public init(url: URL, fileSize: Int64 = 0) {
        self.url = url
        self.id = url.path
        self.filename = url.lastPathComponent
        self.fileSize = fileSize
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(url.path)
    }

    public static func == (lhs: SlideItem, rhs: SlideItem) -> Bool {
        lhs.url.path == rhs.url.path
    }
}
