using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassicWindowsIptvPlayer.Core;

public enum FeedbackDeliveryStatus
{
    Sent,
    QueuedForRetry,
    NothingToSend,
    EndpointNotConfigured
}

public sealed record FeedbackQueueItem(string Id, string Type, string Message, string? Log, DateTimeOffset CreatedUtc);

public sealed record FeedbackDeliveryResult(FeedbackDeliveryStatus Status, int PendingCount)
{
    public string UserMessage => Status switch
    {
        FeedbackDeliveryStatus.Sent => "Report sent.",
        FeedbackDeliveryStatus.QueuedForRetry => "Could not send yet. Your report is saved and can be retried.",
        FeedbackDeliveryStatus.EndpointNotConfigured => "Your report is saved. Sending is not configured.",
        _ => "There is no pending report to send."
    };
}

/// <summary>Opt-in feedback delivery. No network request is made until SendNextAsync is explicitly called.</summary>
public sealed partial class FeedbackOutbox : IDisposable
{
    public const int MaxMessageUtf8Bytes = 8 * 1024;
    public const int MaxLogUtf8Bytes = 48 * 1024;
    public const int MaxPayloadUtf8Bytes = 64 * 1024;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Cyrus IPTV feedback outbox v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _path;
    private Uri? _endpoint;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FeedbackOutbox(string storagePath, Uri? endpoint = null, HttpClient? httpClient = null)
    {
        _path = Path.GetFullPath(storagePath ?? throw new ArgumentNullException(nameof(storagePath)));
        if (endpoint is not null) ConfigureEndpoint(endpoint);
        _client = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
    }

