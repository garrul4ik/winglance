import AppKit

public struct HudInfo: Sendable {
    public let currentIndex: Int
    public let totalCount: Int
    public let filename: String
    public let dimensions: String?
    public let fileSize: String?
    public let isRaw: Bool
    public let isDownscaled: Bool
    public let culling: CullingMetadata
    public let autoAdvance: Bool
    public let filterTitle: String?
    public let isZoomed: Bool
    public let errorMessage: String?

    public init(
        currentIndex: Int,
        totalCount: Int,
        filename: String,
        dimensions: String? = nil,
        fileSize: String? = nil,
        isRaw: Bool = false,
        isDownscaled: Bool = false,
        culling: CullingMetadata = CullingMetadata(),
        autoAdvance: Bool = false,
        filterTitle: String? = nil,
        isZoomed: Bool = false,
        errorMessage: String? = nil
    ) {
        self.currentIndex = currentIndex
        self.totalCount = totalCount
        self.filename = filename
        self.dimensions = dimensions
        self.fileSize = fileSize
        self.isRaw = isRaw
        self.isDownscaled = isDownscaled
        self.culling = culling
        self.autoAdvance = autoAdvance
        self.filterTitle = filterTitle
        self.isZoomed = isZoomed
        self.errorMessage = errorMessage
    }
}

/// Modern macOS frosted-glass HUD overlay container.
/// Displays index, filename, resolution, stars (★), picks/rejects, colors, and culling stats.
@MainActor
public final class GlassHudView: NSView {
    private let visualEffectView = NSVisualEffectView()
    private let indexPill = NSView()
    private let indexLabel = NSTextField(labelWithString: "")

    private let pickBadge = NSView()
    private let pickLabel = NSTextField(labelWithString: "")

    private let ratingLabel = NSTextField(labelWithString: "")

    private let filenameLabel = NSTextField(labelWithString: "")
    private let metaLabel = NSTextField(labelWithString: "")

    private let rawBadge = NSView()
    private let rawLabel = NSTextField(labelWithString: "RAW")

    private let autoPill = NSView()
    private let autoLabel = NSTextField(labelWithString: "AUTO")

    private let filterPill = NSView()
    private let filterLabel = NSTextField(labelWithString: "")

    private let zoomPill = NSView()
    private let zoomLabel = NSTextField(labelWithString: "100%")

    private let stackView = NSStackView()
    private var lastInfo: HudInfo?

    private var fadeTimer: Timer?
    public var isPersistentlyVisible: Bool = false {
        didSet {
            if isPersistentlyVisible {
                fadeTimer?.invalidate()
                fadeIn()
            } else {
                scheduleFadeOut()
            }
        }
    }

