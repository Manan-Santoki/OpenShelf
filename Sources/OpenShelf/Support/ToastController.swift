import AppKit
import Observation
import SwiftUI

/// How a progress toast shows a job that's under way: a real 0...1 fraction
/// where one can be measured, or a busy bar where it can't (an external
/// tool with no progress output, AppKit printing a whole document at once…).
enum ToastProgress: Equatable {
    case determinate(Double)
    case indeterminate
}

/// A short message shown next to the pointer: a confirmation after an action, or
/// the progress of a longer job.
struct Toast: Equatable {
    var systemImage: String
    var title: String
    var subtitle: String?
    /// Set for a job in progress; nil for a plain confirmation.
    var progress: ToastProgress?
}

@MainActor
@Observable
final class ToastModel {
    var toast: Toast?
    var isPresented = false
    @ObservationIgnored var onCancel: (() -> Void)?
}

/// Shows toasts in a small floating panel near the pointer.
@MainActor
final class ToastController {
    static let shared = ToastController()

    private let model = ToastModel()
    private lazy var panel = makePanel()
    private var hideTask: Task<Void, Never>?
    private let panelSize = CGSize(width: 300, height: 92)

    /// Shows a confirmation that hides itself after `duration`.
    func show(_ toast: Toast, near location: NSPoint = NSEvent.mouseLocation, for duration: Duration = .seconds(1.8)) {
        present(toast, near: location, onCancel: nil)
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: duration)
            guard let self, !Task.isCancelled else { return }
            hide()
        }
        announce(toast)
    }

    /// Shows a progress toast that stays until replaced or hidden. Its × button
    /// calls `onCancel`.
    func showProgress(_ toast: Toast, near location: NSPoint = NSEvent.mouseLocation, onCancel: @escaping () -> Void) {
        present(toast, near: location, onCancel: onCancel)
        announce(toast)
    }

    /// Updates the progress toast currently on screen.
    func updateProgress(_ progress: ToastProgress, subtitle: String? = nil) {
        guard var toast = model.toast, toast.progress != nil else { return }
        toast.progress = progress
        if let subtitle { toast.subtitle = subtitle }
        model.toast = toast
    }

    func hide() {
        hideTask?.cancel()
        model.isPresented = false
        model.onCancel = nil
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard let self, !Task.isCancelled else { return }
            panel.orderOut(nil)
        }
    }

    private func present(_ toast: Toast, near location: NSPoint, onCancel: (() -> Void)?) {
        hideTask?.cancel()
        model.toast = toast
        model.onCancel = onCancel
        panel.ignoresMouseEvents = onCancel == nil

        // A plain toast that replaces one already showing stays where it is.
        if !(panel.isVisible && model.isPresented) {
            // Below and to the right of the pointer, like a tooltip, kept on screen.
            var frame = NSRect(
                x: location.x + 14,
                y: location.y - panelSize.height - 14,
                width: panelSize.width,
                height: panelSize.height
            )
            if let screen = NSScreen.screens.first(where: { $0.frame.contains(location) }) {
                frame = frame.clamped(to: screen.visibleFrame)
            }
            panel.setFrame(frame, display: false)
        }
        panel.orderFrontRegardless()
        model.isPresented = true
    }

    private func announce(_ toast: Toast) {
        NSAccessibility.post(
            element: NSApp as Any,
            notification: .announcementRequested,
            userInfo: [.announcement: [toast.title, toast.subtitle].compactMap { $0 }.joined(separator: ", ")]
        )
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true

        panel.contentView = hostingContainer(for: ToastView(model: model), size: panelSize)
        return panel
    }
}

private struct ToastView: View {
    let model: ToastModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if let toast = model.toast {
                if toast.progress != nil {
                    ProgressCard(toast: toast, onCancel: model.onCancel)
                } else {
                    ConfirmationPill(toast: toast)
                }
            }
        }
        .fixedSize()
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(4)
        .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.85, anchor: .topLeading)
        .offset(y: model.isPresented || reduceMotion ? 0 : -6)
        .opacity(model.isPresented ? 1 : 0)
        .animation(.spring(response: 0.32, dampingFraction: 0.75), value: model.isPresented)
        .animation(.spring(response: 0.25, dampingFraction: 0.8), value: model.toast)
    }
}

private struct ConfirmationPill: View {
    let toast: Toast

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: toast.systemImage)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background(Circle().fill(Color.accentColor))
            VStack(alignment: .leading, spacing: 1) {
                Text(toast.title)
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                if let subtitle = toast.subtitle {
                    Text(subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .frame(maxWidth: 220, alignment: .leading)
                }
            }
        }
        .padding(.leading, 9)
        .padding(.trailing, 14)
        .padding(.vertical, 8)
        .background {
            Capsule()
                .fill(.regularMaterial)
                .overlay(Capsule().strokeBorder(.white.opacity(0.35), lineWidth: 0.8))
                .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
        }
        .accessibilityElement(children: .combine)
    }
}

private struct ProgressCard: View {
    let toast: Toast
    let onCancel: (() -> Void)?

    /// The determinate fraction, or nil while busy with nothing measurable to show.
    private var fraction: Double? {
        guard case .determinate(let value) = toast.progress else { return nil }
        return value
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(alignment: .top, spacing: 9) {
                Button {
                    onCancel?()
                } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 8, weight: .bold))
                        .frame(width: 16, height: 16)
                        .background(Circle().fill(.primary.opacity(0.1)))
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Cancel")
                .accessibilityLabel("Cancel")

                VStack(alignment: .leading, spacing: 2) {
                    Text(toast.title)
                        .font(.system(size: 12, weight: .semibold))
                        .lineLimit(1)
                    if let subtitle = toast.subtitle {
                        Text(subtitle)
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
            }
            Group {
                if let fraction {
                    ProgressView(value: min(max(fraction, 0), 1))
                        .animation(.easeOut(duration: 0.2), value: fraction)
                } else {
                    // Busy, with nothing measurable to show: a moving bar
                    // beats one frozen at whatever fraction it last saw.
                    ProgressView()
                }
            }
            .progressViewStyle(.linear)
            .tint(.accentColor)
        }
        .frame(width: 244)
        .padding(12)
        .background {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(.regularMaterial)
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(.white.opacity(0.35), lineWidth: 0.8))
                .shadow(color: .black.opacity(0.18), radius: 12, y: 5)
        }
        .accessibilityElement(children: .contain)
        .accessibilityValue(fraction.map { "\(Int($0 * 100)) percent" } ?? "In progress")
    }
}
