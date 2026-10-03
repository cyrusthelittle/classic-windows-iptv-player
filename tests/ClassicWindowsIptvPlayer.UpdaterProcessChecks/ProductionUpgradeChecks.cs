using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class ProductionUpgradeChecks
{
    public static async Task Run(string repo, string helper, RSA key, string publicKey)
    {
        var root = Path.Combine(FixtureTools.Root, "production-upgrade"); Directory.CreateDirectory(root);
        var versions = new Dictionary<string, string>();
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var artifacts = Path.Combine(root, "build-" + version);
            var built = await FixtureTools.Run("dotnet", ["build", Path.Combine(repo, "src/ClassicWindowsIptvPlayer.Windows/ClassicWindowsIptvPlayer.Windows.csproj"),
                "-c", "Release", "--artifacts-path", artifacts, "-p:UpdateTrustPath=" + publicKey, "-p:Version=" + version,
                "-p:FileVersion=" + version + ".0", "-p:AssemblyVersion=" + version + ".0", "-p:NuGetAudit=false", "-p:DefaultItemExcludesInProjectFolder=**/artifacts/**"],
                Path.Combine(root, "fixture-build-data"), repo, 180);
            if (built.ExitCode != 0) throw new InvalidOperationException("Production fixture build failed: " + built.Output);
            var directory = Path.GetDirectoryName(Directory.GetFiles(Path.Combine(artifacts, "bin", "ClassicWindowsIptvPlayer.Windows"), AuthenticatedUpdate.ApplicationExe, SearchOption.AllDirectories).Single())!;
            // The legacy build copy target uses default outputs. Replace only fixture helper files with this fixture's trusted helper.
            foreach (var path in Directory.GetFiles(Path.GetDirectoryName(helper)!, "ClassicWindowsIptvPlayer.Updater.*"))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), true);
            versions[version] = directory;
            FixtureTools.Check(FileVersionInfo.GetVersionInfo(Path.Combine(directory, "Classic Windows IPTV Player.dll")).FileVersion == version + ".0", "actual production fixture has version " + version);
        }
        var install = Path.Combine(root, "install");
        foreach (var path in Directory.GetFiles(versions["1.0.0"], "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(install, Path.GetRelativePath(versions["1.0.0"], path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
        }
        var data = Path.Combine(root, "fixture-data"); var store = new ConfigStore(data);
        var settings = new AccountSettings { M3uUrl = "http://127.0.0.1:1/unused.m3u", Username = "invented", Password = "invented-password" };
        var recording = Path.Combine(root, "recordings", "fixture.ts"); Directory.CreateDirectory(Path.GetDirectoryName(recording)!); File.WriteAllBytes(recording, [7, 8, 9]);
        var state = new AppState { SelectedAccountId = "production-fixture", Account = settings.Clone(), CheckForUpdatesOnStartup = false,
            RemoteControlEnabled = false, EpgEnabled = false, RecordingFolder = Path.GetDirectoryName(recording)!,
            Accounts = [new() { Id = "production-fixture", Name = "Invented production account", Settings = settings.Clone() }] };
        state.SelectedLibrary.FavoriteIds.Add("fixture-channel");
        state.SelectedLibrary.Recent.Add(new() { ChannelId = "fixture-channel", ItemKey = "fixture-item", Name = "Fixture", Url = settings.M3uUrl });
        store.Save(state); store.SaveChannelCache(state.SelectedAccountId, [new() { Id = "fixture-channel", Name = "Fixture", Url = settings.M3uUrl }]);
        var saved = Directory.GetFiles(data, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var stage = Path.Combine(data, "updates", "production-stage"); Directory.CreateDirectory(stage);
        var package = Path.Combine(stage, AuthenticatedUpdate.PackageName("v2.0.0")); var files = new List<UpdateFile>();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        foreach (var path in Directory.GetFiles(versions["2.0.0"], "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(versions["2.0.0"], path).Replace('\\', '/');
            AuthenticatedUpdate.ValidateApplicationPath(relative);
            using var input = File.OpenRead(path); var hash = Convert.ToHexString(SHA256.HashData(input)); input.Position = 0;
            files.Add(new(relative, input.Length, hash));
            using var output = archive.CreateEntry(relative, CompressionLevel.Fastest).Open(); input.CopyTo(output);
        }
        string packageHash; using (var input = File.OpenRead(package)) packageHash = Convert.ToHexString(SHA256.HashData(input));
        var manifest = new UpdateManifest(1, AuthenticatedUpdate.Repository, "v2.0.0", "2.0.0", "win-x64", Path.GetFileName(package), new FileInfo(package).Length,
            packageHash, AppState.CurrentSchemaVersion, files.ToArray());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest); var signature = key.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        AuthenticatedUpdate.VerifyManifest(manifestBytes, signature, "v2.0.0", new Version(1, 0, 0), key.ExportSubjectPublicKeyInfoPem());
        File.WriteAllBytes(Path.Combine(stage, AuthenticatedUpdate.ManifestName), manifestBytes); File.WriteAllBytes(Path.Combine(stage, AuthenticatedUpdate.SignatureName), signature);
        var job = Path.Combine(stage, "job.json"); File.WriteAllText(job, JsonSerializer.Serialize(new UpdateJob(stage, install, data, 0, "1.0.0", "v2.0.0")));
        try
        {
            // A long-lived relaunched GUI inherits redirected handles. Do not wait for that GUI to close to read helper output.
            var result = await FixtureTools.Run(helper, ["--apply", job], data, root, 180, captureOutput: false);
            FixtureTools.Check(result.ExitCode == 0, "signed update applies actual production 1.0.0 to 2.0.0 and passes WPF/native health probe");
            FixtureTools.Check(FileVersionInfo.GetVersionInfo(Path.Combine(install, "Classic Windows IPTV Player.dll")).FileVersion == "2.0.0.0", "installed production application reports new file version");
            FixtureTools.Check(saved.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "actual production update preserves protected account/history/favorites/cache bytes");
            var loaded = store.Load();
            FixtureTools.Check(loaded.EnsureSelectedAccount().Settings.Password == "invented-password" && loaded.SelectedLibrary.FavoriteIds.Contains("fixture-channel") &&
                loaded.SelectedLibrary.Recent.Count == 1 && loaded.RecordingFolder == Path.GetDirectoryName(recording), "updated production settings retain decryptable credentials and saved preferences");
            FixtureTools.Check(File.ReadAllBytes(recording).SequenceEqual(new byte[] { 7, 8, 9 }), "actual production update retains external recording");
            var deadline = DateTime.UtcNow.AddSeconds(20); bool relaunched = false;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var process in ProcessesAt(install))
                {
                    using (process) if (process.MainWindowHandle != IntPtr.Zero) { relaunched = true; break; }
                }
                if (relaunched) break; await Task.Delay(100);
            }
            FixtureTools.Check(relaunched, "new production application relaunches to an actual account window after health probe");
            FixtureTools.Check(!File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)) && Directory.GetFiles(stage, "installed-journal.json", SearchOption.AllDirectories).Length == 1,
                "healthy production update commits while retaining rollback journal");
        }
        finally
        {
            // Stop only this fixture's exact application path; preserve all normal user app processes.
            foreach (var process in ProcessesAt(install)) using (process) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }
    static IEnumerable<Process> ProcessesAt(string install)
    {
        var executable = Path.Combine(install, AuthenticatedUpdate.ApplicationExe);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            bool match; try { match = string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase); } catch { match = false; }
            if (match) yield return process; else process.Dispose();
        }
    }
}
