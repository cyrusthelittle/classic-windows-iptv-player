using ClassicWindowsIptvPlayer.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>Displays persisted schedule state and delegates cancellation to its owner.</summary>
public partial class ScheduledRecordingsWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Func<IReadOnlyList<ScheduledRecordingJob>> _jobsProvider;
    private readonly Func<string, Task> _cancelJob;
    private readonly ObservableCollection<ScheduledRecordingRow> _rows = [];
    private readonly DispatcherTimer _refreshTimer;
    private bool _cancelInProgress;

    public ScheduledRecordingsWindow(
        Func<IReadOnlyList<ScheduledRecordingJob>> jobsProvider,
        Func<string, Task> cancelJob)
    {
        _jobsProvider = jobsProvider ?? throw new ArgumentNullException(nameof(jobsProvider));
        _cancelJob = cancelJob ?? throw new ArgumentNullException(nameof(cancelJob));

        InitializeComponent();
        JobsGrid.ItemsSource = _rows;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = RefreshInterval
        };
        _refreshTimer.Tick += (_, _) => RefreshJobs();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshJobs();
        _refreshTimer.Start();
        RefreshButton.Focus();
    }

    private void Window_Closed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshJobs();

    private void RefreshJobs()
    {
        try
        {
            var selectedId = (JobsGrid.SelectedItem as ScheduledRecordingRow)?.Id;
            var jobs = _jobsProvider() ?? Array.Empty<ScheduledRecordingJob>();
            var ordered = jobs
                .OrderBy(job => job.RequestedCaptureStartUtc)
                .ThenBy(job => job.ProgrammeTitle, StringComparer.CurrentCultureIgnoreCase)
                .Select(ScheduledRecordingRow.From)
                .ToArray();

            _rows.Clear();
            foreach (var row in ordered) _rows.Add(row);
            if (!string.IsNullOrEmpty(selectedId))
                JobsGrid.SelectedItem = _rows.FirstOrDefault(row => row.Id == selectedId);

            EmptyStateText.Text = _rows.Count == 0 ? "No scheduled recordings." : string.Empty;
            RefreshStatusText.Text = $"Updated {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}";
            UpdateCancelButton();
        }
        catch (Exception ex)
        {
            RefreshStatusText.Text = "Could not refresh scheduled recordings.";
            EmptyStateText.Text = ex.Message;
            UpdateCancelButton();
        }
    }

    private void JobsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateCancelButton();

    private void UpdateCancelButton()
    {
        CancelJobButton.IsEnabled = !_cancelInProgress && JobsGrid.SelectedItem is ScheduledRecordingRow row && row.CanCancel;
    }

    private async void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not ScheduledRecordingRow row || !row.CanCancel || _cancelInProgress) return;

        var confirmation = MessageBox.Show(
            this,
            $"Cancel this scheduled recording?\n\n{row.ProgrammeTitle}\n{row.ChannelName}",
            "Cancel scheduled recording",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        _cancelInProgress = true;
        UpdateCancelButton();
        try
        {
            await _cancelJob(row.Id);
            RefreshJobs();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The scheduled recording could not be cancelled.\n\n{ex.Message}",
                "Cancellation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancelInProgress = false;
            UpdateCancelButton();
        }
    }

    private sealed record ScheduledRecordingRow(
        string Id,
        string ProgrammeTitle,
        string ChannelName,
        string CaptureTimeText,
        string PaddingText,
        string StatusText,
        string Reason,
        bool CanCancel)
    {
        public static ScheduledRecordingRow From(ScheduledRecordingJob job)
        {
            var localStart = job.RequestedCaptureStartUtc.ToLocalTime();
            var localEnd = job.RequestedCaptureEndUtc.ToLocalTime();
            var title = string.IsNullOrWhiteSpace(job.ProgrammeTitle) ? "(Untitled programme)" : job.ProgrammeTitle;
            var channel = string.IsNullOrWhiteSpace(job.ChannelName) ? "(Unknown channel)" : job.ChannelName;
            var padding = $"−{FormatPadding(job.PrePadding)} / +{FormatPadding(job.PostPadding)}";
            var status = job.Status.ToString();
            var canCancel = job.Status is ScheduledRecordingStatus.Scheduled
                or ScheduledRecordingStatus.Conflict
                or ScheduledRecordingStatus.Recording;

            return new ScheduledRecordingRow(
                job.Id,
                title,
                channel,
                $"{localStart.ToString("g", CultureInfo.CurrentCulture)} – {localEnd.ToString("t", CultureInfo.CurrentCulture)}",
                padding,
                status,
                job.Reason ?? string.Empty,
                canCancel);
        }

        private static string FormatPadding(TimeSpan value) =>
            value.TotalMinutes == 0 ? "0m" : value.TotalMinutes.ToString("0", CultureInfo.CurrentCulture) + "m";
    }
}
