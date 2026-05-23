using System.Runtime.InteropServices;
using System.Text;

namespace ClaudetRelay.Services;

/// <summary>
/// Thin P/Invoke wrapper for Windows Credential Manager (CRED_TYPE_GENERIC).
/// API keys are stored under the target name "ClaudetRelay:{provider}".
/// </summary>
public static class WindowsCredentialManager
{
    private const int CRED_TYPE_GENERIC         = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint    Flags;
        public int     Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string  TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long    LastWritten;
        public uint    CredentialBlobSize;
        public IntPtr  CredentialBlob;
        public uint    Persist;
        public uint    AttributeCount;
        public IntPtr  Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string  UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",   CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead  (string target, int type, int flags, out IntPtr credPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",  CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite (ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree  (IntPtr buffer);

    private static string Target(string provider) => $"ClaudetRelay:{provider}";

    /// <summary>Stores <paramref name="apiKey"/> for the given provider in Windows Credential Manager.</summary>
    public static void Save(string provider, string apiKey)
    {
        var bytes  = Encoding.UTF8.GetBytes(apiKey);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type               = CRED_TYPE_GENERIC,
                TargetName         = Target(provider),
                UserName           = provider,
                CredentialBlob     = handle.AddrOfPinnedObject(),
                CredentialBlobSize = (uint)bytes.Length,
                Persist            = CRED_PERSIST_LOCAL_MACHINE
            };
            CredWrite(ref cred, 0);
        }
        finally { handle.Free(); }
    }

    /// <summary>Returns the stored API key for the given provider, or <c>null</c> if not found.</summary>
    public static string? Load(string provider)
    {
        if (!CredRead(Target(provider), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    /// <summary>Removes the stored credential for the given provider (silently ignores if not found).</summary>
    public static void Delete(string provider) =>
        CredDelete(Target(provider), CRED_TYPE_GENERIC, 0);
}
