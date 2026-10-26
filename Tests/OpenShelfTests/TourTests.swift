import AppKit
import AVFoundation
import PDFKit
import Testing
@testable import OpenShelf

struct TourStepTests {
    private let photo = URL(fileURLWithPath: "/tmp/Mountain Lake.jpg")
    private let notes = URL(fileURLWithPath: "/tmp/Trip Notes.md")
    private let report = URL(fileURLWithPath: "/tmp/Field Report.pdf")
    private let clip = URL(fileURLWithPath: "/tmp/Sunset.mov")

    @Test func eachStepWaitsForItsOwnGesture() {
        #expect(TourStep.convert.isCompleted(by: .ran(.convert(.png), [photo], option: false)))
        #expect(!TourStep.convert.isCompleted(by: .ran(.convert(.pdf), [notes], option: false)))
        #expect(TourStep.markdown.isCompleted(by: .ran(.convert(.pdf), [notes], option: false)))
        #expect(!TourStep.markdown.isCompleted(by: .ran(.convert(.docx), [notes], option: false)))
        #expect(TourStep.tools.isCompleted(by: .ran(.tool(.crop), [photo], option: false)))
        #expect(!TourStep.tools.isCompleted(by: .ran(.tool(.splitPDF), [report], option: false)))
        #expect(TourStep.pdf.isCompleted(by: .ran(.tool(.organizePages), [report], option: false)))
        #expect(TourStep.video.isCompleted(by: .ran(.tool(.trim), [clip], option: false)))
        #expect(TourStep.video.isCompleted(by: .ran(.convert(.gif), [clip], option: false)))
        #expect(TourStep.shelf.isCompleted(by: .ran(.action(.newShelf), [photo], option: false)))
        #expect(!TourStep.shelf.isCompleted(by: .ran(.action(.copyPath), [photo], option: false)))
        #expect(TourStep.keyboard.isCompleted(by: .keyboardWheelOpened([photo])))
        #expect(TourStep.options.isCompleted(by: .ran(.convert(.mp4), [clip], option: true)))
        #expect(!TourStep.options.isCompleted(by: .ran(.convert(.mp4), [clip], option: false)))
        // Opening a wheel is progress, not the step itself.
        #expect(!TourStep.convert.isCompleted(by: .wheelOpened(.convert, [photo])))
        #expect(TourStep.convert.isUnderWay(with: .wheelOpened(.convert, [photo])))
        #expect(!TourStep.convert.isUnderWay(with: .wheelOpened(.tools, [photo])))
    }

    @MainActor
    @Test func everyPracticeStepHasAFileAndWords() {
        for step in TourStep.practice {
            #expect(step.practiceFile != nil, "\(step)")
            #expect(TourInstructions.tip(for: step) != nil, "\(step)")
            #expect(!TourInstructions.success(for: step).isEmpty, "\(step)")
        }
        #expect(TourStep.allCases.first == .welcome)
        #expect(TourStep.allCases.last == .finish)
    }
}

@MainActor
struct TourProgressTests {
    private func makeProgress() -> (TourProgress, UserDefaults) {
        let suite = "openshelf-tour-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return (TourProgress(defaults: defaults), defaults)
    }

    private let photo = URL(fileURLWithPath: "/tmp/Mountain Lake.jpg")

    @Test func followsEventsOnlyWhileTheTourIsOpen() {
        let (progress, _) = makeProgress()
        progress.show(.convert)
        progress.record(.ran(.convert(.png), [photo], option: false))
        #expect(progress.completed.isEmpty)

        progress.isActive = true
        progress.record(.wheelOpened(.convert, [photo]))
        #expect(progress.isUnderWay)
        progress.record(.ran(.convert(.png), [photo], option: false))
        #expect(progress.completed == [.convert])
        #expect(!progress.isUnderWay)
        #expect(progress.celebration == 1)
        // Doing it again isn't celebrated twice.
        progress.record(.ran(.convert(.jpg), [photo], option: false))
        #expect(progress.celebration == 1)
    }

