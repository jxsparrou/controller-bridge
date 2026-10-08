using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SBridge.Configuration;

internal interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}

// DPAPI is scoped to the current Windows user, with no machine-wide flag or UI.
// One protected blob lives inside the atomic config document: no two-file secret
// transaction or additional secrets framework is needed.
[SupportedOSPlatform("windows")]
internal sealed class WindowsSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi-current-user-v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("sBridge/SteamGridDB/v1");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public string Protect(string value)
    {
        byte[] clear = Utf8.GetBytes(value);
        try { return Prefix + Convert.ToBase64String(Transform(clear, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported credential protection format.");
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(protectedValue[Prefix.Length..]); }
        catch (FormatException ex) { throw new CryptographicException("Invalid protected credential encoding.", ex); }
        byte[] clear = Transform(encrypted, protect: false);
        try { return Utf8.GetString(clear); }
        catch (DecoderFallbackException ex) { throw new CryptographicException("Protected credential contained invalid text.", ex); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        DataBlob source = default;
        DataBlob entropy = default;
        DataBlob output = default;
        try
        {
            source = Allocate(input);
            entropy = Allocate(Entropy);
            bool succeeded = protect
                ? CryptProtectData(ref source, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new CryptographicException(protect
                ? "Windows could not protect the SteamGridDB credential."
                : "Windows could not decrypt the SteamGridDB credential for this user.", new Win32Exception(Marshal.GetLastWin32Error()));
            byte[] bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Clear(source); Marshal.FreeHGlobal(source.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) { if (!protect) Clear(output); LocalFree(output.Data); }
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }
    private static void Clear(DataBlob blob)
    {
        for (int index = 0; index < blob.Size; index++) Marshal.WriteByte(blob.Data, index, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
