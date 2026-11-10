namespace OpenShelf.Vision;

/// <summary>A failure of an OCR, QR or background-removal operation whose message is already fit to show a person.
/// File problems (unreadable, no text, cannot save) are reported as <see cref="OpenShelf.Convert.ConversionException"/>,
/// like the conversion engines do.</summary>
public class VisionException : Exception
{
    public VisionException(string message) : base(message) { }
    public VisionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>No Windows OCR recognition language is installed, so nothing can be read. Show <see cref="Exception.Message"/>.</summary>
public sealed class OcrNoLanguageException : VisionException
{
    public OcrNoLanguageException() : base(
        "No OCR language is installed on this PC. Open Settings > Time & language > Language & region, " +
        "add your language, then choose its options and install the “Optical character recognition” feature. Then try again.")
    {
    }
}

/// <summary>The image has no clear subject to cut out. Not a failure: callers count it as "without a subject".</summary>
public sealed class NoSubjectException : VisionException
{
    public NoSubjectException() : base(
        "No clear subject was found. Remove Background works best on photos of people, animals or objects against a background.")
    {
    }
}

/// <summary>The segmentation model could not be downloaded or did not pass its checksum.</summary>
public sealed class ModelUnavailableException : VisionException
{
    public ModelUnavailableException(string message) : base(message) { }
    public ModelUnavailableException(string message, Exception inner) : base(message, inner) { }
}
