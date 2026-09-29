using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ClassicWindowsIptvPlayer.Core;

public enum CatchupFailure { None, Unsupported, InvalidProgramme, Future, Expired, TimezoneUnknown, AmbiguousTime, ConnectionLimit, ConnectionUnknown, InvalidAccount }
public enum CatchupStreamFailure { None, Unavailable, AccessDenied, Network }
public sealed record CatchupStreamCheck(bool IsAvailable, CatchupStreamFailure Failure, string Message, HttpStatusCode? StatusCode = null);
public sealed record CatchupAvailability(bool IsAvailable, bool IsStartOver, string Message, CatchupFailure Failure);
public sealed record CatchupRequest(bool IsAvailable, string Url, string Message, CatchupFailure Failure,
    int DurationMinutes = 0, DateTimeOffset StartUtc = default, DateTimeOffset EndUtc = default);

/// <summary>Explicit Xtream archive support. Never infer archives from an ordinary live URL.</summary>
public static class Catchup
{
    public static bool HasSupportedArchive(Channel channel) => channel.MediaKind == MediaKind.Live &&
        channel.CatchupMode == CatchupMode.Xtream && channel.ArchiveDays is > 0 and <= 3650 && NumericId(channel.ArchiveStreamId);
    public static CatchupAvailability Evaluate(Channel channel, EpgProgramme programme, DateTimeOffset now)
    {
        CatchupAvailability No(CatchupFailure failure, string message) => new(false, false, message, failure);
        if (!HasSupportedArchive(channel))
            return No(CatchupFailure.Unsupported, "This channel does not advertise supported Xtream archives.");
        if (programme.Stop <= programme.Start) return No(CatchupFailure.InvalidProgramme, "The programme has invalid guide times.");
        if (programme.Start >= now) return No(CatchupFailure.Future, "This programme has not started yet.");
        // Require the complete beginning of the programme, rather than silently
        // playing only the tail of a partially expired archive.
        if (programme.Start < now.AddDays(-channel.ArchiveDays)) return No(CatchupFailure.Expired, "This programme is outside the provider's advertised archive window.");
        var current = programme.Stop > now;
        return new(true, current, current ? "Start Over" : "Watch archive", CatchupFailure.None);
    }

    public static CatchupRequest CreateRequest(Channel channel, EpgProgramme programme, AccountSettings account,
        AccountProfile profile, DateTimeOffset now, bool replacingCurrentConnection = false)
    {
        CatchupRequest No(CatchupFailure failure, string message) => new(false, "", message, failure);
        var availability = Evaluate(channel, programme, now);
        if (!availability.IsAvailable) return No(availability.Failure, availability.Message);
        var resolved = PlaylistService.ResolveCatchupAccount(account);
        if (resolved is null) return No(CatchupFailure.InvalidAccount, "Xtream account credentials are required for archives.");
        var server = resolved.ServerUrl.Trim();
        if (!server.Contains("://", StringComparison.Ordinal)) server = "http://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo))
            return No(CatchupFailure.InvalidAccount, "The Xtream server address is invalid.");
        TimeZoneInfo zone;
        try { if (string.IsNullOrWhiteSpace(profile.Timezone)) throw new TimeZoneNotFoundException(); zone = TimeZoneInfo.FindSystemTimeZoneById(profile.Timezone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return No(CatchupFailure.TimezoneUnknown, "The provider archive timezone is missing or unsupported; archive playback cannot safely choose a start time."); }
        if (!ConnectionBudget.TryParseAllowance(profile, out var active, out var max))
            return No(CatchupFailure.ConnectionUnknown, "The provider did not report a supported connection allowance; archive playback cannot safely open another stream.");
        if (Math.Max(0, active - (replacingCurrentConnection ? 1 : 0)) >= max)
            return No(CatchupFailure.ConnectionLimit, "The provider's connection allowance is already in use.");
        var start = programme.Start.ToUniversalTime();
        // Xtream has minute precision. Floor start and ceil duration to cover the
        // whole programme. Retention is checked again after this rounding.
        start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerMinute));
        if (start < now.AddDays(-channel.ArchiveDays)) return No(CatchupFailure.Expired, "The programme start is outside the provider's minute-precision archive window.");
        var end = programme.Stop < now ? programme.Stop.ToUniversalTime() : now.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(start, zone);
        if (zone.IsAmbiguousTime(local.DateTime)) return No(CatchupFailure.AmbiguousTime, "This programme starts in a repeated daylight-saving hour that the provider's archive URL cannot distinguish.");
        var duration = Math.Max(1, (int)Math.Ceiling((end - start).TotalMinutes));
        var baseUrl = new UriBuilder(origin.Scheme, origin.Host, origin.Port) { Path = "/", Query = "", Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
        var url = $"{baseUrl}/timeshift/{Uri.EscapeDataString(resolved.Username.Trim())}/{Uri.EscapeDataString(resolved.Password.Trim())}/{duration.ToString(CultureInfo.InvariantCulture)}/{local.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture)}/{channel.ArchiveStreamId}.ts";
        return new(true, url, availability.Message, CatchupFailure.None, duration, start, end);
    }

    // Caller must retire its existing media connection before probing. This
    // validates HTTP availability, not successful decoding or a complete body.
    public static async Task<CatchupStreamCheck> CheckStreamAsync(CatchupRequest request, HttpClient client, CancellationToken token)
    {
        if (!request.IsAvailable || string.IsNullOrEmpty(request.Url))
            return new(false, CatchupStreamFailure.Unavailable, request.Message);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new(true, CatchupStreamFailure.None, "Archive response received.", response.StatusCode);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Gone => new(false, CatchupStreamFailure.Unavailable, "The provider has no archive at this time; advertised retention does not guarantee a recording.", response.StatusCode),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(false, CatchupStreamFailure.AccessDenied, "The provider refused archive access. Check account permission and connection allowance.", response.StatusCode),
                _ => new(false, CatchupStreamFailure.Network, "The provider could not serve the archive. Try again; this does not establish that the archive expired.", response.StatusCode)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, CatchupStreamFailure.Network, "The archive availability request timed out. Try again."); }
        catch (HttpRequestException)
        { return new(false, CatchupStreamFailure.Network, "The archive availability request failed. Check the network or try again."); }
    }

    public static string FailureMessage(Channel channel, EpgProgramme programme, DateTimeOffset now, HttpStatusCode? status = null)
    {
        var availability = Evaluate(channel, programme, now);
        if (availability.Failure == CatchupFailure.Expired) return availability.Message;
        return status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => "The provider has no archive at this time. Its advertised archive window may not match recorded availability.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The provider refused archive access. Check account permission and connection allowance.",
            _ => "Archive playback failed. Check the network or try again; this does not establish that the archive expired."
        };
    }

    internal static bool NumericId(string id) => !string.IsNullOrEmpty(id) && id.All(c => c is >= '0' and <= '9');
    internal static void ReadProviderMetadata(Channel channel, JsonElement item)
    {
        string Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : "";
        if (channel.MediaKind == MediaKind.Live && Text("tv_archive") == "1" && NumericId(Text("stream_id")) &&
            int.TryParse(Text("tv_archive_duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is > 0 and <= 3650)
        { channel.CatchupMode = CatchupMode.Xtream; channel.ArchiveDays = days; channel.ArchiveStreamId = Text("stream_id"); }
    }
}
