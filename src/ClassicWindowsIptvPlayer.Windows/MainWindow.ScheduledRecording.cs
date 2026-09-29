using ClassicWindowsIptvPlayer.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private ScheduledRecordingCoordinator? _scheduledRecordings;
    private readonly DispatcherTimer _scheduledRecordingTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private bool _processingScheduledRecordings;

    private void InitializeScheduledRecordingCoordinator()
    {
        _scheduledRecordingTimer.Stop();
        _scheduledRecordings?.Dispose();
        _scheduledRecordings = null;
        if (_tuner is null || _recordingService is null || string.IsNullOrWhiteSpace(_state.SelectedAccountId)) return;

        _scheduledRecordings = new ScheduledRecordingCoordinator(_state.SelectedAccountId, _store, _tuner, _recordingService);
        _scheduledRecordingTimer.Start();
    }

    private async void ScheduledRecordingTimer_Tick(object? sender, EventArgs e)
    {
        var coordinator = _scheduledRecordings;
        if (_processingScheduledRecordings || coordinator is null || _isShuttingDown) return;
        _processingScheduledRecordings = true;
        try { await coordinator.ProcessDueAsync(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            AppLogger.Error("Scheduled recording processing failed.", ex);
            if (!_isShuttingDown) StatusText.Text = "Scheduled recording check failed: " + AppLogger.SanitizeText(ex.Message);
        }
        finally { _processingScheduledRecordings = false; }
    }

    private async void ScheduleProgramme(Channel channel, EpgProgramme programme)
    {
        if (_scheduledRecordings is null)
        {
            MessageBox.Show(this, "Scheduled recordings are not available until the player is initialized.",
                "Scheduling unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (channel.MediaKind != MediaKind.Live || programme.Stop <= programme.Start)
        {
            MessageBox.Show(this, "Choose a live-channel programme with valid guide times.",
                "Cannot schedule programme", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(GetRecordingFolder());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            AppLogger.Error("Could not create the scheduled recording folder.", ex);
            MessageBox.Show(this,
                "The recording folder could not be created. Choose a writable folder in Settings > Recording folder... and try again.",
                "Recording folder unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var editor = new ScheduledRecordingEditorWindow(_state.SelectedAccountId, channel.Id, channel.Name,
            programme.Title, programme.Start.ToUniversalTime(), programme.Stop.ToUniversalTime(), GetRecordingFolder()) { Owner = this };
        if (editor.ShowDialog() != true || editor.ScheduledJob is not { } job) return;
        try
        {
            var saved = await _scheduledRecordings.AddAsync(job);
            StatusText.Text = saved.Status == ScheduledRecordingStatus.Conflict
                ? "Schedule saved with a conflict: " + saved.Reason
                : "Programme scheduled. It will only record while this matching channel is already playing.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not save scheduled recording.", ex);
            MessageBox.Show(this, "The schedule could not be saved. " + AppLogger.SanitizeText(ex.Message),
                "Schedule failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenScheduledRecordings_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledRecordings is null) return;
        var coordinator = _scheduledRecordings;
        new ScheduledRecordingsWindow(() => coordinator.Jobs, async id =>
        {
            await coordinator.CancelAsync(id);
        }) { Owner = this }.ShowDialog();
    }
}
