using ClassicWindowsIptvPlayer.Core;

internal static class AccountEditingRegressionChecks
{
    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("switching entries retains separate unsaved drafts", SwitchingRetainsDrafts),
            ("cancel and reopen discard uncommitted drafts", CancelDiscardsDrafts),
            ("invalid account blocks all changes without mutating state", InvalidBlocksCommit),
            ("save commits drafts and keeps active account selected", SavePreservesActiveSelection),
            ("confirmed removal is deferred and returns cache IDs on commit", RemovalIsDeferred),
            ("URL and field validation point to the offending entry", ValidationIdentifiesField)
        };
        var failed = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        Console.WriteLine($"{checks.Length - failed}/{checks.Length} account editing checks passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void SwitchingRetainsDrafts()
    {
        var state = State();
        var firstId = state.Accounts[0].Id;
        var secondId = state.Accounts[1].Id;
        var session = new AccountEditingSession(state);
        session.EditSelected("First draft", M3u("https://sample.invalid/first-draft.m3u"));
        session.Select(secondId);
        session.EditSelected("Second draft", M3u("https://sample.invalid/second-draft.m3u"));
        session.Select(firstId);
        Equal("First draft", session.Selected.Name);
        Equal("https://sample.invalid/first-draft.m3u", session.Selected.Settings.M3uUrl);
        session.Select(secondId);
        Equal("Second draft", session.Selected.Name);
        Equal("First", state.Accounts[0].Name);
        Equal("Second", state.Accounts[1].Name);
    }

    private static void CancelDiscardsDrafts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyrus-account-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ConfigStore(directory);
            store.Save(State());
            var session = new AccountEditingSession(store.Load());
            session.EditSelected("Discard me", M3u("https://sample.invalid/draft.m3u"));
            session.Add();
            var reopened = new AccountEditingSession(store.Load());
            Equal(2, reopened.Accounts.Count);
            Equal("First", reopened.Selected.Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void InvalidBlocksCommit()
    {
        var state = State();
        var session = new AccountEditingSession(state);
        session.EditSelected("Changed", M3u("https://sample.invalid/changed.m3u"));
        session.Add();
        Equal("ServerUrl", session.Validate()?.Field);
        try { session.ApplyTo(state); throw new Exception("Expected invalid draft to reject commit."); }
        catch (InvalidOperationException) { }
        Equal(2, state.Accounts.Count);
        Equal("First", state.Accounts[0].Name);
    }

    private static void SavePreservesActiveSelection()
    {
        var state = State();
        var firstId = state.SelectedAccountId;
        var secondId = state.Accounts[1].Id;
        var session = new AccountEditingSession(state);
        session.Select(secondId);
        session.EditSelected("Updated second", M3u("https://sample.invalid/updated.m3u"));
        Equal(null, session.Validate());
        Equal(0, session.ApplyTo(state).Count);
        Equal(firstId, state.SelectedAccountId);
        Equal("Updated second", state.Accounts[1].Name);
        Equal("https://sample.invalid/first.m3u", state.Account.M3uUrl);
        // A fresh dialog sees only committed edits.
        var reopened = new AccountEditingSession(state);
        reopened.Select(secondId);
        Equal("Updated second", reopened.Selected.Name);
    }

    private static void RemovalIsDeferred()
    {
        var state = State();
        var secondId = state.Accounts[1].Id;
        var session = new AccountEditingSession(state);
        session.Select(secondId);
        Equal(true, session.RemoveSelected());
        Equal(2, state.Accounts.Count);
        Equal(1, session.Accounts.Count);
        var removed = session.ApplyTo(state);
        Equal(1, removed.Count);
        Equal(secondId, removed[0]);
        Equal(1, state.Accounts.Count);
        Equal(false, session.RemoveSelected());
    }

    private static void ValidationIdentifiesField()
    {
        var state = State();
        var session = new AccountEditingSession(state);
        session.EditSelected("First", M3u("ftp://sample.invalid/first.m3u"));
        Equal("M3uUrl", session.Validate()?.Field);
        session.EditSelected("First", new AccountSettings { ServerUrl = "https://sample.invalid", Username = "user" });
        Equal("Password", session.Validate()?.Field);
        session.EditSelected("First", M3u("https://sample.invalid/first.m3u", "javascript:bad"));
        Equal("EpgUrl", session.Validate()?.Field);
    }

    private static AppState State()
    {
        var state = new AppState
        {
            BuiltInAccountsVersion = 2,
            Accounts =
            [
                new SavedAccount { Id = "first", Name = "First", Settings = M3u("https://sample.invalid/first.m3u") },
                new SavedAccount { Id = "second", Name = "Second", Settings = M3u("https://sample.invalid/second.m3u") }
            ],
            SelectedAccountId = "first"
        };
        state.EnsureAccounts();
        return state;
    }

    private static AccountSettings M3u(string url, string epg = "") => new() { M3uUrl = url, EpgUrl = epg };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}
