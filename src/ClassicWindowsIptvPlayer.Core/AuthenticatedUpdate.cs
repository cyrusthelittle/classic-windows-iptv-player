using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassicWindowsIptvPlayer.Core;

public sealed record UpdateFile(string Path, long Size, string Sha256);
public sealed record UpdateManifest(int Format, string Repository, string Tag, string Version, string Runtime,
    string Package, long PackageSize, string PackageSha256, int DataSchema, UpdateFile[] Files);
public sealed record StagedUpdate(string Directory, UpdateManifest Manifest);

/// <summary>Both the running app and the separate helper use this compiled trust anchor.</summary>
public static class UpdateTrust
{
    public static string PublicKey
    {
        get
        {
            using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("UpdateTrust.pem");
            if (stream is null) throw new InvalidDataException("This build has no update signing key. Open the release on GitHub.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

public static class AuthenticatedUpdate
{
    public const string Repository = "cyrusthelittle/classic-windows-iptv-player";
    public const string ApplicationExe = "Classic Windows IPTV Player.exe";
    public const string HelperExe = "ClassicWindowsIptvPlayer.Updater.exe";
    public const string ManifestName = "update-manifest.json";
    public const string SignatureName = "update-manifest.sig";
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public static string PackageName(string tag) => "ClassicWindowsIPTVPlayer-" + tag + "-win-x64.zip";
    public static void ValidateTag(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$", RegexOptions.CultureInvariant) || tag.Length > 64)
            throw new InvalidDataException("This release tag does not support automatic installation. Open it on GitHub.");
    }
    public static UpdateManifest VerifyManifest(byte[] bytes, byte[] signature, string tag, Version currentVersion, string? publicKey = null)
    {
        ValidateTag(tag);
        if (bytes.Length is 0 or > 1048576 || signature.Length is < 256 or > 1024) throw new InvalidDataException("Invalid update signature or manifest size.");
        using var key = RSA.Create();
        key.ImportFromPem(publicKey ?? UpdateTrust.PublicKey);
        if (key.KeySize < 3072 || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("The update is not authenticated by this app's signing key. Open the release on GitHub.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes) ?? throw new InvalidDataException("Empty update manifest.");
        if (manifest.Format != 1 || manifest.Repository != Repository || manifest.Tag != tag || manifest.Runtime != "win-x64" ||
            manifest.Package != PackageName(tag) || manifest.PackageSize is <= 0 or > MaximumPackageBytes || manifest.DataSchema != AppState.CurrentSchemaVersion ||
            !Version.TryParse(manifest.Version, out var version) || Normalize(version) <= Normalize(currentVersion) ||
            Normalize(version) != Normalize(Version.Parse(tag.TrimStart('v'))) || !ValidHash(manifest.PackageSha256))
            throw new InvalidDataException("The signed update does not match this release, platform or saved-data schema.");
        if (manifest.Files is null || manifest.Files.Length is 0 or > 20000) throw new InvalidDataException("Invalid update file list.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var file in manifest.Files)
        {
            ValidateApplicationPath(file.Path);
            if (!names.Add(file.Path) || file.Size is < 0 or > MaximumPackageBytes || !ValidHash(file.Sha256)) throw new InvalidDataException("Invalid update file metadata.");
            expanded = checked(expanded + file.Size);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("Update expanded size exceeded its limit.");
        }
        if (!names.Contains(ApplicationExe) || !names.Contains(HelperExe) || !names.Contains("ClassicWindowsIptvPlayer.Core.dll"))
            throw new InvalidDataException("The signed update omits required application files.");
        return manifest;
    }
    private static Version Normalize(Version value) => new(value.Major, Math.Max(0, value.Minor), Math.Max(0, value.Build), Math.Max(0, value.Revision));
    private static bool ValidHash(string? hash) => hash is not null && Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
    public static void ValidateApplicationPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 220 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':')) throw new InvalidDataException("Unsafe update path.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Unsafe update path.");
            if (new[] { "cache", "logs", "updates", "Recordings", "pre-migration" }.Contains(part, StringComparer.OrdinalIgnoreCase) || part.StartsWith("accounts", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Updates cannot contain saved user data.");
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var bundledBlurayJar = path is "libvlc/win-x64/plugins/access/libbluray-awt-j2se-1.4.0.jar" or "libvlc/win-x64/plugins/access/libbluray-j2se-1.4.0.jar";
        if (ext == ".exe" && path != ApplicationExe && path != HelperExe && path != "createdump.exe" ||
            ext == ".json" && !path.EndsWith(".deps.json", StringComparison.Ordinal) && !path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal) ||
            ext is not (".exe" or ".dll" or ".json" or ".pdb" or ".ico" or ".png") && !bundledBlurayJar)
            throw new InvalidDataException("The update contains a file outside the application-file contract.");
    }
    public static string ResolvePath(string root, string relative)
    {
        ValidateApplicationPath(relative);
        var fullRoot = Path.GetFullPath(root);
        RejectLinks(fullRoot);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escaped its root.");
        RejectLinks(result);
        return result;
    }
    public static void RejectLinks(string path)
    {
        for (var value = Path.GetFullPath(path); !string.IsNullOrEmpty(value); value = Path.GetDirectoryName(value))
            if ((File.Exists(value) || Directory.Exists(value)) && (File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked update/install paths are not supported.");
    }
    public static byte[] HashStream(Stream stream, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int read;
        while ((read = stream.Read(buffer)) != 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        cancellationToken.ThrowIfCancellationRequested(); return hash.GetHashAndReset();
    }
    public static void VerifyAndExtract(Stream package, UpdateManifest manifest, string? destination = null, CancellationToken cancellationToken = default)
    {
        if (!package.CanSeek || package.Length != manifest.PackageSize) throw new InvalidDataException("Update package size did not match.");
        package.Position = 0;
        if (!Convert.ToHexString(HashStream(package, cancellationToken)).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update package checksum did not match.");
        package.Position = 0;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Explicit directory entries are unnecessary; release packaging emits files only.
            ValidateApplicationPath(entry.FullName);
            if ((entry.ExternalAttributes >> 16 & 0xF000) is 0xA000 or 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                !seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var file) || entry.Length != file.Size)
                throw new InvalidDataException("The package contains duplicate, linked or unexpected files.");
            using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var targetPath = destination is null ? null : ResolvePath(destination, file.Path);
            if (targetPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var target = targetPath is null ? null : new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536]; long size = 0;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                size += read;
                if (size > file.Size) throw new InvalidDataException("Expanded update file exceeded its signed size.");
                hash.AppendData(buffer, 0, read); target?.Write(buffer, 0, read);
            }
            if (size != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update file checksum did not match.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("The package is missing signed files.");
    }
}
