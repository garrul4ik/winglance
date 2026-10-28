import AppKit
import CoreGraphics
import QuartzCore

@MainActor
public protocol SlideViewDelegate: AnyObject {
    func slideViewRequestNext()
    func slideViewRequestPrevious()
    func slideViewRequestFirst()
    func slideViewRequestLast()
    func slideViewRequestDelete()
    func slideViewRequestPurgeRejects()
    func slideViewToggleFullScreen()
    func slideViewClose()
    func slideViewDidDrop(url: URL)

    // Culling Actions
    func slideViewSetRating(_ rating: Int)
    func slideViewSetPick(_ pick: PickStatus)
    func slideViewSetColor(_ color: ColorLabel)
    func slideViewToggleAutoAdvance()
    func slideViewCycleFilter()
    func slideViewToggleSplitCompare()

    // 100% Zoom Action
    func slideViewRequestZoomToggle(mouseLocation: CGPoint)
}

/// Ultra-responsive, zero-latency layer-backed NSView.
/// Supports single image full-screen display, 2-up split burst compare mode,
/// instant 100% 1:1 pixel peeping with smooth pan/drag, and quick help overlay.
@MainActor
public final class SlideView: NSView {
    public weak var delegate: SlideViewDelegate?

    private let containerLayer = CALayer()
    private let imageLayer = CALayer()
    private let splitLayer = CALayer()

    public let hudView = GlassHudView()
    public let quickHelpView = QuickHelpView()

    public private(set) var isSplitMode: Bool = false
    public private(set) var isZoomed: Bool = false

    // Zoom & Pan state
    private var zoomPanOffset: CGPoint = .zero
    private var dragStartMouse: CGPoint = .zero
    private var dragStartOffset: CGPoint = .zero
    private var isDragging: Bool = false
    private var currentImageSize: CGSize = .zero

    private var cursorTimer: Timer?
    private var trackingArea: NSTrackingArea?

