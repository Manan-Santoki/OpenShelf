import AppKit
import SwiftUI

/// Settings → Extra formats: ffmpeg and LibreOffice, whether they're on this
/// Mac and where from, and installing or removing them.
struct ExtraFormatsSection: View {
    let installer: ToolInstaller

    var body: some View {
        Section {
            ForEach(ExtraTool.allCases) { tool in
                ExtraToolRow(tool: tool, installer: installer)
            }
        } header: {
            Text("Extra formats")
        } footer: {
            Text("Free software, downloaded only when you click Install: ffmpeg (LGPL/GPL) from [ffmpeg.martin-riedl.de](https://ffmpeg.martin-riedl.de), LibreOffice (MPL) from [libreoffice.org](https://www.libreoffice.org).")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

private struct ExtraToolRow: View {
    let tool: ExtraTool
    let installer: ToolInstaller
    @State private var isConfirmingRemoval = false
    @State private var hasCopiedCommand = false

    var body: some View {
        let status = installer.status(of: tool)
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
            Text(message)
                .foregroundStyle(.red)
        case .installing(let progress):
            Text(progress.detail)
                .monospacedDigit()
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
            HStack(spacing: 10) {
                if info.canRemove {
                    Button("Remove") { isConfirmingRemoval = true }
                }
                Label {
                    Text(info.source == .other ? "Installed" : "Installed · \(info.source.rawValue)")
                } icon: {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                }
                .help([tool.name, info.version].compactMap { $0 }.joined(separator: " ") + " at \(info.path.path)")
            }
        case .installing(let progress):
            HStack(spacing: 8) {
                ProgressView(value: progress.fraction)
                    .progressViewStyle(.linear)
                    .frame(width: 110)
                Button("Cancel") { installer.cancel(tool) }
            }
        case .failed:
            VStack(alignment: .trailing, spacing: 2) {
                Button("Retry") { installer.install(tool) }
                // A way forward when the download server is down.
                Button(hasCopiedCommand ? "Copied" : "Copy Homebrew Command") { copyHomebrewCommand() }
                    .buttonStyle(.link)
                    .font(.caption)
                    .help(tool.homebrewCommand)
            }
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
