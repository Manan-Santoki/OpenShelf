import SwiftUI

/// Settings → Output: how the Convert wheel encodes video, sound, images and
/// GIFs. Holding ⌥ on a format chooses them for one conversion instead.
struct OutputSettingsPane: View {
    @Bindable private var preferences = Preferences.shared

    var body: some View {
        Form {
            Section {
                VideoOutputControls(video: $preferences.output.video, showsCodec: true)
            } header: {
                Text("Video")
            } footer: {
                footnote("Automatic converts as OpenShelf always has. The codec is for MP4, MOV and MKV; HDR videos stay HEVC to keep their colours.")
            }

            Section {
                AudioBitRatePicker(kbps: $preferences.output.audio.kbps)
            } header: {
                Text("Audio")
            } footer: {
                footnote("Applies to MP3, M4A, OGG, Opus and WMA, and the sound in videos.")
            }

            Section {
                ImageQualityPicker(quality: $preferences.output.image.quality)
                Toggle("Keep metadata", isOn: $preferences.output.image.keepsMetadata)
            } header: {
                Text("Images")
            } footer: {
                footnote("Quality applies to JPEG, HEIC, AVIF and WebP.")
            }

            Section {
                GIFOutputControls(gif: $preferences.output.gif)
            } header: {
                Text("GIFs from Videos")
            } footer: {
                HStack {
                    footnote("Hold ⌥ on a format to choose just once.")
                    Spacer()
                    Button("Reset to Automatic") { preferences.output = .automatic }
                        .disabled(preferences.output == .automatic)
                }
                .padding(.top, 4)
            }
        }
        .settingsPane()
    }

    private func footnote(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
    }
}

// MARK: - Controls

// Shared by Settings → Output and the ⌥ options window (`ConvertOptionsView`).

/// Codec, quality (with a custom bitrate) and largest size.
struct VideoOutputControls: View {
    @Binding var video: OutputSettings.Video
    var showsCodec: Bool

    var body: some View {
        if showsCodec {
            Picker("Codec", selection: $video.codec) {
                ForEach(OutputSettings.Video.Codec.allCases) { Text($0.title).tag($0) }
            }
        }
        Picker("Quality", selection: $video.quality) {
            ForEach(OutputSettings.Video.Quality.allCases) { Text($0.title).tag($0) }
        }
        if video.quality == .custom {
            LabeledContent("Bitrate") {
                MbpsField(mbps: $video.customMbps)
            }
        }
        Picker("Largest size", selection: $video.maxSize) {
            ForEach(OutputSettings.Video.MaxSize.allCases) { Text($0.title).tag($0) }
        }
    }
}

/// A custom video bitrate in Mb/s, kept within what's sensible.
struct MbpsField: View {
    @Binding var mbps: Double

    var body: some View {
        HStack(spacing: 4) {
            TextField("Bitrate", value: Binding(
                get: { mbps },
                set: { mbps = OutputSettings.Video.clampedMbps($0) }
            ), format: .number.precision(.fractionLength(0...1)))
                .labelsHidden()
                .multilineTextAlignment(.trailing)
                .frame(width: 56)
                .accessibilityLabel("Bitrate in megabits per second")
            Text("Mb/s")
                .foregroundStyle(.secondary)
        }
    }
}

struct AudioBitRatePicker: View {
    @Binding var kbps: Int?
    var title = "Bitrate"

    var body: some View {
        Picker(title, selection: $kbps) {
            Text("Automatic").tag(Int?.none)
            ForEach(OutputSettings.Audio.choices, id: \.self) { Text("\($0) kb/s").tag(Int?.some($0)) }
        }
    }
}

struct ImageQualityPicker: View {
    @Binding var quality: Double?

    var body: some View {
        Picker("Quality", selection: $quality) {
            Text("Automatic").tag(Double?.none)
            ForEach(OutputSettings.Image.qualityChoices, id: \.self) { Text("\(Int(($0 * 100).rounded()))%").tag(Double?.some($0)) }
        }
    }
}

/// Width and frame rate.
struct GIFOutputControls: View {
    @Binding var gif: OutputSettings.GIF

    var body: some View {
        Picker("Width", selection: $gif.width) {
            ForEach(OutputSettings.GIF.Width.allCases) { Text($0.title).tag($0) }
        }
        Picker("Frame rate", selection: $gif.frameRate) {
            ForEach(OutputSettings.GIF.frameRates, id: \.self) { Text("\($0) fps").tag($0) }
        }
    }
}
