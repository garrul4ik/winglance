import AppKit

/// Clean, monochromatic Apple Pro frosted-glass shortcuts sheet.
/// Uses a two-column key-value grid with aligned monospaced keycaps
/// and clean neutral typography.
@MainActor
public final class QuickHelpView: NSView {
    private let visualEffectView = NSVisualEffectView()
    private var dismissTimer: Timer?

    public override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupGlass()
        setupContent()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setupGlass()
        setupContent()
    }

    public override func hitTest(_ point: NSPoint) -> NSView? {
        return nil
    }

    private func setupGlass() {
        wantsLayer = true
        alphaValue = 0.0

        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.55)
        shadow.shadowOffset = NSSize(width: 0, height: -8)
        shadow.shadowBlurRadius = 24
        self.shadow = shadow

        visualEffectView.material = .hudWindow
        visualEffectView.blendingMode = .withinWindow
        visualEffectView.state = .active
        visualEffectView.wantsLayer = true
        visualEffectView.translatesAutoresizingMaskIntoConstraints = false

        if let layer = visualEffectView.layer {
            layer.cornerRadius = 16
            layer.cornerCurve = .continuous
            layer.masksToBounds = true
            layer.borderColor = NSColor.white.withAlphaComponent(0.18).cgColor
            layer.borderWidth = 0.5
        }

        addSubview(visualEffectView)
        NSLayoutConstraint.activate([
            visualEffectView.topAnchor.constraint(equalTo: topAnchor),
            visualEffectView.bottomAnchor.constraint(equalTo: bottomAnchor),
            visualEffectView.leadingAnchor.constraint(equalTo: leadingAnchor),
            visualEffectView.trailingAnchor.constraint(equalTo: trailingAnchor)
        ])
    }

    private func setupContent() {
        let root = NSStackView()
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 14
        root.translatesAutoresizingMaskIntoConstraints = false
        root.edgeInsets = NSEdgeInsets(top: 18, left: 22, bottom: 18, right: 22)
        visualEffectView.addSubview(root)

        // Monochromatic Header
        let titleLabel = NSTextField(labelWithString: "SHORTCUTS")
        titleLabel.font = NSFont.systemFont(ofSize: 10, weight: .bold)
        titleLabel.textColor = NSColor(white: 0.50, alpha: 1.0)
        root.addArrangedSubview(titleLabel)

        // Clean Two-Column Grid
        let columns = NSStackView()
        columns.orientation = .horizontal
        columns.alignment = .top
        columns.spacing = 28
        columns.translatesAutoresizingMaskIntoConstraints = false

        // Column 1: Rating & Culling
        let col1 = NSStackView()
        col1.orientation = .vertical
        col1.alignment = .leading
        col1.spacing = 7
        col1.addArrangedSubview(makeRow(key: "P", desc: "Pick"))
        col1.addArrangedSubview(makeRow(key: "X", desc: "Reject"))
        col1.addArrangedSubview(makeRow(key: "U", desc: "Unflag"))
        col1.addArrangedSubview(makeRow(key: "1 – 5", desc: "Rating"))
        col1.addArrangedSubview(makeRow(key: "0", desc: "Clear"))
        col1.addArrangedSubview(makeRow(key: "C", desc: "Auto-advance"))
        columns.addArrangedSubview(col1)

        // Column 2: Review & Navigation
        let col2 = NSStackView()
        col2.orientation = .vertical
        col2.alignment = .leading
        col2.spacing = 7
        col2.addArrangedSubview(makeRow(key: "S", desc: "Split compare"))
        col2.addArrangedSubview(makeRow(key: "\\", desc: "Cycle filter"))
        col2.addArrangedSubview(makeRow(key: "⇧ ⌫", desc: "Purge rejects"))
        col2.addArrangedSubview(makeRow(key: "Space", desc: "Next"))
        col2.addArrangedSubview(makeRow(key: "F", desc: "Full screen"))
        col2.addArrangedSubview(makeRow(key: "I", desc: "Info overlay"))
        columns.addArrangedSubview(col2)

        root.addArrangedSubview(columns)

        NSLayoutConstraint.activate([
            root.topAnchor.constraint(equalTo: visualEffectView.topAnchor),
            root.bottomAnchor.constraint(equalTo: visualEffectView.bottomAnchor),
            root.leadingAnchor.constraint(equalTo: visualEffectView.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: visualEffectView.trailingAnchor)
        ])
    }

    private func makeRow(key: String, desc: String) -> NSView {
        let row = NSStackView()
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 10
        row.translatesAutoresizingMaskIntoConstraints = false

        // Subtle dark keycap with pure white lettering
        let keycap = NSView()
        keycap.wantsLayer = true
        keycap.layer?.backgroundColor = NSColor(white: 1.0, alpha: 0.10).cgColor
        keycap.layer?.cornerRadius = 5
        keycap.layer?.cornerCurve = .continuous
        keycap.translatesAutoresizingMaskIntoConstraints = false

        let keyLabel = NSTextField(labelWithString: key)
        keyLabel.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .semibold)
        keyLabel.textColor = NSColor(white: 0.95, alpha: 1.0)
        keyLabel.alignment = .center
        keyLabel.translatesAutoresizingMaskIntoConstraints = false

        keycap.addSubview(keyLabel)
        NSLayoutConstraint.activate([
            keycap.widthAnchor.constraint(equalToConstant: 42),
            keycap.heightAnchor.constraint(equalToConstant: 22),
            keyLabel.centerXAnchor.constraint(equalTo: keycap.centerXAnchor),
            keyLabel.centerYAnchor.constraint(equalTo: keycap.centerYAnchor)
        ])
        row.addArrangedSubview(keycap)

        let descLabel = NSTextField(labelWithString: desc)
        descLabel.font = NSFont.systemFont(ofSize: 12, weight: .regular)
        descLabel.textColor = NSColor(white: 0.80, alpha: 1.0)
        row.addArrangedSubview(descLabel)

        return row
    }

    public override func layout() {
        super.layout()
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

    public func toggle() {
        if alphaValue > 0.0 {
            dismiss()
        } else {
            present()
        }
    }

    public func present() {
        dismissTimer?.invalidate()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.15
            self.animator().alphaValue = 1.0
        }
        dismissTimer = Timer.scheduledTimer(withTimeInterval: 5.0, repeats: false) { [weak self] _ in
            Task { @MainActor in
                self?.dismiss()
            }
        }
    }

    public func dismiss() {
        dismissTimer?.invalidate()
        guard alphaValue > 0.0 else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.20
            self.animator().alphaValue = 0.0
        }
    }

    deinit {
        dismissTimer?.invalidate()
    }
}
