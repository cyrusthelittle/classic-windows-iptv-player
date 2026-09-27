using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

// The highlighted library item and the active tune have different lifetimes.
// This class has no WPF or LibVLC dependency so selection and stale callbacks
// can be checked with synthetic channels.
internal sealed class PlaybackBrowseState
{
    public Channel? SelectedChannel { get; private set; }
    public Channel? PlayingChannel { get; private set; }
    public PlaybackCandidate? PlayingCandidate { get; private set; }
    public IReadOnlyList<PlaybackCandidate> Candidates { get; private set; } = [];
    public int CandidateIndex { get; private set; } = -1;
    private object? _activeRequest;

    public void Select(Channel? channel) => SelectedChannel = channel;

    public void Start(Channel channel, IReadOnlyList<PlaybackCandidate> candidates, int index, object request)
    {
        if (index < 0 || index >= candidates.Count) throw new ArgumentOutOfRangeException(nameof(index));
        PlayingChannel = channel;
        Candidates = candidates;
        CandidateIndex = index;
        PlayingCandidate = candidates[index];
        _activeRequest = request;
    }

    public bool Accepts(object? request) => request is not null && ReferenceEquals(request, _activeRequest);

    // Stop retains the title/source for the stopped item, but rejects late
    // states from its tune cycle. Reset is used when changing accounts.
    public void Stop() => _activeRequest = null;

    public void Reset()
    {
        Stop();
        SelectedChannel = null;
        PlayingChannel = null;
        PlayingCandidate = null;
        Candidates = [];
        CandidateIndex = -1;
    }
}
