using System.Text;
using ClassicWindowsIptvPlayer.Core;

internal static class OrganizationRegressionChecks
{
    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("rename hide and order survive provider refresh with stable keys", Organization),
            ("favorite ordering and folder ordering persist", FavoriteOrder),
            ("portable preview and import only matching accounts without secrets", Portable),
            ("protected backup restores settings and cached library", Backup)
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS organization: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL organization: " + name + " - " + ex.Message); }
        }
        Console.WriteLine($"Organization checks: {checks.Length - failures}/{checks.Length} passed");
        return failures;
    }

    private static void Organization()
    {
        InTemporaryStore((store, state, _) =>
        {
            var one = Channel("One", "Alpha", "http://example.invalid/a");
            var two = Channel("Two", "Beta", "http://example.invalid/b");
            var three = Channel("Three", "Alpha", "http://example.invalid/c");
            var key = ItemIdentity.For(one);
            var library = state.SelectedLibrary;
            library.ChannelOrganization[key] = new ChannelOrganization { Name = "First renamed", Order = 1 };
            library.ChannelOrganization[ItemIdentity.For(three)] = new ChannelOrganization { Hidden = true };
            library.GroupOrganization["Alpha"] = new GroupOrganization { Name = "My group", Order = 1 };
            library.GroupOrganization["Beta"] = new GroupOrganization { Order = 2 };
            store.Save(state);
            var restored = store.Load().SelectedLibrary;
            var displayed = LibraryOrganization.Apply([two, one, three], restored);
            Check(displayed.Count == 2, "hidden item reappeared");
            Check(displayed[0].Name == "First renamed" && displayed[0].Group == "My group", "rename or group order failed");
            Check(ItemIdentity.For(displayed[0]) == key, "rename changed identity");
            Check(one.Name == "One" && one.Group == "Alpha", "provider cache source was mutated");
            restored.GroupOrganization["Beta"].Hidden = true;
            Check(LibraryOrganization.Apply([one, two], restored).Count == 1, "hidden group reappeared");
        });
    }

    private static void FavoriteOrder()
    {
        InTemporaryStore((store, state, _) =>
        {
            state.FavoriteIds.AddRange(["first", "second", "third"]);
            LibraryOrganization.MoveFavorite(state.FavoriteIds, "third", -1);
            state.FavoriteFolders.Add(new FavoriteFolder { Name = "Later" });
            state.FavoriteFolders.Insert(0, new FavoriteFolder { Name = "Earlier" });
            store.Save(state);
            var loaded = store.Load();
            Check(string.Join(",", loaded.FavoriteIds) == "first,third,second", "favorite order lost");
            Check(loaded.FavoriteFolders[0].Name == "Earlier", "folder order lost");
        });
    }

    private static void Portable()
    {
        InTemporaryStore((store, state, root) =>
        {
            state.Accounts[0].Settings.Username = "invented-user-secret";
            state.Accounts[0].Settings.Password = "invented-password-secret";
            state.FavoriteIds.Add("item:123");
            state.SelectedLibrary.ChannelOrganization["item:123"] = new ChannelOrganization { Name = "My channel" };
            var path = Path.Combine(root, "organization.json");
            store.ExportPortable(path, state);
            var json = File.ReadAllText(path);
            Check(!json.Contains("invented-user-secret") && !json.Contains("invented-password-secret") && !json.Contains("http://"), "portable export exposed credentials");
            var preview = store.PreviewPortable(path, state);
            Check(preview.MatchingAccountCount == 1 && preview.ChannelRuleCount == 1, "preview counts wrong");
            state.FavoriteIds.Clear();
            state.SelectedLibrary.ChannelOrganization.Clear();
            store.ImportPortable(path, state);
            var loaded = store.Load();
            Check(loaded.FavoriteIds.SequenceEqual(["item:123"]) && loaded.SelectedLibrary.ChannelOrganization.Count == 1, "import failed");
            Check(loaded.Accounts[0].Settings.Password == "invented-password-secret", "import changed credentials");
            var other = new AppState();
            other.EnsureAccounts();
            other.Accounts.Clear();
            other.Accounts.Add(new SavedAccount { Id = "different-local-account", Name = "Destination" });
            other.SelectedAccountId = "different-local-account";
            var unmatched = store.PreviewPortable(path, other);
            Check(unmatched.MatchingAccountCount == 0 && unmatched.Sources.Count == 1, "unmatched source preview wrong");
            store.ImportPortableIntoAccount(path, other, unmatched.Sources[0].AccountId, other.SelectedAccountId);
            Check(other.FavoriteIds.SequenceEqual(["item:123"]) && other.SelectedLibrary.ChannelOrganization.Count == 1,
                "cross-account import failed");
            File.WriteAllText(path, "{\"Format\":\"wrong\"}");
            try { store.ImportPortable(path, loaded); throw new Exception("invalid import accepted"); }
            catch (InvalidDataException) { }
            Check(store.Load().FavoriteIds.Count == 1, "invalid import changed data");
        });
    }

    private static void Backup()
    {
        InTemporaryStore((store, state, root) =>
        {
            state.VolumeLevel = 42;
            store.Save(state);
            store.SaveChannelCache(state.SelectedAccountId, [Channel("Saved", "Group", "http://example.invalid/stream")]);
            var path = Path.Combine(root, "local.zip");
            store.CreateBackup(path);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("example.invalid"), "backup exposed stream URL");
            state.VolumeLevel = 80;
            store.Save(state);
            store.SaveChannelCache(state.SelectedAccountId, [Channel("New", "Group", "http://example.invalid/new")]);
            store.RestoreBackup(path);
            Check(store.Load().VolumeLevel == 42, "settings were not restored");
            Check(store.LoadChannelCache(state.SelectedAccountId).Single().Name == "Saved", "cache was not restored");
        });
    }

    private static Channel Channel(string name, string group, string url) => new() { Name = name, Group = group, Url = url };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void InTemporaryStore(Action<ConfigStore, AppState, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-organization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ConfigStore(root);
            var state = new AppState();
            state.EnsureAccounts();
            action(store, state, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