    public override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupGlassHierarchy()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setupGlassHierarchy()
    }

    public override func hitTest(_ point: NSPoint) -> NSView? {
        return nil
    }

    private func setupGlassHierarchy() {
        wantsLayer = true

        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.45)
        shadow.shadowOffset = NSSize(width: 0, height: -5)
        shadow.shadowBlurRadius = 16
        self.shadow = shadow

        visualEffectView.material = .hudWindow
        visualEffectView.blendingMode = .withinWindow
        visualEffectView.state = .active
        visualEffectView.wantsLayer = true
        visualEffectView.translatesAutoresizingMaskIntoConstraints = false

        if let effectLayer = visualEffectView.layer {
            effectLayer.cornerRadius = 16
            effectLayer.cornerCurve = .continuous
            effectLayer.masksToBounds = true
            effectLayer.borderColor = NSColor.white.withAlphaComponent(0.22).cgColor
            effectLayer.borderWidth = 0.75
        }

        addSubview(visualEffectView)
        NSLayoutConstraint.activate([
            visualEffectView.topAnchor.constraint(equalTo: topAnchor),
            visualEffectView.bottomAnchor.constraint(equalTo: bottomAnchor),
            visualEffectView.leadingAnchor.constraint(equalTo: leadingAnchor),
            visualEffectView.trailingAnchor.constraint(equalTo: trailingAnchor)
        ])

        setupSubviews()
    }

    private func setupSubviews() {
        stackView.orientation = .horizontal
        stackView.alignment = .centerY
        stackView.spacing = 8
        stackView.translatesAutoresizingMaskIntoConstraints = false
        stackView.edgeInsets = NSEdgeInsets(top: 8, left: 14, bottom: 8, right: 14)
        visualEffectView.addSubview(stackView)

        // 1. Index Pill [ 1 / 48 ]
        indexPill.wantsLayer = true
        indexPill.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.12).cgColor
        indexPill.layer?.cornerRadius = 8
        indexPill.layer?.cornerCurve = .continuous
        indexPill.translatesAutoresizingMaskIntoConstraints = false

        indexLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold)
        indexLabel.textColor = NSColor(white: 0.95, alpha: 1.0)
        indexLabel.alignment = .center
        indexLabel.translatesAutoresizingMaskIntoConstraints = false

        indexPill.addSubview(indexLabel)
        NSLayoutConstraint.activate([
            indexLabel.topAnchor.constraint(equalTo: indexPill.topAnchor, constant: 3),
            indexLabel.bottomAnchor.constraint(equalTo: indexPill.bottomAnchor, constant: -3),
            indexLabel.leadingAnchor.constraint(equalTo: indexPill.leadingAnchor, constant: 7),
            indexLabel.trailingAnchor.constraint(equalTo: indexPill.trailingAnchor, constant: -7)
        ])
        stackView.addArrangedSubview(indexPill)

        // 2. Pick / Reject Badge
        pickBadge.wantsLayer = true
        pickBadge.layer?.cornerRadius = 6
        pickBadge.layer?.cornerCurve = .continuous
        pickBadge.translatesAutoresizingMaskIntoConstraints = false

        pickLabel.font = NSFont.systemFont(ofSize: 10, weight: .bold)
        pickLabel.alignment = .center
        pickLabel.translatesAutoresizingMaskIntoConstraints = false

        pickBadge.addSubview(pickLabel)
        NSLayoutConstraint.activate([
            pickLabel.topAnchor.constraint(equalTo: pickBadge.topAnchor, constant: 2),
            pickLabel.bottomAnchor.constraint(equalTo: pickBadge.bottomAnchor, constant: -2),
            pickLabel.leadingAnchor.constraint(equalTo: pickBadge.leadingAnchor, constant: 6),
            pickLabel.trailingAnchor.constraint(equalTo: pickBadge.trailingAnchor, constant: -6)
        ])
        stackView.addArrangedSubview(pickBadge)

        // 3. Star Rating (★ ★ ★ ☆ ☆)
        ratingLabel.font = NSFont.systemFont(ofSize: 12, weight: .bold)
        ratingLabel.textColor = NSColor.systemYellow
        stackView.addArrangedSubview(ratingLabel)

        // 4. Filename
        filenameLabel.font = NSFont.systemFont(ofSize: 13, weight: .semibold)
        filenameLabel.textColor = .white
        filenameLabel.lineBreakMode = .byTruncatingMiddle
        filenameLabel.maximumNumberOfLines = 1
        filenameLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        stackView.addArrangedSubview(filenameLabel)

        // 5. Metadata (Resolution / File Size)
        metaLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        metaLabel.textColor = NSColor(white: 0.75, alpha: 1.0)
        metaLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        stackView.addArrangedSubview(metaLabel)

        // 6. RAW Badge
        rawBadge.wantsLayer = true
        rawBadge.layer?.backgroundColor = NSColor.systemOrange.withAlphaComponent(0.25).cgColor
        rawBadge.layer?.borderColor = NSColor.systemOrange.withAlphaComponent(0.55).cgColor
        rawBadge.layer?.borderWidth = 0.5
        rawBadge.layer?.cornerRadius = 5
        rawBadge.layer?.cornerCurve = .continuous
        rawBadge.translatesAutoresizingMaskIntoConstraints = false

        rawLabel.font = NSFont.systemFont(ofSize: 9, weight: .heavy)
        rawLabel.textColor = NSColor.systemOrange
        rawLabel.alignment = .center
        rawLabel.translatesAutoresizingMaskIntoConstraints = false

        rawBadge.addSubview(rawLabel)
        NSLayoutConstraint.activate([
            rawLabel.topAnchor.constraint(equalTo: rawBadge.topAnchor, constant: 2),
            rawLabel.bottomAnchor.constraint(equalTo: rawBadge.bottomAnchor, constant: -2),
            rawLabel.leadingAnchor.constraint(equalTo: rawBadge.leadingAnchor, constant: 5),
            rawLabel.trailingAnchor.constraint(equalTo: rawBadge.trailingAnchor, constant: -5)
        ])
        stackView.addArrangedSubview(rawBadge)

        // 7. Auto-Advance Indicator (AUTO)
        autoPill.wantsLayer = true
        autoPill.layer?.backgroundColor = NSColor.systemCyan.withAlphaComponent(0.25).cgColor
        autoPill.layer?.borderColor = NSColor.systemCyan.withAlphaComponent(0.55).cgColor
        autoPill.layer?.borderWidth = 0.5
        autoPill.layer?.cornerRadius = 5
        autoPill.layer?.cornerCurve = .continuous
        autoPill.translatesAutoresizingMaskIntoConstraints = false

        autoLabel.font = NSFont.systemFont(ofSize: 9, weight: .bold)
        autoLabel.textColor = NSColor.systemCyan
        autoLabel.alignment = .center
        autoLabel.translatesAutoresizingMaskIntoConstraints = false

        autoPill.addSubview(autoLabel)
        NSLayoutConstraint.activate([
            autoLabel.topAnchor.constraint(equalTo: autoPill.topAnchor, constant: 2),
            autoLabel.bottomAnchor.constraint(equalTo: autoPill.bottomAnchor, constant: -2),
            autoLabel.leadingAnchor.constraint(equalTo: autoPill.leadingAnchor, constant: 5),
            autoLabel.trailingAnchor.constraint(equalTo: autoPill.trailingAnchor, constant: -5)
        ])
        stackView.addArrangedSubview(autoPill)

        // 8. Filter Active Pill
        filterPill.wantsLayer = true
        filterPill.layer?.backgroundColor = NSColor.systemPurple.withAlphaComponent(0.25).cgColor
        filterPill.layer?.borderColor = NSColor.systemPurple.withAlphaComponent(0.55).cgColor
        filterPill.layer?.borderWidth = 0.5
        filterPill.layer?.cornerRadius = 5
        filterPill.layer?.cornerCurve = .continuous
        filterPill.translatesAutoresizingMaskIntoConstraints = false

        filterLabel.font = NSFont.systemFont(ofSize: 9, weight: .bold)
        filterLabel.textColor = NSColor.systemPurple
        filterLabel.alignment = .center
        filterLabel.translatesAutoresizingMaskIntoConstraints = false

        filterPill.addSubview(filterLabel)
        NSLayoutConstraint.activate([
            filterLabel.topAnchor.constraint(equalTo: filterPill.topAnchor, constant: 2),
            filterLabel.bottomAnchor.constraint(equalTo: filterPill.bottomAnchor, constant: -2),
            filterLabel.leadingAnchor.constraint(equalTo: filterPill.leadingAnchor, constant: 6),
            filterLabel.trailingAnchor.constraint(equalTo: filterPill.trailingAnchor, constant: -6)
        ])
        stackView.addArrangedSubview(filterPill)

        // 9. Zoom 100% Indicator Pill
        zoomPill.wantsLayer = true
        zoomPill.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.2).cgColor
        zoomPill.layer?.borderColor = NSColor.white.withAlphaComponent(0.4).cgColor
        zoomPill.layer?.borderWidth = 0.5
        zoomPill.layer?.cornerRadius = 5
        zoomPill.layer?.cornerCurve = .continuous
        zoomPill.translatesAutoresizingMaskIntoConstraints = false

        zoomLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 9, weight: .bold)
        zoomLabel.textColor = NSColor(white: 0.95, alpha: 1.0)
        zoomLabel.alignment = .center
        zoomLabel.translatesAutoresizingMaskIntoConstraints = false

        zoomPill.addSubview(zoomLabel)
        NSLayoutConstraint.activate([
            zoomLabel.topAnchor.constraint(equalTo: zoomPill.topAnchor, constant: 2),
            zoomLabel.bottomAnchor.constraint(equalTo: zoomPill.bottomAnchor, constant: -2),
            zoomLabel.leadingAnchor.constraint(equalTo: zoomPill.leadingAnchor, constant: 6),
            zoomLabel.trailingAnchor.constraint(equalTo: zoomPill.trailingAnchor, constant: -6)
        ])
        stackView.addArrangedSubview(zoomPill)

        NSLayoutConstraint.activate([
            stackView.topAnchor.constraint(equalTo: visualEffectView.topAnchor),
            stackView.bottomAnchor.constraint(equalTo: visualEffectView.bottomAnchor),
            stackView.leadingAnchor.constraint(equalTo: visualEffectView.leadingAnchor),
            stackView.trailingAnchor.constraint(equalTo: visualEffectView.trailingAnchor)
        ])
    }

    public override func layout() {
        super.layout()
        updateMask()
    }

    private func updateMask() {
        guard bounds.width > 0 && bounds.height > 0 else { return }
        let maskImage = NSImage(size: bounds.size, flipped: false) { rect in
            let path = NSBezierPath(roundedRect: rect, xRadius: 16, yRadius: 16)
            NSColor.black.setFill()
            path.fill()
            return true
        }
        maskImage.capInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        maskImage.resizingMode = .stretch
        visualEffectView.maskImage = maskImage
    }

    /// Updates the HUD contents with culling indicators.
    public func update(with info: HudInfo) {
        self.lastInfo = info
        if info.totalCount > 0 {
            indexLabel.stringValue = "\(info.currentIndex + 1) / \(info.totalCount)"
            indexPill.isHidden = false
        } else {
            indexPill.isHidden = true
        }

        // Culling: Pick / Reject
        switch info.culling.pick {
        case .pick:
            pickLabel.stringValue = "PICK"
            pickLabel.textColor = NSColor.systemGreen
            pickBadge.layer?.backgroundColor = NSColor.systemGreen.withAlphaComponent(0.25).cgColor
            pickBadge.layer?.borderColor = NSColor.systemGreen.withAlphaComponent(0.6).cgColor
            pickBadge.layer?.borderWidth = 0.5
            pickBadge.isHidden = false
        case .reject:
            pickLabel.stringValue = "REJECT"
            pickLabel.textColor = NSColor.systemRed
            pickBadge.layer?.backgroundColor = NSColor.systemRed.withAlphaComponent(0.25).cgColor
            pickBadge.layer?.borderColor = NSColor.systemRed.withAlphaComponent(0.6).cgColor
            pickBadge.layer?.borderWidth = 0.5
            pickBadge.isHidden = false
        case .unflagged:
            pickBadge.isHidden = true
        }

        // Culling: Star Rating
        if info.culling.rating > 0 {
            let stars = String(repeating: "★", count: info.culling.rating) + String(repeating: "☆", count: 5 - info.culling.rating)
            ratingLabel.stringValue = stars
            ratingLabel.isHidden = false
        } else {
            ratingLabel.isHidden = true
        }

        filenameLabel.stringValue = info.filename

        // Auto-advance indicator
        autoPill.isHidden = !info.autoAdvance

        // Filter active indicator
        if let filter = info.filterTitle {
            filterLabel.stringValue = filter.uppercased()
            filterPill.isHidden = false
        } else {
            filterPill.isHidden = true
        }

        if let error = info.errorMessage {
            metaLabel.stringValue = error
            metaLabel.textColor = NSColor.systemRed
            rawBadge.isHidden = true
        } else {
            var parts: [String] = []
            if let dim = info.dimensions {
                parts.append(dim)
            }
            if let size = info.fileSize {
                parts.append(size)
            }
            metaLabel.stringValue = parts.joined(separator: "  •  ")
            metaLabel.textColor = NSColor(white: 0.75, alpha: 1.0)
            rawBadge.isHidden = !info.isRaw
        }

        // 100% Zoom Pill
        zoomPill.isHidden = !info.isZoomed

        pulse()
    }

    /// Updates only the zoom status without clearing or re-rendering other photo metadata
    public func updateZoomState(_ isZoomed: Bool) {
        zoomPill.isHidden = !isZoomed
        if let current = lastInfo {
            let updated = HudInfo(
                currentIndex: current.currentIndex,
                totalCount: current.totalCount,
                filename: current.filename,
                dimensions: current.dimensions,
                fileSize: current.fileSize,
                isRaw: current.isRaw,
                isDownscaled: current.isDownscaled,
                culling: current.culling,
                autoAdvance: current.autoAdvance,
                filterTitle: current.filterTitle,
                isZoomed: isZoomed,
                errorMessage: current.errorMessage
            )
            self.lastInfo = updated
        }
        pulse()
    }

    public func showMessage(_ message: String) {
        indexPill.isHidden = true
        pickBadge.isHidden = true
        ratingLabel.isHidden = true
        rawBadge.isHidden = true
        autoPill.isHidden = true
        filterPill.isHidden = true
        metaLabel.stringValue = ""
        filenameLabel.stringValue = message
        pulse()
    }

    public func pulse() {
        fadeIn()
        if !isPersistentlyVisible {
            scheduleFadeOut()
        }
    }

    public func togglePersistent() {
        isPersistentlyVisible.toggle()
    }

    private func fadeIn() {
        fadeTimer?.invalidate()
        guard alphaValue < 1.0 else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.15
            self.animator().alphaValue = 1.0
        }
    }

    private func scheduleFadeOut() {
        fadeTimer?.invalidate()
        fadeTimer = Timer.scheduledTimer(withTimeInterval: 2.5, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self = self, !self.isPersistentlyVisible else { return }
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.4
                    self.animator().alphaValue = 0.0
                }
            }
        }
    }

    deinit {
        fadeTimer?.invalidate()
    }
}
