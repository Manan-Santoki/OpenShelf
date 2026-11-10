using System.Globalization;
using System.Text;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

public sealed record MetadataField(string Label, string Value);

public sealed record MetadataSection(string Title, IReadOnlyList<MetadataField> Fields);

/// <summary>Turns a file's EXIF, GPS and other details into readable, grouped fields. Port of MetadataReader.</summary>
public static class MetadataReader
{
    /// <summary>A file's metadata as loaded for reading.</summary>
    public sealed class Details
    {
        public IExifProfile? Exif { get; init; }
        public int XmpBytes { get; init; }
        public int IptcBytes { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string? DensityText { get; init; }
        public string ColorModel { get; init; } = "";
        public string? ColorProfile { get; init; }
        public int Depth { get; init; }
        public Orientation Orientation { get; init; }
        public IReadOnlyList<(string Name, string Value)> Comments { get; init; } = [];
    }

    public static Details Load(string path)
    {
        using var image = new MagickImage(path);
        string? density = null;
        if (image.Density.X > 0)
        {
            var dpi = image.Density.Units == DensityUnit.PixelsPerCentimeter ? image.Density.X * 2.54 : image.Density.X;
            if (image.Density.Units != DensityUnit.Undefined || dpi > 0) density = Trimmed(dpi) + " dpi";
        }
        var icc = image.GetColorProfile();
        var comments = new List<(string, string)>();
        var comment = image.GetAttribute("comment");
        if (!string.IsNullOrWhiteSpace(comment)) comments.Add(("Comment", comment.Trim()));
        return new Details
        {
            Exif = image.GetExifProfile(),
            XmpBytes = image.GetXmpProfile()?.ToByteArray().Length ?? 0,
            IptcBytes = image.GetIptcProfile()?.ToByteArray().Length ?? 0,
            Width = (int)image.Width,
            Height = (int)image.Height,
            DensityText = density,
            ColorModel = image.ColorSpace switch { ColorSpace.CMYK => "CMYK", ColorSpace.Gray => "Gray", _ => "RGB" },
            ColorProfile = icc?.Description,
            Depth = (int)image.Depth,
            Orientation = image.Orientation,
            Comments = comments,
        };
    }

    public static IReadOnlyList<MetadataSection> Sections(string path) => Sections(Load(path));

