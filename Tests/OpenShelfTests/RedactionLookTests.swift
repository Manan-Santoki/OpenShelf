import CoreGraphics
import Testing
@testable import OpenShelf

@MainActor
struct RedactionLookTests {
    /// A 200×100 image: white on the left half, a red/blue checkerboard on the right.
    private func sample() -> CGImage {
        let context = CGContext(data: nil, width: 200, height: 100, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 200, height: 100))
        for x in stride(from: 100, to: 200, by: 4) {
            for y in stride(from: 0, to: 100, by: 4) {
                let isRed = (x / 4 + y / 4) % 2 == 0
                context.setFillColor(CGColor(red: isRed ? 1 : 0, green: 0, blue: isRed ? 0 : 1, alpha: 1))
                context.fill(CGRect(x: x, y: y, width: 4, height: 4))
            }
        }
        return context.makeImage()!
    }

    private func pixel(_ image: CGImage, _ x: Int, _ y: Int) -> (r: UInt8, g: UInt8, b: UInt8) {
        let context = CGContext(data: nil, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        // Draw so that image pixel (x, y), top-left origin, lands on the single pixel.
        context.draw(image, in: CGRect(x: -x, y: -(image.height - 1 - y), width: image.width, height: image.height))
        let data = context.data!.assumingMemoryBound(to: UInt8.self)
        return (data[0], data[1], data[2])
    }

    @Test func eachAreaKeepsItsOwnLook() throws {
        let image = sample()
        let box = RedactionLook(style: .box, strength: 50)
        let blur = RedactionLook(style: .blur, strength: 60)
        let output = try #require(Redaction.apply(to: image, areas: [
            (rect: CGRect(x: 10, y: 10, width: 60, height: 80), look: box),
            (rect: CGRect(x: 120, y: 10, width: 60, height: 80), look: blur),
        ]))

        let boxed = pixel(output, 40, 50)
        #expect(boxed.r < 10 && boxed.g < 10 && boxed.b < 10)

        // Blurring mixes the red and blue squares; it doesn't black them out.
        let blurred = pixel(output, 150, 50)
        #expect(Int(blurred.r) + Int(blurred.b) > 120)
        #expect(blurred.r > 30 && blurred.b > 30)

        // Outside both areas nothing changes.
        let untouched = pixel(output, 90, 50)
        #expect(untouched.r > 245 && untouched.g > 245 && untouched.b > 245)
    }
}
