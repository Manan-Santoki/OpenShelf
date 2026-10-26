import ServiceManagement
import SwiftUI

/// The guided tour: a list of steps on the left, the current one on the
/// right with its instructions, a practice file to drag, and what happened.
struct TourView: View {
    let model: TourModel
    let progress: TourProgress
    let close: () -> Void

    var body: some View {
        HStack(spacing: 0) {
            TourSidebar(progress: progress, close: close)
                .frame(width: 230)
            Divider()
            VStack(spacing: 0) {
                // Scrolls only when a step has more than fits, so the buttons stay put.
                ScrollView(.vertical) {
                    StepPage(step: progress.current, model: model, progress: progress, close: close)
                        .id(progress.current)
                        .transition(.asymmetric(insertion: .opacity.combined(with: .offset(x: 24)), removal: .opacity))
                        .frame(maxWidth: .infinity, alignment: .topLeading)
                }
                .scrollBounceBehavior(.basedOnSize)
                .frame(maxHeight: .infinity)
                Divider()
                TourFooter(progress: progress, close: close)
            }
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .frame(width: TourWindowController.size.width, height: TourWindowController.size.height)
        .animation(.spring(response: 0.35, dampingFraction: 0.86), value: progress.current)
        .onChange(of: progress.celebration) {
            NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
        }
    }
}

// MARK: - Sidebar

private struct TourSidebar: View {
    let progress: TourProgress
    let close: () -> Void

    var body: some View {
        let total = TourStep.practice.count
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 34, height: 34)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text("OpenShelf Tour")
                        .font(.system(size: 14, weight: .semibold))
                    Text("\(progress.practiceDoneCount) of \(total) tried")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .contentTransition(.numericText())
                }
            }
            .padding(.top, 44)
            .padding(.horizontal, 18)

            ProgressView(value: Double(progress.practiceDoneCount), total: Double(total))
                .progressViewStyle(.linear)
                .tint(.green)
                .padding(.horizontal, 18)
                .padding(.top, 12)
                .padding(.bottom, 14)

            VStack(spacing: 2) {
                ForEach(TourStep.allCases) { step in
                    SidebarRow(step: step, progress: progress)
                }
            }
            .padding(.horizontal, 10)

            Spacer(minLength: 0)

            Button("Skip Tour", action: close)
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 18)
                .padding(.bottom, 16)
                .help("Close the tour. It’s in the menu bar and Settings → General whenever you want it.")
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .background(.regularMaterial)
    }
}

private struct SidebarRow: View {
    let step: TourStep
    let progress: TourProgress

