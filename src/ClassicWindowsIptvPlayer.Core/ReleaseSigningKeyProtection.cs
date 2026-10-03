using System.Text;
namespace ClassicWindowsIptvPlayer.Core;
public static class ReleaseSigningKeyProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassicWindowsIPTVPlayer.ReleaseSigning.v1");
    public static byte[] Protect(byte[] key) => WindowsDataProtection.ProtectSigningKey(key, Entropy);
    public static byte[] Unprotect(byte[] key) => WindowsDataProtection.UnprotectSigningKey(key, Entropy);
}
