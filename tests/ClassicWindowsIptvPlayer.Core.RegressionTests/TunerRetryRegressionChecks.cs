using ClassicWindowsIptvPlayer.Windows;

internal static class TunerRetryRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("initial connection failures keep bounded retries and capped delays", () =>
            {
                var budget = new TunerRetryBudget(6);
                foreach (var (attempt, delay) in new[] { (1, 250), (2, 300), (3, 400), (4, 500), (5, 500) })
                {
                    Equal(attempt, budget.BeginAttempt());
                    Equal<int?>(delay, budget.NextDelayMs(null));
                }
                Equal(6, budget.BeginAttempt());
                Equal<int?>(null, budget.NextDelayMs(null));
                Equal(false, budget.CanAttempt);
                return Task.CompletedTask;
            }),
            ("31-second live drop gets a fresh first retry and full budget", () =>
            {
                var budget = new TunerRetryBudget(3);
                Equal(1, budget.BeginAttempt());
                Equal<int?>(250, budget.NextDelayMs(null));
                Equal(2, budget.BeginAttempt());
                Equal<int?>(250, budget.NextDelayMs(TimeSpan.FromSeconds(31)));
                Equal(1, budget.BeginAttempt());
                Equal<int?>(250, budget.NextDelayMs(null));
                Equal(2, budget.BeginAttempt());
                Equal<int?>(300, budget.NextDelayMs(null));
                Equal(3, budget.BeginAttempt());
                Equal<int?>(null, budget.NextDelayMs(null));
                return Task.CompletedTask;
            }),
            ("30-second threshold resets exhausted budget but short drop does not", () =>
            {
                var budget = new TunerRetryBudget(1);
                Equal(1, budget.BeginAttempt());
                Equal<int?>(null, budget.NextDelayMs(TimeSpan.FromSeconds(29)));
                Equal<int?>(250, budget.NextDelayMs(TimeSpan.FromSeconds(30)));
                Equal(1, budget.BeginAttempt());
                return Task.CompletedTask;
            }),
            ("Stop cancels a pending recovery delay", async () =>
            {
                using var stopToken = new CancellationTokenSource();
                var pending = TunerRetryBudget.WaitForRetryAsync(10_000, stopToken.Token, () => true);
                stopToken.Cancel();
                Equal(false, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            }),
            ("newer tune cancels recovery and stale generation cannot resume", async () =>
            {
                using var oldTuneToken = new CancellationTokenSource();
                var generation = 1;
                var pending = TunerRetryBudget.WaitForRetryAsync(10_000, oldTuneToken.Token, () => generation == 1);
                generation = 2;
                oldTuneToken.Cancel();
                Equal(false, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
                Equal(false, await TunerRetryBudget.WaitForRetryAsync(0, CancellationToken.None, () => generation == 1));
            })
        };

        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                await run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} tuner checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