    public override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupLayers()
        setupHud()
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setupLayers()
        setupHud()
        registerForDraggedTypes([.fileURL])
    }

    private func setupLayers() {
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor

        // Root container for image layer
        containerLayer.backgroundColor = NSColor.black.cgColor
        containerLayer.masksToBounds = true
        layer?.addSublayer(containerLayer)

        // Main image layer
        imageLayer.backgroundColor = NSColor.black.cgColor
        imageLayer.isOpaque = true
        imageLayer.actions = [
            "contents": NSNull(),
            "bounds": NSNull(),
            "position": NSNull(),
            "transform": NSNull()
        ]
        containerLayer.addSublayer(imageLayer)

        // Split compare image layer
        splitLayer.contentsGravity = .resizeAspect
        splitLayer.backgroundColor = NSColor.black.cgColor
        splitLayer.isOpaque = true
        splitLayer.isHidden = true
        splitLayer.actions = [
            "contents": NSNull(),
            "bounds": NSNull(),
            "position": NSNull()
        ]
        layer?.addSublayer(splitLayer)
    }

    private func setupHud() {
        hudView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(hudView)

        quickHelpView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(quickHelpView)

        NSLayoutConstraint.activate([
            hudView.centerXAnchor.constraint(equalTo: centerXAnchor),
            hudView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -28),
            hudView.leadingAnchor.constraint(greaterThanOrEqualTo: leadingAnchor, constant: 32),
            hudView.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -32),
            hudView.heightAnchor.constraint(greaterThanOrEqualToConstant: 38),

            quickHelpView.centerXAnchor.constraint(equalTo: centerXAnchor),
            quickHelpView.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }

    public override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let existing = trackingArea {
            removeTrackingArea(existing)
        }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        self.trackingArea = area
    }

    public override func layout() {
        super.layout()
        updateLayerLayout(animated: false)
    }

    private func updateLayerLayout(animated: Bool) {
        CATransaction.begin()
        CATransaction.setDisableActions(!animated)
        if animated {
            CATransaction.setAnimationDuration(0.18)
            CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(name: .easeOut))
        }

        if isSplitMode {
            let halfWidth = bounds.width / 2
            containerLayer.frame = CGRect(x: 0, y: 0, width: halfWidth - 1, height: bounds.height)
            splitLayer.frame = CGRect(x: halfWidth + 1, y: 0, width: halfWidth - 1, height: bounds.height)
            imageLayer.frame = containerLayer.bounds
            imageLayer.contentsGravity = .resizeAspect
        } else {
            containerLayer.frame = bounds
            splitLayer.frame = .zero

            if isZoomed && currentImageSize.width > 0 && currentImageSize.height > 0 {
                // 100% 1:1 Pixel Peep mode:
                // Size layer to native image pixel dimensions scaled by backingScaleFactor
                let scale = window?.backingScaleFactor ?? 2.0
                let nativeW = currentImageSize.width / scale
                let nativeH = currentImageSize.height / scale

                imageLayer.contentsGravity = .resize
                imageLayer.bounds = CGRect(x: 0, y: 0, width: nativeW, height: nativeH)

                // Clamp panning offset so photo doesn't drift completely off-screen
                let maxPanX = max(0, (nativeW - bounds.width) / 2)
                let maxPanY = max(0, (nativeH - bounds.height) / 2)
                zoomPanOffset.x = min(maxPanX, max(-maxPanX, zoomPanOffset.x))
                zoomPanOffset.y = min(maxPanY, max(-maxPanY, zoomPanOffset.y))

                imageLayer.position = CGPoint(
                    x: (bounds.width / 2) + zoomPanOffset.x,
                    y: (bounds.height / 2) + zoomPanOffset.y
                )
            } else {
                // Fit to screen mode
                imageLayer.contentsGravity = .resizeAspect
                imageLayer.bounds = CGRect(origin: .zero, size: bounds.size)
                imageLayer.position = CGPoint(x: bounds.midX, y: bounds.midY)
                zoomPanOffset = .zero
            }
        }
        CATransaction.commit()
    }

    // MARK: - Image Presentation

    public func displayImage(
        _ cgImage: CGImage?,
        nextImage: CGImage? = nil,
        info: HudInfo?,
        nativeSize: CGSize? = nil
    ) {
        if let size = nativeSize {
            currentImageSize = size
        } else if let img = cgImage {
            currentImageSize = CGSize(width: img.width, height: img.height)
        }

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        imageLayer.contents = cgImage

        if isSplitMode && nextImage != nil {
            splitLayer.contents = nextImage
            splitLayer.isHidden = false
        } else {
            splitLayer.contents = nil
            splitLayer.isHidden = true
        }
        CATransaction.commit()

        updateLayerLayout(animated: false)

        if let info = info {
            hudView.update(with: info)
        }
    }

    public func setSplitMode(_ enabled: Bool) {
        isSplitMode = enabled
        if enabled { isZoomed = false }
        needsLayout = true
    }

    public func displayMessage(_ message: String) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        imageLayer.contents = nil
        splitLayer.contents = nil
        CATransaction.commit()

        hudView.showMessage(message)
    }

    public func toggleHud() {
        hudView.togglePersistent()
    }

    public func toggleHelp() {
        quickHelpView.toggle()
    }

    // MARK: - 100% Zoom & Pan Mechanics

    public func setZoom(
        _ zoomed: Bool,
        anchorPointInView: CGPoint? = nil,
        fullResImage: CGImage? = nil,
        originalDimensions: CGSize? = nil
    ) {
        if let dims = originalDimensions {
            currentImageSize = dims
        }
        if let fullImg = fullResImage {
            imageLayer.contents = fullImg
        }

        guard isZoomed != zoomed else { return }
        isZoomed = zoomed

        if isZoomed {
            // Center zoom around mouse cursor position
            let mousePt = anchorPointInView ?? CGPoint(x: bounds.midX, y: bounds.midY)
            let centerOffset = CGPoint(
                x: bounds.midX - mousePt.x,
                y: bounds.midY - mousePt.y
            )

            let scale = window?.backingScaleFactor ?? 2.0
            let nativeW = currentImageSize.width / scale
            let nativeH = currentImageSize.height / scale

            // Ratio of native size vs viewport fit
            let fitRatioX = nativeW / max(1, bounds.width)
            let fitRatioY = nativeH / max(1, bounds.height)

            zoomPanOffset = CGPoint(
                x: centerOffset.x * fitRatioX,
                y: centerOffset.y * fitRatioY
            )
        } else {
            zoomPanOffset = .zero
        }

        updateLayerLayout(animated: true)
    }

    public override func mouseDown(with event: NSEvent) {
        let mousePt = convert(event.locationInWindow, from: nil)
        if !isZoomed {
            // Click to zoom in
            delegate?.slideViewRequestZoomToggle(mouseLocation: mousePt)
        } else {
            // Start panning
            isDragging = true
            dragStartMouse = mousePt
            dragStartOffset = zoomPanOffset
        }
    }

    public override func mouseDragged(with event: NSEvent) {
        guard isZoomed && isDragging else { return }
        let currentMouse = convert(event.locationInWindow, from: nil)
        let deltaX = currentMouse.x - dragStartMouse.x
        let deltaY = currentMouse.y - dragStartMouse.y

        zoomPanOffset = CGPoint(
            x: dragStartOffset.x + deltaX,
            y: dragStartOffset.y + deltaY
        )

        updateLayerLayout(animated: false)
    }

    public override func mouseUp(with event: NSEvent) {
        if isDragging {
            let currentMouse = convert(event.locationInWindow, from: nil)
            let dist = hypot(currentMouse.x - dragStartMouse.x, currentMouse.y - dragStartMouse.y)
            isDragging = false

            // If mouse didn't drag, treat as click to zoom out
            if dist < 4.0 {
                delegate?.slideViewRequestZoomToggle(mouseLocation: currentMouse)
            }
        }
    }

    public override func scrollWheel(with event: NSEvent) {
        guard isZoomed else {
            super.scrollWheel(with: event)
            return
        }

        // Two-finger trackpad panning when zoomed in
        zoomPanOffset.x += event.scrollingDeltaX
        zoomPanOffset.y -= event.scrollingDeltaY
        updateLayerLayout(animated: false)
    }

    // MARK: - Keyboard Handling

    public override var acceptsFirstResponder: Bool {
        return true
    }

    public override func keyDown(with event: NSEvent) {
        resetCursorTimer()

        if quickHelpView.alphaValue > 0.0 && event.keyCode != 44 {
            quickHelpView.dismiss()
        }

        let isShift = event.modifierFlags.contains(.shift)

        switch event.keyCode {
        // Space, Right Arrow, Down Arrow, Page Down, 'N'
        case 49, 124, 125, 121, 45:
            if isZoomed { setZoom(false) }
            delegate?.slideViewRequestNext()

        // Left Arrow, Up Arrow, Page Up, 'K'
        case 123, 126, 116, 40:
            if isZoomed { setZoom(false) }
            delegate?.slideViewRequestPrevious()

        // Home
        case 115:
            if isZoomed { setZoom(false) }
            delegate?.slideViewRequestFirst()

        // End
        case 119:
            if isZoomed { setZoom(false) }
            delegate?.slideViewRequestLast()

        // 'Z' key (KeyCode 6) -> 100% 1:1 Pixel Peep Zoom
        case 6:
            let mouseInWindow = window?.mouseLocationOutsideOfEventStream ?? CGPoint(x: bounds.midX, y: bounds.midY)
            let localPt = convert(mouseInWindow, from: nil)
            delegate?.slideViewRequestZoomToggle(mouseLocation: localPt)

        // Delete / Backspace
        case 51, 117:
            if isZoomed { setZoom(false) }
            if isShift {
                delegate?.slideViewRequestPurgeRejects()
            } else {
                delegate?.slideViewRequestDelete()
            }

        // --- CULLING HOTKEYS ---
        case 35: // 'P'
            delegate?.slideViewSetPick(.pick)
        case 7:  // 'X'
            delegate?.slideViewSetPick(.reject)
        case 32: // 'U'
            delegate?.slideViewSetPick(.unflagged)
        case 8:  // 'C'
            delegate?.slideViewToggleAutoAdvance()
        case 1, 48: // 'S' or Tab
            delegate?.slideViewToggleSplitCompare()
        case 42: // '\'
            delegate?.slideViewCycleFilter()
        case 44, 4: // '?' or 'H'
            toggleHelp()

        // Numbers 0..5 (Stars) & 6..9 (Colors)
        case 29:
            delegate?.slideViewSetRating(0)
        case 18:
            delegate?.slideViewSetRating(1)
        case 19:
            delegate?.slideViewSetRating(2)
        case 20:
            delegate?.slideViewSetRating(3)
        case 21:
            delegate?.slideViewSetRating(4)
        case 23:
            delegate?.slideViewSetRating(5)

        case 22: // 6 -> Red
            delegate?.slideViewSetColor(.red)
        case 26: // 7 -> Yellow
            delegate?.slideViewSetColor(.yellow)
        case 28: // 8 -> Green
            delegate?.slideViewSetColor(.green)
        case 25: // 9 -> Blue
            delegate?.slideViewSetColor(.blue)

        case 3: // 'F'
            delegate?.slideViewToggleFullScreen()
        case 34: // 'I'
            toggleHud()
        case 53, 12: // Esc or 'Q'
            if isZoomed {
                setZoom(false)
            } else if quickHelpView.alphaValue > 0.0 {
                quickHelpView.dismiss()
            } else {
                delegate?.slideViewClose()
            }

        default:
            super.keyDown(with: event)
        }
    }

    // MARK: - Non-Blocking Cursor Auto-Hide

    public override func mouseMoved(with event: NSEvent) {
        hudView.pulse()
        resetCursorTimer()
    }

    public override func mouseExited(with event: NSEvent) {
        cursorTimer?.invalidate()
    }

    private func resetCursorTimer() {
        cursorTimer?.invalidate()
        cursorTimer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: false) { _ in
            Task { @MainActor in
                NSCursor.setHiddenUntilMouseMoves(true)
            }
        }
    }

    deinit {
        cursorTimer?.invalidate()
    }

    // MARK: - Drag and Drop

    public override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        let pasteboard = sender.draggingPasteboard
        if pasteboard.canReadObject(forClasses: [NSURL.self], options: nil) {
            return .copy
        }
        return []
    }

    public override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL],
              let firstURL = urls.first else {
            return false
        }
        delegate?.slideViewDidDrop(url: firstURL)
        return true
    }
}
