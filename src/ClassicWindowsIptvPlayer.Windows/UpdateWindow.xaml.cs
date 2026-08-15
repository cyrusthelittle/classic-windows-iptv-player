using System.Windows;

namespace ClassicWindowsIptvPlayer.Windows;

public enum UpdatePromptResult
{
    RemindLater,
    NeverRemind,
    UpdateNow
}

public partial class UpdateWindow : Window
{
    public UpdatePromptResult PromptResult { get; private set; } = UpdatePromptResult.RemindLater;

    public UpdateWindow(Version currentVersion, GitHubReleaseInfo release)
    {
        InitializeComponent();
        CurrentVersionText.Text = FormatVersion(currentVersion);
        NewVersionText.Text = release.DisplayVersion;
        ReleaseNameText.Text = release.Name;
    }

    private void Never_Click(object sender, RoutedEventArgs e)
    {
        PromptResult = UpdatePromptResult.NeverRemind;
        DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        PromptResult = UpdatePromptResult.RemindLater;
        DialogResult = false;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        PromptResult = UpdatePromptResult.UpdateNow;
        DialogResult = true;
    }

    private static string FormatVersion(Version version) =>
        version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
}
