import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct MetadataField: Hashable {
    let label: String
    let value: String
}

struct MetadataSection: Identifiable {
    let title: String
    let fields: [MetadataField]

    var id: String { title }
}

/// Turns ImageIO properties into readable, grouped fields.
enum MetadataReader {
    static func sections(for properties: [CFString: Any]) -> [MetadataSection] {
        let exif = dictionary(kCGImagePropertyExifDictionary, in: properties)
        let exifAux = dictionary(kCGImagePropertyExifAuxDictionary, in: properties)
        let tiff = dictionary(kCGImagePropertyTIFFDictionary, in: properties)
        let gps = dictionary(kCGImagePropertyGPSDictionary, in: properties)
        let iptc = dictionary(kCGImagePropertyIPTCDictionary, in: properties)

        var used: [CFString: Set<CFString>] = [:]
        func take(_ key: CFString, from dictionary: [CFString: Any], named name: CFString) -> Any? {
            used[name, default: []].insert(key)
            return dictionary[key]
        }

        let camera = fields([
            ("Make", text(take(kCGImagePropertyTIFFMake, from: tiff, named: kCGImagePropertyTIFFDictionary))),
            ("Model", text(take(kCGImagePropertyTIFFModel, from: tiff, named: kCGImagePropertyTIFFDictionary))),
            ("Lens", text(take(kCGImagePropertyExifLensModel, from: exif, named: kCGImagePropertyExifDictionary)
                ?? take(kCGImagePropertyExifAuxLensModel, from: exifAux, named: kCGImagePropertyExifAuxDictionary))),
            ("Lens Make", text(take(kCGImagePropertyExifLensMake, from: exif, named: kCGImagePropertyExifDictionary))),
            ("Software", text(take(kCGImagePropertyTIFFSoftware, from: tiff, named: kCGImagePropertyTIFFDictionary))),
        ])

        let exposure = fields([
            ("ISO", (take(kCGImagePropertyExifISOSpeedRatings, from: exif, named: kCGImagePropertyExifDictionary) as? [Any])?.first.flatMap(text)),
            ("Aperture", number(take(kCGImagePropertyExifFNumber, from: exif, named: kCGImagePropertyExifDictionary)).map { "f/\(trimmed($0))" }),
            ("Shutter", number(take(kCGImagePropertyExifExposureTime, from: exif, named: kCGImagePropertyExifDictionary)).map(shutterSpeed)),
            ("Focal Length", number(take(kCGImagePropertyExifFocalLength, from: exif, named: kCGImagePropertyExifDictionary)).map { "\(trimmed($0)) mm" }),
            ("35 mm Equivalent", number(take(kCGImagePropertyExifFocalLenIn35mmFilm, from: exif, named: kCGImagePropertyExifDictionary)).map { "\(trimmed($0)) mm" }),
            ("Exposure Bias", number(take(kCGImagePropertyExifExposureBiasValue, from: exif, named: kCGImagePropertyExifDictionary)).map { "\($0 > 0 ? "+" : "")\(trimmed($0)) EV" }),
            ("Flash", number(take(kCGImagePropertyExifFlash, from: exif, named: kCGImagePropertyExifDictionary)).map { Int($0) & 1 == 1 ? "Fired" : "Did not fire" }),
            ("White Balance", number(take(kCGImagePropertyExifWhiteBalance, from: exif, named: kCGImagePropertyExifDictionary)).map { $0 == 0 ? "Auto" : "Manual" }),
        ])

        let dates = fields([
            ("Taken", date(take(kCGImagePropertyExifDateTimeOriginal, from: exif, named: kCGImagePropertyExifDictionary))),
            ("Digitized", date(take(kCGImagePropertyExifDateTimeDigitized, from: exif, named: kCGImagePropertyExifDictionary))),
            ("Modified", date(take(kCGImagePropertyTIFFDateTime, from: tiff, named: kCGImagePropertyTIFFDictionary))),
        ])

        let latitude = number(take(kCGImagePropertyGPSLatitude, from: gps, named: kCGImagePropertyGPSDictionary))
        let latitudeRef = text(take(kCGImagePropertyGPSLatitudeRef, from: gps, named: kCGImagePropertyGPSDictionary)) ?? ""
        let longitude = number(take(kCGImagePropertyGPSLongitude, from: gps, named: kCGImagePropertyGPSDictionary))
        let longitudeRef = text(take(kCGImagePropertyGPSLongitudeRef, from: gps, named: kCGImagePropertyGPSDictionary)) ?? ""
        let location = fields([
            ("Latitude", latitude.map { String(format: "%.6f° %@", $0, latitudeRef) }),
            ("Longitude", longitude.map { String(format: "%.6f° %@", $0, longitudeRef) }),
            ("Altitude", number(take(kCGImagePropertyGPSAltitude, from: gps, named: kCGImagePropertyGPSDictionary)).map { "\(Int($0.rounded())) m" }),
        ])

        var imageFields: [(String, String?)] = []
        if let width = properties[kCGImagePropertyPixelWidth], let height = properties[kCGImagePropertyPixelHeight] {
            imageFields.append(("Dimensions", "\(text(width) ?? "?") × \(text(height) ?? "?")"))
        }
        imageFields += [
            ("Resolution", number(properties[kCGImagePropertyDPIWidth]).map { "\(trimmed($0)) dpi" }),
            ("Colour Model", text(properties[kCGImagePropertyColorModel])),
            ("Colour Profile", text(properties[kCGImagePropertyProfileName])),
            ("Bit Depth", text(properties[kCGImagePropertyDepth])),
            ("Orientation", number(properties[kCGImagePropertyOrientation]).map { orientationName(Int($0)) }),
        ]
        let topLevelUsed: Set<CFString> = [
            kCGImagePropertyPixelWidth, kCGImagePropertyPixelHeight, kCGImagePropertyDPIWidth, kCGImagePropertyDPIHeight,
            kCGImagePropertyColorModel, kCGImagePropertyProfileName, kCGImagePropertyDepth, kCGImagePropertyOrientation,
        ]

        // Everything not shown above, with generated labels.
        var other: [(String, String?)] = []
        for (key, value) in properties.sorted(by: { ($0.key as String) < ($1.key as String) })
        where !(value is [CFString: Any]) && !topLevelUsed.contains(key) {
            other.append((humanize(key as String), text(value)))
        }
        for (name, dictionary) in [
            (kCGImagePropertyExifDictionary, exif), (kCGImagePropertyExifAuxDictionary, exifAux),
            (kCGImagePropertyTIFFDictionary, tiff), (kCGImagePropertyGPSDictionary, gps), (kCGImagePropertyIPTCDictionary, iptc),
        ] {
            for (key, value) in dictionary.sorted(by: { ($0.key as String) < ($1.key as String) })
            where !(used[name]?.contains(key) ?? false) && !(name == kCGImagePropertyTIFFDictionary && key == kCGImagePropertyTIFFOrientation) {
                other.append((humanize(key as String), text(value)))
            }
        }

        return [
            MetadataSection(title: "Camera", fields: camera),
            MetadataSection(title: "Exposure", fields: exposure),
            MetadataSection(title: "Dates", fields: dates),
            MetadataSection(title: "Location", fields: location),
            MetadataSection(title: "Image", fields: fields(imageFields)),
            MetadataSection(title: "Other", fields: fields(other)),
        ].filter { !$0.fields.isEmpty }
    }

