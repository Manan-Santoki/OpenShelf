import AppKit
import SwiftUI

/// Settings → Extra Formats: ffmpeg and LibreOffice, whether they're on this
/// Mac and where from, and installing or removing them.
struct ExtraFormatsPane: View {
    let installer: ToolInstaller

    var body: some View {
        Form {
            Section {
                ForEach(ExtraTool.allCases) { tool in
                    ExtraToolRow(tool: tool, installer: installer)
                }
            } footer: {
                Text("Free software, downloaded only when you click Install: ffmpeg (LGPL/GPL) from [ffmpeg.martin-riedl.de](https://ffmpeg.martin-riedl.de), LibreOffice (MPL) from [libreoffice.org](https://www.libreoffice.org). When the ffmpeg server is down, the same signed build comes from OpenShelf’s mirror on GitHub.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .settingsPane()
    }

    /// What decides a row's height, for `SettingsTab.heightSignature`.
    @MainActor
    static func layout(of tool: ExtraTool, installer: ToolInstaller) -> String {
        switch installer.status(of: tool) {
        case .notInstalled: "missing"
        case .installed(let info): "installed \(info.canRemove)"
        case .installing(let progress): "installing \(progress.source != nil)"
        case .failed(let message): "failed \(message) \(installer.offers(for: tool))"
        }
    }
}

private struct ExtraToolRow: View {
    let tool: ExtraTool
    let installer: ToolInstaller
    @State private var isConfirmingRemoval = false

    var body: some View {
        let status: ToolStatus = installer.status(of: tool)
        LabeledContent {
            controls(for: status)
        } label: {
            Text(tool.name)
            detail(for: status)
        }
        .confirmationDialog("Remove \(tool.name)?", isPresented: $isConfirmingRemoval) {
            Button("Remove", role: .destructive) { installer.remove(tool) }
        } message: {
            Text(tool == .libreOffice
                ? "LibreOffice.app goes to the Trash, and the formats it adds leave the wheel."
                : "The formats it adds leave the wheel until it’s installed again.")
        }
    }

    @ViewBuilder
    private func detail(for status: ToolStatus) -> some View {
        switch status {
        case .failed(let message):
            FailureDetail(message: message, failure: installer.failure(of: tool), offers: installer.offers(for: tool))
        case .installing(let progress):
            Text(progress.detail)
                .monospacedDigit()
            if let source = progress.source {
                Text(source.statusLine(for: tool, step: progress.step))
                    .font(.caption)
            }
        case .installed, .notInstalled:
            Text(tool.unlocks)
        }
    }

    @ViewBuilder
    private func controls(for status: ToolStatus) -> some View {
        switch status {
        case .notInstalled:
            Button("Install (\(tool.approximateDownloadSize))") { installer.install(tool) }
                .help("Downloads \(tool.name) from \(tool.downloadHost) and checks its signature before installing it.")
        case .installed(let info):
            InstalledLabel(tool: tool, info: info) { isConfirmingRemoval = true }
        case .installing(let progress):
            HStack(spacing: 8) {
                ProgressView(value: progress.fraction)
                    .progressViewStyle(.linear)
                    .frame(width: 110)
                Button("Cancel") { installer.cancel(tool) }
            }
        case .failed:
            FailureControls(tool: tool, offers: installer.offers(for: tool), installer: installer)
        }
    }
}

/// "Installed · Homebrew", with Remove for OpenShelf's own copies.
private struct InstalledLabel: View {
    let tool: ExtraTool
    let info: InstalledToolInfo
    let onRemove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            if info.canRemove {
                Button("Remove", action: onRemove)
            }
            Label {
                Text(info.source == .other ? "Installed" : "Installed · \(info.source.rawValue)")
            } icon: {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundStyle(.green)
            }
            .help([tool.name, info.version].compactMap { $0 }.joined(separator: " ") + " at \(info.path.path)")
        }
    }
}

/// Why an install failed. A server being down isn't an error on this Mac,
/// so it's said calmly rather than in red.
private struct FailureDetail: View {
    let message: String
    let failure: InstallFailure?
    let offers: [InstallOffer]

    var body: some View {
        let isServerDown: Bool = failure?.kind == .sourcesUnavailable
        Text(message)
            .foregroundStyle(isServerDown ? Color.secondary : Color.red)
            .help(failure?.detail ?? "")
        if offers.contains(.getHomebrew) {
            Text("Try again later, or install Homebrew from [brew.sh](https://brew.sh).")
        }
    }
}

/// The ways forward after a failure, the first one on top.
private struct FailureControls: View {
    let tool: ExtraTool
    let offers: [InstallOffer]
    let installer: ToolInstaller
    @State private var hasCopiedCommand = false

    var body: some View {
        VStack(alignment: .trailing, spacing: 2) {
            ForEach(offers, id: \.self) { offer in
                control(for: offer)
            }
        }
    }

    @ViewBuilder
    private func control(for offer: InstallOffer) -> some View {
        switch offer {
        case .retry:
            Button("Retry") { installer.install(tool) }
        case .installWithHomebrew:
            Button("Install with Homebrew") { installer.installWithHomebrew(tool) }
                .help("Runs “\(tool.homebrewCommand)” for you.")
        case .copyHomebrewCommand:
            Button(hasCopiedCommand ? "Copied" : "Copy Homebrew Command") { copyHomebrewCommand() }
                .buttonStyle(.link)
                .font(.caption)
                .help(tool.homebrewCommand)
        case .getHomebrew:
            EmptyView()
        }
    }

    private func copyHomebrewCommand() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(tool.homebrewCommand, forType: .string)
        hasCopiedCommand = true
        Task {
            try? await Task.sleep(for: .seconds(1.5))
            hasCopiedCommand = false
        }
    }
}
