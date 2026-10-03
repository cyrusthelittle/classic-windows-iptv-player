using System.Diagnostics;
using System.Windows;
using ClassicWindowsIptvPlayer.Core;

namespace ClassicWindowsIptvPlayer.Windows;
public enum UpdatePromptResult { RemindLater, NeverRemind, UpdateNow }
public partial class UpdateWindow : Window
{
    private readonly Uri _releasePageUri;
    private readonly Version _currentVersion;
    private readonly GitHubReleaseInfo _release;
    private CancellationTokenSource? _downloadCancellation;
    private bool _closed;
    public bool IsModelessPrompt { get; set; }
    public StagedUpdate? StagedUpdate { get; private set; }
    public UpdatePromptResult PromptResult { get; private set; } = UpdatePromptResult.RemindLater;
    public UpdateWindow(Version currentVersion, GitHubReleaseInfo release, bool automaticDownload = false, StagedUpdate? stagedUpdate = null)
    {
        InitializeComponent();
        _releasePageUri = release.PageUri; _currentVersion = currentVersion; _release = release;
        var area = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, area.Height); MinWidth = Math.Min(MinWidth, area.Width);
        Height = Math.Min(Height, area.Height); Width = Math.Min(Width, area.Width);
        PublishedDateText.Text = release.PublishedAt is { } date ? "Published " + date.ToLocalTime().ToString("d") : "Publication date unavailable";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "Release notes are not available here. Open this release on GitHub for details." : release.ReleaseNotes;
        CurrentVersionText.Text = FormatVersion(currentVersion); NewVersionText.Text = release.DisplayVersion; ReleaseNameText.Text = release.Name;
        StagedUpdate = stagedUpdate; if (stagedUpdate is not null) SetReady();
        Closed += (_, _) => { _closed = true; _downloadCancellation?.Cancel(); };
        if (automaticDownload && stagedUpdate is null) Loaded += async (_, _) => await DownloadAsync();
    }
    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_releasePageUri.AbsoluteUri) { UseShellExecute = true }); LinkErrorText.Visibility = Visibility.Collapsed; }
        catch { LinkErrorText.Text = "Windows could not open the release page: " + _releasePageUri.AbsoluteUri; LinkErrorText.Visibility = Visibility.Visible; }
    }
    private void Never_Click(object sender, RoutedEventArgs e) { PromptResult = UpdatePromptResult.NeverRemind; CompletePrompt(true); }
    private void Later_Click(object sender, RoutedEventArgs e) { PromptResult = UpdatePromptResult.RemindLater; CompletePrompt(false); }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (StagedUpdate is null) { await DownloadAsync(); return; }
        PromptResult = UpdatePromptResult.UpdateNow; CompletePrompt(true);
    }
    private void CompletePrompt(bool result) { if (IsModelessPrompt) Close(); else DialogResult = result; }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();
    private async Task DownloadAsync()
    {
        if (_closed || _downloadCancellation is not null || StagedUpdate is not null) return;
        using var cancellation = new CancellationTokenSource(); _downloadCancellation = cancellation;
        UpdateButton.IsEnabled = false; CancelDownloadButton.Visibility = Visibility.Visible; LaterButton.Content = "Later";
        var progress = new Progress<string>(message => { if (!_closed && ReferenceEquals(_downloadCancellation, cancellation)) DownloadStatusText.Text = message; });
        try
        {
            StagedUpdate = await new UpdateDownloadService().StageAsync(_release.DisplayVersion, _currentVersion, AppDataPaths.Updates, progress, cancellation.Token);
            if (!_closed) SetReady();
        }
        catch (OperationCanceledException) { if (!_closed) { DownloadStatusText.Text = "Download canceled or timed out. Choose Retry update or open this release on GitHub."; UpdateButton.Content = "Retry update"; } }
        catch (Exception error) { if (!_closed) { DownloadStatusText.Text = AppLogger.SanitizeText(error.Message) + " Your current app and saved accounts were retained."; UpdateButton.Content = "Retry update"; } }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation)) _downloadCancellation = null;
            if (!_closed) { UpdateButton.IsEnabled = true; CancelDownloadButton.Visibility = Visibility.Collapsed; }
        }
    }
    private void SetReady()
    {
        DownloadStatusText.Text = "Update downloaded and verified. Restart when playback and recording have stopped. Your accounts and saved data stay in place.";
        UpdateButton.Content = "Restart to update"; LaterButton.Content = "Later";
    }
    private static string FormatVersion(Version version) => version.Revision > 0 ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : $"{version.Major}.{version.Minor}.{version.Build}";
}
