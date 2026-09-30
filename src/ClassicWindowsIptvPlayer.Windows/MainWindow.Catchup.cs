using ClassicWindowsIptvPlayer.Core;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private CancellationTokenSource? _catchupCts;
    private int _catchupGeneration;
    private CatchupPlaybackContext? _catchupPlayback;
    private sealed record CatchupPlaybackContext(Channel Source, EpgProgramme Programme, TuneRequest Request);

    private void CancelPendingCatchup()
    {
        ++_catchupGeneration;
        _catchupCts?.Cancel();
        _catchupCts = null;
    }

    private void ClearCatchup()
    {
        CancelPendingCatchup();
        _catchupPlayback = null;
    }

    private void UpdateCatchupButtons(Channel? channel)
    {
        var supported = channel is not null && Catchup.HasSupportedArchive(channel);
        BrowseStartOverButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        var availability = channel is not null && _browseNow is not null ? Catchup.Evaluate(channel, _browseNow, DateTimeOffset.UtcNow) : null;
        BrowseStartOverButton.IsEnabled = availability?.IsAvailable == true && _catchupCts is null;
        BrowseStartOverButton.ToolTip = availability?.Message ?? "No current programme is available in the guide.";
    }

    private void BrowseStartOver_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackState.SelectedChannel is { } channel && _browseNow is { } programme)
            _ = StartCatchupAsync(channel, programme);
    }

    private void ShowProgrammeDetails(Channel channel, EpgProgramme programme) =>
        new ProgrammeDetailsWindow(channel, programme, () => _ = StartCatchupAsync(channel, programme)) { Owner = this }.ShowDialog();

    private async Task ResumeCatchupAsync(CatchupPlaybackContext archive, long timeMs)
    {
        var expectedGeneration = _catchupGeneration + 1;
        await StartCatchupAsync(archive.Source, archive.Programme);
        if (expectedGeneration == _catchupGeneration && _catchupPlayback is not null &&
            !ReferenceEquals(_catchupPlayback.Request, archive.Request) && timeMs > 0)
            QueueResumeSeek(archive.Source.Id, timeMs);
    }

    private async Task StartCatchupAsync(Channel channel, EpgProgramme programme)
    {
        CancelPendingCatchup();
        var generation = _catchupGeneration;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _catchupCts = cts;
        var accountId = _state.SelectedAccountId;
        var account = _state.Account.Clone();
        bool Current() => !cts.IsCancellationRequested && !_isShuttingDown && generation == _catchupGeneration && accountId == _state.SelectedAccountId;
        try
        {
            var availability = Catchup.Evaluate(channel, programme, DateTimeOffset.UtcNow);
            if (!availability.IsAvailable) { StatusText.Text = availability.Message; return; }
            StatusText.Text = "Checking archive availability and connection allowance...";
            UpdateCatchupButtons(_playbackState.SelectedChannel);
            var profile = await _playlistService.FetchCatchupProfileAsync(account, cts.Token);
            if (!Current()) return;
            var replacing = _mediaPlayer is not null &&
                (_mediaPlayer.IsPlaying || _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Paused);
            var archive = Catchup.CreateRequest(channel, programme, account, profile, DateTimeOffset.UtcNow, replacing);
            if (!archive.IsAvailable) { StatusText.Text = archive.Message; return; }
            if (_tuner is null) { StatusText.Text = "Player is not initialized yet."; return; }

            // Retire our previous decoder before the availability GET consumes a
            // provider connection. Awaiting permits dispatcher surface cleanup.
            SavePlaybackProgress(force: true);
            ClearPauseResumeState();
            ClearLiveDelay();
            _playbackState.Stop();
            _sourceProbeCts?.Cancel();
            await _tuner.StopAsync(cts.Token);
            if (!Current()) return;
            using var client = new HttpClient();
            var check = await Catchup.CheckStreamAsync(archive, client, cts.Token);
            if (!Current()) return;
            if (!check.IsAvailable) { StatusText.Text = check.Message; return; }
            // The advertised window may expire while checking the server.
            availability = Catchup.Evaluate(channel, programme, DateTimeOffset.UtcNow);
            if (!availability.IsAvailable) { StatusText.Text = availability.Message; return; }
            var candidate = new PlaybackCandidate(archive.Url, "Catch-up: " + programme.Title);
            var request = new TuneRequest(channel.Id, channel.Name + " — " + programme.Title, archive.Url, candidate.Label, false, GetPlaybackBufferMs());
            _catchupPlayback = new(channel, programme, request);
            _playbackState.Start(channel, new[] { candidate }, 0, request);
            _playingSeriesEpisodes = null;
            _streamInfoTracker.ResetBandwidth();
            NowPlayingText.Text = request.ChannelName;
            UpdateEpgDisplay();
            HideIdleBackground();
            ShowControls();
            UpdateLiveDelayUi();
            StatusText.Text = "Opening archive: " + programme.Title;
            _tuner.Play(request);
        }
        catch (OperationCanceledException)
        {
            if (generation == _catchupGeneration && !_isShuttingDown) StatusText.Text = "Archive request canceled or timed out.";
        }
        catch (Exception ex)
        {
            if (Current())
            {
                AppLogger.Error("Archive request failed.", ex);
                StatusText.Text = Catchup.FailureMessage(channel, programme, DateTimeOffset.UtcNow,
                    (ex as HttpRequestException)?.StatusCode);
            }
        }
        finally
        {
            if (ReferenceEquals(_catchupCts, cts)) _catchupCts = null;
            cts.Dispose();
            if (!_isShuttingDown) UpdateCatchupButtons(_playbackState.SelectedChannel);
        }
    }
}
