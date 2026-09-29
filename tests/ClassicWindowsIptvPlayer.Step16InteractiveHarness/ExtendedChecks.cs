using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
namespace ClassicWindowsIptvPlayer.Step16InteractiveHarness;
internal static partial class Program
{
    private static void RunExtendedProbe(MainWindow window, ListBox list, string root)
    {
        var report = Path.Combine(root, "HARNESS_READY.txt");
        void Check(string label, bool condition) { File.AppendAllText(report, label + "=" + (condition ? "PASS" : "FAIL") + "\n"); if (!condition) throw new InvalidOperationException(label); }
        try
        {
            Rename(window, list, "Renamed Alpha", false, root);
            Check("RenderedItemRename", list.Items.Cast<object>().Any(x => GetChannelName(x) == "Renamed Alpha"));
            Rename(window, list, "Canceled name", true, root);
            Check("RenameCancel", list.Items.Cast<object>().Any(x => GetChannelName(x) == "Renamed Alpha"));
            var menu = OpenMenu(list, 0);
            var folders = menu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Add to favorite folder");
            Click(folders.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Unfiled"));
            WaitUi(window);
            var store = new ConfigStore();
            var state = store.Load();
            Check("FavoriteMenuPersisted", state.SelectedLibrary.FavoriteIds.Count == 1);
            Click(OpenMenu(list, 0).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Hide item"));
            WaitUi(window);
            Check("RenderedHideItem", list.Items.Count == 1 && GetChannelName(list.Items[0]) == "Zebra News");
            state = store.Load();
            Check("HiddenRenameOrderPersisted", state.SelectedLibrary.ChannelOrganization.Values.Any(r => r.Hidden && r.Name == "Renamed Alpha" && r.Order == 1));
            var export = Path.Combine(root, "organization.json");
            store.ExportPortable(export, state);
            var json = File.ReadAllText(export);
            Check("ExportSecretFree", !json.Contains("127.0.0.1") && !json.Contains("M3uUrl") && !json.Contains("Username"));
            Check("ImportPreview", store.PreviewPortable(export, state).MatchingAccountCount == 1);
            state.SelectedLibrary.ChannelOrganization.Clear(); state.SelectedLibrary.FavoriteIds.Clear();
            store.ImportPortable(export, state);
            Check("ImportPersistence", store.Load().SelectedLibrary.ChannelOrganization.Values.Any(r => r.Hidden) && store.Load().SelectedLibrary.FavoriteIds.Count == 1);
            var backup = Path.Combine(root, "synthetic-backup.zip");
            state = store.Load(); state.RemoteControlEnabled = false; store.Save(state);
            store.CreateBackup(backup);
            var changed = store.Load(); changed.SelectedLibrary.ChannelOrganization.Clear(); changed.RemoteControlEnabled = true; store.Save(changed);
            store.RestoreBackup(backup);
            Check("BackupRestorePersistence", store.Load().SelectedLibrary.ChannelOrganization.Values.Any(r => r.Hidden));
            Check("BackupRemotePreference", !store.Load().RemoteControlEnabled);
            RunCompletionChecks(window, list, root, Check);
        }
        catch (Exception ex) { File.AppendAllText(report, "ExtendedProbe=FAIL; " + ex + "\n"); Environment.ExitCode = 1; }
    }
    private static ContextMenu OpenMenu(ListBox list, int index)
    {
        list.SelectedIndex = index; list.ScrollIntoView(list.SelectedItem); list.UpdateLayout();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { item, false }, null)!;
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent; item.RaiseEvent(args); return list.ContextMenu!;
    }
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Rename(MainWindow window, ListBox list, string name, bool cancel, string root, string title = "Rename item", string menuHeader = "Rename item...", int index = 0)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timedOut = false;
        timer.Tick += (_, _) =>
        {
            var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.Title == title);
            if (dialog is null)
            {
                if (DateTime.UtcNow < deadline) return;
                timer.Stop(); timedOut = true;
                foreach (var owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                return;
            }
            timer.Stop(); dialog.UpdateLayout(); Descendants(dialog).OfType<TextBox>().Single().Text = name;
            var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
            using (var output = File.Create(Path.Combine(root, cancel ? "rename-cancel.png" : "rename-save.png"))) new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(output);
            if (cancel) dialog.DialogResult = false;
            else Descendants(dialog).OfType<Button>().Single(b => b.Content?.ToString() == "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start(); Click(OpenMenu(list, index).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == menuHeader)); timer.Stop(); WaitUi(window);
        if (timedOut) throw new TimeoutException("Rename item dialog did not appear within five seconds.");
    }
}
