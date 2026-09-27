using System.Collections;
using System.Diagnostics;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class LargeLibraryRegressionChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        var channels = Enumerable.Range(0, 100_001).Select(i => new Channel
        {
            Id = i.ToString(), Name = i == 100_000 ? "Unique tail station" : $"Sample station {i:D6}",
            Group = i % 2 == 0 ? "News" : "Movies", MediaKind = i % 2 == 0 ? MediaKind.Live : MediaKind.Movie
        }).ToList();
        var timer = Stopwatch.StartNew();
        var index = new MediaSearchIndex(channels);
        timer.Stop();
        Console.WriteLine($"Synthetic index 100,001 items: {timer.Elapsed.TotalMilliseconds:F1} ms");

        Check("result after former 50,000 ceiling is reachable", () =>
        {
            var all = index.SearchAll("", null, CancellationToken.None).ToList();
            Equal(100_001, all.Count);
            Equal("100000", all[^1].Id);
            Equal("100000", index.SearchAll("unique tail", MediaKind.Live, CancellationToken.None).Single().Id);
        });
        Check("kind and multiple search terms retain exact matches", () =>
        {
            Equal(0, index.SearchAll("unique tail", MediaKind.Movie, CancellationToken.None).Count());
            Equal("99999", index.SearchAll("sample 099999", MediaKind.Movie, CancellationToken.None).Single().Id);
        });
        Check("canceled search cannot enumerate results", () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { index.SearchAll("station", null, cts.Token).ToList(); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Search ignored cancellation.");
        });
        Check("virtual list creates only accessed rows and preserves stable row identity", () =>
        {
            var created = 0;
            var rows = new LazyList<Channel>(channels.Count, i => { created++; return channels[i]; });
            Equal(100_001, rows.Count);
            Equal(0, created);
            Equal("100000", ((IList)rows)[100_000] is Channel tail ? tail.Id : "missing");
            Equal(1, created);
            Equal("100000", rows[100_000].Id);
            Equal(1, created);
        });

        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            timer.Restart();
            var matches = index.SearchAll(i % 2 == 0 ? "sample station 09" : "unique tail", null, CancellationToken.None).ToList();
            timer.Stop();
            if (matches.Count == 0) failures++;
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine($"Synthetic search 100,001 items, 30 runs: p95 {samples[28]:F1} ms; max {samples[^1]:F1} ms (process-local, excludes WPF and disk)");
        Console.WriteLine($"{4 - failures}/4 large-library checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
}
