using ClassicWindowsIptvPlayer.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AuthenticatedUpdateRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-update-core-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var passed = 0;
        const string tag = "v99.1.0";
        const string full = UpdateEdition.Full, lite = UpdateEdition.Lite;
        var paths = new[] { AuthenticatedUpdate.ApplicationExe, AuthenticatedUpdate.HelperExe, "ClassicWindowsIptvPlayer.Core.dll" };
        var payload = paths.Select((p, i) => (p, bytes: System.Text.Encoding.UTF8.GetBytes("invented-binary-" + i))).ToArray();
        byte[] Zip((string p, byte[] bytes)[] items)
        {
            using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var (p, bytes) in items) { using var output = zip.CreateEntry(p).Open(); output.Write(bytes); }
            return memory.ToArray();
        }
        var package = Zip(payload);
        var manifest = new UpdateManifest(AuthenticatedUpdate.ManifestFormat, AuthenticatedUpdate.Repository, tag, "99.1.0", "win-x64", AuthenticatedUpdate.PackageName(tag, full), package.Length,
            Convert.ToHexString(SHA256.HashData(package)), AppState.CurrentSchemaVersion, payload.Select(x => new UpdateFile(x.p, x.bytes.Length, Convert.ToHexString(SHA256.HashData(x.bytes)))).ToArray());
        void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS update: " + name); } catch (Exception ex) { throw new InvalidOperationException(name, ex); } }
        void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or OperationCanceledException) { return; } throw new Exception("Expected rejection."); }
        UpdateManifest Verify(UpdateManifest candidate, string offered = tag, Version? current = null, string edition = full) =>
            AuthenticatedUpdate.VerifyManifest(JsonSerializer.SerializeToUtf8Bytes(candidate), offered, current ?? new Version(1, 0, 0), edition);
        try
        {
            Check("valid exact manifest and package", () => { var verified = Verify(manifest); using var stream = new MemoryStream(package); AuthenticatedUpdate.VerifyAndExtract(stream, verified); });
            Check("malformed manifest fails", () => { var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest); bytes[0] = (byte)'x'; Reject(() => AuthenticatedUpdate.VerifyManifest(bytes, tag, new Version(1, 0, 0), full)); });
            Check("old signed-format manifest fails", () => Reject(() => Verify(manifest with { Format = 1 })));
            Check("lite manifest accepted by lite", () => Verify(manifest with { Package = AuthenticatedUpdate.PackageName(tag, lite) }, edition: lite));
            Check("full package refused by lite install", () => Reject(() => Verify(manifest, edition: lite)));
            Check("lite package refused by full install", () => Reject(() => Verify(manifest with { Package = AuthenticatedUpdate.PackageName(tag, lite) })));
            Check("unknown edition refused", () => Reject(() => Verify(manifest, edition: "other")));
            Check("edition asset names differ", () => { if (AuthenticatedUpdate.ManifestName(full) == AuthenticatedUpdate.ManifestName(lite) || AuthenticatedUpdate.PackageName(tag, full) == AuthenticatedUpdate.PackageName(tag, lite)) throw new Exception("Editions share an asset name."); });
            Check("lite setup guide allowed in update", () => AuthenticatedUpdate.ValidateApplicationPath(AuthenticatedUpdate.ReadMeFirst));
            foreach (var candidate in new[] { manifest with { Repository = "other/repo" }, manifest with { Runtime = "win-arm64" }, manifest with { Package = "wrong.zip" }, manifest with { Version = "99.2.0" }, manifest with { DataSchema = 3 }, manifest with { PackageSize = AuthenticatedUpdate.MaximumPackageBytes + 1 }, manifest with { Files = [manifest.Files[0], manifest.Files[0]] } })
                Check("wrong release metadata " + passed, () => Reject(() => Verify(candidate)));
            Check("downgrade denied", () => Reject(() => Verify(manifest, current: new Version(100, 0, 0))));
            foreach (var unsafePath in new[] { "../evil.dll", "lib/../../evil.dll", "/evil.dll", "a\\b.dll", "file.dll:evil", "CACHE/a.dll", "accounts.json", "Recordings/a.dll", "portable-mode.txt", "CON.dll", "file.dll.", "other.exe", "data.json", "notes.txt", "accounts/" + AuthenticatedUpdate.ReadMeFirst })
                Check("unsafe or saved-data path " + unsafePath, () => Reject(() => AuthenticatedUpdate.ValidateApplicationPath(unsafePath)));
            Check("case duplicate payload fails", () => { var duplicate = Zip(payload.Concat(new[] { (paths[0].ToUpperInvariant(), payload[0].bytes) }).ToArray()); var m = manifest with { PackageSize = duplicate.Length, PackageSha256 = Convert.ToHexString(SHA256.HashData(duplicate)) }; using var stream = new MemoryStream(duplicate); Reject(() => AuthenticatedUpdate.VerifyAndExtract(stream, m)); });
            Check("tampered package fails", () => { var changed = package.ToArray(); changed[8] ^= 1; using var stream = new MemoryStream(changed); Reject(() => AuthenticatedUpdate.VerifyAndExtract(stream, manifest)); });
            Check("cancellation stops verification", () => { using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var stream = new MemoryStream(package); Reject(() => AuthenticatedUpdate.VerifyAndExtract(stream, manifest, cancellationToken: cancellation.Token)); });
            Check("automatic preference off and protected roundtrip", () => { var store = new ConfigStore(Path.Combine(root, "settings")); var state = new AppState(); if (state.DownloadUpdatesAutomatically) throw new Exception("Must default off."); state.DownloadUpdatesAutomatically = true; store.Save(state); if (!store.Load().DownloadUpdatesAutomatically) throw new Exception("Preference lost."); });
            Check("same root trailing slash cannot acquire another owner", () =>
            {
                using var lease = AppDataLease.Acquire(root);
                var refused = Task.Run(() => { try { using var other = AppDataLease.Acquire(root + Path.DirectorySeparatorChar); return false; } catch (IOException) { return true; } }).GetAwaiter().GetResult();
                if (!refused) throw new Exception("Trailing slash bypassed data lease.");
            });
            var updates = Path.Combine(root, "updates");
            using (var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
            {
                try { await new UpdateDownloadService(client).StageAsync(tag, new Version(1, 0), full, updates); throw new Exception("Expected unsigned-release fallback."); } catch (InvalidDataException ex) when (ex.Message.Contains("GitHub")) { passed++; }
            }
            using (var client = new HttpClient(new FixtureHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://example.invalid/evil"); return response; })))
            {
                try { await new UpdateDownloadService(client).StageAsync(tag, new Version(1, 0), full, updates); throw new Exception("Expected redirect rejection."); } catch (InvalidDataException) { passed++; }
            }
            using (var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1048577]) })))
            {
                try { await new UpdateDownloadService(client).StageAsync(tag, new Version(1, 0), full, updates); throw new Exception("Expected bounded response."); } catch (InvalidDataException) { passed++; }
            }
            using (var client = new HttpClient(new SlowHandler()))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                try { await new UpdateDownloadService(client).StageAsync(tag, new Version(1, 0), full, updates, cancellationToken: cancellation.Token); throw new Exception("Expected canceled download."); } catch (OperationCanceledException) { passed++; }
            }
            if (Directory.GetDirectories(updates).Length != 0) throw new Exception("Failed download stages were retained.");
            Console.WriteLine($"Authenticated update regressions: {passed} checks passed. Isolated fixture {root}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { await Task.Delay(5000, cancellationToken); return new HttpResponseMessage(HttpStatusCode.OK); }
    }
}
