import AppKit
import SwiftUI

/// What an editor can ask of the window hosting it.
@MainActor
struct EditorActions {
    /// Closes the editor without saving.
    let close: () -> Void
    /// Confirms what a save did. Closes the editor once anything was written;
    /// keeps it open when every file was left as it was.
    let finished: (SaveOutcome) -> Void
    /// Reports an error without closing.
    let failed: (Error) -> Void
    /// Makes the editor's window key, activating the app if needed, so typing
    /// goes to the editor.
    let focus: () -> Void

    /// Closes the editor and confirms which files were written.
    func saved(_ urls: [URL]) {
        finished(SaveOutcome(saved: urls))
    }
}

/// Standard frame for tool editors: a close button and title on top, the
/// editor's content, then Reset and a primary button at the bottom.
struct EditorChrome<Content: View>: View {
    let title: String
    let primaryTitle: String
    var isPrimaryEnabled = true
    /// Whether Esc closes the editor. Editors turn this off while Esc has a more
    /// local meaning, such as ending text entry.
    var closesOnEscape = true
    let actions: EditorActions
    var onReset: (() -> Void)?
    let onPrimary: () -> Void
    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Text(title)
                    .font(.system(size: 13, weight: .semibold))
                    // Presses on the title move the window like the rest of the bar.
                    .allowsHitTesting(false)
                HStack {
                    Button(action: actions.close) {
                        Image(systemName: "xmark")
                            .font(.system(size: 10, weight: .bold))
                            .frame(width: 22, height: 22)
                            .background(Circle().fill(.primary.opacity(0.08)))
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .keyboardShortcut(closesOnEscape ? .cancelAction : nil)
                    .help("Close without saving")
                    .accessibilityLabel("Close")
                    Spacer()
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(WindowDragArea())

            // Pinned to the top, so content that grows or shrinks (a status
            // message, say) never shifts the controls above it.
            content
                .padding(.horizontal, 16)
                .padding(.bottom, 14)
                .frame(maxHeight: .infinity, alignment: .top)

            Divider().opacity(0.6)

            HStack {
                if let onReset {
                    Button("Reset", action: onReset)
                        .controlSize(.large)
                }
                Spacer()
                Button(primaryTitle, action: onPrimary)
                    .controlSize(.large)
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!isPrimaryEnabled)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
    }
}

/// A row with a leading label, used for the settings under an editor's preview.
struct EditorRow<Content: View>: View {
    let label: String
    @ViewBuilder let content: Content

    var body: some View {
        HStack(spacing: 12) {
            Text(label)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .frame(width: 78, alignment: .leading)
            content
        }
    }
}

/// A slider with a label and a live value readout, e.g. "Padding ——o—— 160 px".
struct EditorSlider: View {
    let label: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    var unit = "px"

    var body: some View {
        EditorRow(label: label) {
            Slider(value: $value, in: range)
                .controlSize(.small)
            Text("\(Int(value.rounded())) \(unit)")
                .font(.system(size: 11).monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 56, alignment: .trailing)
        }
        .accessibilityElement(children: .combine)
    }
}

/// A status message under an editor's controls, in a row of fixed height:
/// room for `lines` lines is kept even with no message, so one appearing after
/// a click doesn't move anything the pointer is aiming at.
struct EditorStatusLine: View {
    var message: String?
    var systemImage = "info.circle"
    var tint = Color.secondary
    var lines = 2

