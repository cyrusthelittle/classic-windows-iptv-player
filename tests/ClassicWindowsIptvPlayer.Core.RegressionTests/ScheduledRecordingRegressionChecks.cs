using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class ScheduledRecordingRegressionChecks
{
    private static readonly DateTimeOffset ProgrammeStart = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("valid UTC programme and padded capture window is accepted", ValidWindow),
            ("zero-length, inverted, offset, and mismatched windows are rejected", InvalidWindows),
            ("destination paths are absolute and correlated recording IDs stay secret free", DestinationAndRecordingCorrelation),
            ("negative and overflowing padding are rejected", InvalidPadding),
            ("overlaps conflict only within the same account and active jobs", ConflictClassification),
            ("overlap never overwrites an already recording job's state", ActiveCaptureState),
            ("interval sweep matches pairwise overlap semantics with deterministic output", SweepSemanticsAndOrdering),
            ("large dense overlap component is classified without pairwise scanning", DenseOverlapComponent),
            ("conflicts are re-evaluated as the active job set changes", ConflictReevaluation),
            ("expired and still-open interrupted jobs have deterministic recovery", RecoveryClassification),
            ("scheduled index is account scoped, versioned, and secret free", PersistenceAndSecretFree),
            ("scheduled index recovers its atomic backup", BackupRecovery),
            ("scheduled jobs are preserved by backup and restore", BackupRoundTrip),
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS scheduled recording: " + name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL scheduled recording: " + name + " - " + exception); }
        }
        Console.WriteLine($"Scheduled recording checks: {checks.Length - failures}/{checks.Length} passed");
        return failures;
    }

    private static void ValidWindow()
    {
        var job = Job("one", "a");
        Check(ScheduledRecordingPolicy.Validate(job).IsValid, ScheduledRecordingPolicy.Validate(job).Reason);
        Equal(ProgrammeStart.AddMinutes(-5), job.RequestedCaptureStartUtc);
        Equal(ProgrammeStart.AddHours(1).AddMinutes(10), job.RequestedCaptureEndUtc);
        Check(Path.IsPathFullyQualified(job.DestinationPath), "sample destination is not absolute");
    }

    private static void DestinationAndRecordingCorrelation()
    {
        var job = Job("one", "a");
        Reject(job with { DestinationPath = "recordings\\show.ts" });
        Reject(job with { DestinationPath = "https://provider.example/live?token=secret" });
        Reject(job with { DestinationPath = Path.Combine(Path.GetTempPath(), "recordings", "token-show.ts") });
        Reject(job with { RecordingId = "https://provider.example/live" });
        Reject(job with { RecordingId = "user-password-secret" });

        var linked = job with { RecordingId = "recording-42", Status = ScheduledRecordingStatus.Recording };
        Check(ScheduledRecordingPolicy.Validate(linked).IsValid, "safe recording correlation ID was rejected");
        Equal("recording-42", linked.RecordingId);
    }

    private static void InvalidWindows()
    {
        var job = Job("one", "a");
        Reject(job with { ProgrammeEndUtc = job.ProgrammeStartUtc });
        Reject(job with { ProgrammeEndUtc = job.ProgrammeStartUtc.AddHours(-1) });
        Reject(job with { ChannelId = string.Empty });
        Reject(job with { ProgrammeStartUtc = ProgrammeStart.ToOffset(TimeSpan.FromHours(1)) });
        Reject(job with { RequestedCaptureEndUtc = job.RequestedCaptureEndUtc.AddSeconds(1) });
        Reject(job with { RequestedCaptureStartUtc = job.RequestedCaptureStartUtc.AddSeconds(1) });
    }

    private static void InvalidPadding()
    {
        var job = Job("one", "a");
        Reject(job with { PrePadding = TimeSpan.FromSeconds(-1) });
        Reject(job with { PostPadding = TimeSpan.FromSeconds(-1) });
        var nearMax = job with
        {
            ProgrammeStartUtc = DateTimeOffset.MaxValue.AddMinutes(-1),
            ProgrammeEndUtc = DateTimeOffset.MaxValue,
            PrePadding = TimeSpan.Zero,
            PostPadding = TimeSpan.FromTicks(1),
            RequestedCaptureStartUtc = DateTimeOffset.MaxValue.AddMinutes(-1),
            RequestedCaptureEndUtc = DateTimeOffset.MaxValue
        };
        Reject(nearMax);
    }

    private static void ConflictClassification()
    {
        var first = Job("one", "a");
        var overlap = Job("one", "b", ProgrammeStart.AddMinutes(30));
        var otherAccount = Job("two", "c", ProgrammeStart.AddMinutes(30));
        var separated = Job("one", "d", ProgrammeStart.AddHours(2));
        var cancelled = Job("one", "e", ProgrammeStart.AddMinutes(30)) with { Status = ScheduledRecordingStatus.Cancelled };
        var results = ScheduledRecordingPolicy.ClassifyConflicts([first, overlap, otherAccount, separated, cancelled]);
        Equal(ScheduledRecordingStatus.Conflict, Find(results, "a").Status);
        Equal(ScheduledRecordingStatus.Conflict, Find(results, "b").Status);
        Equal(ScheduledRecordingStatus.Scheduled, Find(results, "c").Status);
        Equal(ScheduledRecordingStatus.Scheduled, Find(results, "d").Status);
        Equal(ScheduledRecordingStatus.Cancelled, Find(results, "e").Status);
        Check(Find(results, "a").Reason.Contains("overlaps", StringComparison.OrdinalIgnoreCase), "conflict reason missing");
    }

    private static void RecoveryClassification()
    {
        var active = Job("one", "active") with { Status = ScheduledRecordingStatus.Recording, RecordingId = "capture-active" };
        var open = ScheduledRecordingPolicy.Recover(active, ProgrammeStart.AddMinutes(30));
        Equal(ScheduledRecordingRecovery.ResumeIfWindowOpen, open.Classification);
        Equal(ScheduledRecordingStatus.Scheduled, open.Job.Status);
        var expired = ScheduledRecordingPolicy.Recover(active, active.RequestedCaptureEndUtc);
        Equal(ScheduledRecordingRecovery.MissedWindowExpired, expired.Classification);
        Equal(ScheduledRecordingStatus.Missed, expired.Job.Status);
        Equal(ScheduledRecordingRecovery.None, ScheduledRecordingPolicy.Recover(Job("one", "later"), ProgrammeStart).Classification);
    }

    private static void ActiveCaptureState()
    {
        var active = Job("one", "active") with { Status = ScheduledRecordingStatus.Recording, RecordingId = "capture-active" };
        var upcoming = Job("one", "upcoming", ProgrammeStart.AddMinutes(30));
        var results = ScheduledRecordingPolicy.ClassifyConflicts([active, upcoming]);
        Equal(ScheduledRecordingStatus.Recording, Find(results, "active").Status);
        Equal("capture-active", Find(results, "active").RecordingId);
        Equal(ScheduledRecordingStatus.Conflict, Find(results, "upcoming").Status);
    }

    private static void ConflictReevaluation()
    {
        var index = new ScheduledRecordingIndex();
        Check(index.Add(Job("one", "a")), "first job refused");
        Check(index.Add(Job("one", "b", ProgrammeStart.AddMinutes(30))), "second job refused");
        Check(index.Add(Job("one", "c", ProgrammeStart.AddMinutes(45))), "third overlapping job refused");
        Equal(3, index.Jobs.Count(job => job.Status == ScheduledRecordingStatus.Conflict));
        index.Jobs = [.. index.Jobs.Where(job => job.Id != "a")];
        index.Jobs = [.. ScheduledRecordingPolicy.ClassifyConflicts(index.Jobs)];
        Equal(2, index.Jobs.Count(job => job.Status == ScheduledRecordingStatus.Conflict));
        index.Jobs = [.. index.Jobs.Where(job => job.Id != "c")];
        index.Jobs = [.. ScheduledRecordingPolicy.ClassifyConflicts(index.Jobs)];
        Equal(ScheduledRecordingStatus.Scheduled, index.Jobs.Single().Status);
    }

    private static void SweepSemanticsAndOrdering()
    {
        var jobs = new[]
        {
            Job("Acct", "recording", ProgrammeStart) with { Status = ScheduledRecordingStatus.Recording, Reason = "preserve recording" },
            Job("acct", "touching", ProgrammeStart.AddHours(1).AddMinutes(15)),
            Job("ACCT", "overlap-a", ProgrammeStart.AddMinutes(10)),
            Job("other", "other-a", ProgrammeStart.AddMinutes(10)),
            Job("acct", "overlap-b", ProgrammeStart.AddMinutes(30)),
            Job("acct", "cancelled", ProgrammeStart.AddMinutes(20)) with { Status = ScheduledRecordingStatus.Cancelled },
            Job("acct", "old-conflict", ProgrammeStart.AddHours(3)) with { Status = ScheduledRecordingStatus.Conflict, Reason = "stale" },
            Job("ACCT", "nested", ProgrammeStart.AddMinutes(20)),
            Job("acct", "failed", ProgrammeStart.AddMinutes(20)) with { Status = ScheduledRecordingStatus.Failed }
        };

        var expected = PairwiseReference(jobs);
        var actual = ScheduledRecordingPolicy.ClassifyConflicts(jobs);
        Equal(string.Join("|", expected.Select(Summary)), string.Join("|", actual.Select(Summary)));
        Equal(ScheduledRecordingStatus.Recording, Find(actual, "recording").Status);
        Equal("preserve recording", Find(actual, "recording").Reason);
        Equal(ScheduledRecordingStatus.Conflict, Find(actual, "touching").Status);
        Equal(ScheduledRecordingStatus.Scheduled, Find(actual, "old-conflict").Status);
        Equal(string.Empty, Find(actual, "old-conflict").Reason);

        // Equal starts/ends and input permutation must not affect each job's classification.
        var tied = new[]
        {
            Job("stable", "z", ProgrammeStart), Job("STABLE", "a", ProgrammeStart),
            Job("stable", "m", ProgrammeStart.AddMinutes(30)), Job("different", "x", ProgrammeStart)
        };
        var baseline = ScheduledRecordingPolicy.ClassifyConflicts(tied).ToDictionary(job => job.Id, Summary);
        var permuted = ScheduledRecordingPolicy.ClassifyConflicts(tied.Reverse()).ToDictionary(job => job.Id, Summary);
        Equal(string.Join("|", baseline.OrderBy(pair => pair.Key).Select(pair => pair.Value)),
            string.Join("|", permuted.OrderBy(pair => pair.Key).Select(pair => pair.Value)));
    }

    private static void DenseOverlapComponent()
    {
        const int count = 20_000;
        var jobs = new ScheduledRecordingJob[count];
        for (var index = 0; index < count; index++)
        {
            var start = ProgrammeStart.AddSeconds(index);
            jobs[index] = Job("dense-account", "dense-" + index, start) with
            {
                Status = index == count / 2 ? ScheduledRecordingStatus.Recording : ScheduledRecordingStatus.Scheduled,
                RecordingId = index == count / 2 ? "dense-active-capture" : null
            };
        }

        var results = ScheduledRecordingPolicy.ClassifyConflicts(jobs);
        Equal(count, results.Count);
        Equal(count - 1, results.Count(job => job.Status == ScheduledRecordingStatus.Conflict));
        var active = Find(results, "dense-" + (count / 2));
        Equal(ScheduledRecordingStatus.Recording, active.Status);
        Equal("dense-active-capture", active.RecordingId);
    }

    private static IReadOnlyList<ScheduledRecordingJob> PairwiseReference(IReadOnlyList<ScheduledRecordingJob> jobs)
    {
        var valid = jobs.Where(job => ScheduledRecordingPolicy.Validate(job).IsValid).ToArray();
        var conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < valid.Length; i++)
        for (var j = i + 1; j < valid.Length; j++)
        {
            if (!string.Equals(valid[i].AccountId, valid[j].AccountId, StringComparison.OrdinalIgnoreCase) ||
                !valid[i].IsLive || !valid[j].IsLive ||
                valid[i].RequestedCaptureStartUtc >= valid[j].RequestedCaptureEndUtc ||
                valid[j].RequestedCaptureStartUtc >= valid[i].RequestedCaptureEndUtc) continue;
            var firstRecording = valid[i].Status == ScheduledRecordingStatus.Recording;
            var secondRecording = valid[j].Status == ScheduledRecordingStatus.Recording;
            if (firstRecording && !secondRecording) conflicted.Add(valid[j].Id);
            else if (secondRecording && !firstRecording) conflicted.Add(valid[i].Id);
            else if (!firstRecording && !secondRecording)
            {
                conflicted.Add(valid[i].Id);
                conflicted.Add(valid[j].Id);
            }
        }
        return valid.Select(job => conflicted.Contains(job.Id)
            ? job with { Status = ScheduledRecordingStatus.Conflict, Reason = "Capture window overlaps another active job for this account." }
            : job.Status == ScheduledRecordingStatus.Conflict
                ? job with { Status = ScheduledRecordingStatus.Scheduled, Reason = string.Empty }
                : job).ToArray();
    }

    private static string Summary(ScheduledRecordingJob job) => $"{job.Id}:{job.Status}:{job.Reason}:{job.RecordingId}";

    private static void PersistenceAndSecretFree()
    {
        InStore((store, root) =>
        {
            var one = new ScheduledRecordingIndex();
            Check(one.Add(Job("account-one", "job-one") with { RecordingId = "recording-account-one" }), "valid job was refused");
            var two = new ScheduledRecordingIndex();
            Check(two.Add(Job("account-two", "job-two")), "second account job was refused");
            store.SaveScheduledRecordingIndex("account-one", one);
            store.SaveScheduledRecordingIndex("account-two", two);
            Check(store.GetScheduledRecordingIndexPath("account-one") != store.GetScheduledRecordingIndexPath("account-two"), "accounts share scheduled index file");
            var reopened = new ConfigStore(root);
            var first = reopened.LoadScheduledRecordingIndex("account-one");
            var second = reopened.LoadScheduledRecordingIndex("account-two");
            Equal(ScheduledRecordingIndex.CurrentVersion, first!.Version);
            Equal("account-one", first.Jobs.Single().AccountId);
            Equal(Job("account-one", "job-one").DestinationPath, first.Jobs.Single().DestinationPath);
            Equal("recording-account-one", first.Jobs.Single().RecordingId);
            Equal("account-two", second!.Jobs.Single().AccountId);
            Equal(0, first.Jobs.Count(job => job.AccountId == "account-two"));
            var plaintext = JsonSerializer.Serialize(first.Jobs);
            Check(!plaintext.Contains("http", StringComparison.OrdinalIgnoreCase) && !plaintext.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                !plaintext.Contains("password", StringComparison.OrdinalIgnoreCase) && !plaintext.Contains("token", StringComparison.OrdinalIgnoreCase),
                "job data carries URL/credential fields");
            var disk = File.ReadAllText(store.GetScheduledRecordingIndexPath("account-one"));
            Check(!disk.Contains("Test Provider Host", StringComparison.OrdinalIgnoreCase), "persisted file is not protected");
            try
            {
                store.SaveScheduledRecordingIndex("account-one", second!);
                throw new InvalidOperationException("cross-account scheduled jobs were persisted");
            }
            catch (InvalidDataException) { }
        });
    }

    private static void BackupRecovery()
    {
        InStore((store, root) =>
        {
            var index = new ScheduledRecordingIndex();
            index.Add(Job("one", "first") with { RecordingId = "recording-first" });
            store.SaveScheduledRecordingIndex("one", index);
            index = new ScheduledRecordingIndex();
            index.Add(Job("one", "second"));
            store.SaveScheduledRecordingIndex("one", index);
            var path = store.GetScheduledRecordingIndexPath("one");
            Check(File.Exists(path + ".bak"), "atomic backup was not created");
            File.WriteAllText(path, "damaged primary");
            var recovering = new ConfigStore(root);
            var recovered = recovering.LoadScheduledRecordingIndex("one")!.Jobs.Single();
            Equal("first", recovered.Id);
            Equal(Job("one", "first").DestinationPath, recovered.DestinationPath);
            Equal("recording-first", recovered.RecordingId);
            Check(recovering.RecoveryNotice?.Contains("Recovered", StringComparison.Ordinal) == true, "recovery notice missing");
        });
    }

    private static void BackupRoundTrip()
    {
        InStore((store, root) =>
        {
            var state = new AppState();
            state.EnsureAccounts();
            store.Save(state);
            var index = new ScheduledRecordingIndex();
            index.Add(Job(state.SelectedAccountId, "backup-job") with { RecordingId = "recording-backup" });
            store.SaveScheduledRecordingIndex(state.SelectedAccountId, index);
            var backup = Path.Combine(root, "backup.zip");
            store.CreateBackup(backup);
            var entryName = "cache/" + Path.GetFileName(store.GetScheduledRecordingIndexPath(state.SelectedAccountId));
            using (var archive = ZipFile.OpenRead(backup)) Check(archive.GetEntry(entryName) is not null, "backup omitted scheduled jobs");
            var restoreRoot = Path.Combine(root, "restored");
            Directory.CreateDirectory(restoreRoot);
            var restored = new ConfigStore(restoreRoot);
            restored.RestoreBackup(backup);
            var restoredJob = restored.LoadScheduledRecordingIndex(state.SelectedAccountId)!.Jobs.Single();
            Equal("backup-job", restoredJob.Id);
            Equal(Job(state.SelectedAccountId, "backup-job").DestinationPath, restoredJob.DestinationPath);
            Equal("recording-backup", restoredJob.RecordingId);
        });
    }

    private static ScheduledRecordingJob Job(string account, string id, DateTimeOffset? start = null)
    {
        var programmeStart = start ?? ProgrammeStart;
        var programmeEnd = programmeStart.AddHours(1);
        var pre = TimeSpan.FromMinutes(5);
        var post = TimeSpan.FromMinutes(10);
        var destination = Path.Combine(Path.GetTempPath(), "cyrus-recordings", account, id + ".ts");
        return new ScheduledRecordingJob
        {
            Id = id, AccountId = account, ChannelId = "channel-1", ChannelName = "Invented Channel",
            ProgrammeTitle = "Invented Programme", ProgrammeStartUtc = programmeStart, ProgrammeEndUtc = programmeEnd,
            PrePadding = pre, PostPadding = post,
            RequestedCaptureStartUtc = programmeStart - pre, RequestedCaptureEndUtc = programmeEnd + post,
            DestinationPath = destination
        };
    }

    private static ScheduledRecordingJob Find(IEnumerable<ScheduledRecordingJob> jobs, string id) => jobs.Single(job => job.Id == id);
    private static void Reject(ScheduledRecordingJob job) => Check(!ScheduledRecordingPolicy.Validate(job).IsValid, "invalid job was accepted");

    private static void InStore(Action<ConfigStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-scheduled-recordings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new ConfigStore(root), root); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