    public static IReadOnlyList<MetadataSection> Sections(Details details)
    {
        var used = new HashSet<ExifTag>();
        object? Take(ExifTag tag)
        {
            used.Add(tag);
            return Raw(details.Exif, tag);
        }

        var camera = Fields(
            ("Make", Text(Take(ExifTag.Make))),
            ("Model", Text(Take(ExifTag.Model))),
            ("Lens", Text(Take(ExifTag.LensModel))),
            ("Lens Make", Text(Take(ExifTag.LensMake))),
            ("Software", Text(Take(ExifTag.Software))));

        var exposure = Fields(
            ("ISO", Text(Take(ExifTag.ISOSpeedRatings))),
            ("Aperture", Number(Take(ExifTag.FNumber)) is { } f ? "f/" + Trimmed(f) : null),
            ("Shutter", Number(Take(ExifTag.ExposureTime)) is { } s ? ShutterSpeed(s) : null),
            ("Focal Length", Number(Take(ExifTag.FocalLength)) is { } fl ? Trimmed(fl) + " mm" : null),
            ("35 mm Equivalent", Number(Take(ExifTag.FocalLengthIn35mmFilm)) is { } fe ? Trimmed(fe) + " mm" : null),
            ("Exposure Bias", Number(Take(ExifTag.ExposureBiasValue)) is { } eb ? (eb > 0 ? "+" : "") + Trimmed(eb) + " EV" : null),
            ("Flash", Number(Take(ExifTag.Flash)) is { } fh ? ((int)fh & 1) == 1 ? "Fired" : "Did not fire" : null),
            ("White Balance", Number(Take(ExifTag.WhiteBalance)) is { } wb ? wb == 0 ? "Auto" : "Manual" : null));

        var dates = Fields(
            ("Taken", Date(Take(ExifTag.DateTimeOriginal))),
            ("Digitized", Date(Take(ExifTag.DateTimeDigitized))),
            ("Modified", Date(Take(ExifTag.DateTime))));

        var latitude = Coordinate(Take(ExifTag.GPSLatitude));
        var latitudeRef = Text(Take(ExifTag.GPSLatitudeRef)) ?? "";
        var longitude = Coordinate(Take(ExifTag.GPSLongitude));
        var longitudeRef = Text(Take(ExifTag.GPSLongitudeRef)) ?? "";
        var altitude = Number(Take(ExifTag.GPSAltitude));
        used.Add(ExifTag.GPSAltitudeRef);
        var location = Fields(
            ("Latitude", latitude is { } la ? $"{la.ToString("0.000000", CultureInfo.InvariantCulture)}° {latitudeRef}".Trim() : null),
            ("Longitude", longitude is { } lo ? $"{lo.ToString("0.000000", CultureInfo.InvariantCulture)}° {longitudeRef}".Trim() : null),
            ("Altitude", altitude is { } al ? $"{(int)Math.Round(al)} m" : null));

        var image = Fields(
            ("Dimensions", details.Width > 0 ? $"{details.Width} × {details.Height}" : null),
            ("Resolution", details.DensityText),
            ("Colour Model", details.ColorModel),
            ("Colour Profile", details.ColorProfile),
            ("Bit Depth", details.Depth > 0 ? details.Depth.ToString(CultureInfo.InvariantCulture) : null),
            ("Orientation", OrientationName(details.Orientation)));

        // Everything not shown above, with generated labels.
        var other = new List<(string, string?)>();
        if (details.Exif is { } exif)
        {
            var skipped = new HashSet<string> { "Orientation", "ExifOffset", "GPSIFDOffset", "SubIFDOffset", "ThumbnailOffset", "ThumbnailLength", "Compression" };
            foreach (var value in exif.Values.OrderBy(v => v.Tag.ToString(), StringComparer.Ordinal))
            {
                if (used.Any(t => t == value.Tag)) continue;
                var name = value.Tag.ToString();
                if (skipped.Contains(name) || name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
                other.Add((Humanize(name), Text(value.GetValue())));
            }
        }
        foreach (var (name, text) in details.Comments) other.Add((name, text));
        if (details.IptcBytes > 0) other.Add(("IPTC", $"{details.IptcBytes} bytes"));
        if (details.XmpBytes > 0) other.Add(("XMP", $"{details.XmpBytes} bytes"));

        return new[]
        {
            new MetadataSection("Camera", camera),
            new MetadataSection("Exposure", exposure),
            new MetadataSection("Dates", dates),
            new MetadataSection("Location", location),
            new MetadataSection("Image", image),
            new MetadataSection("Other", Fields(other.ToArray())),
        }.Where(s => s.Fields.Count > 0).ToList();
    }

    public static bool HasLocation(string path) => HasLocation(Load(path));

    public static bool HasLocation(Details details) => Raw(details.Exif, ExifTag.GPSLatitude) is not null;

    public static bool HasCameraInfo(string path) => HasCameraInfo(Load(path));

    public static bool HasCameraInfo(Details details) =>
        Raw(details.Exif, ExifTag.Make) is not null || Raw(details.Exif, ExifTag.Model) is not null;

    public static string PlainText(IReadOnlyList<MetadataSection> sections, string fileName)
    {
        var lines = new List<string> { fileName };
        foreach (var section in sections)
        {
            lines.Add("");
            lines.Add(section.Title);
            lines.AddRange(section.Fields.Select(f => $"  {f.Label}: {f.Value}"));
        }
        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- formatting

    internal static object? Raw(IExifProfile? exif, ExifTag tag)
    {
        if (exif is null) return null;
        foreach (var value in exif.Values)
            if (value.Tag == tag) return value.GetValue();
        return null;
    }

    private static IReadOnlyList<MetadataField> Fields(params (string Label, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new MetadataField(p.Label, p.Value!)).ToList();

    internal static double? Number(object? value) => value switch
    {
        null => null,
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        int i => i,
        uint ui => ui,
        long l => l,
        ulong ul => ul,
        float f => f,
        double d => d,
        Number n => (double)n,
        Rational r => r.ToDouble(),
        SignedRational r => r.ToDouble(),
        Array { Length: > 0 } array => Number(array.GetValue(0)),
        _ => null,
    };

    internal static string? Text(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s.Trim('\0', ' ', '\t', '\r', '\n');
            case byte[] bytes: return bytes.Length <= 24 ? string.Join(" ", bytes.Select(b => b.ToString("x2"))) : $"{bytes.Length} bytes";
            case Array array:
                var parts = array.Cast<object?>().Select(Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);
            default:
                var number = Number(value);
                return number is { } n ? Trimmed(n) : value.ToString();
        }
    }

    private static double? Coordinate(object? value)
    {
        if (value is not Array { Length: >= 3 } parts) return Number(value);
        var degrees = Number(parts.GetValue(0));
        var minutes = Number(parts.GetValue(1));
        var seconds = Number(parts.GetValue(2));
        if (degrees is null || minutes is null || seconds is null) return null;
        return degrees + minutes / 60 + seconds / 3600;
    }

    internal static string Trimmed(double value) =>
        Math.Round(value) == value ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string ShutterSpeed(double seconds) =>
        seconds > 0 && seconds < 1 ? $"1/{(int)Math.Round(1 / seconds)} s" : $"{Trimmed(seconds)} s";

    private static string? Date(object? value)
    {
        if (Text(value) is not { } text) return null;
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("d MMM yyyy, h:mm tt", CultureInfo.CurrentCulture)
            : text;
    }

    private static string? OrientationName(Orientation orientation) => orientation switch
    {
        Orientation.TopLeft => "Normal",
        Orientation.TopRight => "Mirrored",
        Orientation.BottomRight => "Rotated 180°",
        Orientation.BottomLeft => "Mirrored vertically",
        Orientation.LeftTop => "Mirrored, rotated 90° left",
        Orientation.RightTop => "Rotated 90° right",
        Orientation.RightBottom => "Mirrored, rotated 90° right",
        Orientation.LeftBottom => "Rotated 90° left",
        _ => null,
    };

    /// <summary>"DateTimeOriginal" becomes "Date Time Original".</summary>
    internal static string Humanize(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(key[i - 1]) || char.IsDigit(key[i - 1]) || (char.IsUpper(key[i - 1]) && i + 1 < key.Length && char.IsLower(key[i + 1])))) result.Append(' ');
            result.Append(c);
        }
        return result.ToString();
    }
}

