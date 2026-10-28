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
}

/// Ultra-responsive, zero-latency layer-backed NSView.
/// Supports single image full-screen display, 2-up split burst compare mode,
/// and quick shortcut help overlays.
@MainActor
public final class SlideView: NSView {
    public weak var delegate: SlideViewDelegate?

    private let imageLayer = CALayer()
    private let splitLayer = CALayer()
    public let hudView = GlassHudView()
    public let quickHelpView = QuickHelpView()

    public private(set) var isSplitMode: Bool = false

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

        // Left / Main image
        imageLayer.contentsGravity = .resizeAspect
        imageLayer.backgroundColor = NSColor.black.cgColor
        imageLayer.isOpaque = true
        imageLayer.actions = [
            "contents": NSNull(),
            "bounds": NSNull(),
            "position": NSNull()
        ]
        layer?.addSublayer(imageLayer)

        // Right / Split compare image
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
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        if isSplitMode {
            let halfWidth = bounds.width / 2
            imageLayer.frame = CGRect(x: 0, y: 0, width: halfWidth - 1, height: bounds.height)
            splitLayer.frame = CGRect(x: halfWidth + 1, y: 0, width: halfWidth - 1, height: bounds.height)
        } else {
            imageLayer.frame = bounds
            splitLayer.frame = .zero
        }
        CATransaction.commit()
    }

    // MARK: - Image Presentation

    public func displayImage(_ cgImage: CGImage?, nextImage: CGImage? = nil, info: HudInfo?) {
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

        if let info = info {
            hudView.update(with: info)
        }
    }

    public func setSplitMode(_ enabled: Bool) {
        isSplitMode = enabled
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

    // MARK: - Keyboard Handling

    public override var acceptsFirstResponder: Bool {
        return true
    }

    public override func keyDown(with event: NSEvent) {
        resetCursorTimer()

        // If quick help is visible, dismiss it on any key
        if quickHelpView.alphaValue > 0.0 && event.keyCode != 44 { // 44 = '?' key
            quickHelpView.dismiss()
        }

        let isShift = event.modifierFlags.contains(.shift)

        switch event.keyCode {
        // Space, Right Arrow, Down Arrow, Page Down, 'N'
        case 49, 124, 125, 121, 45:
            delegate?.slideViewRequestNext()

        // Left Arrow, Up Arrow, Page Up, 'K'
        case 123, 126, 116, 40:
            delegate?.slideViewRequestPrevious()

        // Home
        case 115:
            delegate?.slideViewRequestFirst()

        // End
        case 119:
            delegate?.slideViewRequestLast()

        // Delete / Backspace (51 = Backspace, 117 = Forward Delete)
        case 51, 117:
            if isShift {
                delegate?.slideViewRequestPurgeRejects()
            } else {
                delegate?.slideViewRequestDelete()
            }

        // --- CULLING HOTKEYS ---
        // 'P' key (KeyCode 35) -> Flag as Pick
        case 35:
            delegate?.slideViewSetPick(.pick)

        // 'X' key (KeyCode 7) -> Flag as Reject
        case 7:
            delegate?.slideViewSetPick(.reject)

        // 'U' key (KeyCode 32) -> Unflag / Neutral
        case 32:
            delegate?.slideViewSetPick(.unflagged)

        // 'C' key (KeyCode 8) -> Toggle Auto-Advance mode
        case 8:
            delegate?.slideViewToggleAutoAdvance()

        // 'S' key (KeyCode 1) or Tab (KeyCode 48) -> Split 2-up compare mode
        case 1, 48:
            delegate?.slideViewToggleSplitCompare()

        // Backslash '\' key (KeyCode 42) -> Cycle Filter
        case 42:
            delegate?.slideViewCycleFilter()

        // '?' or '/' key (KeyCode 44) or 'H' key (KeyCode 4) -> Toggle Shortcuts Sheet
        case 44, 4:
            toggleHelp()

        // Number Keys 0..5 (Star Ratings) & 6..9 (Color Labels)
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

        // 6 key (KeyCode 22) -> Red
        case 22:
            delegate?.slideViewSetColor(.red)
        // 7 key (KeyCode 26) -> Yellow
        case 26:
            delegate?.slideViewSetColor(.yellow)
        // 8 key (KeyCode 28) -> Green
        case 28:
            delegate?.slideViewSetColor(.green)
        // 9 key (KeyCode 25) -> Blue
        case 25:
            delegate?.slideViewSetColor(.blue)

        // 'F' key (KeyCode 3) -> Fullscreen toggle
        case 3:
            delegate?.slideViewToggleFullScreen()

        // 'I' key (KeyCode 34) -> Info HUD toggle
        case 34:
            toggleHud()

        // Escape (KeyCode 53) or 'Q' (KeyCode 12)
        case 53, 12:
            if quickHelpView.alphaValue > 0.0 {
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
