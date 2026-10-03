using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ClassicWindowsIptvPlayer.Windows;
internal static class Program
{
    [STAThread]
    static int Main()
    {
        Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cyrus-ui-isolated-" + Guid.NewGuid().ToString("N")));
        try
        {
            // Initialize resources only. Never run App startup, load settings, create MainWindow, or contact GitHub.
            var app = new App(); app.InitializeComponent();
            var page = new Uri("https://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v2.0");
            var literal = "<script>literal</script> ![remote](https://example.test/image)\n" + string.Join("\n", Enumerable.Range(0, 600).Select(i => "Fixture note " + i));
            var window = new UpdateWindow(new Version(1, 0, 0), new(new Version(2, 0), "v2.0", new string('n', 256), page, literal, DateTimeOffset.Parse("2026-10-03T10:00:00Z")));
            var grid = (Grid)window.Content;
            var notes = (TextBox)window.FindName("ReleaseNotesText");
            Check(notes.Text == literal && notes.IsReadOnly && notes.Focusable && notes.IsTabStop, "literal, selectable, keyboard-accessible notes");
            Check(AutomationProperties.GetName(notes) == "What's new release notes", "notes automation label");
            foreach (var size in new[] { new Size(592, 510), new Size(432, 272), new Size(336, 256) })
            {
                grid.Measure(size); grid.Arrange(new Rect(size)); grid.UpdateLayout();
                foreach (var button in ((WrapPanel)grid.Children[1]).Children.OfType<Button>())
                {
                    var point = button.TranslatePoint(new Point(), grid);
                    Check(point.Y >= 0 && point.Y + button.ActualHeight <= grid.ActualHeight + 0.1, "footer button within compact body " + size);
                }
                var outer = (ScrollViewer)grid.Children[0];
                Check(outer.ScrollableHeight > 0 || size.Height > 400, "body scroll available for small viewport");
            }
            notes.ScrollToEnd(); notes.UpdateLayout();
            Check(notes.VerticalOffset > 0, "long notes can scroll to end");
            var themes = typeof(App).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.ThemeManager")!.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;
            foreach (var dark in new[] { false, true })
            {
                themes.Invoke(null, [dark]);
                Check(((SolidColorBrush)notes.Foreground).Color == ((SolidColorBrush)app.Resources["Text0Brush"]).Color &&
                    ((SolidColorBrush)notes.Background).Color == ((SolidColorBrush)app.Resources["Bg2Brush"]).Color, "theme brushes update " + dark);
            }
            var fallback = new UpdateWindow(new Version(1, 0, 0), new(new Version(2, 0), "v2.0", "fixture", page));
            Check(((TextBox)fallback.FindName("ReleaseNotesText")).Text.Contains("not available") && ((TextBlock)fallback.FindName("PublishedDateText")).Text.Contains("unavailable"), "missing metadata helpful UI fallback");
            Check(window.PromptResult == UpdatePromptResult.RemindLater, "default reminder result preserved");
            window.Close(); fallback.Close();
            Console.WriteLine("PASS hidden update prompt: literal notes, fallback, scroll extent, keyboard metadata, theme brush changes and footer at three compact viewports. No windows shown, network requests, normal settings or browser calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
}