    static func hasLocation(_ properties: [CFString: Any]) -> Bool {
        dictionary(kCGImagePropertyGPSDictionary, in: properties)[kCGImagePropertyGPSLatitude] != nil
    }

    static func hasCameraInfo(_ properties: [CFString: Any]) -> Bool {
        let tiff = dictionary(kCGImagePropertyTIFFDictionary, in: properties)
        return tiff[kCGImagePropertyTIFFMake] != nil || tiff[kCGImagePropertyTIFFModel] != nil
    }

    static func plainText(_ sections: [MetadataSection], fileName: String) -> String {
        var lines = [fileName]
        for section in sections {
            lines.append("")
            lines.append(section.title)
            lines += section.fields.map { "  \($0.label): \($0.value)" }
        }
        return lines.joined(separator: "\n")
    }

    // MARK: Formatting

    private static func dictionary(_ key: CFString, in properties: [CFString: Any]) -> [CFString: Any] {
        properties[key] as? [CFString: Any] ?? [:]
    }

    private static func fields(_ pairs: [(String, String?)]) -> [MetadataField] {
        pairs.compactMap { label, value in
            guard let value, !value.isEmpty else { return nil }
            return MetadataField(label: label, value: value)
        }
    }

    private static func number(_ value: Any?) -> Double? {
        (value as? NSNumber)?.doubleValue
    }

