using ClassicWindowsIptvPlayer.Core;
using System;
using System.Linq;
using System.Windows;
using LibVLCSharp.Shared;
using MessageBox = System.Windows.MessageBox;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private MultiViewWindow? _multiViewWindow;
    private bool _openingMultiView;

    private async void OpenMultiView_Click(object sender, RoutedEventArgs e)
    {
        if (_multiViewWindow is { IsVisible: true })
        {
            _multiViewWindow.Activate();
            return;
        }
        if (_openingMultiView) return;

        if (_libVlc is null)
        {
            MessageBox.Show(this, "The video player is not ready yet.", "Multi-view", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var channels = _channels.Where(channel => channel.MediaKind == MediaKind.Live &&
            PlayerService.BuildPlaybackCandidates(channel, _state.Account).Count > 0).ToList();
        if (channels.Count < 2)
        {
            MessageBox.Show(this, "Load at least two playable live channels to use multi-view.", "Multi-view", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var accountId = _state.SelectedAccountId;
        if (!ConnectionBudget.TryParseAllowance(_recordingProfile, out _, out _))
        {
            _openingMultiView = true;
            try
            {
                _recordingProfile = await _playlistService.FetchCatchupProfileAsync(_state.Account, System.Threading.CancellationToken.None);
            }
            catch (Exception exception)
            {
                AppLogger.Warn("Multi-view: provider connection allowance could not be fetched. " + AppLogger.SanitizeText(exception.Message));
            }
            finally { _openingMultiView = false; }
        }
        if (_isShuttingDown || !string.Equals(accountId, _state.SelectedAccountId, StringComparison.Ordinal)) return;

        var profile = CloneMultiViewProfile(_recordingProfile);
        var allowanceKnown = ConnectionBudget.TryParseAllowance(profile, out _, out _);
        var acceptUnknown = false;
        if (!allowanceKnown)
        {
            var answer = MessageBox.Show(this,
                "Your provider did not report a connection limit. Multi-view can use additional provider streams, and the provider may reject them. Open it anyway?",
                "Connection limit unknown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            acceptUnknown = true;
        }

        // The provider's active-connection snapshot may have been fetched before
        // playback began. Count this app's already-playing main stream at least once,
        // then reserve additional leases for every multi-view tile.
        var budget = new ConnectionBudget();
        _multiViewWindow = new MultiViewWindow(_libVlc, channels, _state.Account.Clone(),
            (channel, replacing) => budget.Acquire(profile, StreamLeaseKind.MultiView, channel.Name,
                replacing: replacing, acceptUnknownConnection: acceptUnknown),
            lease => budget.Release(lease))
        {
            Owner = this
        };
        _multiViewWindow.Closed += (_, _) =>
        {
            budget.ReleaseAll();
            _multiViewWindow = null;
        };
        _multiViewWindow.Show();
    }

    private AccountProfile CloneMultiViewProfile(AccountProfile? profile)
    {
        var clone = profile is null ? new AccountProfile() : new AccountProfile
        {
            Username = profile.Username,
            Status = profile.Status,
            ExpiryDateText = profile.ExpiryDateText,
            CreatedAtText = profile.CreatedAtText,
            IsTrial = profile.IsTrial,
            ActiveConnections = profile.ActiveConnections,
            MaxConnections = profile.MaxConnections,
            ServerTime = profile.ServerTime,
            Timezone = profile.Timezone
        };

        if (_tuner?.CurrentPlayer is { IsPlaying: true } &&
            int.TryParse(clone.MaxConnections, out var maximum) && maximum > 0 &&
            (!int.TryParse(clone.ActiveConnections, out var active) || active < 1))
            clone.ActiveConnections = "1";
        return clone;
    }
}
