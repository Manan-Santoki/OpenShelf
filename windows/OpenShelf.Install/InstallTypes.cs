using System.IO;
namespace OpenShelf.Install;

public enum InstallFailureKind
{
    /// <summary>Every download source was down: winget may still work.</summary>
    SourcesUnavailable,
    /// <summary>A checksum or signature did not match. Never retried from another source.</summary>
    Verification,
    /// <summary>The download could not be unpacked or did not contain the program.</summary>
    Unpack,
    /// <summary>winget itself failed.</summary>
    Winget,
    Other,
}

/// <summary>An install or removal failed; <see cref="Exception.Message"/> is fit to show a person.</summary>
public sealed class ToolInstallException(string message, InstallFailureKind kind, string? detail = null, Exception? inner = null)
    : Exception(message, inner)
{
    public InstallFailureKind Kind { get; } = kind;
    /// <summary>More about it, for a tooltip: what the last server answered.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>A source could not be reached or answered with an error, so the next one may work.</summary>
public sealed class SourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What OpenShelf checked about an installed copy, written next to it as OpenShelf-install.json.</summary>
public sealed record InstallRecord(
    string Tool,
    string Version,
    string Source,
    string Url,
    string Sha256,
    bool ChecksumVerified,
    string Signature,
    DateTimeOffset InstalledAt);

public enum SignatureState { Valid, Unsigned, Invalid }

/// <summary>The outcome of an Authenticode check.</summary>
public sealed record SignatureResult(SignatureState State, string? Signer = null, string? Detail = null);

public interface ISignatureVerifier
{
    SignatureResult Verify(string path);
}

/// <summary>How strictly a tool's main program must be signed.</summary>
public enum SignaturePolicy
{
    /// <summary>The vendor does not sign (ffmpeg builds, Pandoc): the checksum is the check.</summary>
    None,
    /// <summary>A signature, when present, must be valid; a missing one is recorded. For vendors that sign.</summary>
    IfSigned,
    /// <summary>Must be signed and valid.</summary>
    Required,
}
