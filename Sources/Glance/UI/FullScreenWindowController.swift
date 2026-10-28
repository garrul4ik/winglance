import AppKit
import CoreGraphics

@MainActor
public final class FullScreenWindowController: NSWindowController, SlideViewDelegate, NSWindowDelegate {
    private let slideView: SlideView
    private let pipeline: SlidePipeline
    private let scanner: DirectoryScanner

    private var allItems: [SlideItem] = []
    private var cullingData: [String: CullingMetadata] = [:]

    // Filter & Navigation
    private var currentFilter: CullingFilter = .all
    private var filteredIndices: [Int] = []
    private var currentFilteredPos: Int = 0

    private var autoAdvance: Bool = false
    private var isDeleting: Bool = false

    public init() {
        let screenRect = NSScreen.main?.frame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let window = NSWindow(
            contentRect: screenRect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.backgroundColor = .black
        window.isOpaque = true
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.hasShadow = false
        window.collectionBehavior = [.fullScreenPrimary, .canJoinAllSpaces]

        self.slideView = SlideView(frame: screenRect)
        self.pipeline = SlidePipeline()
        self.scanner = DirectoryScanner()

        super.init(window: window)

        window.delegate = self
        window.contentView = slideView
        slideView.delegate = self
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    // MARK: - Lifecycle & Opening

    public func open(targetURL: URL, startFullScreen: Bool = true) {
        guard let window = self.window else { return }

        window.makeKeyAndOrderFront(nil)
        if startFullScreen && !window.styleMask.contains(.fullScreen) {
            window.toggleFullScreen(nil)
        }

        Task {
            do {
                let scanResult = try await scanner.scan(targetURL: targetURL)
                guard !scanResult.items.isEmpty else {
                    displayEmptyState("No supported images found in directory.")
                    return
                }

                self.allItems = scanResult.items
                self.rebuildFilter(targetIndex: scanResult.initialIndex)

                let screen = window.screen ?? NSScreen.main
                let scale = screen?.backingScaleFactor ?? 2.0
                let screenSize = screen?.frame.size ?? CGSize(width: 1920, height: 1080)
                let maxPixelSize = Int(max(screenSize.width, screenSize.height) * scale)

                let rawInitial = self.currentRawIndex ?? 0
                await pipeline.setPlaylist(
                    items: self.allItems,
                    initialIndex: rawInitial,
                    maxPixelSize: maxPixelSize
                )

                await loadAndDisplayCurrent()
            } catch {
                displayEmptyState("Error loading: \(error.localizedDescription)")
            }
        }
    }

    private var currentRawIndex: Int? {
        guard !filteredIndices.isEmpty, currentFilteredPos >= 0 && currentFilteredPos < filteredIndices.count else {
            return nil
        }
        return filteredIndices[currentFilteredPos]
    }

    private func displayEmptyState(_ message: String) {
        slideView.displayMessage(message)
    }

    // MARK: - Filter Management

    private func rebuildFilter(targetIndex: Int? = nil) {
        let prevRawIndex = targetIndex ?? currentRawIndex

        filteredIndices = allItems.indices.filter { idx in
            let item = allItems[idx]
            let meta = cullingData[item.id] ?? CullingMetadata()
            return currentFilter.matches(meta: meta)
        }

        if filteredIndices.isEmpty {
            currentFilteredPos = 0
        } else if let prev = prevRawIndex, let match = filteredIndices.firstIndex(of: prev) {
            currentFilteredPos = match
        } else {
            currentFilteredPos = max(0, min(currentFilteredPos, filteredIndices.count - 1))
        }
    }

    // MARK: - Navigation

    private func navigate(toFilteredPos pos: Int, direction: SlideDirection) {
        guard !filteredIndices.isEmpty else { return }
        let clamped = max(0, min(pos, filteredIndices.count - 1))
        guard clamped != currentFilteredPos || filteredIndices.count == 1 else { return }

        self.currentFilteredPos = clamped
        guard let rawIndex = currentRawIndex else { return }

        Task {
            await pipeline.navigate(to: rawIndex, direction: direction)
            await loadAndDisplayCurrent()
        }
    }

    private func loadAndDisplayCurrent() async {
        guard let rawIndex = currentRawIndex, rawIndex >= 0 && rawIndex < allItems.count else {
            displayEmptyState("No matching photos for filter '\(currentFilter.title)'. Press [ \\ ] to show all.")
            return
        }

        let item = allItems[rawIndex]
        let meta = cullingData[item.id] ?? CullingMetadata()

        let formattedSize = item.fileSize > 0
            ? ByteCountFormatter.string(fromByteCount: item.fileSize, countStyle: .file)
            : nil

        do {
            let decoded = try await pipeline.getImage(at: rawIndex)
            let resText = "\(decoded.originalWidth) × \(decoded.originalHeight)"

            var splitCgImage: CGImage? = nil
            if slideView.isSplitMode && currentFilteredPos + 1 < filteredIndices.count {
                let nextRaw = filteredIndices[currentFilteredPos + 1]
                if let nextDecoded = try? await pipeline.getImage(at: nextRaw) {
                    splitCgImage = nextDecoded.image
                }
            }

            let filterBadge = currentFilter == .all ? nil : currentFilter.title

            let info = HudInfo(
                currentIndex: currentFilteredPos,
                totalCount: filteredIndices.count,
                filename: item.filename,
                dimensions: resText,
                fileSize: formattedSize,
                isRaw: decoded.isRaw,
                isDownscaled: decoded.isDownscaled,
                culling: meta,
                autoAdvance: autoAdvance,
                filterTitle: filterBadge
            )

            let nativeSize = CGSize(width: decoded.originalWidth, height: decoded.originalHeight)
            slideView.displayImage(
                decoded.image,
                nextImage: splitCgImage,
                info: info,
                nativeSize: nativeSize
            )
        } catch {
            let info = HudInfo(
                currentIndex: currentFilteredPos,
                totalCount: filteredIndices.count,
                filename: item.filename,
                culling: meta,
                autoAdvance: autoAdvance,
                errorMessage: "Decode error: \(error.localizedDescription)"
            )
            slideView.displayImage(nil, info: info)
        }
    }

    // MARK: - SlideViewDelegate Navigation

    public func slideViewRequestNext() {
        guard !filteredIndices.isEmpty else { return }
        if currentFilteredPos + 1 < filteredIndices.count {
            navigate(toFilteredPos: currentFilteredPos + 1, direction: .forward)
        } else {
            navigate(toFilteredPos: 0, direction: .forward)
        }
    }

    public func slideViewRequestPrevious() {
        guard !filteredIndices.isEmpty else { return }
        if currentFilteredPos - 1 >= 0 {
            navigate(toFilteredPos: currentFilteredPos - 1, direction: .backward)
        } else {
            navigate(toFilteredPos: filteredIndices.count - 1, direction: .backward)
        }
    }

    public func slideViewRequestFirst() {
        navigate(toFilteredPos: 0, direction: .jump)
    }

    public func slideViewRequestLast() {
        guard !filteredIndices.isEmpty else { return }
        navigate(toFilteredPos: filteredIndices.count - 1, direction: .jump)
    }

    public func slideViewRequestDelete() {
        guard let rawIndex = currentRawIndex, !isDeleting else { return }
        let itemToDelete = allItems[rawIndex]
        isDeleting = true

        Task {
            do {
                _ = try await TrashManager.moveToTrash(url: itemToDelete.url)
                _ = await self.pipeline.removeItem(at: rawIndex)
                self.allItems.remove(at: rawIndex)
                self.cullingData.removeValue(forKey: itemToDelete.id)

                self.rebuildFilter()
                self.isDeleting = false

                if self.allItems.isEmpty {
                    self.displayEmptyState("Directory is now empty.")
                } else {
                    await self.loadAndDisplayCurrent()
                }
            } catch {
                self.isDeleting = false
                self.displayEmptyState("Failed to trash: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Batch Purge Rejects

    public func slideViewRequestPurgeRejects() {
        let rejectIndices = allItems.indices.filter { idx in
            let item = allItems[idx]
            return cullingData[item.id]?.pick == .reject
        }

        guard !rejectIndices.isEmpty else {
            slideView.hudView.showMessage("No rejected files to purge.")
            return
        }

        let alert = NSAlert()
        alert.messageText = "Purge \(rejectIndices.count) Rejected Files?"
        alert.informativeText = "The files will be safely moved to macOS Trash."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Move to Trash")
        alert.addButton(withTitle: "Cancel")

        guard let window = self.window else { return }
        alert.beginSheetModal(for: window) { [weak self] response in
            guard let self = self, response == .alertFirstButtonReturn else { return }
            self.executePurgeRejects(indices: Set(rejectIndices))
        }
    }

    private func executePurgeRejects(indices: Set<Int>) {
        isDeleting = true
        slideView.hudView.showMessage("Trashing \(indices.count) rejected files...")

        Task {
            var trashedCount = 0
            for idx in indices {
                let item = allItems[idx]
                if (try? await TrashManager.moveToTrash(url: item.url)) != nil {
                    trashedCount += 1
                }
                cullingData.removeValue(forKey: item.id)
            }

            await pipeline.removeItems(at: indices)
            let sortedDesc = indices.sorted(by: >)
            for idx in sortedDesc {
                allItems.remove(at: idx)
            }

            self.rebuildFilter()
            self.isDeleting = false

            if self.allItems.isEmpty {
                self.displayEmptyState("Directory is now empty.")
            } else {
                slideView.hudView.showMessage("Moved \(trashedCount) rejected files to Trash.")
                await self.loadAndDisplayCurrent()
            }
        }
    }

    // MARK: - 100% 1:1 Pixel Peep Action

    public func slideViewRequestZoomToggle(mouseLocation: CGPoint) {
        if slideView.isZoomed {
            slideView.setZoom(false)
            slideView.hudView.showMessage("Fit to Screen [Z]")
        } else {
            guard let rawIndex = currentRawIndex else { return }
            slideView.hudView.showMessage("100% Actual Pixels [Z]")

            // Instantly zoom the view using the screen-bounded layer first (0ms latency!)
            slideView.setZoom(true, anchorPointInView: mouseLocation)

            // Concurrently fetch the full 100% unconstrained sensor resolution in background
            Task {
                if let fullRes = try? await pipeline.getFullResolutionImage(at: rawIndex) {
                    let fullDims = CGSize(width: fullRes.originalWidth, height: fullRes.originalHeight)
                    self.slideView.setZoom(
                        true,
                        anchorPointInView: mouseLocation,
                        fullResImage: fullRes.image,
                        originalDimensions: fullDims
                    )
                }
            }
        }
    }

    // MARK: - Culling Actions

    public func slideViewSetRating(_ rating: Int) {
        guard let rawIndex = currentRawIndex else { return }
        let item = allItems[rawIndex]
        var meta = cullingData[item.id] ?? CullingMetadata()
        meta.rating = rating
        cullingData[item.id] = meta

        applyCullingUpdate()
    }

    public func slideViewSetPick(_ pick: PickStatus) {
        guard let rawIndex = currentRawIndex else { return }
        let item = allItems[rawIndex]
        var meta = cullingData[item.id] ?? CullingMetadata()
        meta.pick = pick
        cullingData[item.id] = meta

        applyCullingUpdate()
    }

    public func slideViewSetColor(_ color: ColorLabel) {
        guard let rawIndex = currentRawIndex else { return }
        let item = allItems[rawIndex]
        var meta = cullingData[item.id] ?? CullingMetadata()
        meta.color = color
        cullingData[item.id] = meta

        applyCullingUpdate()
    }

    private func applyCullingUpdate() {
        Task {
            if currentFilter != .all {
                let prevRaw = currentRawIndex
                rebuildFilter(targetIndex: prevRaw)
            }

            await loadAndDisplayCurrent()

            if autoAdvance {
                slideViewRequestNext()
            }
        }
    }

    public func slideViewToggleAutoAdvance() {
        autoAdvance.toggle()
        let status = autoAdvance ? "Auto-Advance: ON [C]" : "Auto-Advance: OFF [C]"
        slideView.hudView.showMessage(status)
        Task {
            await loadAndDisplayCurrent()
        }
    }

    public func slideViewCycleFilter() {
        switch currentFilter {
        case .all:
            currentFilter = .hideRejects
        case .hideRejects:
            currentFilter = .picksOnly
        case .picksOnly:
            currentFilter = .rejectsOnly
        case .rejectsOnly:
            currentFilter = .unratedOnly
        case .unratedOnly:
            currentFilter = .minRating(3)
        case .minRating(3):
            currentFilter = .minRating(5)
        case .minRating:
            currentFilter = .all
        }

        rebuildFilter()
        slideView.hudView.showMessage("Filter: \(currentFilter.title) [ \\ ]")
        Task {
            await loadAndDisplayCurrent()
        }
    }

    public func slideViewToggleSplitCompare() {
        let newState = !slideView.isSplitMode
        slideView.setSplitMode(newState)
        let modeMsg = newState ? "Burst Compare: 2-UP [S]" : "Single Fullscreen [S]"
        slideView.hudView.showMessage(modeMsg)
        Task {
            await loadAndDisplayCurrent()
        }
    }

    public func slideViewToggleFullScreen() {
        window?.toggleFullScreen(nil)
    }

    public func slideViewClose() {
        if window?.styleMask.contains(.fullScreen) == true {
            window?.toggleFullScreen(nil)
        } else {
            NSApp.terminate(nil)
        }
    }

    public func slideViewDidDrop(url: URL) {
        open(targetURL: url, startFullScreen: false)
    }

    // MARK: - Window Delegate

    public func windowDidResize(_ notification: Notification) {
        guard let window = self.window else { return }
        let scale = window.backingScaleFactor
        let maxPixelSize = Int(max(window.frame.width, window.frame.height) * scale)
        Task {
            await pipeline.updateBoundingSize(maxPixelSize)
        }
    }

    public func windowWillClose(_ notification: Notification) {
        NSApp.terminate(nil)
    }
}
