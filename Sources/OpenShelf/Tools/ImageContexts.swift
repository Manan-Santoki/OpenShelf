import CoreImage
import Foundation

/// Core Image contexts are costly to make (each sets up its own GPU caches,
/// which outlive it), so editors share them rather than making one per call.
enum ImageContexts {
    /// Rendering in Core Image's default working space.
    static let standard = CIContext()

    /// A context working in `space`, made once per named colour space; an
    /// unnamed one (an image's own ICC profile) gets its own, as before.
    static func working(in space: CGColorSpace) -> CIContext {
        guard let name = space.name as String? else { return CIContext(options: [.workingColorSpace: space]) }
        return cache.context(named: name) { CIContext(options: [.workingColorSpace: space]) }
    }

    private static let cache = Cache()

    private final class Cache: @unchecked Sendable {
        private let lock = NSLock()
        private var contexts: [String: CIContext] = [:]

        func context(named name: String, make: () -> CIContext) -> CIContext {
            lock.withLock {
                if let context = contexts[name] { return context }
                let context = make()
                contexts[name] = context
                return context
            }
        }
    }
}
