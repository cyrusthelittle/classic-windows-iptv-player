using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class StorageRegressionChecks
{
    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("settings backup survives stale temporary write and corrupt primary", SettingsRecovery),
            ("cache backup survives corrupt primary", CacheRecovery),
            ("legacy migration keeps unmatched favorites and original backup", LegacyMigration),
            ("account scoped series favorites and history stay distinct", AccountIsolation),
            ("stable item keys survive title and Xtream credential changes", StableIdentity),
            ("local files hide invented credentials; portable export omits them", ProtectedStorageAndExport),
            ("unrecoverable settings preserve existing files and explain failure", UnrecoverableSettings)
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS storage: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL storage: " + name + " - " + ex.Message); }
        }
        Console.WriteLine($"Storage checks: {checks.Length - failures}/{checks.Length} passed");
        return failures;
    }

    private static void SettingsRecovery()
    {
        UsingStore((store, root) =>
        {
            var state = NewState();
            state.VolumeLevel = 35;
            store.Save(state);
            state.VolumeLevel = 60;
            store.Save(state);
            File.WriteAllText(store.StatePath + ".tmp", "interrupted");
            Check(new ConfigStore(root).Load().VolumeLevel == 60, "stale temp replaced current settings");
            File.WriteAllText(store.StatePath, "{corrupt");
            var recovering = new ConfigStore(root);
            Check(recovering.Load().VolumeLevel == 35, "last good backup was not restored");
            Check(recovering.RecoveryNotice?.Contains("Recovered") == true, "recovery notice missing");
            Check(new ConfigStore(root).Load().VolumeLevel == 35, "recovered primary cannot be reopened");
        });
    }

    private static void CacheRecovery()
    {
        UsingStore((store, root) =>
        {
            var account = "one";
            store.SaveChannelCache(account, [Series("first")]);
            store.SaveChannelCache(account, [Series("second")]);
            File.WriteAllText(store.GetChannelCachePath(account), "bad");
            var recovering = new ConfigStore(root);
            Check(recovering.LoadChannelCache(account).Single().Name == "first", "cache backup was not restored");
            Check(recovering.RecoveryNotice?.Contains("Recovered") == true, "cache recovery notice missing");
        });
    }

    private static void LegacyMigration()
    {
        UsingStore((store, root) =>
        {
            var channel = Series("old title");
            channel.Id = "old-unstable-id";
            var stateJson = """
                {"SchemaVersion":1,"Accounts":[{"Id":"one","Name":"One","Settings":{}},{"Id":"two","Name":"Two","Settings":{}},{"Id":"three","Name":"Three","Settings":{}}],
                "SelectedAccountId":"one","FavoriteIds":["old-unstable-id","unmatched-id"],
                "FavoriteFolders":[{"Id":"folder","Name":"Saved","ChannelIds":["old-unstable-id","unmatched-id"]}],
                "Recent":[{"ChannelId":"old-unstable-id","Name":"old title","Url":"series:42","MediaKind":2}]}
                """;
            File.WriteAllText(store.StatePath, stateJson);
            Directory.CreateDirectory(Path.GetDirectoryName(store.ChannelCachePath)!);
            using (var file = File.Create(store.ChannelCachePath))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new List<Channel> { channel });
            using (var file = File.Create(store.GetChannelCachePath("three")))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new List<Channel> { Series("another") });
            var loaded = store.Load();
            Check(loaded.FavoriteIds.Contains(ItemIdentity.For(channel)), "matched favorite not remapped");
            Check(loaded.FavoriteIds.Contains("unmatched-id"), "unmatched favorite dropped");
            Check(loaded.FavoriteFolders.Single().ChannelIds.Contains("unmatched-id"), "unmatched folder entry dropped");
            Check(loaded.Recent.Single().ItemKey == ItemIdentity.For(channel), "recent identity not migrated");
            Check(File.Exists(store.StatePath + ".migration.bak"), "settings migration backup missing");
            Check(File.Exists(store.ChannelCachePath + ".migration.bak"), "cache migration backup missing");
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(store.StatePath + ".migration.bak")).StartsWith("CIPTV2"), "migration backup not protected");
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(store.StatePath + ".bak")).StartsWith("CIPTV2"), "regular backup left plaintext");
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(store.ChannelCachePath + ".bak")).StartsWith("CIPTV2"), "cache backup left plaintext");
            Check(store.LoadChannelCache("one").Count == 1, "legacy cache not assigned to original account");
            Check(store.LoadChannelCache("two").Count == 0, "legacy cache leaked into another account");
            Check(store.LoadChannelCache("three").Single().Name == "another", "existing account cache was lost");
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(store.GetChannelCachePath("three"))).StartsWith("CIPTV2"), "existing account cache was not protected");
            loaded.SelectedAccountId = "two";
            Check(loaded.FavoriteIds.Count == 0 && loaded.Recent.Count == 0, "legacy organization leaked into another account");
        });
    }

    private static void AccountIsolation()
    {
        UsingStore((store, root) =>
        {
            var state = NewState();
            var key = ItemIdentity.For(Series("show"));
            state.FavoriteIds.Add(key);
            state.Recent.Add(new RecentItem { ItemKey = key, Name = "one" });
            state.FavoriteFolders.Add(new FavoriteFolder { Name = "one", ChannelIds = [key] });
            state.SelectedAccountId = "two";
            Check(state.FavoriteIds.Count == 0 && state.Recent.Count == 0 && state.FavoriteFolders.Count == 0, "second account inherited organization");
            state.FavoriteIds.Add(key);
            state.Recent.Add(new RecentItem { ItemKey = key, Name = "two" });
            store.Save(state);
            var loaded = new ConfigStore(root).Load();
            Check(loaded.Recent.Single().Name == "two", "second account history was lost");
            loaded.SelectedAccountId = "one";
            Check(loaded.Recent.Single().Name == "one" && loaded.FavoriteFolders.Single().Name == "one", "same series ID crossed accounts");
        });
    }

    private static void StableIdentity()
    {
        var first = Series("Original");
        var renamed = Series("Renamed");
        Check(ItemIdentity.For(first) == ItemIdentity.For(renamed), "series rename changed identity");
        var a = new Channel { MediaKind = MediaKind.Movie, Url = "https://example.invalid/movie/user1/pass1/55.mp4", Name = "Old" };
        var b = new Channel { MediaKind = MediaKind.Movie, Url = "https://example.invalid/movie/user2/pass2/55.mp4", Name = "New" };
        Check(ItemIdentity.For(a) == ItemIdentity.For(b), "Xtream credential rotation changed identity");
        var c = new Channel { Url = "https://example.invalid/play?id=1&token=old" };
        var d = new Channel { Url = "https://example.invalid/play?token=new&id=1" };
        var e = new Channel { Url = "https://example.invalid/play?id=2&token=new" };
        Check(ItemIdentity.For(c) == ItemIdentity.For(d), "query token rotation changed identity");
        Check(ItemIdentity.For(c) != ItemIdentity.For(e), "distinct query stream IDs collided");
    }

    private static void ProtectedStorageAndExport()
    {
        UsingStore((store, root) =>
        {
            var state = NewState();
            state.Accounts[0].Settings.Password = "invented-secret-password";
            state.Accounts[0].Settings.M3uUrl = "https://example.invalid/list?token=invented-secret-token";
            store.Save(state);
            store.SaveChannelCache("one", [new Channel { Url = "https://example.invalid/live/user/invented-cache-secret/1.ts" }]);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.StatePath)).Contains("invented-secret"), "local settings expose credentials");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.GetChannelCachePath("one"))).Contains("invented-cache-secret"), "local cache exposes credentials");
            Check(store.Load().Accounts[0].Settings.Password == "invented-secret-password", "protected credentials cannot be reopened");
            var exportPath = Path.Combine(root, "portable.json");
            store.ExportPortable(exportPath, state);
            var export = File.ReadAllText(exportPath);
            Check(!export.Contains("invented-secret") && !export.Contains("example.invalid"), "portable export exposes provider data");
        });
    }

    private static void UnrecoverableSettings()
    {
        UsingStore((store, root) =>
        {
            File.WriteAllText(store.StatePath, "{bad");
            File.WriteAllText(store.StatePath + ".bak", "also bad");
            try { new ConfigStore(root).Load(); throw new Exception("corruption was ignored"); }
            catch (InvalidDataException ex)
            {
                Check(ex.Message.Contains("backup") && ex.Message.Contains("left in place"), "failure lacks recovery guidance");
                Check(File.ReadAllText(store.StatePath) == "{bad", "corrupt original was erased");
            }
        });
    }

    private static AppState NewState() => new()
    {
        Accounts = [new SavedAccount { Id = "one", Name = "One" }, new SavedAccount { Id = "two", Name = "Two" }],
        SelectedAccountId = "one",
        BuiltInAccountsVersion = 2
    };

    private static Channel Series(string name) => new() { Id = "legacy-" + name, Name = name, MediaKind = MediaKind.Series, Url = "series:42" };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void UsingStore(Action<ConfigStore, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-step04-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        check(new ConfigStore(root), root);
    }
}
