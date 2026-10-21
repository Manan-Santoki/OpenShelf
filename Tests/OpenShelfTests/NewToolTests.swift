import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

struct ResizeRuleTests {
    let photo = CGSize(width: 4000, height: 3000)

    @Test func percentScalesBothSides() {
        #expect(ResizeRule(mode: .percent, percent: 50).targetSize(for: photo) == CGSize(width: 2000, height: 1500))
    }

    @Test func longEdgeKeepsProportions() {
        #expect(ResizeRule(mode: .longEdge, longEdge: 1920).targetSize(for: photo) == CGSize(width: 1920, height: 1440))
        #expect(ResizeRule(mode: .longEdge, longEdge: 1080).targetSize(for: CGSize(width: 3000, height: 4000)) == CGSize(width: 810, height: 1080))
    }

    @Test func exactSizeFollowsTheMissingSide() {
        #expect(ResizeRule(mode: .exact, width: 800, height: 0).targetSize(for: photo) == CGSize(width: 800, height: 600))
        #expect(ResizeRule(mode: .exact, width: 0, height: 300).targetSize(for: photo) == CGSize(width: 400, height: 300))
        #expect(ResizeRule(mode: .exact, width: 500, height: 500).targetSize(for: photo) == CGSize(width: 500, height: 500))
    }

    @Test func neverEnlargesUnlessAllowed() {
        let small = CGSize(width: 800, height: 600)
        #expect(ResizeRule(mode: .longEdge, longEdge: 1920).targetSize(for: small) == small)
        #expect(ResizeRule(mode: .percent, percent: 200).targetSize(for: small) == small)
        #expect(ResizeRule(mode: .longEdge, longEdge: 1600, allowsEnlarging: true).targetSize(for: small) == CGSize(width: 1600, height: 1200))
    }

    @Test func resizingKeepsTransparency() throws {
        let context = try #require(CGContext(
            data: nil, width: 200, height: 100, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 50, y: 25, width: 100, height: 50))
        let image = try #require(context.makeImage())
        let resized = try #require(ImageResizer.resize(image, to: CGSize(width: 100, height: 50)))
        #expect(resized.width == 100 && resized.height == 50)
        #expect(resized.alphaInfo != .none && resized.alphaInfo != .noneSkipLast)
    }
}

struct SplitPlanTests {
    @Test func equalPartsCoverTheWholeFile() {
        let parts = SplitPlan.equalParts(duration: 10, count: 4)
        #expect(parts.count == 4)
        #expect(parts.first?.start == 0 && parts.last?.end == 10)
        #expect(parts.allSatisfy { abs($0.duration - 2.5) < 0.0001 })
        #expect(SplitPlan.equalParts(duration: 10, count: 99).count == 20)
    }

    @Test func everyIntervalLeavesAShorterLastPart() throws {
        let parts = try SplitPlan.every(30, duration: 75)
        #expect(parts.map(\.start) == [0, 30, 60])
        #expect(parts.last?.end == 75)
    }

    @Test func everyIntervalJoinsATinyTail() throws {
        #expect(try SplitPlan.every(10, duration: 20.1).count == 2)
    }

    @Test func intervalLimits() {
        #expect(throws: SplitPlanError.intervalTooShort) { try SplitPlan.every(0.5, duration: 60) }
        #expect(throws: SplitPlanError.tooMany(3600)) { try SplitPlan.every(1, duration: 3600) }
    }

    @Test func cutsAtTimes() throws {
        let parts = try SplitPlan.at("1:15, 0:30 ,2:00", duration: 180)
        #expect(parts.map(\.start) == [0, 30, 75, 120])
        #expect(parts.last?.end == 180)
    }

    @Test func badTimesAreExplained() {
        #expect(throws: SplitPlanError.unreadable("abc")) { try SplitPlan.at("0:30, abc", duration: 60) }
        #expect(throws: SplitPlanError.outOfRange("2:00")) { try SplitPlan.at("2:00", duration: 60) }
        #expect(throws: SplitPlanError.noCuts) { try SplitPlan.at("0:00", duration: 60) }
    }
}

struct VideoGeometryTests {
    let landscape = CGSize(width: 1920, height: 1080)

    private func orientedSize(_ transform: CGAffineTransform, _ size: CGSize) -> CGRect {
        CGRect(origin: .zero, size: size).applying(transform)
    }

    @Test func quarterTurnsSwapSidesAndStayAtTheOrigin() {
        for rotation in [VideoRotation.left, .right] {
            let rect = orientedSize(rotation.applied(to: .identity, naturalSize: landscape), landscape)
            #expect(abs(rect.minX) < 0.001 && abs(rect.minY) < 0.001)
            #expect(abs(rect.width - 1080) < 0.001 && abs(rect.height - 1920) < 0.001)
        }
    }

    @Test func halfTurnAndFlipKeepTheSize() {
        for rotation in [VideoRotation.upsideDown, .flipHorizontal] {
            let rect = orientedSize(rotation.applied(to: .identity, naturalSize: landscape), landscape)
            #expect(abs(rect.minX) < 0.001 && abs(rect.minY) < 0.001)
            #expect(abs(rect.width - 1920) < 0.001 && abs(rect.height - 1080) < 0.001)
        }
        #expect(!VideoRotation.flipHorizontal.isLossless && VideoRotation.right.isLossless)
    }

    @Test func turningAnAlreadyRotatedClipAddsUp() {
        let portraitPhone = CGAffineTransform(rotationAngle: .pi / 2)
        let rect = orientedSize(VideoRotation.right.applied(to: portraitPhone, naturalSize: landscape), landscape)
        // Two quarter turns: back to landscape, upside down.
        #expect(abs(rect.width - 1920) < 0.001 && abs(rect.height - 1080) < 0.001)
    }

    @Test func mergeLetterboxesADifferentShape() {
        let render = CGSize(width: 1920, height: 1080)
        let square = CGSize(width: 500, height: 500)
        let rect = CGRect(origin: .zero, size: square).applying(
            MediaTools.fitTransform(naturalSize: square, preferredTransform: .identity, renderSize: render)
        )
        #expect(abs(rect.height - 1080) < 0.001 && abs(rect.width - 1080) < 0.001)
        #expect(abs(rect.midX - 960) < 0.001 && abs(rect.midY - 540) < 0.001)
    }

    @Test func evenSizes() {
        #expect(MediaTools.evenSize(CGSize(width: 1081, height: 607)) == CGSize(width: 1080, height: 606))
    }
}

struct BackgroundRemovalTests {
    @Test func plainImageHasNoSubject() throws {
        let context = try #require(CGContext(
            data: nil, width: 256, height: 256, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(gray: 0.5, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 256, height: 256))
        let image = try #require(context.makeImage())
        #expect(throws: BackgroundRemovalError.noSubject) { try BackgroundRemover.removeBackground(from: image) }
    }
}
