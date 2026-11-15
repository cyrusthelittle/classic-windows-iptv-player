using ClassicWindowsIptvPlayer.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

// Builds the automatic-update assets for one edition: the package ZIP and its manifest listing every
// file's size and SHA-256. No signing key is involved. Nothing is uploaded.
try
{
    if (args.Length == 5 && args[0] == "package")
    {
        var payload = Path.GetFullPath(args[1]); var output = Path.GetFullPath(args[2]); var tag = args[3]; var edition = UpdateEdition.Validate(args[4]);
        AuthenticatedUpdate.ValidateTag(tag); AuthenticatedUpdate.RejectLinks(payload); AuthenticatedUpdate.RejectLinks(output);
        if (!Directory.Exists(payload)) throw new IOException("The application payload directory does not exist.");
        Directory.CreateDirectory(output);
        var packageName = AuthenticatedUpdate.PackageName(tag, edition); var manifestName = AuthenticatedUpdate.ManifestName(edition);
        foreach (var name in new[] { packageName, manifestName }) if (File.Exists(Path.Combine(output, name))) throw new IOException("Release outputs already exist; choose a fresh output folder.");
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
        // A Full payload carries the .NET host; a Lite payload must not, so an edition can never be packaged as the other.
        var selfContained = files.Any(f => f.Path == "hostfxr.dll");
        if (selfContained != (edition == UpdateEdition.Full)) throw new InvalidDataException($"The payload does not look like the {edition} edition.");
        var packagePath = Path.Combine(output, packageName);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create)) foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(payload, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.Path, CompressionLevel.Optimal);
        using var package = File.OpenRead(packagePath);
        var manifest = new UpdateManifest(AuthenticatedUpdate.ManifestFormat, AuthenticatedUpdate.Repository, tag, tag.TrimStart('v'), "win-x64", packageName, package.Length,
            Convert.ToHexString(SHA256.HashData(package)), AppState.CurrentSchemaVersion, files);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        // Reuse production validation before writing any release metadata.
        _ = AuthenticatedUpdate.VerifyManifest(bytes, tag, new Version(0, 0, 0, 0), edition);
        AuthenticatedUpdate.VerifyAndExtract(package, manifest);
        File.WriteAllBytes(Path.Combine(output, manifestName), bytes);
        Console.WriteLine($"Created and verified {edition} update assets locally: {packageName}, {manifestName}. Nothing was uploaded."); return 0;
    }
    Console.Error.WriteLine("Usage: package <app-payload-directory> <fresh-output-directory> <v1.2.3> <full|lite>"); return 2;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
