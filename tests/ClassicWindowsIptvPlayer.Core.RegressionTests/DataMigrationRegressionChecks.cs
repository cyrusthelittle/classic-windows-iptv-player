using ClassicWindowsIptvPlayer.Core;
internal static class DataMigrationRegressionChecks
{
    public static int Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "cyrus-data-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string Folder(string name) { var p = Path.Combine(fixture, name); Directory.CreateDirectory(p); return p; }
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS data migration: " + name); }
            void Fails(Action action, string name) { try { action(); } catch (Exception error) when (error is InvalidDataException or MissingAccountSettingsException) { Check(true, name); return; } throw new Exception(name); }
            var legacy = Folder("legacy"); var local = Folder("local");
            var store = new ConfigStore(legacy); var state = new AppState(); state.EnsureAccounts();
            var account = state.EnsureSelectedAccount(); account.Name = "Invented fixture"; account.Settings.Username = "invented-user"; account.Settings.Password = "invented-secret";
            state.DarkMode = true; state.RecordingFolder = ""; state.FavoriteIds.Add("fixture-favorite"); state.SelectedLibrary.ChannelOrganization["fixture"] = new ChannelOrganization { Name = "Renamed fixture" }; store.Save(state);
            store.SaveChannelCache(account.Id, new List<Channel> { new() { Id = "fixture", Name = "Fixture", Url = "http://example.test/fixture" } });
            using (var outbox = new FeedbackOutbox(Path.Combine(legacy, "cache", "feedback-outbox.dat"))) outbox.EnqueueFeedbackAsync("Invented pending message").GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.Combine(legacy, "logs", "session-fixture")); File.WriteAllText(Path.Combine(legacy,"logs","active-session.txt"),Path.Combine(legacy,"logs","session-fixture"));
            store.SaveGuideCache(account.Id, new EpgGuideSnapshot(DateTimeOffset.UtcNow, new(), new(), new()));
            // Model legacy plaintext cache files, including their previous good backup.
            foreach (var cachePath in new[] { store.GetChannelCachePath(account.Id), store.GetGuideCachePath(account.Id) })
            {
                var protectedText = File.ReadAllText(cachePath);
                var unprotect = typeof(ConfigStore).GetMethod("Unprotect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var plaintext = (byte[])unprotect.Invoke(null, new object[] { System.Text.Encoding.UTF8.GetBytes(protectedText) })!;
                File.WriteAllBytes(cachePath, plaintext);
                File.WriteAllBytes(cachePath + ".bak", plaintext);
            }
            var recording = new RecordingIndex { Entries = new() { new RecordingEntry { Id = "fixture-recording", AccountId = account.Id, FileName = "fixture.ts", FilePath = "Recordings/fixture.ts", Outcome = RecordingOutcome.Recording } } };
            using (var output = File.Create(store.GetRecordingIndexPath(account.Id)))
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
                System.Text.Json.JsonSerializer.Serialize(gzip, recording);
            File.WriteAllText(Path.Combine(legacy,"cache","channels-stale.json.gz.refresh.tmp"),"unfinished");
            File.WriteAllText(Path.Combine(legacy,"cache","recordings-stale.json.gz.migration.bak"),"artifact");
            var originals = Directory.GetFiles(legacy,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
            foreach (var p in originals.Keys) File.SetAttributes(p,File.GetAttributes(p)|FileAttributes.ReadOnly);
            var root = AppDataPaths.Initialize(legacy, local); var loaded = new ConfigStore(root).Load();
            Check(loaded.EnsureSelectedAccount().Settings.Password == "invented-secret" && loaded.DarkMode, "same-user DPAPI accounts and preference continuity");
            Check(loaded.FavoriteIds.Contains("fixture-favorite") && loaded.SelectedLibrary.ChannelOrganization["fixture"].Name == "Renamed fixture", "favorites and organization survive");
            Check(new ConfigStore(root).LoadGuideCache(account.Id) is not null,"guide cache survives");
            Check(new[] { store.GetChannelCachePath(account.Id), store.GetGuideCachePath(account.Id) }.All(p =>
                new[] { p, p + ".bak" }.All(source => File.ReadAllText(Path.Combine(root, "cache", Path.GetFileName(source))).StartsWith("CIPTV2\n"))), "legacy plaintext channel and guide primary/backup copies protected before commit");
            var capture = new ConfigStore(root).LoadRecordingIndex(account.Id)!.Entries.Single();
            Check(capture.FilePath == Path.GetFullPath("Recordings/fixture.ts",legacy) && capture.StopReason == RecordingStopReason.Restarted,"relative recording path retained and interrupted capture finalized on staged copy");
            Check(File.Exists(Path.Combine(root,"cache","channels-stale.json.gz.refresh.tmp")) && !File.Exists(Path.Combine(root,"cache","recordings-stale.json.gz.migration")),"stale temp and migration artifacts retained without operational validation");
            Check(new ConfigStore(root).LoadChannelCache(account.Id).Single().Id == "fixture", "verified cached channels survive");
            Check(loaded.RecordingFolder == Path.Combine(legacy,"Recordings") && !Directory.Exists(Path.Combine(root,"Recordings")), "exact legacy default recordings destination retained without moving files");
            Check(originals.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "read-only legacy sources untouched");
            Check(File.Exists(Path.Combine(root,"pre-migration","accounts.json")),"protected pre-migration recovery retained");
            Check(File.ReadAllText(Path.Combine(root,"logs","active-session.txt"))==Path.Combine(root,"logs","session-fixture"),"session marker remapped");
            using(var outbox=new FeedbackOutbox(Path.Combine(root,"cache","feedback-outbox.dat"))) Check(outbox.GetPendingAsync().GetAwaiter().GetResult().Count==1,"pending feedback retained without sending");
            Check(AppDataPaths.Initialize(legacy,local)==root,"repeat migration reuses established user data");
            var badLegacy=Folder("bad-legacy"); File.WriteAllText(Path.Combine(badLegacy,"accounts.json"),"broken");var badLocal=Folder("bad-local");
            Fails(()=>AppDataPaths.Initialize(badLegacy,badLocal),"corrupt primary and absent backup fail closed");
            Check(!Directory.Exists(Path.Combine(badLocal,"ClassicWindowsIptvPlayer")) && Directory.GetDirectories(badLocal,"*.migration-*").Length==1,"failed stage retained, no empty destination committed");
            new ConfigStore(badLegacy).Save(new AppState()); Check(File.Exists(Path.Combine(AppDataPaths.Initialize(badLegacy,badLocal),"accounts.json")),"retry after corrected source succeeds");
            var recovered=Folder("recover-legacy"); var rs=new ConfigStore(recovered);rs.Save(state);rs.Save(state);File.WriteAllText(rs.StatePath,"corrupt");
            Check(new ConfigStore(AppDataPaths.Initialize(recovered,Folder("recover-local"))).Load().EnsureSelectedAccount().Settings.Password=="invented-secret","valid protected backup recovers corrupt primary on staged copy");
            Check(File.ReadAllText(rs.StatePath)=="corrupt","backup recovery leaves original primary unchanged");
            var empty=Folder("empty-install");var freshLocal=Folder("fresh-local");var fresh=AppDataPaths.Initialize(empty,freshLocal);Directory.CreateDirectory(Path.Combine(fresh,"logs"));File.WriteAllText(Path.Combine(fresh,"logs","first.log"),"fixture");Check(AppDataPaths.Initialize(empty,freshLocal)==fresh,"canceled unsaved first run retries despite logs");
            var orphanLocal=Folder("orphan-local");Directory.CreateDirectory(Path.Combine(orphanLocal,"ClassicWindowsIptvPlayer","cache"));Fails(()=>AppDataPaths.Initialize(empty,orphanLocal),"unmarked orphan current root fails closed");
            var diagnosticLocal = Folder("diagnostic-local");
            var diagnosticRoot = Path.Combine(diagnosticLocal, "ClassicWindowsIptvPlayer");
            Directory.CreateDirectory(Path.Combine(diagnosticRoot, "logs"));
            File.WriteAllText(Path.Combine(diagnosticRoot, "startup-crash.log"), "invented historical diagnostic");
            var diagnosticsMigrated = AppDataPaths.Initialize(legacy, diagnosticLocal);
            Check(new ConfigStore(diagnosticsMigrated).Load().EnsureSelectedAccount().Settings.Password == "invented-secret", "historical diagnostic-only root permits legacy account migration");
            Check(Directory.GetDirectories(diagnosticLocal, "*.first-run-*").Any(p => File.ReadAllText(Path.Combine(p, "startup-crash.log")) == "invented historical diagnostic"), "historical diagnostics retained during migration");
            var recoveryLegacy = Folder("fresh-recovery-legacy");
            Directory.CreateDirectory(Path.Combine(recoveryLegacy, "cache"));
            File.WriteAllText(Path.Combine(recoveryLegacy, "cache", "fixture"), "orphan legacy cache");
            Directory.CreateDirectory(Path.Combine(recoveryLegacy, "Recordings"));
            File.WriteAllText(Path.Combine(recoveryLegacy, "Recordings", "fixture.ts"), "invented recording");
            var recoveryLocal = Folder("fresh-recovery-local");
            var recoveryRoot = Path.Combine(recoveryLocal, "ClassicWindowsIptvPlayer");
            Directory.CreateDirectory(Path.Combine(recoveryRoot, "cache"));
            File.WriteAllText(Path.Combine(recoveryRoot, "cache", "fixture"), "orphan current cache");
            MissingAccountSettingsException recovery;
            try { AppDataPaths.Initialize(recoveryLegacy, recoveryLocal); throw new Exception("Expected missing settings"); }
            catch (MissingAccountSettingsException error) { recovery = error; }
            Check(!File.Exists(Path.Combine(recoveryRoot, "accounts.json")), "declining recovery requires no writes to account settings");
            recovery.StartFresh();
            Check(File.ReadAllText(Path.Combine(recoveryRoot, "accounts.json")).StartsWith("CIPTV2\n"), "explicit fresh recovery writes protected settings");
            Check(AppDataPaths.Initialize(recoveryLegacy, recoveryLocal) == recoveryRoot, "fresh settings bypass orphan legacy caches on reopen");
            Check(File.ReadAllText(Path.Combine(recoveryRoot, "cache", "fixture")) == "orphan current cache" && File.ReadAllText(Path.Combine(recoveryLegacy, "cache", "fixture")) == "orphan legacy cache" && File.ReadAllText(Path.Combine(recoveryLegacy, "Recordings", "fixture.ts")) == "invented recording", "fresh recovery preserves current and legacy cache and recordings");
            Check(new ConfigStore(recoveryRoot).Load().RecordingFolder == Path.Combine(recoveryLegacy, "Recordings"), "fresh recovery preserves original default recording destination");
            var freshBytes = File.ReadAllBytes(Path.Combine(recoveryRoot, "accounts.json"));
            var refusedFresh = false;
            try { recovery.StartFresh(); } catch (IOException) { refusedFresh = true; }
            Check(refusedFresh && File.ReadAllBytes(Path.Combine(recoveryRoot, "accounts.json")).SequenceEqual(freshBytes), "fresh recovery refuses overwriting restored settings");
            var reportedLocal = Folder("reported-shape-local");
            var reportedRoot = Path.Combine(reportedLocal, "ClassicWindowsIptvPlayer");
            Directory.CreateDirectory(Path.Combine(reportedRoot, "logs"));
            File.WriteAllText(Path.Combine(reportedRoot, "runtime-crash.log"), "invented old diagnostic");
            MissingAccountSettingsException reportedRecovery;
            try { AppDataPaths.Initialize(recoveryLegacy, reportedLocal); throw new Exception("Expected legacy missing settings"); }
            catch (MissingAccountSettingsException error) { reportedRecovery = error; }
            using (var activeLease = AppDataLease.Acquire(reportedRoot))
            {
                var blockedRecovery = false;
                var contender = new Thread(() => { try { reportedRecovery.StartFresh(); } catch (IOException) { blockedRecovery = true; } });
                contender.Start(); contender.Join();
                Check(blockedRecovery && !File.Exists(Path.Combine(reportedRoot, "accounts.json")), "fresh recovery refuses competing runtime owner without writing settings");
            }
            File.WriteAllBytes(Path.Combine(reportedRoot, "accounts.json.bak"), freshBytes);
            var backupWon = false;
            try { reportedRecovery.StartFresh(); } catch (IOException) { backupWon = true; }
            Check(backupWon && !File.Exists(Path.Combine(reportedRoot, "accounts.json")), "restored backup wins over requested fresh recovery");
            File.Delete(Path.Combine(reportedRoot, "accounts.json.bak"));
            reportedRecovery.StartFresh();
            Check(AppDataPaths.Initialize(recoveryLegacy, reportedLocal) == reportedRoot && File.ReadAllText(Path.Combine(reportedRoot, "runtime-crash.log")) == "invented old diagnostic", "reported diagnostic-only root plus orphan legacy cache recovers explicitly and reopens with diagnostics intact");
            var lateLegacy=Folder("late-legacy");new ConfigStore(lateLegacy).Save(state);Check(File.Exists(Path.Combine(AppDataPaths.Initialize(lateLegacy,freshLocal),"accounts.json")) && Directory.GetDirectories(freshLocal,"*.first-run-*").Length==1,"legacy data appearing after canceled first run retains prior diagnostics");
            var brokenCache=Folder("broken-cache");var bs=new ConfigStore(brokenCache);bs.Save(state);File.WriteAllText(bs.GetChannelCachePath(account.Id),"invalid");File.WriteAllText(bs.GetChannelCachePath(account.Id)+".bak","invalid");Fails(()=>AppDataPaths.Initialize(brokenCache,Folder("broken-cache-local")),"corrupt primary and backup cache abort commit");
            var newer=Folder("newer-schema");File.WriteAllText(Path.Combine(newer,"accounts.json"),"{\"SchemaVersion\":999,\"Accounts\":[],\"Account\":{},\"AccountLibraries\":{}}");Fails(()=>AppDataPaths.Initialize(newer,Folder("newer-schema-local")),"newer settings schema rejected without overwrite");
            var missingSettings=Folder("missing-settings");Directory.CreateDirectory(Path.Combine(missingSettings,"cache"));File.WriteAllText(Path.Combine(missingSettings,"cache","fixture"),"orphan");Fails(()=>AppDataPaths.Initialize(missingSettings,Folder("missing-settings-local")),"orphan legacy cache fails closed");
            var blocked=Folder("blocked-local");File.WriteAllText(Path.Combine(blocked,"ClassicWindowsIptvPlayer"),"blocking file");Fails(()=>AppDataPaths.Initialize(legacy,blocked),"destination collision retains source and stage");
            var badSession=Folder("bad-session");new ConfigStore(badSession).Save(state);Directory.CreateDirectory(Path.Combine(badSession,"logs"));File.WriteAllText(Path.Combine(badSession,"logs","active-session.txt"),"outside-relative");var badSessionRoot=AppDataPaths.Initialize(badSession,Folder("bad-session-local"));Check(!File.Exists(Path.Combine(badSessionRoot,"logs","active-session.txt")) && File.Exists(Path.Combine(badSessionRoot,"pre-migration","logs","active-session.txt")),"invalid crash marker ignored with original snapshot retained");
            var queueCorruption=Folder("queue-corruption");new ConfigStore(queueCorruption).Save(state);File.WriteAllBytes(Path.Combine(queueCorruption,"cache","feedback-outbox.dat"),new byte[]{1,2,3});Fails(()=>AppDataPaths.Initialize(queueCorruption,Folder("queue-corruption-local")),"unreadable feedback queue prevents commit");
            var leaseRoot=Folder("lease");using(var lease=AppDataLease.Acquire(leaseRoot))
            {
                var refused=false;
                var thread=new Thread(()=>{try{using var competing=AppDataLease.Acquire(leaseRoot);}catch(IOException){refused=true;}});thread.Start();thread.Join();Check(refused,"runtime data lease refuses competing owner");
            }
            using(var lease=AppDataLease.Acquire(leaseRoot)) Check(true,"runtime data lease releases cleanly");
            var firstState=new ConfigStore(fresh);firstState.Save(state);Check(!File.Exists(Path.Combine(fresh,"first-run.pending")),"saved first run removes pending marker");
            var portable=Folder("portable");File.WriteAllText(Path.Combine(portable,AppDataPaths.PortableMarker),"");Check(AppDataPaths.Initialize(portable,local)==portable,"explicit portable marker uses executable root");
            var overridden=Folder("override");Check(AppDataPaths.Initialize(badLegacy,local,overridden)==overridden,"explicit isolated override never imports install data");
            var pinned=AppDataPaths.Root;Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable,Folder("changed-env"));Check(AppDataPaths.Root==pinned,"root remains pinned throughout launch");
            Console.WriteLine("Data migration fixtures: "+fixture);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