/// <summary>How the Metadata tools name their copies.</summary>
public static class MetadataSuffix
{
    /// <summary>A copy with the metadata removed: "Photo Clean.jpg".</summary>
    public const string Removed = "Clean";
}

/// <summary>Writes copies of images without their metadata.</summary>
public static class MetadataStripper
{
    /// <summary>EXIF fields any writer adds; they say nothing about the camera, place or person.</summary>
    private static readonly HashSet<string> NeutralExif = new(StringComparer.Ordinal)
    {
        "ExifVersion", "FlashpixVersion", "ColorSpace", "PixelXDimension", "PixelYDimension", "ComponentsConfiguration",
        "Orientation", "XResolution", "YResolution", "ResolutionUnit", "YCbCrPositioning", "Compression", "PhotometricInterpretation",
    };

    /// <summary>Names of identifying metadata present in the file at <paramref name="path"/>: location, camera, dates, descriptions.</summary>
    public static IReadOnlyList<string> LeftoverMetadata(string path)
    {
        var found = new List<string>();
        try
        {
            using var image = new MagickImage(path);
            if (image.GetExifProfile() is { } exif)
            {
                if (exif.Values.Any(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal))) found.Add("location");
                if (exif.Values.Any(v => !v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal) && !NeutralExif.Contains(v.Tag.ToString())))
                    found.Add("camera details");
            }
            if (image.GetIptcProfile() is not null) found.Add("IPTC");
            if (image.GetXmpProfile() is not null) found.Add("XMP");
            if (!string.IsNullOrWhiteSpace(image.GetAttribute("comment"))) found.Add("comment");
        }
        catch (MagickException) { }
        return found;
    }

    /// <summary>Whether the file at <paramref name="path"/> has anything for <see cref="Strip"/> to remove.</summary>
    public static bool HasIdentifyingMetadata(string path) => LeftoverMetadata(path).Count > 0;

    /// <summary>Removes every profile and comment from an in-memory image, keeping the colour profile when asked.
    /// Without it the pixels are converted to sRGB first, so the picture looks the same.</summary>
    public static void Apply(IMagickImage<byte> image, bool keepsColorProfile)
    {
        var profile = image.GetColorProfile();
        if (!keepsColorProfile && profile is not null)
        {
            try { image.TransformColorSpace(profile, ColorProfiles.SRGB); }
            catch (MagickException) { }
            profile = null;
        }
        image.Strip();
        foreach (var name in image.AttributeNames.ToList()) image.RemoveAttribute(name);
        if (keepsColorProfile && profile is not null) image.SetProfile(profile);
        image.Orientation = Orientation.TopLeft;
    }

    /// <summary>Saves "Name Clean.ext" next to the original. The result is checked before it is returned.</summary>
    public static string Strip(string path, bool keepsColorProfile)
    {
        var sourceFormat = ImageFiles.FormatOf(path);
        using var image = ImageFiles.ReadUpright(path);
        Apply(image, keepsColorProfile);
        var family = ImageFiles.Family(sourceFormat);
        var destination = ImageFiles.SaveNext(image, path, MetadataSuffix.Removed, sourceFormat: sourceFormat,
            quality: family == MagickFormat.Jpeg ? 95 : null);
        var leftovers = LeftoverMetadata(destination);
        if (leftovers.Count > 0)
        {
            try { File.Delete(destination); }
            catch (IOException) { }
            throw new SaveException(SaveException.Reason.Failed,
                $"Some details couldn’t be removed from “{Path.GetFileName(path)}” ({string.Join(", ", leftovers)}), so no copy was saved.");
        }
        return destination;
    }

    /// <summary>Saves clean copies of the images that have identifying metadata; the rest are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<string> paths, bool keepsColorProfile, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(paths, new SaveOutcome { SavedLabel = "cleaned", UnchangedLabel = "already clean" },
            path => HasIdentifyingMetadata(path) ? Strip(path, keepsColorProfile) : null, progress, cancellation);
}
