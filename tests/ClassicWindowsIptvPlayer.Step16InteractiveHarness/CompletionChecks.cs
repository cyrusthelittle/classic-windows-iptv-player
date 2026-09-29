using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Automation;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

namespace ClassicWindowsIptvPlayer.Step16InteractiveHarness;
internal static partial class Program
{
    private static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
    private static void RunCompletionChecks(MainWindow window, ListBox list, string root, Action<string, bool> check)
    {
        Invoke(window, "FolderBack_Click"); WaitUi(window);
        list.SelectedItem = list.Items.Cast<object>().Single(x => GetFolder(x) == "Sports");
        var index = list.SelectedIndex;
        Rename(window, list, "Renamed Sports", false, root, "Rename group", "Rename group...", index);
        check("RenderedGroupRename", list.Items.Cast<object>().Any(x => GetFolder(x) == "Renamed Sports"));
        var target = list.Items.Cast<object>().Single(x => GetFolder(x) == "Renamed Sports");
        Click(OpenMenu(list, list.Items.IndexOf(target)).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Hide group")); WaitUi(window);
        check("RenderedGroupHide", !list.Items.Cast<object>().Any(x => GetFolder(x) == "Renamed Sports"));
        WithNativeDialogs(window, () => Invoke(window, "RestoreHiddenItems_Click"), null, "Restore hidden items", 6, root);
        WaitUi(window);
        check("HiddenRecoveryDialog", list.Items.Cast<object>().Any(x => GetFolder(x) == "Renamed Sports") && new ConfigStore().Load().SelectedLibrary.ChannelOrganization.Values.All(x => !x.Hidden));
        if (Environment.GetCommandLineArgs().Contains("--native-dialogs"))
        {
        var export = Path.Combine(root, "native-export.json");
        WithNativeDialogs(window, () => Invoke(window, "ExportOrganization_Click"), export, "Organization exported", 1, root);
        check("NativeExportPicker", File.Exists(export));
        WithNativeDialogs(window, () => Invoke(window, "ImportOrganization_Click"), export, "Preview organization import", 6, root); WaitUi(window);
        check("NativeImportPreview", ((TextBlock)window.FindName("StatusText")).Text == "Organization imported.");
        var backup = Path.Combine(root, "native-backup.zip");
        WithNativeDialogs(window, () => Invoke(window, "BackupLocalData_Click"), backup, "Backup complete", 1, root);
        check("NativeBackupPicker", File.Exists(backup));
        WithNativeDialogs(window, () => Invoke(window, "RestoreLocalData_Click"), backup, "Restore backup", 6, root); WaitUi(window);
        check("NativeRestorePicker", ((TextBlock)window.FindName("StatusText")).Text == "Local backup restored.");
        }
        WithNativeDialogs(window, () => Invoke(window, "AccountInfo_Click"), null, "Account information", 1, root);
        check("AccountInformationPlaylistDialog", File.ReadAllText(Path.Combine(root, "native-dialogs.txt")).Contains("Connection use and expiry are unavailable"));
        var accountState = (AppState)typeof(MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var originalAccount = accountState.Account.Clone();
        using (var fixture = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            fixture.Start(); var port = ((System.Net.IPEndPoint)fixture.LocalEndpoint).Port;
            var serving = System.Threading.Tasks.Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var client = await fixture.AcceptTcpClientAsync(deadline.Token);
                using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                var body = "{\"user_info\":{\"status\":\"Active\",\"exp_date\":\"1893456000\",\"active_cons\":\"1\",\"max_connections\":\"3\"},\"server_info\":{\"timezone\":\"UTC\"}}";
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
            });
            try
            {
                accountState.Account.ServerUrl = "http://127.0.0.1:" + port; accountState.Account.Username = "fixture"; accountState.Account.Password = "invented";
                WithNativeDialogs(window, () => Invoke(window, "AccountInfo_Click"), null, "Account information", 1, root);
                var dialogText = File.ReadAllText(Path.Combine(root, "native-dialogs.txt"));
                check("AccountConnectionExpiryFixtureDialog", dialogText.Contains("Connections in use / account limit: 1 / 3") && dialogText.Contains("Expiry (local time): 2030-01-01"));
                serving.GetAwaiter().GetResult();
            }
            finally { accountState.Account = originalAccount; fixture.Stop(); }
        }
        var state = new ConfigStore().Load();
        var fresh = new MainWindow(new LoginResult { Account = state.Account.Clone(), AccountId = state.SelectedAccountId, UpdatePlaylist = false });
        fresh.Show(); WaitUi(fresh); WaitUi(fresh);
        var freshList = (ListBox)fresh.FindName("ChannelList");
        check("FreshWindowOrganization", freshList.Items.Cast<object>().Any(x => GetFolder(x) == "Renamed Sports"));
        fresh.Close();
        var activeState = (AppState)typeof(MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var sourceChannels = (List<Channel>)typeof(MainWindow).GetField("_sourceChannels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var firstKey = ItemIdentity.For(sourceChannels[0]); var secondKey = ItemIdentity.For(sourceChannels[1]);
        activeState.FavoriteIds.Clear(); activeState.FavoriteIds.AddRange(new[] { firstKey, secondKey });
        activeState.FavoriteFolders.Clear();
        activeState.FavoriteFolders.Add(new FavoriteFolder { Name = "Fixture one", ChannelIds = new() { firstKey, secondKey } });
        activeState.FavoriteFolders.Add(new FavoriteFolder { Name = "Fixture two", ChannelIds = new() { firstKey } });
        new ConfigStore().Save(activeState);
        Invoke(window, "FavoritesView_Click"); WaitUi(window);
        var folderIndex = list.Items.Cast<object>().Select((x, i) => (x, i)).Single(p => GetFolder(p.x) == "Fixture one").i;
        Click(OpenMenu(list, folderIndex).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Move folder down")); WaitUi(window);
        check("FavoriteFolderOrderMenu", new ConfigStore().Load().FavoriteFolders[0].Name == "Fixture two");
        folderIndex = list.Items.Cast<object>().Select((x, i) => (x, i)).Single(p => GetFolder(p.x) == "Fixture one").i;
        Rename(window, list, "Renamed favorite", false, root, "Rename favorite folder", "Rename folder", folderIndex);
        check("FavoriteFolderRenameDialog", new ConfigStore().Load().FavoriteFolders.Any(x => x.Name == "Renamed favorite"));
        list.SelectedItem = list.Items.Cast<object>().Single(x => GetFolder(x) == "Renamed favorite");
        typeof(MainWindow).GetMethod("ActivateSelectedListEntry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); WaitUi(window);
        var favoriteBefore = new ConfigStore().Load().FavoriteIds.ToArray();
        Click(OpenMenu(list, 0).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Move favorite down")); WaitUi(window);
        check("FavoriteItemOrderMenu", new ConfigStore().Load().FavoriteIds.SequenceEqual(favoriteBefore.Reverse()));
        var remapFile = Path.Combine(root, "remap-organization.json"); new ConfigStore().ExportPortable(remapFile, activeState);
        var destination = new AppState(); var targetAccount = destination.EnsureSelectedAccount();
        new ConfigStore().ImportPortableIntoAccount(remapFile, destination, activeState.SelectedAccountId, targetAccount.Id);
        check("ImportAccountRemapStorage", destination.SelectedLibrary.FavoriteIds.Count == 2 && destination.FavoriteFolders.Any(x => x.Name == "Renamed favorite"));
        window.Width = 850; window.Height = 550; window.UpdateLayout();
        check("CompactSearchReachable", ((TextBox)window.FindName("SearchBox")).IsVisible && ((TextBox)window.FindName("SearchBox")).ActualWidth > 20);
        File.AppendAllText(Path.Combine(root, "HARNESS_READY.txt"), "Limits=Rendered production WPF; native MessageBox confirmations driven by native messages, not physical keyboard. Native filepicker automation attempts timed out and remain unverified; optional --native-dialogs reproduces attempt. Compact size measured, not OS DPI scaling. Physical keyboard, Narrator and actual 125/150/200% scaling remain manual.\n");
    }

    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private delegate bool EnumWindow(nint handle, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder value, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint handle, int id);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint handle);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint handle, uint message, nint wparam, string lparam);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);
    private static string NativeText(nint handle) { var text = new StringBuilder(4096); GetWindowText(handle, text, text.Capacity); return text.ToString(); }
    private static void WithNativeDialogs(MainWindow window, Action action, string? path, string messageTitle, int button, string root)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8); var pickerDone = path is null; var messageDone = false; var failed = false;
        var worker = System.Threading.Tasks.Task.Run(() =>
        {
            while (!messageDone && !failed)
            {
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var process);
                if (process != Environment.ProcessId) return true;
                var title = NativeText(handle);
                if (title == messageTitle)
                {
                    var body = new StringBuilder(); EnumChildWindows(handle, (child, _) => { body.AppendLine(NativeText(child)); return true; }, 0);
                    File.AppendAllText(Path.Combine(root, "native-dialogs.txt"), title + "\n" + body + "\n");
                    nint accept = GetDlgItem(handle, button);
                    EnumChildWindows(handle, (child, _) => { if (NativeText(child).Replace("&", "") == (button == 6 ? "Yes" : "OK")) accept = child; return true; }, 0);
                    messageDone = true; PostMessage(accept, 0x00F5, 0, 0);
                }
                else if (!pickerDone && (title.StartsWith("Export organization") || title.StartsWith("Preview organization") || title.StartsWith("Back up protected") || title.StartsWith("Restore protected")))
                {
                    var element = AutomationElement.FromHandle(handle);
                    var edit = element.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.AutomationIdProperty, "1001")));
                    if (edit is null) throw new InvalidOperationException("Native filename accessibility control unavailable.");
                    ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path!);
                    var accept = element.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, title.StartsWith("Export") || title.StartsWith("Back up") ? "Save" : "Open")));
                    if (accept is null) throw new InvalidOperationException("Native accept accessibility control unavailable.");
                    pickerDone = true;
                    ((InvokePattern)accept.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }
                if (DateTime.UtcNow > deadline && !messageDone) { failed = true; PostMessage(handle, 0x0010, 0, 0); }
                return true;
            }, 0);
            System.Threading.Thread.Sleep(150);
            }
        });
        action();
        while (!worker.IsCompleted && DateTime.UtcNow < deadline) WaitUi(window);
        if (!pickerDone || !messageDone || failed) throw new TimeoutException("Native dialog flow failed: " + messageTitle);
    }
}