    private static func text(_ value: Any?) -> String? {
        switch value {
        case let string as String:
            return string.trimmingCharacters(in: .whitespacesAndNewlines)
        case let number as NSNumber:
            return trimmed(number.doubleValue)
        case let array as [Any]:
            let parts = array.compactMap(text)
            return parts.isEmpty ? nil : parts.joined(separator: ", ")
        default:
            return nil
        }
    }

    private static func trimmed(_ value: Double) -> String {
        value.rounded() == value ? String(Int(value)) : String(format: "%.4g", value)
    }

    private static func shutterSpeed(_ seconds: Double) -> String {
        guard seconds > 0, seconds < 1 else { return "\(trimmed(seconds)) s" }
        return "1/\(Int((1 / seconds).rounded())) s"
    }

    private static func date(_ value: Any?) -> String? {
        guard let string = value as? String else { return nil }
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX")
        parser.dateFormat = "yyyy:MM:dd HH:mm:ss"
        guard let date = parser.date(from: string) else { return string }
        return date.formatted(date: .abbreviated, time: .shortened)
    }

    private static func orientationName(_ value: Int) -> String {
        switch value {
        case 1: "Normal"
        case 2: "Mirrored"
        case 3: "Rotated 180°"
        case 4: "Mirrored vertically"
        case 5: "Mirrored, rotated 90° left"
        case 6: "Rotated 90° right"
        case 7: "Mirrored, rotated 90° right"
        case 8: "Rotated 90° left"
        default: "\(value)"
        }
    }

    /// "{Exif}" → "Exif", "DateTimeOriginal" → "Date Time Original".
    private static func humanize(_ key: String) -> String {
        let bare = key.trimmingCharacters(in: CharacterSet(charactersIn: "{}"))
        var result = ""
        var previous: Character?
        for character in bare {
            if let previous, character.isUppercase, previous.isLowercase || previous.isNumber {
                result.append(" ")
            }
            result.append(character)
            previous = character
        }
        return result
    }
}

/// How the Metadata tools name their copies, for images and PDFs alike.
enum MetadataSuffix {
    /// A copy with the metadata removed: "Photo Clean.jpg", "Report Clean.pdf".
    static let removed = "Clean"
    /// A PDF with edited document info: "Report Metadata.pdf". Distinct from the
    /// Pages editor's "Edited" so the two copies can't be confused.
    static let edited = "Metadata"
}

/// Writes copies of images without their metadata.
enum MetadataStripper {
    /// Formats whose metadata ImageIO can't reliably drop without re-encoding
    /// (GPS survives a lossless copy of HEIC, for example).
    static let reencodedTypes: Set<String> = ["public.heic", "public.heics", "public.heif", "public.avif"]