    @Test func remembersWhatWasDoneAndStartsOver() {
        let (progress, defaults) = makeProgress()
        progress.isActive = true
        progress.show(.shelf)
        progress.record(.ran(.action(.newShelf), [photo], option: false))
        #expect(TourProgress(defaults: defaults).completed == [.shelf])
        #expect(progress.practiceDoneCount == 1)

        progress.reset()
        #expect(progress.completed.isEmpty)
        #expect(progress.current == .welcome)
        #expect(TourProgress(defaults: defaults).completed.isEmpty)
    }

    @Test func movesThroughTheSteps() {
        let (progress, _) = makeProgress()
        progress.previous()
        #expect(progress.current == .welcome)
        progress.next()
        #expect(progress.current == .convert)
        progress.show(.finish)
        progress.next()
        #expect(progress.current == .finish)
    }

    @Test func instructionsUseTheKeysThatAreSet() {
        let keys = TourInstructions.keys(for: .convert)
        let expected = TriggerKey.describe(Preferences.shared.trigger(for: .convert))
        #expect(keys.map { String($0.prefix(1)) }.joined() == expected)
        let model = TourModel(folder: URL(fileURLWithPath: "/tmp/practice"))
        let steps = TourInstructions.steps(for: .convert, model: model)
        #expect(steps.count == 3)
        #expect(steps[0].text.contains("Mountain Lake.jpg"))
        #expect(steps[1].keys == keys)
        #expect(steps[2].text.contains("PNG"))
    }
}

struct PracticeFilesTests {
    @Test func makesEveryFileAndRecognisesWhatWasMade() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-practice-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        try await PracticeFiles.prepare(in: folder)

        let photo = try #require(NSImage(contentsOf: PracticeFiles.url(of: .photo, in: folder)))
        #expect(photo.size.width > photo.size.height)
        #expect(FileKind(url: PracticeFiles.url(of: .photo, in: folder)) == .image)

        let report = try #require(PDFDocument(url: PracticeFiles.url(of: .report, in: folder)))
        #expect(report.pageCount == 3)
        #expect(report.string?.contains("Field Report") == true)

        let clip = AVURLAsset(url: PracticeFiles.url(of: .clip, in: folder))
        let duration = try await clip.load(.duration).seconds
        #expect(abs(duration - 4) < 0.2)
        #expect(try await clip.loadTracks(withMediaType: .video).count == 1)

        let notes = try String(contentsOf: PracticeFiles.url(of: .notes, in: folder), encoding: .utf8)
        #expect(MarkdownDocument(parsing: notes).blocks.contains { if case .table = $0 { true } else { false } })
        #expect(FileManager.default.fileExists(atPath: PracticeFiles.url(of: .letter, in: folder).path))

        // Only what the person makes counts as a result.
        #expect(PracticeFiles.results(in: folder).isEmpty)
        let made = folder.appendingPathComponent("Mountain Lake.png")
        try Data("png".utf8).write(to: made)
        #expect(PracticeFiles.results(in: folder).map(\.lastPathComponent) == [made.lastPathComponent])

        // Preparing again keeps what's there.
        try await PracticeFiles.prepare(in: folder)
        #expect(PracticeFiles.results(in: folder).map(\.lastPathComponent) == [made.lastPathComponent])
    }

    @Test func thePracticeFilesConvert() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-practice-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        try await PracticeFiles.prepare(in: folder)
        let tools = ExternalTools()
        let png = try await Converter.convert(PracticeFiles.url(of: .photo, in: folder), to: .png, tools: tools) { _ in }
        #expect(png.map(\.lastPathComponent) == ["Mountain Lake.png"])
        let pdf = try await Converter.convert(PracticeFiles.url(of: .notes, in: folder), to: .pdf, tools: tools) { _ in }
        #expect(PDFDocument(url: pdf[0])?.string?.contains("Packing list") == true)
        #expect(ConversionCatalog.formats(for: [PracticeFiles.url(of: .clip, in: folder)], tools: tools).contains(.gif))
        #expect(FileTool.available(for: [PracticeFiles.url(of: .report, in: folder)]).contains(.organizePages))
        #expect(FileTool.available(for: [PracticeFiles.url(of: .clip, in: folder)]).contains(.trim))
    }
}
