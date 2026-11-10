using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenShelf.Install;

/// <summary>Authenticode check with WinVerifyTrust (no UI, no revocation lookup so it works offline).</summary>
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureResult Verify(string path)
    {
        var status = WinVerifyTrustFile(path);
        switch (unchecked((uint)status))
        {
            case 0:
                return new SignatureResult(SignatureState.Valid, SignerOf(path));
            case 0x800B0100: // TRUST_E_NOSIGNATURE
            case 0x800B0001: // TRUST_E_PROVIDER_UNKNOWN
            case 0x800B0003: // TRUST_E_SUBJECT_FORM_UNKNOWN
                return new SignatureResult(SignatureState.Unsigned);
            default:
                return new SignatureResult(SignatureState.Invalid, SignerOf(path), $"0x{unchecked((uint)status):X8}");
        }
    }

    private static string? SignerOf(string path)
    {
        try
        {
            #pragma warning disable SYSLIB0057 // no replacement exists for reading the signer out of a signed file
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException) { return null; }
    }

    private static int WinVerifyTrustFile(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        var fileMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileMemory, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileMemory,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00000080,  // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = ActionGenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(fileMemory);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
}