    /// Saves "<name> Clean.<ext>" next to the original.
    ///
    /// When orientation and colour profile are kept, the image data is copied
    /// as-is (no re-encoding) with only the orientation tag, where the format
    /// allows. Otherwise, or if anything sensitive survives the copy, the
    /// upright pixels are re-encoded with no metadata at all (in sRGB unless the
    /// profile is kept). The result is always checked before it's returned.
    static func strip(_ image: EditableImage, keepsOrientationAndProfile: Bool) throws -> URL {
        if keepsOrientationAndProfile, !reencodedTypes.contains(image.type.identifier),
           let url = losslessCopy(of: image) {
            if leftoverMetadata(at: url).isEmpty { return url }
            try? FileManager.default.removeItem(at: url)
        }
        let type = EditableImage.writableTypes.contains(image.type) ? image.type : .png
        let url = OutputNaming.destination(for: image.url, suffix: MetadataSuffix.removed, pathExtension: image.pathExtension(for: type))
        var pixels = image.cgImage
        if !keepsOrientationAndProfile {
            pixels = sRGB(pixels) ?? pixels
        }
        try EditableImage.write(pixels, to: url, type: type, quality: 0.95, metadata: [:])
        let leftovers = leftoverMetadata(at: url)
        guard leftovers.isEmpty else {
            try? FileManager.default.removeItem(at: url)
            throw StripError.couldNotRemove(file: image.url.lastPathComponent, fields: leftovers)
        }
        return url
    }

    /// Whether the file at `url` has anything for `strip` to remove.
    static func hasIdentifyingMetadata(at url: URL) -> Bool {
        !leftoverMetadata(at: url).isEmpty
    }

    /// Saves clean copies of the images that have identifying metadata; the
    /// rest are left alone and counted, since a copy would be identical.
    static func run(_ images: [EditableImage], keepsOrientationAndProfile: Bool) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "cleaned", unchangedLabel: "already clean")
        for image in images {
            guard hasIdentifyingMetadata(at: image.url) else {
                outcome.unchanged += 1
                continue
            }
            outcome.saved.append(try strip(image, keepsOrientationAndProfile: keepsOrientationAndProfile))
        }
        return outcome
    }

    enum StripError: LocalizedError {
        case couldNotRemove(file: String, fields: [String])

        var errorDescription: String? {
            switch self {
            case .couldNotRemove(let file, let fields):
                "Some details couldn’t be removed from “\(file)” (\(fields.joined(separator: ", "))), so no copy was saved."
            }
        }
    }

    /// EXIF fields ImageIO writes for any image; they say nothing about the
    /// camera, place or person.
    private static let neutralExifKeys: Set<String> = [
        kCGImagePropertyExifVersion, kCGImagePropertyExifFlashPixVersion, kCGImagePropertyExifColorSpace,
        kCGImagePropertyExifPixelXDimension, kCGImagePropertyExifPixelYDimension, kCGImagePropertyExifComponentsConfiguration,
    ].reduce(into: []) { $0.insert($1 as String) }
    private static let neutralTIFFKeys: Set<String> = [
        kCGImagePropertyTIFFOrientation, kCGImagePropertyTIFFXResolution, kCGImagePropertyTIFFYResolution,
        kCGImagePropertyTIFFResolutionUnit, kCGImagePropertyTIFFCompression, kCGImagePropertyTIFFPhotometricInterpretation,
        kCGImagePropertyTIFFTileWidth, kCGImagePropertyTIFFTileLength,
    ].reduce(into: []) { $0.insert($1 as String) }
    private static let identifyingDictionaries: [String] = [
        kCGImagePropertyMakerAppleDictionary, kCGImagePropertyMakerCanonDictionary, kCGImagePropertyMakerNikonDictionary,
        kCGImagePropertyMakerMinoltaDictionary, kCGImagePropertyMakerFujiDictionary, kCGImagePropertyMakerOlympusDictionary,
        kCGImagePropertyMakerPentaxDictionary, kCGImagePropertyIPTCDictionary, kCGImagePropertyExifAuxDictionary,
        kCGImagePropertyGPSDictionary,
    ].map { $0 as String }

    /// Names of identifying metadata still present in the file at `url`:
    /// location, camera, dates, maker notes, IPTC or XMP descriptions.
    static func leftoverMetadata(at url: URL) -> [String] {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [String: Any]
        else { return [] }
        var found: [String] = []
        for key in identifyingDictionaries {
            if let dictionary = properties[key] as? [String: Any], !dictionary.isEmpty {
                found.append(key == kCGImagePropertyGPSDictionary as String ? "location" : key)
            }
        }
        if let exif = properties[kCGImagePropertyExifDictionary as String] as? [String: Any],
           exif.keys.contains(where: { !neutralExifKeys.contains($0) }) {
            found.append("camera details")
        }
        if let tiff = properties[kCGImagePropertyTIFFDictionary as String] as? [String: Any],
           tiff.keys.contains(where: { !neutralTIFFKeys.contains($0) }) {
            found.append("camera details")
        }
        // XMP can repeat any of the above; only the EXIF/TIFF basics are allowed.
        if let metadata = CGImageSourceCopyMetadataAtIndex(source, 0, nil),
           let tags = CGImageMetadataCopyTags(metadata) as? [CGImageMetadataTag] {
            let allowed: Set<String> = ["exif", "tiff"]
            for tag in tags {
                let prefix = CGImageMetadataTagCopyPrefix(tag) as String? ?? ""
                let name = CGImageMetadataTagCopyName(tag) as String? ?? ""
                if !allowed.contains(prefix) || name.hasPrefix("GPS") {
                    found.append("XMP \(prefix):\(name)")
                }
            }
        }
        var unique: [String] = []
        for item in found where !unique.contains(item) { unique.append(item) }
        return unique
    }

    private static func losslessCopy(of image: EditableImage) -> URL? {
        guard let source = CGImageSourceCreateWithURL(image.url as CFURL, nil),
              let typeIdentifier = CGImageSourceGetType(source)
        else { return nil }
        let url = OutputNaming.destination(for: image.url, suffix: MetadataSuffix.removed, pathExtension: image.pathExtension(for: image.type))
        guard (try? SaveError.checkWritable(url)) != nil,
              let destination = CGImageDestinationCreateWithURL(url as CFURL, typeIdentifier, 1, nil)
        else { return nil }

        let metadata = CGImageMetadataCreateMutable()
        if let orientation = image.properties[kCGImagePropertyOrientation] as? Int, orientation != 1 {
            CGImageMetadataSetValueMatchingImageProperty(
                metadata, kCGImagePropertyTIFFDictionary, kCGImagePropertyTIFFOrientation, orientation as CFNumber
            )
        }
        let options: [CFString: Any] = [
            kCGImageDestinationMetadata: metadata,
            kCGImageDestinationMergeMetadata: false,
            kCGImageMetadataShouldExcludeXMP: true,
            kCGImageMetadataShouldExcludeGPS: true,
        ]
        guard CGImageDestinationCopyImageSource(destination, source, options as CFDictionary, nil) else {
            try? FileManager.default.removeItem(at: url)
            return nil
        }
        return url
    }

    private static func sRGB(_ image: CGImage) -> CGImage? {
        guard let context = CGContext(
            data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return context.makeImage()
    }
}

