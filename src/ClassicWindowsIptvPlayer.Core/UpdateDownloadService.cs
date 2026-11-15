using System.Net;
using System.Net.Http.Headers;
namespace ClassicWindowsIptvPlayer.Core;

public sealed class UpdateDownloadService
{
    private readonly HttpClient _client;
    public UpdateDownloadService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (!_client.DefaultRequestHeaders.UserAgent.Any()) _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Classic-Windows-IPTV-Player", "1.0"));
    }
    public async Task<StagedUpdate> StageAsync(string tag, Version currentVersion, string edition, string updatesRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        AuthenticatedUpdate.ValidateTag(tag); UpdateEdition.Validate(edition);
        AuthenticatedUpdate.RejectLinks(updatesRoot);
        Directory.CreateDirectory(updatesRoot);
        var stage = Path.Combine(updatesRoot, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            progress?.Report("Checking the update details...");
            var manifestName = AuthenticatedUpdate.ManifestName(edition);
            await DownloadAsync(tag, manifestName, Path.Combine(stage, manifestName), 1048576, null, deadline.Token);
            var manifest = AuthenticatedUpdate.VerifyManifest(await File.ReadAllBytesAsync(Path.Combine(stage, manifestName), deadline.Token), tag, currentVersion, edition);
            progress?.Report("Downloading the update...");
            await DownloadAsync(tag, manifest.Package, Path.Combine(stage, manifest.Package), manifest.PackageSize, progress, deadline.Token);
            progress?.Report("Verifying the download...");
            await Task.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var stream = new FileStream(Path.Combine(stage, manifest.Package), FileMode.Open, FileAccess.Read, FileShare.Read);
                AuthenticatedUpdate.VerifyAndExtract(stream, manifest, cancellationToken: deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
            }, deadline.Token);
            return new StagedUpdate(stage, manifest);
        }
        catch
        {
            // The stage is our newly created directory and contains only our download files.
            foreach (var path in Directory.GetFiles(stage)) { try { File.Delete(path); } catch (IOException) { } }
            try { Directory.Delete(stage); } catch (IOException) { }
            throw;
        }
    }
    private async Task DownloadAsync(string tag, string asset, string destination, long maximum, IProgress<string>? progress, CancellationToken token)
    {
        var uri = new Uri($"https://github.com/{AuthenticatedUpdate.Repository}/releases/download/{tag}/{asset}");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            ValidateDownloadUri(uri, tag);
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } location) throw new InvalidDataException("The update download redirect was empty.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("This release has no automatic-update package for this edition. Open this release on GitHub to download it manually.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The update download exceeded its size limit.");
            await using var body = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var bytes = new byte[65536]; long received = 0; int read;
            while ((read = await body.ReadAsync(bytes, token)) != 0)
            {
                received += read;
                if (received > maximum) throw new InvalidDataException("The update download exceeded its size limit.");
                await output.WriteAsync(bytes.AsMemory(0, read), token);
                progress?.Report($"Downloading update: {received / (1024 * 1024)} MiB of {maximum / (1024 * 1024)} MiB");
            }
            await output.FlushAsync(token);
            return;
        }
        throw new InvalidDataException("The update download redirected too many times.");
    }
    private static void ValidateDownloadUri(Uri uri, string tag)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0 && uri.AbsolutePath.StartsWith($"/{AuthenticatedUpdate.Repository}/releases/download/{tag}/", StringComparison.Ordinal)))
            throw new InvalidDataException("The update download redirected outside the trusted GitHub asset hosts.");
    }
}