    public void ConfigureEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsoluteUri.Length > 2048)
            throw new ArgumentException("Feedback endpoint must use HTTPS.", nameof(endpoint));
        _endpoint = endpoint;
    }

    public Task<FeedbackQueueItem> EnqueueFeedbackAsync(string message, CancellationToken cancellationToken = default) =>
        EnqueueFeedbackAsync(message, null, cancellationToken);

    public async Task<FeedbackQueueItem> EnqueueFeedbackAsync(string message, string? log, CancellationToken cancellationToken = default)
    {
        var cleanMessage = FitUtf8(message ?? string.Empty, MaxMessageUtf8Bytes);
        if (string.IsNullOrWhiteSpace(cleanMessage)) throw new ArgumentException("A feedback message is required.", nameof(message));
        var cleanLog = log is null ? null : FitFeedbackLog(cleanMessage, SanitizeCrashLog(log));
        var item = new FeedbackQueueItem(Guid.NewGuid().ToString("N"), "feedback", cleanMessage, cleanLog, DateTimeOffset.UtcNow);
        await EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<bool> UpdateFeedbackAsync(string id, string message, string? log, CancellationToken cancellationToken = default)
    {
        var cleanMessage = FitUtf8(message ?? string.Empty, MaxMessageUtf8Bytes);
        if (string.IsNullOrWhiteSpace(cleanMessage)) throw new ArgumentException("A feedback message is required.", nameof(message));
        var cleanLog = log is null ? null : FitFeedbackLog(cleanMessage, SanitizeCrashLog(log));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            var index = items.FindIndex(x => x.Id == id && x.Type == "feedback");
            if (index < 0) return false;
            var old = items[index];
            var updated = old with { Message = cleanMessage, Log = cleanLog };
            EnsurePayloadFits(updated);
            items[index] = updated;
            Save(items);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<FeedbackQueueItem> EnqueueCrashAsync(string? message, string? log, CancellationToken cancellationToken = default)
    {
        var cleanMessage = FitUtf8(message ?? string.Empty, MaxMessageUtf8Bytes);
        var cleanLog = FitUtf8(SanitizeCrashLog(log ?? string.Empty), MaxLogUtf8Bytes);
        var item = new FeedbackQueueItem(Guid.NewGuid().ToString("N"), "crash", cleanMessage, cleanLog, DateTimeOffset.UtcNow);
        EnsurePayloadFits(item);
        await EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<FeedbackQueueItem>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Load(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> DismissAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            var removed = items.RemoveAll(x => x.Id == id) != 0;
            if (removed) Save(items);
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> UpdateMessageAsync(string id, string? message, CancellationToken cancellationToken = default)
    {
        var cleanMessage = FitUtf8(message ?? string.Empty, MaxMessageUtf8Bytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            var old = items[index];
            var updated = old with { Message = cleanMessage };
            EnsurePayloadFits(updated);
            items[index] = updated;
            Save(items);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicitly attempts delivery of the oldest queued item. 2xx removes it; any other result keeps it queued.</summary>
    public async Task<FeedbackDeliveryResult> SendNextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            if (items.Count == 0) return new(FeedbackDeliveryStatus.NothingToSend, 0);
            return await SendAtAsync(items, 0, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicitly attempts delivery of this specific item, so a new message never sends an older queued report by accident.</summary>
    public async Task<FeedbackDeliveryResult> SendAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) return new(FeedbackDeliveryStatus.NothingToSend, items.Count);
            return await SendAtAsync(items, index, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FeedbackDeliveryResult> SendAtAsync(List<FeedbackQueueItem> items, int index, CancellationToken cancellationToken)
    {
        if (_endpoint is null) return new(FeedbackDeliveryStatus.EndpointNotConfigured, items.Count);
        var item = items[index];
        var json = SerializePayload(item);
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadUtf8Bytes)
            return new(FeedbackDeliveryStatus.QueuedForRetry, items.Count);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, new UTF8Encoding(false), "application/json")
            };
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                items.RemoveAt(index);
                Save(items);
                return new(FeedbackDeliveryStatus.Sent, items.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* Keep the queued report; never log URLs, response bodies, or exception details. */ }
        return new(FeedbackDeliveryStatus.QueuedForRetry, items.Count);
    }

    private async Task EnqueueAsync(FeedbackQueueItem item, CancellationToken cancellationToken)
    {
        EnsurePayloadFits(item);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = Load();
            items.Add(item);
            Save(items);
        }
        finally { _gate.Release(); }
    }

    private List<FeedbackQueueItem> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var clear = WindowsDataProtection.Unprotect(encrypted, Entropy);
            return JsonSerializer.Deserialize<List<FeedbackQueueItem>>(clear, JsonOptions) ?? [];
        }
        catch
        {
            // Corrupt/unreadable queues fail closed. Do not overwrite them or disclose details.
            throw new InvalidOperationException("Feedback queue is unavailable.");
        }
    }

    private void Save(List<FeedbackQueueItem> items)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var bytes = WindowsDataProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(items, JsonOptions), Entropy);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string SerializePayload(FeedbackQueueItem item)
    {
        if (item.Type == "feedback") return JsonSerializer.Serialize(new FeedbackPayload("feedback", item.Message, item.Log), JsonOptions);
        return JsonSerializer.Serialize(new CrashPayload("crash", item.Log ?? string.Empty, string.IsNullOrEmpty(item.Message) ? null : item.Message), JsonOptions);
    }

    private static string FitFeedbackLog(string message, string log)
    {
        var cleanLog = FitUtf8(log, MaxLogUtf8Bytes);
        var item = new FeedbackQueueItem(string.Empty, "feedback", message, cleanLog, DateTimeOffset.MinValue);
        if (Encoding.UTF8.GetByteCount(SerializePayload(item)) <= MaxPayloadUtf8Bytes) return cleanLog;

        var runes = cleanLog.EnumerateRunes().ToArray();
        var low = 0;
        var high = runes.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var candidate = string.Concat(runes.Take(middle).Select(rune => rune.ToString()));
            item = item with { Log = candidate };
            if (Encoding.UTF8.GetByteCount(SerializePayload(item)) <= MaxPayloadUtf8Bytes) low = middle;
            else high = middle - 1;
        }
        return string.Concat(runes.Take(low).Select(rune => rune.ToString()));
    }

    private static void EnsurePayloadFits(FeedbackQueueItem item)
    {
        if (Encoding.UTF8.GetByteCount(SerializePayload(item)) > MaxPayloadUtf8Bytes)
            throw new ArgumentException("Feedback payload exceeds the size limit.");
    }

    private static string FitUtf8(string value, int limit)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var count = rune.Utf8SequenceLength;
            if (bytes + count > limit) break;
            result.Append(rune.ToString());
            bytes += count;
        }
        return result.ToString();
    }

    public static string SanitizeCrashLog(string log)
    {
        try
        {
            var text = log.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            text = UrlRegex().Replace(text, "[provider URL redacted]");
            text = BearerRegex().Replace(text, "$1[redacted]");
            text = JsonSecretRegex().Replace(text, "$1\"[redacted]\"$2");
            text = SecretRegex().Replace(text, "$1[redacted]");
            text = RecordingPathRegex().Replace(text, "[recording path redacted]");
            text = UserPathRegex().Replace(text, "[local path redacted]");
            return text;
        }
        catch { return "[log omitted because sensitive data could not be removed]"; }
    }

    [GeneratedRegex("""\b[a-z][a-z0-9+.-]*://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
    [GeneratedRegex(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
    [GeneratedRegex("""(?i)(\b(?:password|passwd|pwd|username|user|token|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|credential|auth|authorization|cookie|set-cookie|session|sid|secret)\b\s*(?:=|:|%3d)\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s&,;<>"']+)""")]
    private static partial Regex SecretRegex();
    [GeneratedRegex("""(?i)("(?:password|passwd|pwd|username|user|token|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|credential|auth|authorization|cookie|session|sid|secret)"\s*:\s*)"[^"\r\n]*"(\s*[,}])""")]
    private static partial Regex JsonSecretRegex();
    [GeneratedRegex("""(?i)(?:[A-Z]:\\[^\s"'<>]+|/Users/[^\s"'<>]+|/home/[^\s"'<>]+|\\\\[^\\\s]+\\[^\s"'<>]+)""")]
    private static partial Regex UserPathRegex();
    [GeneratedRegex("""(?i)(?:[A-Z]:\\[^\s"']*\\Recordings?(?:\\[^\s"']*)?|/(?:[^\s/]+/)*Recordings?(?:/[^\s]*)?)""")]
    private static partial Regex RecordingPathRegex();

    private sealed record FeedbackPayload(string Type, string Message, string? Log);
    private sealed record CrashPayload(string Type, string Log, string? Message);
    public void Dispose() { if (_ownsClient) _client.Dispose(); _gate.Dispose(); }
}