    var body: some View {
        let isCurrent = progress.current == step
        let isDone = progress.completed.contains(step)
        Button {
            progress.show(step)
        } label: {
            HStack(spacing: 10) {
                ZStack {
                    if isDone {
                        Image(systemName: "checkmark.circle.fill")
                            .font(.system(size: 18))
                            .foregroundStyle(.green)
                            .transition(.scale.combined(with: .opacity))
                    } else {
                        Circle()
                            .strokeBorder(isCurrent ? Color.accentColor : Color.secondary.opacity(0.4), lineWidth: 1.5)
                        Image(systemName: step.systemImage)
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(isCurrent ? Color.accentColor : .secondary)
                    }
                }
                .frame(width: 20, height: 20)
                Text(step.title)
                    .font(.system(size: 13, weight: isCurrent ? .semibold : .regular))
                    .foregroundStyle(isCurrent ? .primary : .secondary)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .frame(height: 30)
            .background {
                if isCurrent {
                    RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.accentColor.opacity(0.14))
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isDone)
        .accessibilityLabel("\(step.title)\(isDone ? ", done" : "")")
    }
}

// MARK: - Footer

private struct TourFooter: View {
    let progress: TourProgress
    let close: () -> Void

    var body: some View {
        let step = progress.current
        let isDone = progress.completed.contains(step)
        HStack(spacing: 10) {
            if step != .welcome {
                Button("Back") { progress.previous() }
                    .keyboardShortcut(.leftArrow, modifiers: .command)
            }
            Spacer()
            switch step {
            case .welcome:
                Button("Not Now", action: close)
                Button("Start the Tour") { progress.next() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            case .finish:
                Button("Done", action: close)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            default:
                if isDone {
                    Button("Continue") { progress.next() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                } else {
                    Button("Skip for Now") { progress.next() }
                        .help("Go on without trying this one. You can come back to it from the list.")
                }
            }
        }
        .controlSize(.large)
        .padding(.horizontal, 24)
        .frame(height: 60)
    }
}

// MARK: - Pages

private struct StepPage: View {
    let step: TourStep
    let model: TourModel
    let progress: TourProgress
    let close: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            header
            switch step {
            case .welcome:
                WelcomePage(model: model)
            case .finish:
                FinishPage(model: model, progress: progress, close: close)
            default:
                PracticePage(step: step, model: model, progress: progress)
            }
        }
        .padding(.horizontal, 32)
        .padding(.top, 36)
        .padding(.bottom, 16)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let index = TourStep.practice.firstIndex(of: step) {
                Text("STEP \(index + 1) OF \(TourStep.practice.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .tracking(0.6)
                    .foregroundStyle(Color.accentColor)
            }
            Text(step == .welcome ? (model.isReturning ? "New: Learn OpenShelf Hands-On" : "Welcome to OpenShelf") : step.title)
                .font(.system(size: 26, weight: .bold))
            Text(step.summary)
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

private struct WelcomePage: View {
    let model: TourModel

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Everything starts with a drag. Start dragging any file, keep holding, and tap a key:")
                .font(.system(size: 13))
            VStack(spacing: 10) {
                GestureRow(kind: .convert, title: "Convert", detail: "Turn it into JPG, PNG, PDF, DOCX, MP3, ZIP…")
                GestureRow(kind: .tools, title: "Tools", detail: "Crop, annotate, compress, trim, split PDFs…")
                GestureRow(kind: .actions, title: "Actions", detail: "Put it on a shelf, AirDrop, copy, move…")
            }
            HStack(spacing: 8) {
                if model.isReady {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text("Practice files are ready. Nothing of yours is touched, and it takes about three minutes.")
                } else if let failure = model.failure {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Text(failure)
                } else {
                    ProgressView().controlSize(.small)
                    Text("Making practice files…")
                }
            }
            .font(.system(size: 12))
            .foregroundStyle(.secondary)
        }
    }
}

private struct PracticePage: View {
    let step: TourStep
    let model: TourModel
    let progress: TourProgress

    var body: some View {
        let isDone = progress.completed.contains(step)
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 28) {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(Array(TourInstructions.steps(for: step, model: model).enumerated()), id: \.offset) { index, instruction in
                        InstructionRow(number: index + 1, instruction: instruction)
                    }
                    if let tip = TourInstructions.tip(for: step) {
                        HStack(alignment: .top, spacing: 8) {
                            Image(systemName: "lightbulb")
                                .foregroundStyle(.yellow)
                            Text(.init(tip))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.04)))
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                if let file = step.practiceFile {
                    PracticeTray(
                        file: file,
                        model: model,
                        nudge: step == .keyboard ? "Select me in Finder" : "Drag me",
                        isWaiting: !isDone && !progress.isUnderWay
                    )
                }
            }

            StatusBanner(step: step, isDone: isDone, isUnderWay: progress.isUnderWay, celebration: progress.celebration)
            ResultsStrip(model: model)
        }
    }
}