/// The "Metadata" tool: inspect an image's metadata and save clean copies.
struct MetadataEditor: View {
    static let windowSize = CGSize(width: 460, height: 620)

    let images: [EditableImage]
    let actions: EditorActions

    @State private var keepsOrientationAndProfile = true
    @State private var isWorking = false
    @State private var didCopy = false
    /// How many of the images have camera, date, location or other details to remove.
    private let identifiableCount: Int

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        identifiableCount = images.filter { MetadataStripper.hasIdentifyingMetadata(at: $0.url) }.count
    }

    private var explanation: String {
        if identifiableCount == 0 {
            return images.count == 1
                ? "No camera, date, location or other details to remove."
                : "None of these have camera, date, location or other details to remove."
        }
        let clean = images.count - identifiableCount
        if clean == 0 {
            return "Saves a copy without camera, date, location and other details."
        }
        return "Saves clean copies of \(identifiableCount) of \(images.count); \(clean) \(clean == 1 ? "is" : "are") already clean."
    }

    var body: some View {
        EditorChrome(
            title: "Metadata",
            primaryTitle: isWorking ? "Removing…" : "Remove Metadata",
            isPrimaryEnabled: !isWorking && identifiableCount > 0,
            actions: actions,
            onPrimary: removeMetadata
        ) {
            VStack(alignment: .leading, spacing: 12) {
                if images.count == 1 {
                    SingleImageMetadata(image: images[0], didCopy: $didCopy)
                } else {
                    BatchMetadataSummary(images: images)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Toggle("Keep orientation and colour profile", isOn: $keepsOrientationAndProfile)
                        .font(.system(size: 12))
                    Text(explanation)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private func removeMetadata() {
        isWorking = true
        let images = images
        let keeps = keepsOrientationAndProfile
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result { try MetadataStripper.run(images, keepsOrientationAndProfile: keeps) }
            }.value
            isWorking = false
            switch result {
            case .success(let outcome): actions.finished(outcome)
            case .failure(let error): actions.failed(error)
            }
        }
    }
}

private struct SingleImageMetadata: View {
    let image: EditableImage
    @Binding var didCopy: Bool

    private var sections: [MetadataSection] { MetadataReader.sections(for: image.properties) }

    var body: some View {
        let sections = sections
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(decorative: image.cgImage, scale: 1)
                    .resizable()
                    .scaledToFill()
                    .frame(width: 52, height: 52)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text(image.url.lastPathComponent)
                        .font(.system(size: 13, weight: .semibold))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    HStack(spacing: 6) {
                        Text("\(image.cgImage.width) × \(image.cgImage.height)")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                        if MetadataReader.hasLocation(image.properties) {
                            LocationChip()
                        }
                    }
                }
                Spacer(minLength: 0)
                Button(didCopy ? "Copied" : "Copy All") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(
                        MetadataReader.plainText(sections, fileName: image.url.lastPathComponent),
                        forType: .string
                    )
                    didCopy = true
                }
                .controlSize(.small)
                .disabled(sections.isEmpty)
            }

            if sections.isEmpty {
                Text("No metadata found.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 14) {
                        ForEach(sections) { section in
                            SectionView(section: section)
                        }
                    }
                    .padding(.vertical, 2)
                }
                .frame(maxHeight: .infinity)
            }
        }
    }
}

