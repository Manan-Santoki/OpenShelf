import AppIntents
import Foundation
import Testing
@testable import OpenShelf

struct IntentFormatTests {
    @Test func everyShortcutsFormatIsAConvertFormat() {
        for format in IntentFormat.allCases {
            #expect(OutputFormat(rawValue: format.rawValue) != nil, "\(format) has no OutputFormat")
        }
    }

    /// A format added to the Convert wheel needs adding to Shortcuts too.
    @Test func everyConvertFormatIsOfferedInShortcuts() {
        let offered = Set(IntentFormat.allCases.map(\.outputFormat))
        #expect(offered == Set(OutputFormat.allCases))
        #expect(IntentFormat.allCases.count == OutputFormat.allCases.count)
    }

    @Test func formatsMapToTheirNamesakes() {
        #expect(IntentFormat.png.outputFormat == .png)
        #expect(IntentFormat.tgz.outputFormat == .tgz)
        #expect(IntentFormat.extract.outputFormat == .extract)
    }

    /// The titles are literals for App Intents; they read like the wheel's.
    @Test func titlesMatchTheWheel() {
        let representations = IntentFormat.caseDisplayRepresentations
        for format in IntentFormat.allCases {
            let title = representations[format].map { String(localized: $0.title) }
            #expect(title == format.outputFormat.title, "\(format)")
        }
    }

    @Test func compressionLevelsMatchThePDFTool() {
        #expect(IntentCompression.allCases.map(\.pdfLevel) == PDFCompressionLevel.allCases)
        #expect(IntentCompression.light.imageQuality > IntentCompression.balanced.imageQuality)
        #expect(IntentCompression.balanced.imageQuality > IntentCompression.strong.imageQuality)
        for level in IntentCompression.allCases {
            #expect((10...100).contains(level.imageQuality))
            #expect(IntentCompression.caseDisplayRepresentations[level] != nil)
        }
    }

    @Test func textOutputsMapToOCR() {
        #expect(IntentTextOutput.text.ocrOutput == .text)
        #expect(IntentTextOutput.searchablePDF.ocrOutput == .searchablePDF)
        for output in IntentTextOutput.allCases {
            #expect(IntentTextOutput.caseDisplayRepresentations[output] != nil)
        }
    }
}

struct IntentRoutingTests {
    private func url(_ name: String) -> URL {
        URL(fileURLWithPath: "/tmp/\(name)")
    }

    @Test func aFileAlreadyInTheFormatIsKept() {
        #expect(IntentRouting.convertStep(pathExtension: "png", to: .png, available: [.jpg, .zip]) == .keep)
        #expect(IntentRouting.convertStep(pathExtension: "JPEG", to: .jpg, available: [.png, .zip]) == .keep)
        #expect(IntentRouting.convertStep(pathExtension: "markdown", to: .md, available: [.pdf, .zip]) == .keep)
    }

    @Test func offeredFormatsConvert() {
        #expect(IntentRouting.convertStep(pathExtension: "heic", to: .jpg, available: [.png, .jpg, .zip]) == .convert)
        #expect(IntentRouting.convertStep(pathExtension: "zip", to: .extract, available: [.extract]) == .convert)
    }

    @Test func formatsTheWheelDoesNotOfferAreUnsupported() {
        #expect(IntentRouting.convertStep(pathExtension: "mp3", to: .png, available: [.m4a, .wav, .zip]) == .unsupported)
        #expect(IntentRouting.convertStep(pathExtension: "txt", to: .extract, available: [.zip, .tar, .tgz]) == .unsupported)
    }

    @Test func onlyFilesWithoutAWritableFolderAreStaged() {
        #expect(!IntentRouting.needsStaging(hasFileURL: true, folderIsWritable: true))
        #expect(IntentRouting.needsStaging(hasFileURL: true, folderIsWritable: false))
        #expect(IntentRouting.needsStaging(hasFileURL: false, folderIsWritable: true))
    }

    @Test func unsupportedMessageListsTheAlternatives() {
        let message = IntentRouting.unsupportedMessage(name: "Song.mp3", format: .png, available: [.m4a, .wav, .zip])
        #expect(message == "“Song.mp3” can’t be converted to PNG. It can become M4A, WAV.")
        let none = IntentRouting.unsupportedMessage(name: "a.xyz", format: .pdf, available: [.zip, .tar])
        #expect(none == "“a.xyz” can’t be converted to PDF. It can become TAR.")
        let nothing = IntentRouting.unsupportedMessage(name: "a.key", format: .pdf, available: [.zip])
        #expect(nothing == "“a.key” can’t be converted to PDF.")
    }

    @Test func failureMessagesNameTheFirstFileAndCountTheRest() {
        #expect(IntentRouting.failureMessage([]) == nil)
        #expect(IntentRouting.failureMessage([("a.png", "Broken.")]) == "“a.png”: Broken.")
        #expect(IntentRouting.failureMessage([("a.png", "Broken."), ("b.png", "Also.")]) == "“a.png”: Broken. (and 1 more file failed)")
        #expect(IntentRouting.failureMessage([("a", "x"), ("b", "y"), ("c", "z")]) == "“a”: x (and 2 more files failed)")
    }

    @Test func toolsAcceptTheKindsTheirWheelEntriesDo() {
        #expect(IntentTool.compress.acceptedKinds == [.image, .pdf])
        #expect(IntentTool.extractText.acceptedKinds == [.image, .pdf])
        #expect(IntentTool.removeBackground.acceptedKinds == [.image])
        #expect(IntentTool.readQR.acceptedKinds == [.image])
        #expect(IntentTool.removeAudio.acceptedKinds == [.video])
        #expect(IntentTool.combinePDF.acceptedKinds == [.image, .pdf])
    }

    /// Each action is only offered for files the Tools wheel would run the
    /// same tool on.
    @Test func everyActionMatchesAToolsWheelEntry() throws {
        let samples: [FileKind: URL] = [.image: url("a.jpg"), .pdf: url("a.pdf"), .video: url("a.mp4")]
        for tool in IntentTool.allCases {
            for kind in tool.acceptedKinds {
                let file = try #require(samples[kind])
                // Combine is offered once there are two files.
                let files = tool == .combinePDF ? [file, file.deletingLastPathComponent().appendingPathComponent("b.\(file.pathExtension)")] : [file]
                let available = FileTool.available(for: files)
                for wheelTool in tool.wheelTools(for: kind) {
                    #expect(available.contains(wheelTool), "\(tool) on \(kind) should match \(wheelTool)")
                }
            }
        }
    }

    @Test func unacceptedFilesAreNamed() {
        #expect(IntentRouting.unacceptedMessage(name: "a.mp4", tool: .removeBackground) == "Remove Background works on images; “a.mp4” isn’t one.")
        #expect(IntentRouting.unacceptedMessage(name: "a.png", tool: .removeAudio) == "Remove Audio works on videos; “a.png” isn’t one.")
        #expect(IntentRouting.unacceptedMessage(name: "a.zip", tool: .compress) == "Compress works on images and PDFs; “a.zip” isn’t one.")
    }
}
