using System.Net;
using System.Text;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class FeedbackOutboxRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-feedback-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (!AppState.TryValidateFeedbackEndpoint("https://feedback.invalid/webhook", out _) ||
                AppState.TryValidateFeedbackEndpoint("http://feedback.invalid/webhook", out _) ||
                AppState.TryValidateFeedbackEndpoint("https://user:pass@feedback.invalid/webhook", out _) ||
                AppState.TryValidateFeedbackEndpoint("https://feedback.invalid/" + new string('x', AppState.FeedbackEndpointMaxLength), out _))
                return Fail("endpoint accepts only bounded HTTPS URLs without credentials");

            var config = new ConfigStore(Path.Combine(root, "settings"));
            var appState = config.Load();
            appState.FeedbackEndpoint = "https://feedback.invalid/webhook/secret-token";
            config.Save(appState);
            if (File.ReadAllText(config.StatePath).Contains(appState.FeedbackEndpoint, StringComparison.Ordinal) ||
                config.Load().FeedbackEndpoint != appState.FeedbackEndpoint)
                return Fail("endpoint is protected at rest and survives reload");

            var path = Path.Combine(root, "outbox.dat");
            using var failedHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            using (var outbox = new FeedbackOutbox(path, httpClient: failedHttp))
            {
                await outbox.EnqueueFeedbackAsync("My message");
                var fileBytes = await File.ReadAllBytesAsync(path);
                if (Encoding.UTF8.GetString(fileBytes).Contains("My message", StringComparison.Ordinal)) return Fail("outbox is protected at rest");
                var unconfigured = await outbox.SendNextAsync();
                if (unconfigured.Status != FeedbackDeliveryStatus.EndpointNotConfigured || unconfigured.PendingCount != 1) return Fail("unconfigured endpoint keeps reports pending without sending");
                outbox.ConfigureEndpoint(new Uri("http://127.0.0.1/mock"));
                var result = await outbox.SendNextAsync();
                if (result.Status != FeedbackDeliveryStatus.QueuedForRetry || result.PendingCount != 1) return Fail("failed delivery remains queued");
            }

            string? received = null;
            using var successHttp = new HttpClient(new MockHandler(async request =>
            {
                received = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }));
            using (var outbox = new FeedbackOutbox(path, new Uri("http://127.0.0.1/mock"), successHttp))
            {
                var result = await outbox.SendNextAsync();
                if (result.Status != FeedbackDeliveryStatus.Sent || result.PendingCount != 0) return Fail("explicit retry removes successful delivery");
            }
            using (var json = JsonDocument.Parse(received!))
            {
                var rootObject = json.RootElement;
                if (rootObject.GetProperty("type").GetString() != "feedback" || rootObject.GetProperty("message").GetString() != "My message" || rootObject.EnumerateObject().Count() != 2)
                    return Fail("feedback without log has only type and message");
            }

            var raw = "2026-09-29 10:30:00 [ERROR] login https://provider.example/live/alice/pw/22.ts?token=abc " +
                      "Authorization: Bearer supersecret {\"password\":\"secret\"} C:\\Users\\Alice\\app.log C:\\App\\Recordings\\x.ts";
            var sanitized = FeedbackOutbox.SanitizeCrashLog(raw);
            foreach (var secret in new[] { "provider.example", "alice", "pw", "abc", "supersecret", "secret", "Alice", "x.ts" })
                if (sanitized.Contains(secret, StringComparison.OrdinalIgnoreCase)) return Fail("crash log redaction: " + secret);
            if (!sanitized.Contains("2026-09-29 10:30:00 [ERROR]", StringComparison.Ordinal)) return Fail("crash redaction preserves timestamp and level");

            var feedbackLogPath = Path.Combine(root, "feedback-log.dat");
            using var feedbackFailureHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            using (var outbox = new FeedbackOutbox(feedbackLogPath, new Uri("http://127.0.0.1/mock"), feedbackFailureHttp))
            {
                var feedback = await outbox.EnqueueFeedbackAsync("Logs help", raw);
                if (feedback.Log is null || Encoding.UTF8.GetByteCount(feedback.Log) > FeedbackOutbox.MaxLogUtf8Bytes)
                    return Fail("opted-in feedback log is stored with its byte limit");
                if (new[] { "provider.example", "alice", "pw", "abc", "supersecret", "secret", "Alice", "x.ts" }
                    .Any(secret => feedback.Log.Contains(secret, StringComparison.OrdinalIgnoreCase)))
                    return Fail("opted-in feedback log is redacted before queueing");
                var failed = await outbox.SendNextAsync();
                if (failed.Status != FeedbackDeliveryStatus.QueuedForRetry || failed.PendingCount != 1)
                    return Fail("feedback with logs remains queued after a failed send");
                if ((await outbox.GetPendingAsync()).Single().Log != feedback.Log)
                    return Fail("failed retry preserves the user's log-inclusion choice");
            }

            string? feedbackWithLogPayload = null;
            using var feedbackSuccessHttp = new HttpClient(new MockHandler(async request =>
            {
                feedbackWithLogPayload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }));
            using (var outbox = new FeedbackOutbox(feedbackLogPath, new Uri("http://127.0.0.1/mock"), feedbackSuccessHttp))
            {
                var result = await outbox.SendNextAsync();
                if (result.Status != FeedbackDeliveryStatus.Sent) return Fail("explicit retry sends opted-in feedback logs");
            }
            using (var json = JsonDocument.Parse(feedbackWithLogPayload!))
            {
                var rootObject = json.RootElement;
                if (rootObject.GetProperty("type").GetString() != "feedback" ||
                    rootObject.GetProperty("message").GetString() != "Logs help" ||
                    !rootObject.TryGetProperty("log", out var feedbackLog) ||
                    feedbackLog.GetString() != sanitized ||
                    Encoding.UTF8.GetByteCount(feedbackWithLogPayload!) > FeedbackOutbox.MaxPayloadUtf8Bytes)
                    return Fail("opted-in feedback schema includes only the redacted bounded log");
            }

            using (var outbox = new FeedbackOutbox(Path.Combine(root, "toggle-log.dat")))
            {
                var feedback = await outbox.EnqueueFeedbackAsync("Logs help", raw);
                if (!await outbox.UpdateFeedbackAsync(feedback.Id, "Logs help", null))
                    return Fail("user can remove logs from a queued feedback item before retry");
                if ((await outbox.GetPendingAsync()).Single().Log is not null)
                    return Fail("unchecking logs removes them from the queued payload");
            }

            using var crashHttp = new HttpClient(new MockHandler(async request =>
            {
                received = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            using (var outbox = new FeedbackOutbox(path, new Uri("http://127.0.0.1/mock"), crashHttp))
            {
                var crash = await outbox.EnqueueCrashAsync("optional", raw);
                if (Encoding.UTF8.GetByteCount(crash.Log!) > FeedbackOutbox.MaxLogUtf8Bytes) return Fail("log byte limit");
                var result = await outbox.SendNextAsync();
                if (result.Status != FeedbackDeliveryStatus.Sent) return Fail("crash submission sends after explicit action");
            }
            using (var json = JsonDocument.Parse(received!))
            {
                var rootObject = json.RootElement;
                if (rootObject.GetProperty("type").GetString() != "crash" || !rootObject.TryGetProperty("log", out _) || rootObject.GetProperty("message").GetString() != "optional")
                    return Fail("crash schema includes type, log, and message");
            }

            using (var declined = new FeedbackOutbox(Path.Combine(root, "declined.dat")))
            {
                var report = await declined.EnqueueCrashAsync(null, raw);
                if (!await declined.DismissAsync(report.Id) || (await declined.GetPendingAsync()).Count != 0)
                    return Fail("explicit decline removes only the selected queued report");
            }
            var longMessage = "🙂".Repeat(10_000);
            using (var limitHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
            using (var outbox = new FeedbackOutbox(Path.Combine(root, "limit.dat"), httpClient: limitHttp))
            {
                var queued = await outbox.EnqueueFeedbackAsync(longMessage);
                if (Encoding.UTF8.GetByteCount(queued.Message) > FeedbackOutbox.MaxMessageUtf8Bytes) return Fail("strict UTF-8 message byte limit");
            }
            Console.WriteLine("Feedback outbox checks: encrypted queue, redaction, schema, byte limits, and mock retry passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL feedback outbox regression: " + ex.GetType().Name);
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static int Fail(string check)
    {
        Console.Error.WriteLine("FAIL feedback outbox: " + check);
        return 1;
    }

    private sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static string Repeat(this string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
