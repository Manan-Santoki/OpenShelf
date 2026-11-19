using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenShelf.Vision;

public enum OcrPackResult { Installed, Cancelled, NotAvailable, Failed }

/// <summary>Adds a Windows OCR language (the "Optical character recognition" optional feature of a language) so Read Text works
/// without OpenShelf shipping a recognizer of its own. Windows 10 and 11 both carry these as capabilities named
/// <c>Language.OCR~~~&lt;tag&gt;~0.0.1.0</c>; adding one needs administrator rights, so Windows asks with its usual prompt.</summary>
public static class OcrLanguagePack
{
    private static readonly Regex SafeTag = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$");

    /// <summary>The person's first Windows display language ("en-US"), the one Read Text would use.</summary>
    public static string PreferredTag()
    {
        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (languages.Count > 0 && SafeTag.IsMatch(languages[0])) return languages[0];
        }
        catch (Exception) { }
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        return SafeTag.IsMatch(culture) ? culture : "en-US";
    }

    /// <summary>"English (United States)".</summary>
    public static string DisplayName(string tag)
    {
        try { return new Windows.Globalization.Language(tag).DisplayName; }
        catch (Exception) { return tag; }
    }

    /// <summary>The PowerShell that adds the OCR feature for <paramref name="tag"/>, or for another region of the same language when
    /// that exact one has none (en-IN gets en-US, say). Exit code 0 installed, 3 no such feature, anything else failed.</summary>
    public static string Script(string tag)
    {
        if (!SafeTag.IsMatch(tag)) throw new ArgumentException("Not a language tag.", nameof(tag));
        var language = tag.Split('-')[0];
        return "$ErrorActionPreference='Stop'; " +
               "$all = Get-WindowsCapability -Online | Where-Object { $_.Name -like 'Language.OCR~~~*' }; " +
               $"$c = $all | Where-Object {{ $_.Name -like 'Language.OCR~~~{tag}~*' }} | Select-Object -First 1; " +
               $"if (-not $c) {{ $c = $all | Where-Object {{ $_.Name -like 'Language.OCR~~~{language}-*' }} | Sort-Object Name | Select-Object -First 1 }}; " +
               "if (-not $c) { exit 3 }; " +
               "if ($c.State -ne 'Installed') { Add-WindowsCapability -Online -Name $c.Name | Out-Null }; " +
               "if ((Get-WindowsCapability -Online -Name $c.Name).State -eq 'Installed') { exit 0 } else { exit 1 }";
    }

    /// <summary>Installs the OCR language, asking Windows for permission. Returns when Windows has finished (a minute or two:
    /// the feature is downloaded from Windows Update).</summary>
    public static async Task<OcrPackResult> InstallAsync(string tag, CancellationToken ct = default)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Script(tag) })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return OcrPackResult.Failed;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode switch
            {
                0 => OcrPackResult.Installed,
                3 => OcrPackResult.NotAvailable,
                _ => OcrPackResult.Failed,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)   // ERROR_CANCELLED: "No" at the Windows prompt
        {
            return OcrPackResult.Cancelled;
        }
        catch (Win32Exception)
        {
            return OcrPackResult.Failed;
        }
    }
}
