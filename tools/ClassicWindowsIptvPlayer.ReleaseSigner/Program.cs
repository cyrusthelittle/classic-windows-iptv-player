using ClassicWindowsIptvPlayer.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    if (args.Length == 3 && args[0] == "keygen")
    {
        var privatePath = Path.GetFullPath(args[1]); var publicPath = Path.GetFullPath(args[2]);
        if (File.Exists(privatePath) || File.Exists(publicPath) || privatePath.Equals(publicPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Key output paths must be distinct new files. Existing keys are never overwritten.");
        AuthenticatedUpdate.RejectLinks(privatePath); AuthenticatedUpdate.RejectLinks(publicPath);
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!); Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using var key = RSA.Create(3072);
        var secret = key.ExportPkcs8PrivateKey();
        try
        {
            var protectedKey = ReleaseSigningKeyProtection.Protect(secret);
            using (var output = new FileStream(privatePath, FileMode.CreateNew)) { output.Write(protectedKey); output.Flush(true); }
            using var publicOutput = new StreamWriter(new FileStream(publicPath, FileMode.CreateNew)); publicOutput.Write(key.ExportSubjectPublicKeyInfoPem());
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        Console.WriteLine("Created same-user DPAPI-protected signing key and public trust resource. No key material printed."); return 0;
    }
    if (args.Length == 5 && args[0] == "sign")
    {
        var privatePath = Path.GetFullPath(args[1]); var payload = Path.GetFullPath(args[2]); var output = Path.GetFullPath(args[3]); var tag = args[4];
        AuthenticatedUpdate.ValidateTag(tag); AuthenticatedUpdate.RejectLinks(payload); AuthenticatedUpdate.RejectLinks(output);
        if (!Directory.Exists(payload)) throw new IOException("The application payload directory does not exist.");
        Directory.CreateDirectory(output);
        var packageName = AuthenticatedUpdate.PackageName(tag);
        foreach (var name in new[] { packageName, AuthenticatedUpdate.ManifestName, AuthenticatedUpdate.SignatureName }) if (File.Exists(Path.Combine(output, name))) throw new IOException("Release outputs already exist; choose a fresh output folder.");
        var files = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
        {
            AuthenticatedUpdate.RejectLinks(path);
            var relative = Path.GetRelativePath(payload, path).Replace('\\', '/'); AuthenticatedUpdate.ValidateApplicationPath(relative);
            using var stream = File.OpenRead(path); return new UpdateFile(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToArray();
        var appVersionText = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(payload, AuthenticatedUpdate.ApplicationExe)).FileVersion;
        if (!Version.TryParse(appVersionText, out var appVersion)) throw new InvalidDataException("The payload executable has no supported Windows file version.");
        var tagVersion = Version.Parse(tag.TrimStart('v'));
        static Version Normalize(Version version) => new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
        if (Normalize(appVersion) != Normalize(tagVersion)) throw new InvalidDataException("The release tag does not match the payload executable's file version.");
        var packagePath = Path.Combine(output, packageName);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create)) foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(payload, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.Path, CompressionLevel.Optimal);
        using var package = File.OpenRead(packagePath);
        var manifest = new UpdateManifest(1, AuthenticatedUpdate.Repository, tag, tag.TrimStart('v'), "win-x64", packageName, package.Length, Convert.ToHexString(SHA256.HashData(package)), AppState.CurrentSchemaVersion, files);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        var secret = ReleaseSigningKeyProtection.Unprotect(File.ReadAllBytes(privatePath));
        using var key = RSA.Create();
        try { key.ImportPkcs8PrivateKey(secret, out _); } finally { CryptographicOperations.ZeroMemory(secret); }
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        // Reuse production validation before exposing any release metadata.
        _ = AuthenticatedUpdate.VerifyManifest(bytes, signature, tag, new Version(0, 0, 0, 0), key.ExportSubjectPublicKeyInfoPem());
        AuthenticatedUpdate.VerifyAndExtract(package, manifest);
        File.WriteAllBytes(Path.Combine(output, AuthenticatedUpdate.ManifestName), bytes);
        File.WriteAllBytes(Path.Combine(output, AuthenticatedUpdate.SignatureName), signature);
        Console.WriteLine("Created and verified signed release assets locally. Nothing was uploaded."); return 0;
    }
    Console.Error.WriteLine("Usage: keygen <NEW-private.dpapi> <NEW-public.pem> | sign <private.dpapi> <app-payload-directory> <fresh-output-directory> <v1.2.3>"); return 2;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
