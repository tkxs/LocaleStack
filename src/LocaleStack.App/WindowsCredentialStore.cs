using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace LocaleStack.App;

internal static class WindowsCredentialStore
{
    internal const string DefaultTarget = "LocaleStack:GitHub:tkxs/LocaleStack";
    internal static void Save(string token, string user, string target = DefaultTarget)
    {
        var blob = Marshal.StringToCoTaskMemUni(token);
        try
        {
            var credential = new Credential { Type = 1, TargetName = target, UserName = user, CredentialBlob = blob, CredentialBlobSize = checked((uint)Encoding.Unicode.GetByteCount(token)), Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    internal static string? Read(string target = DefaultTarget)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        { var error = Marshal.GetLastWin32Error(); if (error == 1168) return null; throw new Win32Exception(error, "无法读取 Windows 凭据。"); }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 5120 || credential.CredentialBlobSize % 2 != 0) throw new IOException("保存的 GitHub 凭据无效，请重新连接。");
            var bytes = new byte[credential.CredentialBlobSize];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.Unicode.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
    internal static void Delete(string target = DefaultTarget)
    {
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 Windows 凭据。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes, TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);
}
