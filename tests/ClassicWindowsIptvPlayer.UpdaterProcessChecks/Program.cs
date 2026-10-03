using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(FixtureTools.Root);
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, Path.Combine(FixtureTools.Root, "fixture-data-harness"));
        try { if (args is ["--ui"]) UiChecks.Run(); else Run(args).GetAwaiter().GetResult(); Console.WriteLine(File.ReadAllText(Path.Combine(FixtureTools.Root, "evidence.txt"))); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Console.WriteLine("Isolated evidence: " + FixtureTools.Root); }
    }
    static async Task Run(string[] args)
    {
        var production = args.Length == 2 && args[0] == "--production";
        if (args.Length != 1 && !production) throw new ArgumentException("Pass [--production] and the absolute public repository directory.");
        var repo = Path.GetFullPath(args[^1]);
        using var key = RSA.Create(3072);
        // Fixture private material remains only in memory. The helper embeds only the fixture public key.
        var publicKey = Path.Combine(FixtureTools.Root, "fixture-public.pem");
        File.WriteAllText(publicKey, key.ExportSubjectPublicKeyInfoPem());
        var artifacts = Path.Combine(FixtureTools.Root, "build");
        foreach (var project in new[] { "src/ClassicWindowsIptvPlayer.Updater/ClassicWindowsIptvPlayer.Updater.csproj", "tests/ClassicWindowsIptvPlayer.UpdaterProcessChecks/FixtureApp/FixtureApp.csproj" })
        {
            var build = await FixtureTools.Run("dotnet", ["build", Path.Combine(repo, project), "-c", "Release", "--artifacts-path", artifacts, "-p:UpdateTrustPath=" + publicKey, "-p:NuGetAudit=false"], Path.Combine(FixtureTools.Root, "fixture-data-build"), repo, 180);
            if (build.ExitCode != 0) throw new InvalidOperationException("Fixture build failed: " + build.Output);
        }
        var helper = Directory.GetFiles(Path.Combine(artifacts, "bin"), AuthenticatedUpdate.HelperExe, SearchOption.AllDirectories).Single();
        if (production) { await ProductionUpgradeChecks.Run(repo, helper, key, publicKey); return; }
        var probe = Directory.GetFiles(Path.Combine(artifacts, "bin", "FixtureApp"), AuthenticatedUpdate.ApplicationExe, SearchOption.AllDirectories).Single();
        foreach (var kind in new[] { "healthy", "signature", "hash", "unsafe-zip", "locked", "launch-failure", "health-crash", "interrupted", "killed-apply" })
            await Scenario(kind, helper, Path.GetDirectoryName(probe)!, key);
    }
    static async Task Scenario(string kind, string helper, string probeDirectory, RSA key)
    {
        var root = Path.Combine(FixtureTools.Root, kind);
        var install = Path.Combine(root, "install");
        var data = Path.Combine(root, "fixture-data-" + kind);
        var stage = Path.Combine(data, "updates", "stage");
        Directory.CreateDirectory(install); Directory.CreateDirectory(stage);
        // Generate an invented protected account through the production persistence API, scoped explicitly.
        var store = new ConfigStore(data);
        var account = new AccountSettings { Username = "fixture-user", Password = "fixture-password", M3uUrl = "http://127.0.0.1:1/fixture.m3u" };
        var recordingFolder = Path.Combine(root, "recordings"); Directory.CreateDirectory(recordingFolder);
        var recording = Path.Combine(recordingFolder, "invented-recording.ts"); File.WriteAllBytes(recording, [1, 5, 9]);
        var state = new AppState { Account = account.Clone(), SelectedAccountId = "fixture", DarkMode = true, RecordingFolder = recordingFolder, Accounts = [new() { Id = "fixture", Name = "Invented", Settings = account.Clone() }] };
        state.SelectedLibrary.FavoriteIds.Add("fixture-favorite");
        state.SelectedLibrary.Recent.Add(new() { ChannelId = "fixture-favorite", ItemKey = "fixture-history", Name = "Invented recent", Url = "http://127.0.0.1:1/fixture" });
        store.Save(state);
        store.SaveChannelCache("fixture", [new Channel { Id = "fixture-favorite", Name = "Invented channel", Url = "http://127.0.0.1:1/fixture", MediaKind = MediaKind.Live }]);
        var savedBefore = Directory.GetFiles(data, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(stage)).ToDictionary(p => p, File.ReadAllBytes);
        var files = Directory.GetFiles(probeDirectory).Where(p => new[] { ".exe", ".dll", ".json", ".pdb" }.Contains(Path.GetExtension(p))).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
        files[AuthenticatedUpdate.HelperExe] = File.ReadAllBytes(helper);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(install, name), bytes);
        // A signed extra file establishes replacement independent of the probe's unchanged build identity.
        File.WriteAllBytes(Path.Combine(install, "fixture-marker.dll"), [1, 2, 3]);
        files["fixture-marker.dll"] = [4, 5, 6];
        if (kind == "killed-apply")
        {
            File.WriteAllBytes(Path.Combine(install, "fixture-large.dll"), [1, 3, 5]);
            var large = new byte[32 * 1024 * 1024];
            Random.Shared.NextBytes(large);
            files["fixture-large.dll"] = large;
        }
        if (kind == "health-crash") files["fixture-marker.dll"] = [8, 8, 8];
        var previous = Directory.GetFiles(install).ToDictionary(p => p, File.ReadAllBytes);
        if (kind == "launch-failure") files[AuthenticatedUpdate.ApplicationExe] = [0, 1, 2];
        var package = Path.Combine(stage, AuthenticatedUpdate.PackageName("v2.0.0"));
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in files) { using var entry = zip.CreateEntry(name).Open(); entry.Write(bytes); }
            if (kind == "unsafe-zip") { using var entry = zip.CreateEntry("../escape.dll").Open(); entry.WriteByte(7); }
        }
        var packageBytes = File.ReadAllBytes(package);
        var manifest = new UpdateManifest(1, AuthenticatedUpdate.Repository, "v2.0.0", "2.0.0", "win-x64", Path.GetFileName(package), packageBytes.Length,
            Convert.ToHexString(SHA256.HashData(packageBytes)), AppState.CurrentSchemaVersion, files.Select(p => new UpdateFile(p.Key, p.Value.Length, Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray());
        var bytesManifest = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signature = key.SignData(bytesManifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (kind == "signature") signature[0] ^= 1;
        File.WriteAllBytes(Path.Combine(stage, AuthenticatedUpdate.ManifestName), bytesManifest);
        File.WriteAllBytes(Path.Combine(stage, AuthenticatedUpdate.SignatureName), signature);
        if (kind == "hash") { packageBytes[^1] ^= 1; File.WriteAllBytes(package, packageBytes); }
        var job = Path.Combine(stage, "job.json");
        File.WriteAllText(job, JsonSerializer.Serialize(new { StageDirectory = stage, InstallDirectory = install, DataRoot = data, ParentProcessId = 0, CurrentVersion = "1.0.0", Tag = "v2.0.0" }));
        if (kind == "killed-apply")
        {
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(install, "fixture-large.dll.update-new");
            watcher.Created += (_, _) => writing.TrySetResult();
            watcher.EnableRaisingEvents = true;
            var start = new System.Diagnostics.ProcessStartInfo(helper)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, WorkingDirectory = root };
            start.ArgumentList.Add("--apply"); start.ArgumentList.Add(job);
            start.Environment[AppDataPaths.OverrideVariable] = data;
            using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("Kill fixture did not start.");
            try
            {
                await writing.Task.WaitAsync(TimeSpan.FromSeconds(60));
                FixtureTools.Check(!child.HasExited && File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)), "real apply reaches replacement with durable rollback journal");
                child.Kill(true);
                await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            FixtureTools.Check(File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)), "terminated apply retains recovery journal");
            var recovery = await FixtureTools.Run(helper, ["--recover", job], data, root, 100);
            FixtureTools.Check(recovery.ExitCode == 0 && previous.All(p => File.Exists(p.Key) && File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "separate helper restores original application after actual apply process kill");
            FixtureTools.Check(!File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)) && !Directory.GetFiles(install, "*.update-new", SearchOption.AllDirectories).Any(), "kill recovery clears journal and interrupted replacement files");
            FixtureTools.Check(savedBefore.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && File.ReadAllBytes(recording).SequenceEqual(new byte[] { 1, 5, 9 }), "actual kill recovery preserves account/cache and external recording bytes");
            return;
        }
        if (kind == "interrupted")
        {
            var work = Path.Combine(stage, "apply-interrupted"); var backup = Path.Combine(work, "backup"); Directory.CreateDirectory(backup);
            var marker = "fixture-marker.dll"; var old = previous[Path.Combine(install, marker)];
            File.WriteAllBytes(Path.Combine(backup, marker), old); File.WriteAllBytes(Path.Combine(install, marker), [9, 9, 9]);
            File.WriteAllText(Path.Combine(install, UpdateInstaller.PendingMarker), JsonSerializer.Serialize(new UpdateJournal(work, [new UpdateBackup(marker, true, Convert.ToHexString(SHA256.HashData(old)))])));
            var recovery = await FixtureTools.Run(helper, ["--recover", job], data, root, 100);
            FixtureTools.Check(recovery.ExitCode == 0 && previous.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && !File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)), "separate helper recovers durable pending journal after simulated interrupted apply");
            FixtureTools.Check(savedBefore.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "saved account/cache bytes preserved after interrupted recovery");
            return;
        }
        using var locked = kind == "locked" ? new FileStream(Path.Combine(install, "ClassicWindowsIptvPlayer.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var result = await FixtureTools.Run(helper, ["--apply", job], data, root, 100);
        locked?.Dispose();
        if (kind == "locked" && File.Exists(Path.Combine(install, UpdateInstaller.PendingMarker)))
        {
            var recovery = await FixtureTools.Run(helper, ["--recover", job], data, root, 100);
            FixtureTools.Check(recovery.ExitCode == 0, "locked apply retains durable rollback journal recoverable after lock released");
        }
        if (kind == "healthy")
        {
            FixtureTools.Check(result.ExitCode == 0, "real helper successfully applies and observes new process health");
            FixtureTools.Check(File.ReadAllBytes(Path.Combine(install, "fixture-marker.dll")).SequenceEqual(new byte[] { 4, 5, 6 }), "new application file installed");
            using var probe = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "relaunch-probe.json")));
            FixtureTools.Check(probe.RootElement.GetProperty("Root").GetString() == data && probe.RootElement.GetProperty("Valid").GetBoolean(), "relaunch inherits identical data root and decrypts account/favorite");
        }
        else
        {
            FixtureTools.Check(result.ExitCode != 0, "helper rejects " + kind);
            FixtureTools.Check(previous.All(p => File.Exists(p.Key) && File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "previous application bytes intact after " + kind);
        }
        FixtureTools.Check(savedBefore.All(p => File.Exists(p.Key) && File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "saved account/cache bytes preserved after " + kind);
        FixtureTools.Check(File.ReadAllBytes(recording).SequenceEqual(new byte[] { 1, 5, 9 }), "external recording bytes preserved after " + kind);
        FixtureTools.Check(!File.Exists(Path.Combine(data, "updates", "escape.dll")), "unsafe archive cannot escape its extraction root " + kind);
    }
}
