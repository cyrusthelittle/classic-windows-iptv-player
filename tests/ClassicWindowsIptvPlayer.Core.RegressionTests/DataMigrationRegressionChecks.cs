using ClassicWindowsIptvPlayer.Core;
internal static class DataMigrationRegressionChecks
{
    public static int Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "cyrus-portable-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string Folder(string name) { var path = Path.Combine(fixture, name); Directory.CreateDirectory(path); return path; }
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
        try
        {
            var install = Folder("install"); var local = Folder("local");
            var old = Path.Combine(local, "ClassicWindowsIptvPlayer");
            var state = new AppState(); state.EnsureAccounts(); state.EnsureSelectedAccount().Settings.Password = "invented-fixture";
            var oldStore = new ConfigStore(old); oldStore.Save(state);
            Directory.CreateDirectory(Path.Combine(old, "Recordings")); File.WriteAllText(Path.Combine(old, "Recordings", "fixture.ts"), "fixture recording");
            File.WriteAllText(Path.Combine(old, "cache", "fixture.txt"), "fixture cache");
            var original = File.ReadAllBytes(oldStore.StatePath);
            Check(AppDataPaths.Initialize(install, local) == install, "default root is beside executable");
            var portable = new ConfigStore(install);
            Check(portable.StatePath == Path.Combine(install, "accounts", "accounts.json"), "accounts have their own folder");
            Check(portable.Load().EnsureSelectedAccount().Settings.Password == "invented-fixture", "old AppData accounts imported");
            Check(File.ReadAllText(Path.Combine(install, "Recordings", "fixture.ts")) == "fixture recording", "recording files imported");
            Check(original.SequenceEqual(File.ReadAllBytes(oldStore.StatePath)), "original accounts retained unchanged");
            Check(AppDataPaths.Initialize(install, local) == install, "migration is idempotent");
            var existing = Folder("existing"); new ConfigStore(existing).Save(state);
            AppDataPaths.Initialize(existing, local);
            Check(new ConfigStore(existing).Load().EnsureSelectedAccount().Settings.Password == "invented-fixture", "existing executable accounts preserved");
            var isolated = Folder("isolated"); AppDataPaths.Initialize(existing, local, isolated);
            Check(!File.Exists(Path.Combine(isolated, "accounts", "accounts.json")), "explicit fixture root does not import user accounts");
            var bad = Folder("bad-install"); File.WriteAllText(Path.Combine(bad, "accounts.json"), "corrupt");
            var rejected = false; try { AppDataPaths.Initialize(bad, local); } catch { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(bad, "accounts.json")) == "corrupt", "corrupt source fails without replacing original");
            Check(!File.Exists(Path.Combine(bad, "accounts", "accounts.json")), "failed migration does not publish accounts");
            var orphan = Folder("orphan"); Directory.CreateDirectory(Path.Combine(orphan, "cache")); File.WriteAllText(Path.Combine(orphan, "cache", "keep.txt"), "keep");
            var orphanLocal = Folder("orphan-local"); var oldOrphan = Path.Combine(orphanLocal, "ClassicWindowsIptvPlayer");
            Directory.CreateDirectory(Path.Combine(oldOrphan, "cache")); File.WriteAllText(Path.Combine(oldOrphan, "cache", "keep.txt"), "keep");
            MissingAccountSettingsException? missing = null;
            try { AppDataPaths.Initialize(orphan, orphanLocal); } catch (MissingAccountSettingsException ex) { missing = ex; }
            Check(missing is not null, "orphan user data fails closed");
            missing!.StartFresh();
            Check(File.Exists(Path.Combine(orphan, "accounts", "accounts.json")), "explicit fresh recovery uses accounts folder");
            Check(File.ReadAllText(Path.Combine(orphan, "cache", "keep.txt")) == "keep", "fresh recovery retains existing files");
            var external = Folder("external-recordings"); var recording = Path.Combine(external, "keep.ts"); File.WriteAllText(recording, "recording fixture");
            var playlist = Path.Combine(external, "keep.m3u"); File.WriteAllText(playlist, "#EXTM3U");
            state.RecordingFolder = external; state.EnsureSelectedAccount().Settings.M3uUrl = playlist;
            var defaultStore = new ConfigStore(); defaultStore.Save(state); var loaded = defaultStore.Load();
            Check(loaded.RecordingFolder == Path.Combine(AppDataPaths.Root, "Recordings") && File.Exists(Path.Combine(loaded.RecordingFolder, "keep.ts")), "external recordings imported beside executable");
            Check(loaded.EnsureSelectedAccount().Settings.M3uUrl == Path.Combine(AppDataPaths.Playlists, "keep.m3u"), "local playlist imported and account reference updated");
            Check(File.Exists(recording) && File.Exists(playlist), "external originals retained");
            Console.WriteLine("Portable migration fixtures: " + fixture);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
