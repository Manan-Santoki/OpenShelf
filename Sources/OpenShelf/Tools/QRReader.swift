import CoreGraphics
import Foundation
import Vision

/// Finds QR codes (and Aztec, Data Matrix and PDF417 codes) in images.
enum QRReader {
    /// Decoded payloads from every image, in reading order, without duplicates.
    /// Returns an empty array when no code is found.
    static func read(_ urls: [URL]) async throws -> [String] {
        try await Task.detached(priority: .userInitiated) {
            var seen = Set<String>()
            var payloads: [String] = []
            for url in urls {
                for payload in try self.payloads(in: url) where seen.insert(payload).inserted {
                    payloads.append(payload)
                }
            }
            return payloads
        }.value
    }

    static func payloads(in url: URL) throws -> [String] {
        let image = try EditableImage.load(url)
        let request = VNDetectBarcodesRequest()
        request.symbologies = [.qr, .aztec, .dataMatrix, .pdf417]
        try VNImageRequestHandler(cgImage: image.cgImage, options: [:]).perform([request])

        // Vision boxes are normalised with the origin at the bottom left, so
        // reading order is top to bottom (higher y first), then left to right.
        let observations = (request.results ?? []).filter { $0.payloadStringValue != nil }
        let sorted = observations.sorted { a, b in
            if abs(a.boundingBox.midY - b.boundingBox.midY) > 0.05 {
                return a.boundingBox.midY > b.boundingBox.midY
            }
            return a.boundingBox.minX < b.boundingBox.minX
        }
        return sorted.compactMap(\.payloadStringValue)
    }
}
