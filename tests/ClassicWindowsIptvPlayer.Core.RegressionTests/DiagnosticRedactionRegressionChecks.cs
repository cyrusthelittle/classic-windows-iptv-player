using ClassicWindowsIptvPlayer.Core;
using System.Net;

internal static class DiagnosticRedactionRegressionChecks
{
    private static readonly (string Name, Func<string> Output, string[] Secrets)[] Checks =
    [
        ("query credentials", () => AppLogger.SanitizeUrl("https://example.invalid/get.php?username=fictional-user-71&password=fictional-pass-72&token=fictional-token-73&mode=live"),
            ["fictional-user-71", "fictional-pass-72", "fictional-token-73"]),
        ("encoded query names and values", () => AppLogger.SanitizeUrl("https://example.invalid/get.php?user%6Eame=fictional%2Duser%2D81&password=fictional%2Dpass%2D82&api_key=fictional%2Dkey%2D83"),
            ["fictional-user-81", "fictional-pass-82", "fictional-key-83", "fictional%2Duser%2D81", "fictional%2Dpass%2D82", "fictional%2Dkey%2D83"]),
        ("stream path credentials", () => AppLogger.SanitizeUrl("https://example.invalid/live/fictional-user-91/fictional-pass-92/123.ts"),
            ["fictional-user-91", "fictional-pass-92"]),
        ("encoded stream path credentials", () => AppLogger.SanitizeUrl("https://example.invalid/movie/fictional%2Duser%2D101/fictional%2Dpass%2D102/456.mp4"),
            ["fictional-user-101", "fictional-pass-102", "fictional%2Duser%2D101", "fictional%2Dpass%2D102"]),
        ("malformed URL and loose assignments", () => AppLogger.SanitizeText("Bad URL http://[invalid/get.php?username=fictional-user-111&password=fictional-pass-112; token: fictional-token-113"),
            ["fictional-user-111", "fictional-pass-112", "fictional-token-113"]),
        ("malformed encoded assignments and path", () => AppLogger.SanitizeText("Bad URL http://[invalid/live/fictional-user-115/fictional-pass-116/12.ts?user%6Eame%3Dfictional%2Duser%2D117"),
            ["fictional-user-115", "fictional-pass-116", "fictional-user-117", "fictional%2Duser%2D117"]),
        ("exception and nested exception strings", () => AppLogger.SanitizeException(new InvalidOperationException("outer password=fictional-pass-122", new Exception("inner https://example.invalid/live/fictional-user-121/fictional-pass-123/12.ts?token=fictional-token-124"))),
            ["fictional-user-121", "fictional-pass-122", "fictional-pass-123", "fictional-token-124"]),
        ("source summary fields", () => AppLogger.DescribeChannel(new Channel { Name = "token=fictional-token-131", Group = "password=fictional-pass-132", Url = "https://example.invalid/live/fictional-user-133/fictional-pass-134/99.ts" }),
            ["fictional-token-131", "fictional-pass-132", "fictional-user-133", "fictional-pass-134"]),
        ("log message and exception", LogMessageAndException,
            ["fictional-user-141", "fictional-pass-142", "fictional-token-143"])
    ];

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, output, secrets) in Checks)
        {
            try
            {
                var result = output();
                foreach (var secret in secrets)
                    if (result.Contains(secret, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A synthetic credential remained in output.");
                if (!result.Contains("***", StringComparison.Ordinal))
                    throw new InvalidOperationException("Output has no redaction marker.");
                Console.WriteLine("PASS diagnostic: " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL diagnostic: " + name + " — " + ex.Message);
            }
        }
        foreach (var (name, handler, secret) in new (string, HttpMessageHandler, string)[]
        {
            ("source probe exception", new FixtureHandler(_ => throw new HttpRequestException("token=fictional-token-151")), "fictional-token-151"),
            ("source probe HTTP reason", new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { ReasonPhrase = "password=fictional-pass-152" }), "fictional-pass-152")
        })
        {
            try
            {
                using var client = new HttpClient(handler);
                var probe = new StreamProbeService(client);
                var candidate = new PlaybackCandidate("https://example.invalid/live/fictional-user-153/fictional-pass-154/1.ts", "Synthetic source");
                var result = (await probe.ProbeAsync([candidate], CancellationToken.None))[0];
                if (result.Message.Contains(secret, StringComparison.OrdinalIgnoreCase) || !result.Message.Contains("***", StringComparison.Ordinal))
                    throw new InvalidOperationException("A synthetic credential remained in a probe result.");
                Console.WriteLine("PASS diagnostic: " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL diagnostic: " + name + " — " + ex.Message);
            }
        }
        Console.WriteLine($"Diagnostic redaction: {Checks.Length + 2 - failed}/{Checks.Length + 2} passed.");
        return failed == 0 ? 0 : 1;
    }

    private static string LogMessageAndException()
    {
        AppLogger.Error("Synthetic failure username=fictional-user-141", new Exception("password=fictional-pass-142; token=fictional-token-143"));
        return File.ReadAllText(AppLogger.CurrentLogPath);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
