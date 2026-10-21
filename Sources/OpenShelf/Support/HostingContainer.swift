import AppKit
import SwiftUI

/// Wraps SwiftUI content in a plain view to use as a window's content view.
///
/// An `NSHostingView` that is itself the content view resizes its window to fit
/// animated content changes during the window's layout pass, which AppKit can
/// reject with an exception that crashes the app (seen when files were dropped
/// on a shelf mid-resize). Nested one level down it never touches the window;
/// window sizes stay entirely under our control.
///
/// - Parameter container: The view to host the content in, when it has a job of
///   its own such as taking drops; a plain view by default.
@MainActor
func hostingContainer<Content: View>(for rootView: Content, size: CGSize, in container: NSView? = nil) -> NSView {
    let container = container ?? NSView()
    container.frame = NSRect(origin: .zero, size: size)
    let hostingView = NSHostingView(rootView: rootView)
    hostingView.sizingOptions = []
    hostingView.frame = container.bounds
    hostingView.autoresizingMask = [.width, .height]
    container.addSubview(hostingView)
    return container
}
