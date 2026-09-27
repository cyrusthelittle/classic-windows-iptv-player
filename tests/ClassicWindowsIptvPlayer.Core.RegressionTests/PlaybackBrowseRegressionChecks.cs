using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class PlaybackBrowseRegressionChecks
{
    private static readonly Channel A = new() { Id = "synthetic-a", Name = "A", Url = "http://localhost/a" };
    private static readonly Channel B = new() { Id = "synthetic-b", Name = "B", Url = "http://localhost/b" };
    private static readonly PlaybackCandidate SourceA = new(A.Url, "A source");
    private static readonly PlaybackCandidate SourceB = new(B.Url, "B source");

    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("select B while A plays retains A's source, transport target and Now Playing", () =>
            {
                var state = new PlaybackBrowseState();
                var requestA = new object();
                state.Select(A);
                state.Start(A, [SourceA], 0, requestA);
                state.Select(B);
                Equal(B, state.SelectedChannel); // favorite target
                Equal(A, state.PlayingChannel); // restart, pause, stop and Now Playing target
                Equal(SourceA, state.PlayingCandidate); // URL and diagnostics source
                Equal(0, state.CandidateIndex);
                Equal(true, state.Accepts(requestA));
            }),
            ("A/B/A browsing never changes the active tune", () =>
            {
                var state = new PlaybackBrowseState();
                var request = new object();
                state.Start(A, [SourceA], 0, request);
                foreach (var selected in new[] { A, B, A, B, A }) state.Select(selected);
                Equal(A, state.SelectedChannel);
                Equal(A, state.PlayingChannel);
                Equal(SourceA, state.PlayingCandidate);
                Equal(true, state.Accepts(request));
            }),
            ("rapid A/B/A tunes reject both stale requests including old A", () =>
            {
                var state = new PlaybackBrowseState();
                var oldA = new object();
                var requestB = new object();
                var newA = new object();
                state.Start(A, [SourceA], 0, oldA);
                state.Start(B, [SourceB], 0, requestB);
                state.Start(A, [SourceA], 0, newA);
                Equal(false, state.Accepts(oldA));
                Equal(false, state.Accepts(requestB));
                Equal(true, state.Accepts(newA));
                Equal(A, state.PlayingChannel);
                Equal(SourceA, state.PlayingCandidate);
            }),
            ("same-channel restart rejects the earlier request", () =>
            {
                var state = new PlaybackBrowseState();
                var oldRequest = new object();
                var newRequest = new object();
                state.Start(A, [SourceA], 0, oldRequest);
                state.Start(A, [SourceA], 0, newRequest);
                Equal(false, state.Accepts(oldRequest));
                Equal(true, state.Accepts(newRequest));
            }),
            ("stop rejects late events and reset clears account playback", () =>
            {
                var state = new PlaybackBrowseState();
                var request = new object();
                state.Select(B);
                state.Start(A, [SourceA], 0, request);
                state.Stop();
                Equal(false, state.Accepts(request));
                Equal(A, state.PlayingChannel);
                Equal(SourceA, state.PlayingCandidate);
                state.Reset();
                Equal<Channel?>(null, state.SelectedChannel);
                Equal<Channel?>(null, state.PlayingChannel);
                Equal<PlaybackCandidate?>(null, state.PlayingCandidate);
            })
        };

        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} playback browse checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
