using System.Runtime.InteropServices;
using System.Windows;
using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>
/// The one-click "Read QR" tool (SPEC §2.4): decodes the QR (and Aztec, Data Matrix, PDF417) codes in the images, copies the text to
/// the clipboard, and says what happened in a toast with the Vision messages: "Copied QR code · https://…", "No QR code found",
/// "Couldn’t read QR code · reason".
/// </summary>
public static class QrTool
{
    /// <summary>Longest payload shown in the toast; the clipboard always gets all of it.</summary>
    public const int ToastPayloadLimit = 70;

    /// <summary>Reads the codes in <paramref name="paths"/>, copies them with <paramref name="copy"/> and toasts the result.</summary>
    /// <param name="read">Replaces the decoder (for tests).</param>
    /// <param name="copy">Replaces the clipboard (for tests). Throws when the clipboard cannot be written.</param>
    public static async Task RunAsync(IReadOnlyList<string> paths, ScreenPoint near, IToastService toasts,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? read = null, Action<string>? copy = null)
    {
        read ??= list => QrReader.ReadAsync(list);
        copy ??= CopyToClipboard;
        IReadOnlyList<string> codes;
        try
        {
            codes = await read(paths);
        }
        catch (Exception e) when (e is ConversionException or VisionException or IOException or UnauthorizedAccessException)
        {
            toasts.Show(QrReader.FailedMessage + " · " + e.Message, near);
            return;
        }
        toasts.Show(Describe(codes, copy), near);
    }

    /// <summary>Copies <paramref name="codes"/> and returns the toast text.</summary>
    internal static string Describe(IReadOnlyList<string> codes, Action<string> copy)
    {
        if (codes.Count == 0) return QrReader.NothingFoundMessage;
        try
        {
            copy(QrReader.ClipboardText(codes));
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException)
        {
            return QrReader.FailedMessage + " · the clipboard is in use by another app. Try again.";
        }
        return QrReader.CopiedMessage(codes.Count) + " · " + Shorten(codes[0]);
    }

    /// <summary>The first line of a payload, cut to fit a toast.</summary>
    internal static string Shorten(string payload)
    {
        var line = payload.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        if (line.Length == 0) line = payload.Trim();
        return line.Length <= ToastPayloadLimit ? line : line[..(ToastPayloadLimit - 1)].TrimEnd() + "…";
    }

    /// <summary>Puts text on the clipboard, trying again for a moment when another app has it open.</summary>
    internal static void CopyToClipboard(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(40);
            }
        }
    }
}