    var body: some View {
        ZStack(alignment: .topLeading) {
            // Reserves the height of `lines` lines in this font.
            Text(String(repeating: "\n", count: max(lines - 1, 0)))
                .hidden()
                .accessibilityHidden(true)
            if let message {
                Label(message, systemImage: systemImage)
                    .foregroundStyle(tint)
                    .lineLimit(lines)
            }
        }
        .font(.system(size: 11))
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Floating window that hosts one tool editor. It zooms in from the point where
/// the file was dropped and shrinks away when closed.
@MainActor
final class EditorWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [EditorWindowController] = []
    private let model = EditorPresentation()

    static func present<Content: View>(
        size: CGSize,
        near location: NSPoint = NSEvent.mouseLocation,
        @ViewBuilder content: (EditorActions) -> Content
    ) {
        let controller = EditorWindowController(size: size)
        let actions = EditorActions(
            close: { [weak controller] in controller?.dismiss() },
            finished: { [weak controller] outcome in
                if outcome.savedAnything { controller?.dismiss() }
                // A mixed or empty result has more to read than "Saved".
                let duration: Duration = outcome.unchanged > 0 ? .seconds(3) : .seconds(1.8)
                ToastController.shared.show(outcome.toast, for: duration)
            },
            failed: { error in
                ToastController.shared.show(Toast(systemImage: "exclamationmark.triangle", title: "Couldn’t save", subtitle: error.localizedDescription))
            },
            focus: { [weak controller] in controller?.focus() }
        )
        controller.install(EditorContainer(model: controller.model, content: content(actions)), near: location)
        open.append(controller)
    }

    private init(size: CGSize) {
        let window = EditorWindow(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.borderless, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.level = .floating
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        // Only the header moves the window, so drags on the canvas reach the editor.
        window.isMovableByWindowBackground = false
        window.isReleasedWhenClosed = false
        window.collectionBehavior = [.fullScreenAuxiliary, .moveToActiveSpace]
        // Borderless windows don't build a Tab order on their own; without one,
        // Tab can't move between fields such as Crop's width and height.
        window.autorecalculatesKeyViewLoop = true
        super.init(window: window)
        window.delegate = self
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    private func install<Content: View>(_ root: Content, near location: NSPoint) {
        guard let window else { return }
        window.contentView = hostingContainer(for: root, size: window.frame.size)

        var frame = window.frame
        frame.origin = NSPoint(x: location.x - frame.width / 2, y: location.y - frame.height / 2)
        let screen = NSScreen.screens.first { $0.frame.contains(location) } ?? NSScreen.main
        if let visibleFrame = screen?.visibleFrame {
            frame = frame.clamped(to: visibleFrame)
        }
        window.setFrame(frame, display: false)

        // Grow out of the drop point.
        let anchor = CGPoint(x: location.x - frame.minX, y: frame.maxY - location.y)
        model.anchor = UnitPoint(x: anchor.x / frame.width, y: anchor.y / frame.height)
        focus()
        model.isPresented = true
    }

    /// Makes the editor the key window of the active app. OpenShelf is usually
    /// in the background when an editor opens (files come from Finder), and
    /// keystrokes go to the active app's key window, not to ours.
    func focus() {
        guard let window else { return }
        if !NSApp.isActive { NSApp.activate() }
        if !window.isKeyWindow { window.makeKeyAndOrderFront(nil) }
    }

    func dismiss() {
        guard model.isPresented else { return }
        model.isPresented = false
        Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(220))
            guard let self else { return }
            window?.orderOut(nil)
            window?.close()
        }
    }

    func windowWillClose(_ notification: Notification) {
        Self.open.removeAll { $0 === self }
    }
}

/// A borderless window that can still take keyboard focus for text fields.
private final class EditorWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

@MainActor
@Observable
private final class EditorPresentation {
    var isPresented = false
    var anchor = UnitPoint.center
}

private struct EditorContainer<Content: View>: View {
    let model: EditorPresentation
    let content: Content
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        content
            .background {
                RoundedRectangle(cornerRadius: 22, style: .continuous)
                    .fill(.regularMaterial)
                    .overlay {
                        RoundedRectangle(cornerRadius: 22, style: .continuous)
                            .strokeBorder(.white.opacity(0.25), lineWidth: 0.8)
                    }
            }
            .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
            .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.3, anchor: model.anchor)
            .opacity(model.isPresented ? 1 : 0)
            .animation(reduceMotion ? .easeOut(duration: 0.15) : .spring(response: 0.42, dampingFraction: 0.8), value: model.isPresented)
    }
}