private struct FinishPage: View {
    let model: TourModel
    let progress: TourProgress
    let close: () -> Void
    @State private var opensAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                Image(systemName: "checkmark.seal.fill")
                    .font(.system(size: 40))
                    .foregroundStyle(.green)
                    .symbolEffect(.bounce, value: progress.current)
                Text(summary)
                    .font(.system(size: 13))
                    .fixedSize(horizontal: false, vertical: true)
            }

            LazyVGrid(columns: [GridItem(.flexible(), alignment: .leading), GridItem(.flexible(), alignment: .leading)], spacing: 8) {
                ForEach(TourStep.practice) { step in
                    let isDone = progress.completed.contains(step)
                    Button {
                        progress.show(step)
                    } label: {
                        Label(step.title, systemImage: isDone ? "checkmark.circle.fill" : "circle.dashed")
                            .foregroundStyle(isDone ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                            .symbolRenderingMode(.multicolor)
                    }
                    .buttonStyle(.plain)
                    .help(isDone ? "Done. Try it again." : "Try this one.")
                }
            }
            .font(.system(size: 13))

            Divider()

            VStack(alignment: .leading, spacing: 10) {
                LabeledContent("Keep going") {
                    HStack(spacing: 8) {
                        Button("Extra Formats…") { SettingsWindowController.shared.show(tab: .extraFormats) }
                            .help("Install ffmpeg, LibreOffice or Pandoc for more formats.")
                        Button("Wheel Keys…") { SettingsWindowController.shared.show(tab: .wheels) }
                            .help("Change the keys that open each wheel, and the shortcut.")
                    }
                }
                LabeledContent("Practice files") {
                    HStack(spacing: 8) {
                        if model.isReady {
                            Button("Show in Finder") { NSWorkspace.shared.open(model.folder) }
                            Button("Remove") { model.removePracticeFiles() }
                                .help("Moves the practice folder, and everything made in it, to the Trash.")
                        } else {
                            Button("Make Them Again") { model.restore() }
                        }
                    }
                }
            }
            .font(.system(size: 13))

            Toggle("Open OpenShelf at login", isOn: $opensAtLogin)
                .onChange(of: opensAtLogin) { _, enabled in
                    do {
                        if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                    } catch {
                        opensAtLogin = SMAppService.mainApp.status == .enabled
                    }
                }
        }
    }

    private var summary: String {
        let done = progress.practiceDoneCount
        let total = TourStep.practice.count
        if done == total {
            return "You’ve tried everything. OpenShelf lives in the menu bar; drag any file and tap a key whenever you need it."
        }
        return "You’ve tried \(done) of \(total). The rest are waiting below, and the tour is in the menu bar whenever you want it."
    }
}

// MARK: - Pieces

/// One line of how-to, with the keys to press drawn as keys.
struct TourInstruction {
    var text: String
    var keys: [String] = []
    var action: (title: String, run: @MainActor () -> Void)?
}

private struct InstructionRow: View {
    let number: Int
    let instruction: TourInstruction

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text("\(number)")
                .font(.system(size: 11, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .frame(width: 20, height: 20)
                .background(Circle().fill(Color.accentColor))
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            VStack(alignment: .leading, spacing: 8) {
                Text(.init(instruction.text))
                    .font(.system(size: 13))
                    .fixedSize(horizontal: false, vertical: true)
                if !instruction.keys.isEmpty {
                    HStack(spacing: 6) {
                        ForEach(instruction.keys, id: \.self) { Keycap(label: $0) }
                    }
                }
                if let action = instruction.action {
                    Button(action.title) { action.run() }
                        .controlSize(.small)
                }
            }
        }
    }
}

/// A key on the keyboard: "⇧ Shift".
struct Keycap: View {
    let label: String

    var body: some View {
        Text(label)
            .font(.system(size: 12, weight: .semibold))
            .padding(.horizontal, 9)
            .frame(minWidth: 30, minHeight: 26)
            .background {
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Color(nsColor: .controlBackgroundColor))
                    .shadow(color: .black.opacity(0.25), radius: 0, x: 0, y: 1.5)
            }
            .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(.primary.opacity(0.14)))
            .accessibilityLabel(label)
    }
}

/// The practice file to drag, with a nudge until it's picked up.
private struct PracticeTray: View {
    let file: PracticeFile
    let model: TourModel
    /// What the label over the file says until it's used.
    let nudge: String
    let isWaiting: Bool
    @State private var bobbing = false

