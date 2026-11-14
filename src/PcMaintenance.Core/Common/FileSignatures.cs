using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PcMaintenance.Core.Common;

public enum SignatureState
{
    /// <summary>Signed, and Windows trusts the signature.</summary>
    Valid,
    Unsigned,
    /// <summary>Signed, but the file changed afterwards or the certificate is not trusted.</summary>
    Invalid,
    /// <summary>The file could not be read.</summary>
    Unknown,
}

/// <param name="Signer">The signing certificate's name, such as "Microsoft Windows" or "Google LLC"; empty when unknown.</param>
public sealed record FileSignature(SignatureState State, string Signer)
{
    public static readonly FileSignature Unknown = new(SignatureState.Unknown, "");

    public bool IsMicrosoft => State == SignatureState.Valid && Signer.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Authenticode checks through WinVerifyTrust, the same decision Windows makes for "verified publisher": the file's own
/// signature first, then the system catalogs most of Windows' files are signed through. Certificate revocation is not
/// checked and nothing is downloaded, so it works offline and takes milliseconds per file.
/// </summary>
public static class FileSignatures
{
    private static readonly ConcurrentDictionary<string, FileSignature> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FileSignature Check(string path) => Cache.GetOrAdd(path, Read);

    private static FileSignature Read(string path)
    {
        if (!File.Exists(path)) return FileSignature.Unknown;
        try
        {
            var embedded = Verify(path, null);
            if (embedded == 0) return new FileSignature(SignatureState.Valid, SignerOf(path));
            if (embedded is not (TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN))
                return new FileSignature(SignatureState.Invalid, SignerOf(path));
            return CatalogSignature(path) ?? new FileSignature(SignatureState.Unsigned, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ExternalException)
        {
            return FileSignature.Unknown;
        }
    }

    /// <summary>The embedded signature's certificate name, without judging it.</summary>
    private static string SignerOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // There is no X509CertificateLoader equivalent for reading a signed file's signer.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    /// <summary>Looks the file's hash up in the system catalogs (SHA-256 first, then SHA-1) and verifies the catalog that lists it.</summary>
    private static FileSignature? CatalogSignature(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var algorithm in new[] { "SHA256", null })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size == 0) continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) continue;
                var catalogInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catalogInfo == IntPtr.Zero) continue;
                try
                {
                    var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                    if (!CryptCATCatalogInfoFromContext(catalogInfo, ref info, 0)) continue;
                    var result = Verify(path, (info.wszCatalogFile, Convert.ToHexString(hash), admin));
                    return new FileSignature(result == 0 ? SignatureState.Valid : SignatureState.Invalid, SignerOf(info.wszCatalogFile));
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, catalogInfo, 0);
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    private static int Verify(string path, (string File, string MemberTag, IntPtr Admin)? catalog)
    {
        var strings = new List<IntPtr>();
        IntPtr Text(string value)
        {
            var pointer = Marshal.StringToHGlobalUni(value);
            strings.Add(pointer);
            return pointer;
        }

        IntPtr info;
        uint choice;
        if (catalog is { } c)
        {
            info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            Marshal.StructureToPtr(new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = Text(c.File),
                pcwszMemberTag = Text(c.MemberTag),
                pcwszMemberFilePath = Text(path),
                hCatAdmin = c.Admin,
            }, info, false);
            choice = WTD_CHOICE_CATALOG;
        }
        else
        {
            info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = Text(path) }, info, false);
            choice = WTD_CHOICE_FILE;
        }

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pInfo = info,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
        };
        var action = GenericVerifyV2;
        try
        {
            return WinVerifyTrust(InvalidHandle, ref action, ref data);
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(InvalidHandle, ref action, ref data);
            Marshal.FreeHGlobal(info);
            foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        }
    }

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalogInfo, ref CATALOG_INFO info, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalogInfo, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
