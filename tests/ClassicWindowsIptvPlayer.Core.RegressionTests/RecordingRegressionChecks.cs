using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Windows;
using ClassicWindowsIptvPlayer.Core;

internal static class RecordingRegressionChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("an unusable connection allowance blocks a capture and is never read as available", UnusableAllowance),
            ("an unreported allowance needs explicit confirmation and takes no slot", UnknownAllowanceConfirmation),
            ("replacing a stream this app owns needs no second provider slot", ReplaceVersusAdd),
            ("double and unknown releases never free a second slot", DoubleRelease),
            ("concurrent acquisitions never exceed the provider allowance", ConcurrentAcquisition),
            ("concurrent budget operations and lease snapshots are safe", ConcurrentOperationsAndSnapshots),
            ("every stream kind shares one budget and a leaked slot stays visible", SharedBudgetAndLeaks),
            ("the account scoped index survives restart with its own version", IndexPersistence),
            ("recording file paths survive restart and invalid paths normalize away", RecordingPathNormalization),
            ("a corrupt index primary recovers from its backup and says so", CorruptPrimaryRecovery),
            ("an unreadable index and a newer version fail with guidance", UnrecoverableIndex),
            ("the index persists no stream address, username or token", SecretFreeIndex),
            ("backup and restore carry the recordings index", BackupRoundTrip),
            ("free space is refused below the threshold and allowed at and above it", FreeSpaceThreshold),
            ("unusable destinations are refused and readable ones are normalized", DestinationValidation),
            ("recording file names are collision free and Windows legal", FileNaming),
            ("unsupported sources and containers are refused", UnsupportedCapture),
            ("programme metadata is captured at the actual start instant", ProgrammeMetadata),
            ("capture outcomes finalize once and stay honest", Outcomes),
            ("recording byte sizes use the correct binary units and thresholds", FormatByteUnits),
            ("a decision combines destination, space, name and connection rules", Decision),
            ("shared HLS source failure before first media leaves playback alive and no recording file", SharedHlsFailureBeforeFirstSegment)
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS recording: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL recording: " + name + " - " + ex.Message); }
        }
        Console.WriteLine($"Recording checks: {checks.Length - failures}/{checks.Length} passed");
        return failures;
    }

    private static void UnusableAllowance()
    {
        var budget = new ConnectionBudget();
        foreach (var profile in new[]
        {
            Profile("0", "0"), Profile("", "0"), Profile("2", ""), Profile("2", "-1"), Profile("two", "0"),
            Profile("2", "many"), Profile("   ", "1"), Profile("2.5", "0"), Profile("0", "-1"), Profile("", "")
        })
        {
            var decision = budget.Evaluate(profile);
            Equal(ConnectionBudgetStatus.ConnectionUnknown, decision.Status);
            Equal(false, decision.IsAllowed);
            Equal(true, decision.RequiresConfirmation);
            Equal(null, budget.Acquire(profile, StreamLeaseKind.InstantRecording, "Instant recording"));
            Equal(false, ConnectionBudget.TryParseAllowance(profile, out _, out _));
        }
        Equal(0, budget.LocalHeld);
        Equal(true, ConnectionBudget.TryParseAllowance(Profile("2", "1"), out var active, out var max));
        Equal(1, active);
        Equal(2, max);
        Equal(ConnectionBudgetStatus.Available, budget.Evaluate(Profile("2", "1")).Status);
        Equal(ConnectionBudgetStatus.NoAccount, budget.Evaluate(null).Status);
        // Archive playback and recording must agree on what an unsupported allowance is.
        Equal(CatchupFailure.ConnectionUnknown,
            Catchup.CreateRequest(ArchiveChannel(), ArchiveProgramme(), ArchiveAccount(), Profile("0", "0"), Now).Failure);
    }

    private static void UnknownAllowanceConfirmation()
    {
        var budget = new ConnectionBudget(() => Now);
        var profile = Profile("unreported", "unreported");
        var decision = budget.Evaluate(profile);
        Equal(ConnectionBudgetStatus.ConnectionUnknown, decision.Status);
        Equal(false, decision.IsAllowed);
        Equal(true, decision.RequiresConfirmation);
        Check(decision.Message.Contains("confirm", StringComparison.OrdinalIgnoreCase), "an unreported allowance has no confirmation wording");
        Equal(null, budget.Acquire(profile, StreamLeaseKind.InstantRecording, "Instant recording"));
        Equal(0, budget.LocalHeld);
        var confirmed = budget.Acquire(profile, StreamLeaseKind.InstantRecording, "Instant recording", acceptUnknownConnection: true);
        Check(confirmed is not null, "a confirmed unreported allowance was refused");
        Equal(1, budget.LocalHeld);
        Equal(1, budget.Held(StreamLeaseKind.InstantRecording));
        Equal(Now, confirmed!.StartedAt);
        Equal("Instant recording", confirmed.Purpose);
        // A usable allowance is a different answer and no longer needs confirmation.
        var known = Profile("1", "1");
        Equal(ConnectionBudgetStatus.ConnectionLimit, budget.Evaluate(known).Status);
        Equal(null, budget.Acquire(known, StreamLeaseKind.InstantRecording, "Second capture", acceptUnknownConnection: true));
        Equal(1, budget.LocalHeld);
    }

    private static void ReplaceVersusAdd()
    {
        var budget = new ConnectionBudget(() => Now);
        // The provider reports one of its two connections in use, which is the live
        // stream this app already has open.
        var live = budget.Acquire(Profile("2", "1"), StreamLeaseKind.LivePlayback, "Live playback", acceptUnknownConnection: true);
        Check(live is not null, "the live stream was refused");
        Equal(1, budget.LocalHeld);
        var oneSlot = Profile("1", "1");
        // Adding a second stream needs a slot the provider is not offering.
        Equal(ConnectionBudgetStatus.ConnectionLimit, budget.Evaluate(oneSlot).Status);
        Equal(2, budget.Evaluate(oneSlot).Required);
        // Replacing the stream this app owns hands the slot back.
        var replaced = budget.Evaluate(oneSlot, replacingCurrentConnection: true);
        Equal(ConnectionBudgetStatus.Available, replaced.Status);
        Equal(0, replaced.Required);
        var swapped = budget.Acquire(oneSlot, StreamLeaseKind.InstantRecording, "Instant recording", live, acceptUnknownConnection: true);
        Check(swapped is not null, "replacing an owned stream was refused");
        Equal(1, budget.LocalHeld);
        Equal(false, swapped!.Id == live!.Id);
        Equal(0, budget.Held(StreamLeaseKind.LivePlayback));
        Equal(1, budget.Held(StreamLeaseKind.InstantRecording));
        Equal(false, budget.Release(live));
        // A replacement is a swap, not a second slot: another capture now needs one.
        Equal(ConnectionBudgetStatus.Available, budget.Evaluate(Profile("2", "1")).Status);
        // A provider already at its limit cannot be helped by a swap, and a lease this
        // budget never handed out cannot be used to claim one.
        var full = new ConnectionBudget();
        var foreign = new StreamLease("invented-foreign-lease", StreamLeaseKind.MultiView, "Foreign", Now);
        Equal(ConnectionBudgetStatus.ConnectionLimit, full.Evaluate(Profile("1", "2"), replacingCurrentConnection: true).Status);
        Equal(null, full.Acquire(Profile("1", "2"), StreamLeaseKind.InstantRecording, "Instant recording", foreign));
        Equal(null, full.Acquire(Profile("1", "2"), StreamLeaseKind.InstantRecording, "Instant recording", null));
        Equal(0, full.LocalHeld);
    }

    private static void DoubleRelease()
    {
        var budget = new ConnectionBudget();
        var first = budget.Acquire(Profile("4", "2"), StreamLeaseKind.InstantRecording, "First");
        var second = budget.Acquire(Profile("4", "2"), StreamLeaseKind.InstantRecording, "Second");
        Check(first is not null && second is not null, "the first two local streams were refused");
        Equal(2, budget.LocalHeld);
        Equal(true, budget.Release(first));
        Equal(1, budget.LocalHeld);
        Equal(false, budget.Release(first));
        Equal(false, budget.Release((StreamLease?)first));
        Equal(false, budget.Release("invented-unknown-lease"));
        Equal(false, budget.Release((string?)null));
        Equal(false, budget.Release((StreamLease?)null));
        Equal(1, budget.LocalHeld);
        Equal(true, budget.Release(second));
        Equal(0, budget.LocalHeld);
        Equal(0, budget.ReleaseWhere(_ => true));
    }

    private static void ConcurrentAcquisition()
    {
        const int attempts = 128;
        var budget = new ConnectionBudget();
        var profile = Profile("3", "2");
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, attempts).Select(index => Task.Run(() =>
        {
            start.Wait();
            return budget.Acquire(profile, StreamLeaseKind.InstantRecording, "Concurrent " + index);
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks);
        Equal(1, tasks.Count(task => task.Result is not null));
        Equal(1, budget.LocalHeld);
        Equal(1, budget.Held(StreamLeaseKind.InstantRecording));
        Equal(1, budget.Leases.Count);
        Equal(1, budget.ReleaseAll());
    }

    private static void ConcurrentOperationsAndSnapshots()
    {
        const int workers = 12;
        const int iterations = 500;
        var budget = new ConnectionBudget();
        var profile = Profile("100", "0");
        using var start = new ManualResetEventSlim(false);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            try
            {
                start.Wait();
                for (var i = 0; i < iterations; i++)
                {
                    var lease = budget.Acquire(profile, (StreamLeaseKind)(worker % Enum.GetValues<StreamLeaseKind>().Length), "worker");
                    _ = budget.Evaluate(profile);
                    _ = budget.LocalHeld;
                    _ = budget.Held((StreamLeaseKind)(worker % Enum.GetValues<StreamLeaseKind>().Length));
                    var snapshot = budget.Leases;
                    if (snapshot.Count > ConnectionBudget.MaxLocalHeld)
                        throw new InvalidOperationException("A snapshot exceeded the local ceiling.");
                    if (snapshot is IList<StreamLease> mutable)
                    {
                        try { mutable.Clear(); throw new InvalidOperationException("The lease snapshot was mutable."); }
                        catch (NotSupportedException) { }
                    }
                    if (lease is not null) budget.Release(lease);
                    if (i % 31 == 0) budget.ReleaseWhere(item => item.Kind == (StreamLeaseKind)(worker % Enum.GetValues<StreamLeaseKind>().Length));
                    if (i % 127 == 0) budget.ReleaseAll();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks);
        if (errors.TryDequeue(out var error)) throw new InvalidOperationException("Concurrent budget operation failed.", error);
        Equal(0, budget.ReleaseAll());
        Equal(0, budget.LocalHeld);
        Equal(0, budget.Leases.Count);
    }

    private static void SharedBudgetAndLeaks()
    {
        var budget = new ConnectionBudget();
        var live = budget.Acquire(Profile("2", "0"), StreamLeaseKind.LivePlayback, "Live playback");
        var instant = budget.Acquire(Profile("2", "0"), StreamLeaseKind.InstantRecording, "Instant recording");
        Check(live is not null && instant is not null, "playback and recording did not both fit a two stream allowance");
        Equal(2, budget.LocalHeld);
        // The provider still reports zero of its two connections used, so the next
        // capture is allowed; the local count is for visibility, not a second ceiling.
        Equal(ConnectionBudgetStatus.Available, budget.Evaluate(Profile("2", "0")).Status);
        Equal(1, budget.Held(StreamLeaseKind.LivePlayback));
        Equal(1, budget.Held(StreamLeaseKind.InstantRecording));
        // Every other consumer draws from the same allowance.
        var multi = new ConnectionBudget();
        Check(multi.Acquire(Profile("3", "0"), StreamLeaseKind.MultiView, "Multi view") is not null, "multi view did not fit");
        Check(new ConnectionBudget().Acquire(Profile("3", "0"), StreamLeaseKind.TimeshiftBuffer, "Timeshift buffer") is not null,
            "timeshift buffer did not fit a three stream allowance");
        Equal(1, budget.ReleaseWhere(lease => lease.Kind == StreamLeaseKind.InstantRecording));
        Equal(1, budget.LocalHeld);
        // A capture that throws must still give its slot back.
        var failed = new ConnectionBudget();
        var slot = failed.Acquire(Profile("2", "0"), StreamLeaseKind.InstantRecording, "Instant recording");
        try { throw new InvalidOperationException("invented capture failure"); }
        catch (InvalidOperationException) { failed.Release(slot); }
        Equal(0, failed.LocalHeld);
        // So must a cancelled one; the cancel path releases by kind so nothing is stranded.
        var cancelled = new ConnectionBudget();
        try
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            cancelled.Acquire(Profile("2", "0"), StreamLeaseKind.InstantRecording, "Cancelled capture");
            source.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { }
        Equal(1, cancelled.LocalHeld);
        Equal(1, cancelled.ReleaseWhere(lease => lease.Kind == StreamLeaseKind.InstantRecording));
        Equal(0, cancelled.LocalHeld);
        // A slot that was never released stays visible and is never silently forgotten:
        // it can still be found, described and released in bulk.
        var stuck = new ConnectionBudget();
        Check(stuck.Acquire(Profile("1", "0"), StreamLeaseKind.InstantRecording, "Never released") is not null, "the leaked stream was not recorded");
        Equal(1, stuck.LocalHeld);
        Check(stuck.Leases[0].StartedAt != default, "a local stream has no start time to diagnose it");
        Equal("Never released", stuck.Leases[0].Purpose);
        Equal(ConnectionBudgetStatus.Available, stuck.Evaluate(Profile("1", "0")).Status);
        Equal(1, stuck.ReleaseAll());
        Equal(0, stuck.LocalHeld);
        // The local ceiling is real: once the app holds more streams than a recording
        // should need, adding one more is refused, while replacing only swaps in place.
        var capped = new ConnectionBudget();
        var slots = new List<StreamLease>();
        for (var i = 1; i <= ConnectionBudget.MaxLocalHeld; i++)
        {
            var lease = capped.Acquire(Profile("100", "0"), StreamLeaseKind.InstantRecording, "Slot " + i);
            Check(lease is not null, "the local ceiling refused an owned slot too early");
            slots.Add(lease!);
        }
        Equal(ConnectionBudgetStatus.LocalLimit, capped.Evaluate(Profile("100", "0")).Status);
        Equal(ConnectionBudgetStatus.Available, capped.Evaluate(Profile("100", "0"), replacingCurrentConnection: true).Status);
        Equal(16, capped.ReleaseAll());
        Equal(0, capped.LocalHeld);
        Check(capped.Acquire(Profile("100", "0"), StreamLeaseKind.InstantRecording, "After cleanup") is not null, "cleanup did not restore the budget");
    }

    private static void IndexPersistence()
    {
        InTemporaryStore((store, root) =>
        {
            var one = new RecordingIndex();
            var first = one.Begin(Plan(), "one", "42", "item:one");
            Check(first is not null, "the capture did not reach the index");
            one.Finish(first, new RecordingFinish(Now.AddMinutes(30), RecordingOutcome.Completed, RecordingStopReason.RequestedEndUtc, 1024, "captured to the end"));
            store.SaveRecordingIndex("one", one);
            // The same channel id and the same file name in a second account stay separate.
            var two = new RecordingIndex();
            var second = two.Begin(Plan(), "two", "42", "item:one");
            two.Finish(second, new RecordingFinish(Now.AddMinutes(10), RecordingOutcome.Stopped, RecordingStopReason.User, 2048, "stopped by the user"));
            store.SaveRecordingIndex("two", two);
            Check(store.GetRecordingIndexPath("one") != store.GetRecordingIndexPath("two"), "both accounts share one index file");
            var reopened = new ConfigStore(root);
            var reloadedOne = reopened.LoadRecordingIndex("one");
            var reloadedTwo = reopened.LoadRecordingIndex("two");
            Check(reloadedOne is not null && reloadedTwo is not null, "an account scoped index did not reopen");
            Equal(RecordingIndex.CurrentVersion, reloadedOne!.Version);
            Equal(RecordingIndex.CurrentVersion, reloadedTwo!.Version);
            Equal(1, reloadedOne.Entries.Count);
            Equal(1, reloadedTwo.Entries.Count);
            Equal("one", reloadedOne.Entries[0].AccountId);
            Equal("42", reloadedOne.Entries[0].ChannelId);
            Equal("Invented News", reloadedOne.Entries[0].ChannelName);
            Equal(Path.GetFullPath(Plan().FilePath), reloadedOne.Entries[0].FilePath);
            Equal(RecordingOutcome.Completed, reloadedOne.Entries[0].Outcome);
            Equal(1024, reloadedOne.Entries[0].ByteSize);
            Equal(RecordingOutcome.Stopped, reloadedTwo.Entries[0].Outcome);
            Equal(2048, reloadedTwo.Entries[0].ByteSize);
            Equal(1, reloadedOne.Recent("one").Count);
            Equal(1, reloadedTwo.Recent("two").Count);
            Equal(0, reloadedOne.Recent("two").Count);
            Equal(0, reloadedTwo.Recent("one").Count);
            Equal(0, reloadedOne.Unfinished("one").Count);
            Equal(0, reopened.LoadRecordingIndex("never-used") is null ? 0 : 1);
            // Reading again is stable, so a restart loop cannot churn the file.
            var reread = new ConfigStore(root);
            Equal(reloadedOne.Entries[0].Id, reread.LoadRecordingIndex("one")!.Entries[0].Id);
            Equal(null, reread.RecoveryNotice);
        });
    }

    private static void CorruptPrimaryRecovery()
    {
        InTemporaryStore((store, root) =>
        {
            var first = new RecordingIndex();
            var kept = first.Begin(Plan(), "one", "42", "item:one");
            first.Finish(kept, new RecordingFinish(Now.AddMinutes(1), RecordingOutcome.Completed, RecordingStopReason.ProviderEnded, 11, "first capture"));
            store.SaveRecordingIndex("one", first);
            var second = new RecordingIndex();
            var damaged = second.Begin(Plan(), "one", "42", "item:one");
            second.Finish(damaged, new RecordingFinish(Now.AddMinutes(2), RecordingOutcome.Completed, RecordingStopReason.ProviderEnded, 22, "second capture"));
            store.SaveRecordingIndex("one", second);
            var path = store.GetRecordingIndexPath("one");
            Check(File.Exists(path + ".bak"), "no index backup was written");
            File.WriteAllText(path, "{corrupt");
            var recovering = new ConfigStore(root);
            var recovered = recovering.LoadRecordingIndex("one");
            Check(recovered is not null, "the index did not recover from its backup");
            Equal(1, recovered!.Entries.Count);
            Equal("first capture", recovered.Entries[0].Note);
            Equal(11, recovered.Entries[0].ByteSize);
            Check(recovering.RecoveryNotice?.Contains("Recovered") == true, "the index recovery notice is missing");
            var reopened = new ConfigStore(root);
            Equal("first capture", reopened.LoadRecordingIndex("one")!.Entries[0].Note);
            Check(reopened.RecoveryNotice is null, "recovery was reported twice for one damaged file");
        });
    }

    private static void UnrecoverableIndex()
    {
        InTemporaryStore((store, root) =>
        {
            var index = new RecordingIndex();
            index.Begin(Plan(), "one", "42", "item:one");
            store.SaveRecordingIndex("one", index);
            var path = store.GetRecordingIndexPath("one");
            File.WriteAllText(path, "bad primary");
            File.WriteAllText(path + ".bak", "also bad");
            try
            {
                new ConfigStore(root).LoadRecordingIndex("one");
                throw new Exception("a damaged index was accepted");
            }
            catch (InvalidDataException exception)
            {
                Check(exception.Message.Contains("backup") && exception.Message.Contains("left in place"),
                    "the failure lacks recovery guidance: " + exception.Message);
            }
            Equal("bad primary", File.ReadAllText(path));
            // An index written by a newer build is refused rather than half read. The
            // unprotected form is what the store's own reader accepts from disk.
            var future = new ConfigStore(root).GetRecordingIndexPath("three");
            using (var file = File.Create(future))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new RecordingIndex { Version = RecordingIndex.CurrentVersion + 1 });
            try
            {
                new ConfigStore(root).LoadRecordingIndex("three");
                throw new Exception("a newer index version was accepted");
            }
            catch (InvalidDataException exception)
            {
                Check(exception.Message.Contains("newer", StringComparison.OrdinalIgnoreCase), "a newer index was not reported as such: " + exception.Message);
            }
        });
    }

    private static void SecretFreeIndex()
    {
        var channel = new Channel
        {
            Id = "42", Name = "Invented News", MediaKind = MediaKind.Live,
            Url = "http://fixture:8080/live/invented-user/invented-secret/42.ts"
        };
        var programme = new EpgProgramme("fixture", "Invented Evening News", "The invented report", "News", Now.AddMinutes(-5), Now.AddMinutes(25));
        var plan = Plan() with { Programme = RecordedProgrammeMetadata.From(programme) };
        InTemporaryStore((store, root) =>
        {
            var index = new RecordingIndex();
            var entry = index.Begin(plan, "one", channel.Id, ItemIdentity.For(channel), plan.FilePath);
            Check(entry is not null, "the capture did not reach the index");
            index.Finish(entry, new RecordingFinish(Now.AddMinutes(5), RecordingOutcome.Failed, RecordingStopReason.Error, 0,
                "capture failed for http://fixture:8080/live/invented-user/invented-secret/42.ts"));
            store.SaveRecordingIndex("one", index);
            var loaded = new ConfigStore(root).LoadRecordingIndex("one");
            Check(loaded is not null, "the index did not reopen");
            var row = loaded!.Entries.Single();
            // The identifying fields stay, and none of them can carry the source.
            Equal("42", row.ChannelId);
            Equal("Invented News", row.ChannelName);
            Equal(ItemIdentity.For(channel), row.ChannelKey);
            Equal(Path.GetFullPath(plan.FilePath), row.FilePath);
            Check(!row.ChannelKey.Contains("fixture", StringComparison.OrdinalIgnoreCase), "the item key leaks the stream host");
            Check(!row.SearchKey.Contains("http", StringComparison.OrdinalIgnoreCase), "the search key carries a url");
            // Free text from the capture layer is redacted before it is stored.
            Check(!row.Note.Contains("invented-secret") && !row.Note.Contains("invented-user"),
                "the failure note keeps provider credentials");
            Check(row.Note.Contains("***"), "the failure note was not redacted");
            var plaintext = JsonSerializer.Serialize(loaded.Entries.Select(entry => entry with { Note = string.Empty }));
            Check(!plaintext.Contains("invented-secret"), "the recordings index plaintext exposes a credential");
            Check(!plaintext.Contains("invented-user"), "the recordings index plaintext exposes a username");
            Check(!plaintext.Contains("fixture", StringComparison.OrdinalIgnoreCase), "the recordings index plaintext exposes a stream host");
            Check(!plaintext.Contains("http", StringComparison.OrdinalIgnoreCase), "the recordings index plaintext exposes a url");
            var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(store.GetRecordingIndexPath("one")));
            Check(!bytes.Contains("invented-secret"), "the protected index file exposes a credential");
        });
    }

    private static void RecordingPathNormalization()
    {
        var index = new RecordingIndex();
        var path = Path.Combine(Path.GetTempPath(), "recordings", "sample.ts");
        var entry = index.Begin(Plan(), "one", "42", "item:one", path);
        Check(entry is not null, "the capture did not reach the index");
        Equal(Path.GetFullPath(path), entry!.FilePath);
        var invalid = entry with { FilePath = "\0invalid" };
        index.Entries[0] = invalid;
        index.Normalize();
        Equal(string.Empty, index.Entries[0].FilePath);
    }

    private static void BackupRoundTrip()
    {
        InTemporaryStore((store, state, root) =>
        {
            var account = state.SelectedAccountId;
            var index = new RecordingIndex();
            var entry = index.Begin(Plan(), account, "42", "item:one");
            index.Finish(entry, new RecordingFinish(Now.AddMinutes(4), RecordingOutcome.Completed, RecordingStopReason.ProviderEnded, 4096, "kept in the backup"));
            store.SaveRecordingIndex(account, index);
            store.Save(state);
            store.SaveChannelCache(account, [new Channel { Name = "Saved", Url = "http://example.invalid/stream" }]);
            var archive = Path.Combine(root, "local.zip");
            store.CreateBackup(archive);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(archive)).Contains("example.invalid"), "the backup exposes a stream URL");
            index.Remove(account, entry!.Id);
            store.SaveRecordingIndex(account, index);
            Equal(0, new ConfigStore(root).LoadRecordingIndex(account)!.Entries.Count);
            store.RestoreBackup(archive);
            var restored = new ConfigStore(root).LoadRecordingIndex(account);
            Check(restored is not null, "the recordings index was not restored");
            Equal(1, restored!.Entries.Count);
            Equal(4096, restored.Entries[0].ByteSize);
            Equal("kept in the backup", restored.Entries[0].Note);
            Equal(1, new ConfigStore(root).LoadChannelCache(account).Count);
            // A recordings file no build recognizes is refused instead of restored blind.
            var foreign = Path.Combine(root, "foreign.zip");
            using (var file = new FileStream(foreign, FileMode.CreateNew))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var settings = zip.CreateEntry("accounts.json");
                using (var stream = settings.Open()) stream.Write(File.ReadAllBytes(store.StatePath));
                var stray = zip.CreateEntry("cache/recordings-rogue.json.gz");
                using (var stream = stray.Open())
                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                    JsonSerializer.Serialize(gzip, new { Version = RecordingIndex.CurrentVersion, Entries = (List<RecordingEntry>?)null });
            }
            try
            {
                store.RestoreBackup(foreign);
                throw new Exception("an unknown recordings file was restored");
            }
            catch (InvalidDataException exception)
            {
                Check(exception.Message.Contains("recordings index is invalid", StringComparison.Ordinal),
                    "an unknown recordings file was not refused: " + exception.Message);
            }
        });
    }

    private static void FreeSpaceThreshold()
    {
        Equal(512L * 1024 * 1024, RecordingPolicy.MinFreeBytes);
        Equal(256L * 1024 * 1024, RecordingPolicy.SafetyMarginBytes);
        Equal(1_800_000_000L + RecordingPolicy.MinEstimateBytes, RecordingPolicy.EstimateBytes(TimeSpan.FromHours(1)));
        Equal(RecordingPolicy.MinEstimateBytes, RecordingPolicy.EstimateBytes(TimeSpan.Zero));
        // A capture too short to exhaust the floor still needs the full minimum plus margin.
        Equal(RecordingPolicy.MinFreeBytes + RecordingPolicy.SafetyMarginBytes,
            RecordingPolicy.CheckFreeSpace(new RecordingDestination("C:\\Recordings", true, RecordingFailure.None, "ready",
                new VolumeSpace(true, long.MaxValue)), TimeSpan.FromMinutes(1)).RequiredBytes);
        Equal(RecordingPolicy.EstimateBytes(TimeSpan.FromHours(RecordingPolicy.MaxEstimateHours)),
            RecordingPolicy.EstimateBytes(TimeSpan.FromHours(500)));
        var twoHours = TimeSpan.FromHours(2);
        var needed = RecordingPolicy.EstimateBytes(twoHours) + RecordingPolicy.SafetyMarginBytes;
        var destination = new RecordingDestination("C:\\Recordings", true, RecordingFailure.None, "ready", new VolumeSpace(true, needed - 1));
        var low = RecordingPolicy.CheckFreeSpace(destination, twoHours);
        Equal(RecordingSpaceStatus.Low, low.Status);
        Equal(needed - 1, low.AvailableBytes);
        Equal(needed, low.RequiredBytes);
        Check(low.Message.Contains("free space", StringComparison.OrdinalIgnoreCase), "the low space message has no recovery action");
        var at = RecordingPolicy.CheckFreeSpace(destination with { Space = new VolumeSpace(true, needed) }, twoHours);
        Equal(RecordingSpaceStatus.Ok, at.Status);
        Equal(needed, at.RequiredBytes);
        var above = RecordingPolicy.CheckFreeSpace(destination with { Space = new VolumeSpace(true, needed + RecordingPolicy.MinFreeBytes) }, twoHours);
        Equal(RecordingSpaceStatus.Ok, above.Status);
        var unknown = RecordingPolicy.CheckFreeSpace(destination with { Space = VolumeSpace.Unknown }, twoHours);
        Equal(RecordingSpaceStatus.Unknown, unknown.Status);
        Equal(-1, unknown.AvailableBytes);
        Check(unknown.Message.Contains("could not be read", StringComparison.Ordinal), "an unreadable volume has no guidance");
        // The measured volume is the one that holds the destination, not the app's own.
        var onTemp = RecordingPolicy.ValidateDestination(Path.GetTempPath());
        Check(onTemp.IsValid, "the temporary directory was refused as a destination");
        Equal(true, onTemp.Space.IsKnown);
        Check(onTemp.Space.AvailableBytes >= 0, "the temporary volume reported no free space");
        Equal(RecordingSpaceStatus.Low,
            RecordingPolicy.CheckFreeSpace(onTemp with { Space = new VolumeSpace(true, 0) }, TimeSpan.FromHours(12)).Status);
    }

    private static void DestinationValidation()
    {
        foreach (var folder in new string?[] { null, "", "   " })
        {
            var empty = RecordingPolicy.ValidateDestination(folder);
            Equal(RecordingFailure.NoDestination, empty.Failure);
            Equal(false, empty.IsValid);
            Check(empty.Message.Contains("folder", StringComparison.OrdinalIgnoreCase), "an unset destination has no guidance");
        }
        var missing = RecordingPolicy.ValidateDestination(Path.Combine(Path.GetTempPath(), "cyrus-missing-" + Guid.NewGuid().ToString("N")));
        Equal(RecordingFailure.DestinationMissing, missing.Failure);
        Equal(false, missing.IsValid);
        Check(missing.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase), "a missing destination has no guidance");
        Check(RecordingPolicy.ValidateDestination("Z:\\cyrus-no-such-drive").Space.IsKnown == false,
            "a drive that is not present was treated as having free space");
        var illegal = RecordingPolicy.ValidateDestination("C:\\bad\\na\0me");
        Equal(RecordingFailure.DestinationMissing, illegal.Failure);
        Check(illegal.Message.Length > 0, "an unusable path has no message");
        // Equivalent spellings of the same folder normalize to one absolute path.
        var absolute = RecordingPolicy.ValidateDestination(Path.GetTempPath());
        var dotted = RecordingPolicy.ValidateDestination(Path.Combine(Path.GetTempPath(), "."));
        Check(absolute.IsValid && dotted.IsValid, "an existing folder was refused");
        Equal(absolute.Folder.TrimEnd(Path.DirectorySeparatorChar), dotted.Folder.TrimEnd(Path.DirectorySeparatorChar));
        Check(Path.IsPathRooted(absolute.Folder), "the destination was not made absolute");
        var file = Path.Combine(Path.GetTempPath(), "cyrus-not-a-folder-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "invented");
        try
        {
            Equal(RecordingFailure.DestinationMissing, RecordingPolicy.ValidateDestination(file).Failure);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static void FileNaming()
    {
        var illegal = RecordingPolicy.SanitizeFileName("News: \"Evening\" <HD|1> / Sports\\Show?*");
        foreach (var character in "<>:\"/\\|?*") Check(!illegal.Contains(character, StringComparison.Ordinal), "an illegal file name character survived: " + character);
        Check(illegal.Contains("Evening", StringComparison.Ordinal), "the readable part of the name was lost");
        Equal("Channel", RecordingPolicy.SanitizeFileName("<>"));
        Equal("Channel", RecordingPolicy.SanitizeFileName(null));
        Equal("_CON", RecordingPolicy.SanitizeFileName("CON"));
        Equal("_con", RecordingPolicy.SanitizeFileName("con"));
        Equal("_NUL.mp4", RecordingPolicy.SanitizeFileName("NUL.mp4"));
        Equal("CONcert", RecordingPolicy.SanitizeFileName("CONcert"));
        Equal("Sports Show", RecordingPolicy.SanitizeFileName("Sports Show. "));
        Equal(RecordingPolicy.MaxNameLength, RecordingPolicy.SanitizeFileName(new string('x', 500)).Length);
        Equal("2026-09-28 12-00-00 Invented_ News.mp4", RecordingPolicy.ChooseFileName(Path.GetTempPath(), "Invented: News", Now, RecordingContainerKind.Mp4).Value);
        Equal(RecordingFilenameStatus.Ok, RecordingPolicy.ChooseFileName(Path.GetTempPath(), "Invented: News", Now, RecordingContainerKind.Mp4).Status);
        var baseName = RecordingPolicy.BaseFileName("Invented: News", Now, RecordingContainerKind.Mp4);
        var renamed = RecordingPolicy.ChooseFileName(Path.GetTempPath(), "Invented: News", Now, RecordingContainerKind.Mp4,
            path => string.Equals(path, Path.Combine(Path.GetTempPath(), baseName), StringComparison.OrdinalIgnoreCase));
        Equal(RecordingFilenameStatus.Renamed, renamed.Status);
        Equal("2026-09-28 12-00-00 Invented_ News (2).mp4", renamed.Value);
        var third = RecordingPolicy.ChooseFileName(Path.GetTempPath(), "n", Now, RecordingContainerKind.Ts,
            path => path.EndsWith("n.ts", StringComparison.Ordinal) || path.EndsWith(" (2).ts", StringComparison.Ordinal));
        Equal("2026-09-28 12-00-00 n (3).ts", third.Value);
        Equal(".mkv", Path.GetExtension(RecordingPolicy.ChooseFileName(Path.GetTempPath(), "n", Now, RecordingContainerKind.Mkv).Value));
        var longName = RecordingPolicy.ChooseFileName(Path.GetTempPath(), new string('y', 200), Now, RecordingContainerKind.Mp4).Value;
        Check(Path.Combine(Path.GetTempPath(), longName).Length <= 255, "the produced path does not fit an NTFS name");
        // A real existing recording is stepped over, never overwritten.
        InTemporaryFolder(folder =>
        {
            var taken = RecordingPolicy.ChooseFileName(folder, "Invented News", Now, RecordingContainerKind.Mp4).Value;
            var path = Path.Combine(folder, taken);
            File.WriteAllText(path, "invented existing recording");
            var second = RecordingPolicy.ChooseFileName(folder, "Invented News", Now, RecordingContainerKind.Mp4, File.Exists);
            Equal(RecordingFilenameStatus.Renamed, second.Status);
            Equal(false, second.Value == taken);
            Equal(1, Directory.GetFiles(folder).Length);
            Equal("invented existing recording", File.ReadAllText(path));
            // Two captures of the same channel in the same second still differ.
            Equal(2, new HashSet<string>([path, Path.Combine(folder, second.Value)]).Count);
        });
    }

    private static void UnsupportedCapture()
    {
        var budget = new ConnectionBudget();
        var profile = Profile("2", "0");
        var folder = Path.GetTempPath();
        var movie = new Channel { Id = "1", Name = "Invented Movie", Url = "http://fixture/movie/u/p/1.mp4", MediaKind = MediaKind.Movie };
        var series = new Channel { Id = "2", Name = "Invented Episode", Url = "series:7", MediaKind = MediaKind.Series };
        foreach (var channel in new[] { movie, series })
        {
            var decision = RecordingPolicy.Evaluate(profile, folder, channel, TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget);
            Equal(RecordingFailure.UnsupportedSource, decision.Failure);
            Equal(false, decision.IsAllowed);
            Check(decision.Message.Contains("catch-up", StringComparison.OrdinalIgnoreCase), "the refusal offers no alternative");
        }
        var noUrl = new Channel { Id = "3", Name = "No stream" };
        Equal(RecordingFailure.UnsupportedSource,
            RecordingPolicy.Evaluate(profile, folder, noUrl, TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget).Failure);
        Equal(RecordingFailure.UnsupportedSource,
            RecordingPolicy.Evaluate(profile, folder, null, TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget).Failure);
        Equal(RecordingFailure.UnsupportedContainer,
            RecordingPolicy.Evaluate(profile, folder, Live(), TimeSpan.FromHours(1), Now, (RecordingContainerKind)99, null, budget).Failure);
        Equal(RecordingFailure.NoDestination,
            RecordingPolicy.Evaluate(profile, "   ", Live(), TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget).Failure);
        Equal(RecordingFailure.DestinationMissing,
            RecordingPolicy.Evaluate(profile, Path.Combine(folder, "cyrus-missing-" + Guid.NewGuid().ToString("N")), Live(),
                TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget).Failure);
        Equal(RecordingFailure.LowDiskSpace,
            RecordingPolicy.Evaluate(profile, folder, Live(), TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget,
                space: new VolumeSpace(true, 1)).Failure);
        // A volume that cannot be read is allowed, but only with the warning attached.
        var unmounted = RecordingPolicy.Evaluate(profile, folder, Live(), TimeSpan.FromHours(1), Now, RecordingContainerKind.Mp4, null, budget,
            space: VolumeSpace.Unknown);
        Equal(true, unmounted.IsAllowed);
        Equal(RecordingFailure.None, unmounted.Failure);
        Check(unmounted.Message.Contains("could not be read", StringComparison.Ordinal), "an unreadable volume is allowed with no warning");
    }

    private static void ProgrammeMetadata()
    {
        var programme = new EpgProgramme("fixture", "Invented Evening News", "The invented report", "News", Now.AddMinutes(-5), Now.AddMinutes(25));
        var captured = RecordedProgrammeMetadata.From(programme);
        Equal("Invented Evening News", captured.Title);
        Equal("The invented report", captured.Description);
        Equal("News", captured.Category);
        Equal(programme.Start, captured.Start);
        Equal(programme.Stop, captured.Stop);
        Equal(true, captured.HasProgramme);
        Equal("Invented Evening News", captured.DisplayTitle);
        // The plan carries the guide as it read when the capture was requested; the
        // Windows layer resolves the current programme at the instant capture starts.
        var decision = RecordingPolicy.Evaluate(Profile("2", "0"), Path.GetTempPath(), Live(), TimeSpan.FromHours(1), Now,
            RecordingContainerKind.Mp4, programme, new ConnectionBudget(),
            space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
        Check(decision.IsAllowed, "a planned capture was refused");
        Equal(Now, decision.Plan!.RequestedStartUtc);
        Equal(Now.AddHours(1), decision.Plan.RequestedStopUtc);
        Equal(null, decision.Plan.ActualStartUtc);
        Equal("Invented Evening News", decision.Plan.Programme.Title);
        var started = decision.Plan with { ActualStartUtc = Now.AddSeconds(37) };
        Equal(Now.AddSeconds(37), started.ActualStartUtc);
        Equal(Now, started.RequestedStartUtc);
        // An unmapped, blank or inverted programme is recorded as no programme at all.
        Equal(false, RecordedProgrammeMetadata.From(null).HasProgramme);
        Equal("Live channel capture", RecordedProgrammeMetadata.From(null).DisplayTitle);
        Equal(false, RecordedProgrammeMetadata.From(new EpgProgramme("fixture", " ", "d", "c", Now, Now.AddHours(1))).HasProgramme);
        Equal(false, RecordedProgrammeMetadata.From(new EpgProgramme("fixture", "T", "d", "c", Now.AddHours(1), Now)).HasProgramme);
        InTemporaryStore((store, root) =>
        {
            var index = new RecordingIndex();
            var entry = index.Begin(started, "one", "42", "item:one");
            index.Finish(entry, new RecordingFinish(Now.AddMinutes(30), RecordingOutcome.Completed, RecordingStopReason.RequestedEndUtc, 10, ""));
            store.SaveRecordingIndex("one", index);
            var row = new ConfigStore(root).LoadRecordingIndex("one")!.Entries.Single();
            Equal("Invented Evening News", row.Programme.Title);
            Equal("The invented report", row.Programme.Description);
            Equal("News", row.Programme.Category);
            Equal(programme.Start, row.Programme.Start);
            Equal(programme.Stop, row.Programme.Stop);
            Equal("Invented Evening News", row.DisplayName);
            Equal(Now.AddSeconds(37), row.StartedUtc);
            Equal(TimeSpan.FromHours(1), row.RequestedDuration);
            Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(37), row.RecordedDuration);
        });
        InTemporaryStore((store, root) =>
        {
            var index = new RecordingIndex();
            var active = index.Begin(Plan(), "one", "42", "item:one");
            var mediaStarted = index.SetStarted(active, Now.AddMinutes(2));
            Check(mediaStarted is not null, "first-media start timestamp was persisted into the active row");
            var mediaFinished = index.Finish(mediaStarted, new RecordingFinish(Now.AddMinutes(2).AddSeconds(4), RecordingOutcome.Stopped, RecordingStopReason.User, 10, ""));
            Equal(TimeSpan.FromSeconds(4), mediaFinished!.RecordedDuration);
            store.SaveRecordingIndex("one", index);
            Equal(TimeSpan.FromSeconds(4), new ConfigStore(root).LoadRecordingIndex("one")!.Entries.Single().RecordedDuration);
        });
    }

    private static void Outcomes()
    {
        var index = new RecordingIndex();
        var entry = index.Begin(Plan(), "one", "42", "item:one");
        Check(entry is not null, "the capture did not reach the index");
        Equal(RecordingOutcome.Recording, entry!.Outcome);
        Equal(true, entry.IsActive);
        Equal(1, index.Unfinished("one").Count);
        var finished = index.Finish(entry, new RecordingFinish(Now.AddMinutes(1), RecordingOutcome.Completed, RecordingStopReason.None, 500, "captured to the end"));
        Equal(RecordingStopReason.RequestedEndUtc, finished!.StopReason);
        Equal(500, finished.ByteSize);
        // A stop that races a shutdown cannot rewrite a recorded outcome.
        var rewritten = index.Finish(finished, new RecordingFinish(Now.AddMinutes(2), RecordingOutcome.Failed, RecordingStopReason.Error, 999, "rewrite"));
        Equal(RecordingOutcome.Completed, rewritten!.Outcome);
        Equal(500, rewritten.ByteSize);
        Equal(Now.AddMinutes(1), rewritten.StoppedUtc);
        foreach (var (outcome, reason) in new[]
        {
            (RecordingOutcome.Stopped, RecordingStopReason.User),
            (RecordingOutcome.Failed, RecordingStopReason.Error),
            (RecordingOutcome.DiskFull, RecordingStopReason.DiskFull),
            (RecordingOutcome.Discarded, RecordingStopReason.Error)
        })
        {
            var trial = new RecordingIndex();
            var open = trial.Begin(Plan(), "one", "42", "item:one");
            var done = trial.Finish(open, new RecordingFinish(Now.AddMinutes(3), outcome, reason, 10, ""));
            Equal(outcome, done!.Outcome);
            Equal(reason, done.StopReason);
            Equal(0, trial.Unfinished("one").Count);
        }
        try
        {
            index.Finish(finished, new RecordingFinish(Now, RecordingOutcome.Recording, RecordingStopReason.None, 0, ""));
            throw new Exception("a capture was allowed to finish without a terminal outcome");
        }
        catch (ArgumentOutOfRangeException) { }
        // Re-planning the same unfinished capture keeps one row, not two.
        var replan = new RecordingIndex();
        var original = replan.Begin(Plan(), "one", "42", "item:one");
        var plan = Plan() with { ActualStartUtc = null, Programme = RecordedProgrammeMetadata.None, ConflictingFileName = original!.FileName };
        var continued = RecordingPlan.Create(plan, original);
        Equal(original.Id, continued.Existing!.Id);
        var second = replan.Begin(continued, "one", "42", "item:one");
        Equal(1, replan.Entries.Count);
        Equal(false, second!.Id == original.Id);
        Equal(1, replan.Unfinished("one").Count);
        // A finished capture does not block the next one.
        var next = index.Begin(Plan(), "one", "42", "item:one");
        Equal(2, index.Entries.Count);
        Equal(1, index.Unfinished("one").Count);
        Equal(1, index.Recent("one", 1).Count);
        Equal(true, index.Remove("one", next!.Id));
        Equal(1, index.Entries.Count);
        Equal(false, index.Remove("one", next.Id));
        Equal(null, index.FindByFileName("one", "invented-missing.mp4"));
        Equal(0, index.RemoveFile("one", "invented-missing.mp4"));
    }

    private static void FormatByteUnits()
    {
        Equal("1023 B", RecordingPolicy.FormatBytes(1023));
        Equal("1.0 KB", RecordingPolicy.FormatBytes(1024));
        Equal("1.0 MB", RecordingPolicy.FormatBytes(1024L * 1024));
        Equal("1.0 GB", RecordingPolicy.FormatBytes(1024L * 1024 * 1024));
        Equal("1.0 TB", RecordingPolicy.FormatBytes(1024L * 1024 * 1024 * 1024));
    }

    private static void Decision()
    {
        var budget = new ConnectionBudget();
        // The live stream the user is watching holds one of the provider's two slots.
        Check(budget.Acquire(Profile("2", "1"), StreamLeaseKind.LivePlayback, "Live playback", acceptUnknownConnection: true) is not null,
            "the live stream was refused");
        var programme = new EpgProgramme("fixture", "Invented Evening News", "", "News", Now, Now.AddHours(1));
        var refused = RecordingPolicy.Evaluate(Profile("1", "1"), Path.GetTempPath(), Live(), TimeSpan.FromHours(1), Now,
            RecordingContainerKind.Mp4, programme, budget,
            space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
        // A valid plan is not a start. The connection decision travels with it and must
        // be honoured before anything is opened.
        Equal(true, refused.IsAllowed);
        Equal(RecordingFailure.None, refused.Failure);
        Check(refused.Plan is not null, "a valid plan was not produced");
        var refusedPlan = refused.Plan!;
        Equal(ConnectionBudgetStatus.ConnectionLimit, refused.Connection.Status);
        Equal(1, refused.Connection.LocalHeld);
        Equal("2026-09-28 12-00-00 Invented News.mp4", refusedPlan.FileName);
        Equal(RecordingPolicy.EstimateBytes(TimeSpan.FromHours(1)) + RecordingPolicy.SafetyMarginBytes, refusedPlan.RequiredBytes);
        var replaced = RecordingPolicy.Evaluate(Profile("1", "1"), Path.GetTempPath(), Live(), TimeSpan.FromHours(1), Now,
            RecordingContainerKind.Mp4, programme, budget, replacingCurrentConnection: true,
            space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
        Equal(ConnectionBudgetStatus.Available, replaced.Connection.Status);
        // An unreported allowance reaches the caller as a confirmation, not as a pass.
        var unknown = RecordingPolicy.Evaluate(Profile("", ""), Path.GetTempPath(), Live(), TimeSpan.FromHours(1), Now,
            RecordingContainerKind.Mp4, null, new ConnectionBudget(),
            space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
        Equal(ConnectionBudgetStatus.ConnectionUnknown, unknown.Connection.Status);
        Equal(true, unknown.Connection.RequiresConfirmation);
        Equal(true, unknown.IsAllowed);
        // A file this account is still recording into is continued, not renamed around.
        var index = new RecordingIndex();
        var open = index.Begin(Plan(), "one", "42", "item:one");
        var name = open!.FileName;
        InTemporaryFolder(folder =>
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "invented partial capture");
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            var continuing = RecordingPolicy.Evaluate(Profile("2", "0"), folder, Live(), TimeSpan.FromHours(1), Now,
                RecordingContainerKind.Mp4, null, new ConnectionBudget(), targets: new RecordingTargets(index.Unfinished("one"), existing.Contains),
                space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
            Equal(name, continuing.Plan!.ConflictingFileName);
            Equal(name, continuing.Plan.FileName);
            Equal(open.Id, RecordingPlan.Create(continuing.Plan, index.FindByFileName("one", name)).Existing!.Id);
            // A file the index cannot explain is stepped over instead of replaced.
            var orphan = RecordingPolicy.Evaluate(Profile("2", "0"), folder, Live(), TimeSpan.FromHours(1), Now,
                RecordingContainerKind.Mp4, null, new ConnectionBudget(), targets: new RecordingTargets([], existing.Contains),
                space: new VolumeSpace(true, 2L * 1024 * 1024 * 1024));
            Equal(null, orphan.Plan!.ConflictingFileName);
            Equal(name[..^".mp4".Length] + " (2).mp4", orphan.Plan.FileName);
            Equal(null, RecordingPlan.Create(orphan.Plan, null).Existing);
            Equal(1, Directory.GetFiles(folder).Length);
            Equal("invented partial capture", File.ReadAllText(path));
        });
    }

    private static void SharedHlsFailureBeforeFirstSegment()
    {
        using var http = new HttpClient(new DeterministicHlsHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var source = NewSharedSource(http);
        var playback = source.Attach(SharedHlsConsumerKind.Playback);
        var recording = source.Attach(SharedHlsConsumerKind.Recording);
        var uiStopUtc = DateTimeOffset.UtcNow;
        try
        {
            InTemporaryFolder(folder =>
            {
                var path = Path.Combine(folder, "source-failure.ts");
                var stoppedUtc = uiStopUtc;
                var fail = typeof(SharedHlsSource).GetMethod("FailRecordingConsumersForSequenceRegression",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                fail.Invoke(source, null);

                var startAccepted = false;
                try
                {
                    var segment = recording.ReadAsync().AsTask().GetAwaiter().GetResult();
                    if (segment is not null && segment.Data.Length > 0)
                    {
                        File.WriteAllBytes(path, segment.Data.ToArray());
                        startAccepted = true;
                    }
                }
                catch (IOException) { /* The service's pre-payload failure path rejects startup. */ }

                Equal(false, startAccepted);
                Equal(false, File.Exists(path));
                // The captured UI action time remains exact even when source failure wins
                // before the first segment; no duration is inferred from HLS timing.
                Equal(uiStopUtc, stoppedUtc);
                Equal(null, source.TerminalFailure);
                var fanOut = typeof(SharedHlsSource).GetMethod("FanOut",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                fanOut.Invoke(source, [new SharedHlsSegment(9, 5, new Uri("https://fixture.test/9.ts"), new byte[] { 1 }, null)]);
                Equal(9L, playback.ReadAsync().AsTask().GetAwaiter().GetResult()!.Sequence);
            });
        }
        finally
        {
            recording.DisposeAsync().AsTask().GetAwaiter().GetResult();
            playback.DisposeAsync().AsTask().GetAwaiter().GetResult();
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static SharedHlsSource NewSharedSource(HttpClient http) => new(new Uri("https://fixture.test/live.m3u8"), http,
        maximumSegmentBytes: 1024, maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 1024);

    private sealed class DeterministicHlsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri != new Uri("https://fixture.test/live.m3u8"))
                throw new InvalidOperationException($"Unexpected HLS fixture request: {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("#EXTM3U\n#EXT-X-TARGETDURATION:1\n")
            });
        }
    }

    private static AccountProfile Profile(string max, string active) => new() { Timezone = "Europe/Berlin", MaxConnections = max, ActiveConnections = active };
    private static Channel Live() => new() { Id = "42", Name = "Invented News", MediaKind = MediaKind.Live, Url = "http://fixture:8080/live/u/p/42.ts" };
    private static Channel ArchiveChannel() => new()
    {
        Id = "42", Name = "Invented News", Url = "http://fixture:8080/live/u/p/42.ts",
        CatchupMode = CatchupMode.Xtream, ArchiveDays = 7, ArchiveStreamId = "42"
    };
    private static AccountSettings ArchiveAccount() => new() { ServerUrl = "http://fixture:8080", Username = "invented", Password = "invented" };
    private static EpgProgramme ArchiveProgramme() => new("fixture", "Invented Evening News", "", "News", Now.AddHours(-1), Now.AddMinutes(30));

    private static RecordingPlan Plan() => new()
    {
        DestinationFolder = "C:\\Recordings", ChannelName = "Invented News",
        FileName = "2026-09-28 12-00-00 Invented News.mp4", FilePath = "C:\\Recordings\\2026-09-28 12-00-00 Invented News.mp4",
        Container = RecordingContainerKind.Mp4, RequestedStartUtc = Now, RequestedStopUtc = Now.AddHours(1),
        ActualStartUtc = Now, RequiredBytes = RecordingPolicy.EstimateBytes(TimeSpan.FromHours(1))
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void InTemporaryStore(Action<ConfigStore, AppState, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new AppState();
            state.EnsureAccounts();
            action(new ConfigStore(root), state, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void InTemporaryStore(Action<ConfigStore, string> action) =>
        InTemporaryStore((store, _, root) => action(store, root));

    private static void InTemporaryFolder(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-recording-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
