import CoreGraphics
import SwiftUI

/// The files an editor was given, as a row of thumbnails under its canvas:
/// click one (or use the arrows) to work on it. A dot marks files with an edit
/// waiting to be saved. With `sameForAll`, it also offers applying one edit to
/// every file instead of editing each on its own.
struct EditorFilmstrip: View {
    /// A picture of each file; nil shows `placeholder`, for files without one
    /// (sound) or whose picture is still loading.
    let thumbnails: [CGImage?]
    let names: [String]
    @Binding var selection: Int
    /// Whether each file has an edit waiting to be saved.
    let edited: [Bool]
    var sameForAll: Binding<Bool>?
    /// The SF Symbol shown for a file without a picture.
    var placeholder = "waveform"
    /// What the files are called in the Same for All help, e.g. "video".
    var itemName = "image"

    static let height: CGFloat = 104

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Text("\(selection + 1) of \(names.count)")
                    .font(.system(size: 11, weight: .semibold).monospacedDigit())
                Text(names[selection])
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 8)
                if let sameForAll {
                    Toggle("Same for all", isOn: sameForAll)
                        .toggleStyle(.switch)
                        .controlSize(.mini)
                        .font(.system(size: 11))
                        .help("On: one edit applies to every \(itemName). Off: edit each \(itemName) on its own.")
                }
            }

            HStack(spacing: 6) {
                stepButton("chevron.left", label: "Previous", to: selection - 1)
                ScrollViewReader { proxy in
                    ScrollView(.horizontal, showsIndicators: false) {
                        LazyHStack(spacing: 6) {
                            ForEach(thumbnails.indices, id: \.self) { index in
                                thumbnail(index)
                                    .id(index)
                            }
                        }
                        .padding(.vertical, 2)
                    }
                    .onChange(of: selection) { _, newValue in
                        withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(newValue, anchor: .center) }
                    }
                }
                stepButton("chevron.right", label: "Next", to: selection + 1)
            }
            .frame(height: 56)
        }
        .frame(height: Self.height - 24, alignment: .top)
    }

    private func thumbnail(_ index: Int) -> some View {
        let isSelected = index == selection
        return Button {
            selection = index
        } label: {
            picture(thumbnails[index])
                .frame(width: 52, height: 52)
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.15), lineWidth: isSelected ? 2 : 1)
                }
                .overlay(alignment: .topTrailing) {
                    if edited[index] {
                        Circle()
                            .fill(Color.accentColor)
                            .overlay(Circle().strokeBorder(.white.opacity(0.9), lineWidth: 1))
                            .frame(width: 9, height: 9)
                            .offset(x: 3, y: -3)
                    }
                }
                .padding(3)
        }
        .buttonStyle(.plain)
        .help(names[index])
        .accessibilityLabel(names[index])
        .accessibilityValue(edited[index] ? "Edited" : "")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    @ViewBuilder
    private func picture(_ image: CGImage?) -> some View {
        if let image {
            Image(decorative: image, scale: 1)
                .resizable()
                .aspectRatio(contentMode: .fill)
        } else {
            Image(systemName: placeholder)
                .font(.system(size: 18, weight: .light))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.primary.opacity(0.07))
        }
    }

    private func stepButton(_ systemImage: String, label: String, to index: Int) -> some View {
        Button {
            selection = index
        } label: {
            Image(systemName: systemImage)
                .font(.system(size: 11, weight: .semibold))
                .frame(width: 22, height: 52)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(.secondary)
        .disabled(!thumbnails.indices.contains(index))
        .help(label)
        .accessibilityLabel(label)
    }
}

extension EditableImage {
    /// A small copy for a filmstrip, with its long edge at most `maxPixelSize`.
    func thumbnail(maxPixelSize: Int = 160) -> CGImage {
        let scale = min(1, CGFloat(maxPixelSize) / CGFloat(max(cgImage.width, cgImage.height)))
        guard scale < 1 else { return cgImage }
        let width = max(1, Int(CGFloat(cgImage.width) * scale))
        let height = max(1, Int(CGFloat(cgImage.height) * scale))
        guard let context = CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return cgImage }
        context.interpolationQuality = .high
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage() ?? cgImage
    }
}