    var body: some View {
        let url = model.url(of: file)
        VStack(spacing: 8) {
            Label(nudge, systemImage: "hand.point.up.left.fill")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Color.accentColor)
                .offset(y: bobbing && isWaiting ? -4 : 0)
                .opacity(isWaiting ? 1 : 0)
                .animation(isWaiting ? .easeInOut(duration: 0.7).repeatForever(autoreverses: true) : .default, value: bobbing)
            VStack(spacing: 6) {
                ZStack {
                    if model.isReady, let thumbnail = model.thumbnails[url] {
                        Image(nsImage: thumbnail)
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                            .shadow(color: .black.opacity(0.2), radius: 4, y: 2)
                    } else if model.isReady {
                        Image(nsImage: NSWorkspace.shared.icon(forFile: url.path))
                            .resizable()
                    } else {
                        ProgressView()
                    }
                }
                .frame(width: 96, height: 96)
                Text(file.fileName)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                Text(file.caption)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            .padding(14)
            .frame(width: 180)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.primary.opacity(0.04)))
            .overlay {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(Color.accentColor.opacity(isWaiting ? 0.6 : 0.2), style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
            }
            .overlay {
                if model.isReady {
                    FileDragSource(url: url, image: model.thumbnails[url])
                        .help("Drag \(file.fileName), then tap the keys while you’re still holding it.")
                }
            }
        }
        .onAppear { bobbing = true }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Practice file \(file.fileName). Drag it.")
    }
}

/// What's happening now: waiting, the wheel open, or done.
private struct StatusBanner: View {
    let step: TourStep
    let isDone: Bool
    let isUnderWay: Bool
    let celebration: Int
    @State private var pulsing = false

    var body: some View {
        HStack(spacing: 10) {
            ZStack {
                if isDone {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 20))
                        .foregroundStyle(.green)
                        .symbolEffect(.bounce, value: celebration)
                } else if isUnderWay {
                    Image(systemName: "arrow.down.circle.fill")
                        .font(.system(size: 20))
                        .foregroundStyle(Color.accentColor)
                } else {
                    Circle()
                        .fill(Color.accentColor)
                        .frame(width: 9, height: 9)
                        .scaleEffect(pulsing ? 1.35 : 0.8)
                        .opacity(pulsing ? 0.45 : 1)
                        .animation(.easeInOut(duration: 0.9).repeatForever(autoreverses: true), value: pulsing)
                }
            }
            .frame(width: 22)
            Text(.init(message))
                .font(.system(size: 13, weight: isDone || isUnderWay ? .semibold : .regular))
                .foregroundStyle(isDone || isUnderWay ? .primary : .secondary)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .frame(height: 44)
        .background {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(isDone ? Color.green.opacity(0.12) : isUnderWay ? Color.accentColor.opacity(0.12) : Color.primary.opacity(0.04))
        }
        .animation(.spring(response: 0.3, dampingFraction: 0.8), value: isDone)
        .animation(.spring(response: 0.3, dampingFraction: 0.8), value: isUnderWay)
        .onAppear { pulsing = true }
        .accessibilityElement(children: .combine)
    }

    private var message: String {
        if isDone { return TourInstructions.success(for: step) }
        if isUnderWay, let wheel = step.wheel {
            return "The \(wheel.shortTitle) wheel is open. Now drop on **\(step.target)**."
        }
        return "Waiting for you to try it…"
    }
}

/// What the person has made so far, in the practice folder.
private struct ResultsStrip: View {
    let model: TourModel

    var body: some View {
        if !model.results.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Made so far")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Show in Finder") { FileActions.reveal(Array(model.results.prefix(1))) }
                        .buttonStyle(.link)
                        .font(.system(size: 11))
                }
                HStack(spacing: 8) {
                    ForEach(model.results.prefix(4), id: \.self) { url in
                        Button {
                            FileActions.reveal([url])
                        } label: {
                            HStack(spacing: 6) {
                                Image(nsImage: model.thumbnails[url] ?? NSWorkspace.shared.icon(forFile: url.path))
                                    .resizable()
                                    .aspectRatio(contentMode: .fit)
                                    .frame(width: 22, height: 22)
                                Text(url.lastPathComponent)
                                    .font(.system(size: 11))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                            }
                            .padding(.horizontal, 8)
                            .frame(height: 30)
                            .background(Capsule().fill(.primary.opacity(0.06)))
                        }
                        .buttonStyle(.plain)
                        .help("Show \(url.lastPathComponent) in Finder")
                        .overlay {
                            FileDragSource(url: url, image: model.thumbnails[url])
                        }
                    }
                }
            }
            .transition(.opacity.combined(with: .move(edge: .bottom)))
        }
    }
}

