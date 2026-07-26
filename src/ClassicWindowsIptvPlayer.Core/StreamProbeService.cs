using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Core;

public sealed record StreamProbeResult(
    PlaybackCandidate Candidate,
    bool LooksReachable,
    string Message,
    int? StatusCode,
    string ContentType);

public sealed class StreamProbeService
{
    private readonly HttpClient _httpClient;

    public StreamProbeService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    public async Task<IReadOnlyList<StreamProbeResult>> ProbeAsync(
        IReadOnlyList<PlaybackCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var results = new List<StreamProbeResult>();

        foreach (var candidate in candidates.Take(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProbeOneAsync(candidate, cancellationToken));
        }

        return results;
    }

    private async Task<StreamProbeResult> ProbeOneAsync(PlaybackCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
            request.Headers.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.Range = new RangeHeaderValue(0, 2047);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            var length = response.Content.Headers.ContentLength;
            var lengthText = length.HasValue ? $", {length.Value:N0} bytes" : string.Empty;

            if ((int)response.StatusCode == 416)
            {
                // Some live servers reject Range requests but can still play. Retry without Range.
                return await ProbeWithoutRangeAsync(candidate, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new StreamProbeResult(candidate, false, $"HTTP {status} {response.ReasonPhrase}", status, contentType);
            }

            if (IsProbablyErrorPage(contentType))
            {
                return new StreamProbeResult(candidate, false, $"HTTP {status}, but response looks like {contentType}{lengthText}", status, contentType);
            }

            return new StreamProbeResult(candidate, true, $"HTTP {status} OK, {contentType}{lengthText}", status, contentType);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StreamProbeResult(candidate, false, "Timed out while testing this URL", null, string.Empty);
        }
        catch (Exception ex)
        {
            return new StreamProbeResult(candidate, false, ex.Message, null, string.Empty);
        }
    }

    private async Task<StreamProbeResult> ProbeWithoutRangeAsync(PlaybackCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
            request.Headers.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");
            request.Headers.Accept.ParseAdd("*/*");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;

            if (!response.IsSuccessStatusCode)
            {
                return new StreamProbeResult(candidate, false, $"HTTP {status} {response.ReasonPhrase}", status, contentType);
            }

            if (IsProbablyErrorPage(contentType))
            {
                return new StreamProbeResult(candidate, false, $"HTTP {status}, but response looks like {contentType}", status, contentType);
            }

            return new StreamProbeResult(candidate, true, $"HTTP {status} OK, {contentType}", status, contentType);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StreamProbeResult(candidate, false, "Timed out while testing this URL", null, string.Empty);
        }
        catch (Exception ex)
        {
            return new StreamProbeResult(candidate, false, ex.Message, null, string.Empty);
        }
    }

    private static bool IsProbablyErrorPage(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        return contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);
    }
}
