import Foundation
import CoreGraphics

public enum SlideDirection: Sendable {
    case forward
    case backward
    case jump
}

/// Actor managing the triple/lookahead buffer cache and background preloading.
public actor SlidePipeline {
    private var items: [SlideItem] = []
    private var currentIndex: Int = 0
    private var maxPixelSize: Int = 3840

    /// In-memory cache of decoded images: [Index: DecodedImage]
    private var cache: [Int: DecodedImage] = [:]

    /// Active decode tasks: [Index: Task<DecodedImage, Error>]
    private var inFlightTasks: [Int: Task<DecodedImage, Error>] = [:]

    private let decoder: ImageDecoder

    /// Cache radius: 1 gives triple buffering [curr-1, curr, curr+1].
    /// Setting to 2 gives 5 images [curr-2, curr-1, curr, curr+1, curr+2] for silky smooth fast-skipping.
    private let cacheRadius: Int = 2

    public init(decoder: ImageDecoder = ImageDecoder.shared) {
        self.decoder = decoder
    }

    /// Configures the pipeline with a new list of items and initial index.
    public func setPlaylist(items: [SlideItem], initialIndex: Int, maxPixelSize: Int) {
        // Cancel all existing tasks
        cancelAllTasks()
        cache.removeAll()

        self.items = items
        self.currentIndex = max(0, min(initialIndex, items.count - 1))
        self.maxPixelSize = maxPixelSize

        // Preload immediate window
        preloadWindow(around: self.currentIndex, direction: .forward)
    }

    /// Updates the screen bounding size (e.g., if window is resized or moved to another screen).
    public func updateBoundingSize(_ newSize: Int) {
        guard self.maxPixelSize != newSize else { return }
        self.maxPixelSize = newSize
        // Re-preload current window with new resolution
        preloadWindow(around: self.currentIndex, direction: .forward)
    }

    /// Retrieves the decoded image for a given index.
    /// If cached, returns immediately.
    /// If not cached, awaits its decode or starts decoding immediately with high priority.
    public func getImage(at index: Int) async throws -> DecodedImage {
        guard index >= 0 && index < items.count else {
            throw NSError(domain: "SlidePipeline", code: 404, userInfo: [
                NSLocalizedDescriptionKey: "Index \(index) out of bounds."
            ])
        }

        // 1. Check cache hit
        if let cached = cache[index] {
            return cached
        }

        // 2. Check if already in flight
        if let existingTask = inFlightTasks[index] {
            return try await existingTask.value
        }

        // 3. Start high-priority decode task
        let item = items[index]
        let bounding = self.maxPixelSize
        let decoder = self.decoder

        let task = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            return try decoder.decode(at: item.url, maxBoundingPixelSize: bounding)
        }

        inFlightTasks[index] = task

        do {
            let decoded = try await task.value
            cache[index] = decoded
            inFlightTasks.removeValue(forKey: index)
            return decoded
        } catch {
            inFlightTasks.removeValue(forKey: index)
            throw error
        }
    }

    /// Notifies the pipeline of navigation to a new index, adjusting preloading priority and pruning old cache entries.
    public func navigate(to index: Int, direction: SlideDirection) {
        guard index >= 0 && index < items.count else { return }
        self.currentIndex = index
        pruneAndPreload(around: index, direction: direction)
    }

    /// Removes an item at the given index (e.g. after deletion) and updates internal state.
    public func removeItem(at index: Int) -> Int {
        guard index >= 0 && index < items.count else { return currentIndex }

        // Cancel tasks and clear cache
        inFlightTasks[index]?.cancel()
        inFlightTasks.removeValue(forKey: index)
        cache.removeValue(forKey: index)

        items.remove(at: index)

        // Shift cache keys above index
        var newCache: [Int: DecodedImage] = [:]
        for (k, v) in cache {
            if k < index {
                newCache[k] = v
            } else if k > index {
                newCache[k - 1] = v
            }
        }
        cache = newCache

        // Shift inFlight tasks
        var newTasks: [Int: Task<DecodedImage, Error>] = [:]
        for (k, v) in inFlightTasks {
            if k < index {
                newTasks[k] = v
            } else if k > index {
                newTasks[k - 1] = v
            }
        }
        inFlightTasks = newTasks

        // Adjust current index
        if items.isEmpty {
            currentIndex = 0
        } else if currentIndex >= items.count {
            currentIndex = items.count - 1
        }

        pruneAndPreload(around: currentIndex, direction: .forward)
        return currentIndex
    }

    /// Batch removes items at the given indices (e.g. purging rejects)
    public func removeItems(at indices: Set<Int>) {
        cancelAllTasks()
        cache.removeAll()

        let sorted = indices.sorted(by: >)
        for idx in sorted {
            if idx >= 0 && idx < items.count {
                items.remove(at: idx)
            }
        }

        if currentIndex >= items.count {
            currentIndex = max(0, items.count - 1)
        }
        pruneAndPreload(around: currentIndex, direction: .forward)
    }

    // MARK: - Private Cache Management

    private func pruneAndPreload(around centerIndex: Int, direction: SlideDirection) {
        let validRange = (centerIndex - cacheRadius)...(centerIndex + cacheRadius)

        // 1. Evict images outside sliding window
        let keysToEvict = cache.keys.filter { !validRange.contains($0) }
        for key in keysToEvict {
            cache.removeValue(forKey: key)
        }

        // 2. Cancel tasks outside sliding window
        let tasksToCancel = inFlightTasks.keys.filter { !validRange.contains($0) }
        for key in tasksToCancel {
            inFlightTasks[key]?.cancel()
            inFlightTasks.removeValue(forKey: key)
        }

        // 3. Preload window
        preloadWindow(around: centerIndex, direction: direction)
    }

    private func preloadWindow(around centerIndex: Int, direction: SlideDirection) {
        var loadOrder: [Int] = [centerIndex]

        switch direction {
        case .forward, .jump:
            for offset in 1...cacheRadius {
                loadOrder.append(centerIndex + offset)
                loadOrder.append(centerIndex - offset)
            }
        case .backward:
            for offset in 1...cacheRadius {
                loadOrder.append(centerIndex - offset)
                loadOrder.append(centerIndex + offset)
            }
        }

        for idx in loadOrder {
            guard idx >= 0 && idx < items.count else { continue }
            guard cache[idx] == nil && inFlightTasks[idx] == nil else { continue }

            let item = items[idx]
            let bounding = self.maxPixelSize
            let decoder = self.decoder

            let task = Task.detached(priority: .utility) {
                try Task.checkCancellation()
                return try decoder.decode(at: item.url, maxBoundingPixelSize: bounding)
            }

            inFlightTasks[idx] = task

            Task { [weak self, idx] in
                do {
                    let decoded = try await task.value
                    await self?.saveDecoded(decoded, for: idx)
                } catch {
                    await self?.taskFinished(for: idx)
                }
            }
        }
    }

    private func saveDecoded(_ decoded: DecodedImage, for index: Int) {
        let validRange = (currentIndex - cacheRadius)...(currentIndex + cacheRadius)
        if validRange.contains(index) {
            cache[index] = decoded
        }
        inFlightTasks.removeValue(forKey: index)
    }

    private func taskFinished(for index: Int) {
        inFlightTasks.removeValue(forKey: index)
    }

    private func cancelAllTasks() {
        for (_, task) in inFlightTasks {
            task.cancel()
        }
        inFlightTasks.removeAll()
    }
}