/// The three gestures, with the keys set in Settings.
private struct GestureRow: View {
    let kind: WheelKind
    let title: String
    let detail: String

    var body: some View {
        HStack(spacing: 12) {
            Text(TriggerKey.describe(Preferences.shared.trigger(for: kind)))
                .font(.system(size: 17, weight: .semibold, design: .rounded))
                .frame(width: 52, height: 40)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.15)))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(detail).font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.primary.opacity(0.05)))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Words

/// What each step says, with the keys this person has set.
@MainActor
enum TourInstructions {
    /// "⇧ Shift", "⌃ Control"… for the keys that open `kind`'s wheel.
    static func keys(for kind: WheelKind) -> [String] {
        let flags = Preferences.shared.trigger(for: kind)
        return TriggerKey.allCases.filter { flags.contains($0.flag) }.map { "\($0.symbol) \($0.name)" }
    }

    static func steps(for step: TourStep, model: TourModel) -> [TourInstruction] {
        let name = step.practiceFile.map { "**\($0.fileName)**" } ?? ""
        switch step {
        case .keyboard:
            let letter = step.practiceFile.map(model.url(of:))
            var press = TourInstruction(text: "Press the shortcut. The Convert wheel opens at the pointer.")
            if let combo = Preferences.shared.openWheelShortcut {
                press.keys = [combo.displayString]
            } else {
                press.text = "Set a shortcut to open the wheel, then press it."
                press.action = ("Set a Shortcut…", { SettingsWindowController.shared.show(tab: .wheels) })
            }
            var select = TourInstruction(text: "Select \(name) in Finder (or files on a shelf).")
            if let letter {
                select.action = ("Show It in Finder", { FileActions.reveal([letter]) })
            }
            return [
                select,
                press,
                TourInstruction(text: "Use the arrow keys and **Return** to run a wedge, or **Tab** to switch wheels. **Esc** closes."),
            ]
        case .options:
            return [
                TourInstruction(text: "Drag \(name) and keep holding."),
                TourInstruction(text: "Tap the Convert keys.", keys: step.wheel.map(keys(for:)) ?? []),
                TourInstruction(text: "Hold **⌥ Option** as you drop on **\(step.target)**, and choose the quality and size for this one conversion."),
            ]
        default:
            guard let wheel = step.wheel else { return [] }
            return [
                TourInstruction(text: "Drag \(name) from here and keep holding the mouse button."),
                TourInstruction(text: "While you’re dragging, tap these keys. No need to keep holding them.", keys: keys(for: wheel)),
                TourInstruction(text: "Drop on **\(step.target)**."),
            ]
        }
    }

    static func tip(for step: TourStep) -> String? {
        switch step {
        case .convert: "Results are saved next to the original, and nothing is ever overwritten. Arrow keys and Return work too, while you’re still dragging; Esc cancels."
        case .markdown: "Headings, lists, tables, code and footnotes come out typeset. Try DOCX or HTML too."
        case .tools: "Tools open a small editor that saves a copy, like “Mountain Lake Cropped.jpg”. Drag several photos to edit them all at once."
        case .pdf: "Drag a PDF and an image together to Combine them into one PDF."
        case .video: "Tap the Convert keys instead to make a GIF, or an MP4 from the MOV."
        case .shelf: "Drag files out of a shelf into any app. Space previews them, and a shelf can dock to the edge of the screen."
        case .keyboard: "Hold ⌘ in a keyboard wheel to see a number on every wedge, then press it."
        case .options: "Settings → Output sets the quality, size and codec for every conversion."
        case .welcome, .finish: nil
        }
    }

    static func success(for step: TourStep) -> String {
        switch step {
        case .convert: "Nicely done! The new file is next to the original."
        case .markdown: "There’s your PDF, properly typeset."
        case .tools: "That’s the Tools wheel. Save in the editor to keep a copy."
        case .pdf: "PDFs handled."
        case .video: "Clip handled."
        case .shelf: "Your files are on a shelf. Drag them out whenever you’re ready."
        case .keyboard: "That’s the wheel without a drag."
        case .options: "Those settings are for this conversion only."
        case .welcome, .finish: ""
        }
    }
}
