import AppKit
import Observation
import SwiftUI

/// A short confirmation shown next to the pointer after an action.
struct Toast: Equatable {
    var systemImage: String
    var title: String
    var subtitle: String?
}

@MainActor
@Observable
final class ToastModel {
    var toast: Toast?
    var isPresented = false
}

/// Shows toasts in a small floating panel near the pointer.
@MainActor
final class ToastController {
    static let shared = ToastController()

    private let model = ToastModel()
    private lazy var panel = makePanel()
    private var hideTask: Task<Void, Never>?
    private let panelSize = CGSize(width: 280, height: 64)

    func show(_ toast: Toast, near location: NSPoint = NSEvent.mouseLocation, for duration: Duration = .seconds(1.8)) {
        hideTask?.cancel()
        model.toast = toast

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
        panel.orderFrontRegardless()
        model.isPresented = true
        NSAccessibility.post(
            element: NSApp as Any,
            notification: .announcementRequested,
            userInfo: [.announcement: [toast.title, toast.subtitle].compactMap { $0 }.joined(separator: ", ")]
        )

        hideTask = Task { [weak self] in
            try? await Task.sleep(for: duration)
            guard let self, !Task.isCancelled else { return }
            hide()
        }
    }

    func hide() {
        hideTask?.cancel()
        model.isPresented = false
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard let self, !Task.isCancelled else { return }
            panel.orderOut(nil)
        }
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

        let hostingView = NSHostingView(rootView: ToastView(model: model))
        hostingView.sizingOptions = []
        panel.contentView = hostingView
        return panel
    }
}

private struct ToastView: View {
    let model: ToastModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 10) {
            if let toast = model.toast {
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
                    }
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
        .fixedSize()
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(4)
        .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.85, anchor: .topLeading)
        .offset(y: model.isPresented || reduceMotion ? 0 : -6)
        .opacity(model.isPresented ? 1 : 0)
        .animation(.spring(response: 0.32, dampingFraction: 0.75), value: model.isPresented)
        .animation(.spring(response: 0.25, dampingFraction: 0.8), value: model.toast)
        .accessibilityElement(children: .combine)
    }
}
