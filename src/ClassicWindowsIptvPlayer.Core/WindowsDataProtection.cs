using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClassicWindowsIptvPlayer.Core;

internal static class WindowsDataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static byte[] Protect(byte[] bytes, byte[] entropy) => Transform(bytes, entropy, protect: true);
    public static byte[] Unprotect(byte[] bytes, byte[] entropy) => Transform(bytes, entropy, protect: false);

    private static byte[] Transform(byte[] bytes, byte[] entropyBytes, bool protect)
    {
        var input = Allocate(bytes);
        var entropy = Allocate(entropyBytes);
        Blob output = default;
        try
        {
            // Current Windows user scope; no CRYPTPROTECT_LOCAL_MACHINE flag.
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static Blob Allocate(byte[] bytes)
    {
        var blob = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }
}