private struct SectionView: View {
    let section: MetadataSection

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(section.title.uppercased())
                .font(.system(size: 10, weight: .semibold))
                .tracking(0.5)
                .foregroundStyle(.secondary)
            VStack(spacing: 0) {
                ForEach(Array(section.fields.enumerated()), id: \.element) { index, field in
                    if index > 0 {
                        Divider().opacity(0.5)
                    }
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Text(field.label)
                            .foregroundStyle(.secondary)
                            .frame(width: 120, alignment: .leading)
                        Text(field.value)
                            .frame(maxWidth: .infinity, alignment: .trailing)
                            .multilineTextAlignment(.trailing)
                            .textSelection(.enabled)
                    }
                    .font(.system(size: 12))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .accessibilityElement(children: .combine)
                }
            }
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
        }
    }
}

private struct BatchMetadataSummary: View {
    let images: [EditableImage]

    var body: some View {
        let withLocation = images.filter { MetadataReader.hasLocation($0.properties) }.count
        VStack(alignment: .leading, spacing: 10) {
            Text("\(images.count) images · \(withLocation == 0 ? "none have" : withLocation == 1 ? "1 has" : "\(withLocation) have") location data")
                .font(.system(size: 13, weight: .semibold))
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(Array(images.enumerated()), id: \.offset) { index, image in
                        if index > 0 {
                            Divider().opacity(0.5)
                        }
                        HStack(spacing: 10) {
                            Image(decorative: image.cgImage, scale: 1)
                                .resizable()
                                .scaledToFill()
                                .frame(width: 28, height: 28)
                                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                            Text(image.url.lastPathComponent)
                                .font(.system(size: 12))
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Spacer(minLength: 0)
                            if MetadataReader.hasCameraInfo(image.properties) {
                                Chip(title: "Camera", systemImage: "camera", tint: .secondary)
                            }
                            if MetadataReader.hasLocation(image.properties) {
                                LocationChip()
                            }
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                    }
                }
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
            }
            .frame(maxHeight: .infinity)
        }
    }
}

private struct LocationChip: View {
    var body: some View {
        Chip(title: "Has location", systemImage: "location.fill", tint: .orange)
    }
}

private struct Chip: View {
    let title: String
    let systemImage: String
    let tint: Color

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(tint)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(tint.opacity(0.15)))
    }
}
