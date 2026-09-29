using System.Globalization;

namespace ClassicWindowsIptvPlayer.Core;

public enum StreamLeaseKind
{
    LivePlayback = 0,
    InstantRecording,
    ScheduledRecording,
    TimeshiftBuffer,
    MultiView
}

public enum ConnectionBudgetStatus
{
    Available = 0,
    ConnectionLimit,
    ConnectionUnknown,
    NoAccount,
    LocalLimit
}

/// <summary>A stream slot this application currently holds. Account-scoped by the caller that owns the budget.</summary>
public sealed record StreamLease(string Id, StreamLeaseKind Kind, string Purpose, DateTimeOffset StartedAt);

/// <summary>
/// <paramref name="IsAllowed"/> is true only for <see cref="ConnectionBudgetStatus.Available"/>.
/// A confirmation-required decision is never automatically allowed, so an unreported
/// allowance can never be mistaken for available headroom.
/// </summary>
public sealed record ConnectionBudgetDecision(bool IsAllowed, ConnectionBudgetStatus Status, string Message,
    int ProviderActive, int ProviderMax, int LocalHeld, int Required, bool RequiresConfirmation = false);

/// <summary>
/// Local, honest accounting of the streams this application currently holds. Playback,
/// instant recording, scheduled recording, the timeshift buffer and later multi-view all
/// consume a provider connection, so the budget is asked before a capture is opened rather
/// than discovering the limit as a provider error. This type performs no I/O, starts no
/// decoder and is safe to exercise in the regression runner.
///
/// The provider-reported allowance is a string pair on <see cref="AccountProfile"/>; the
/// parse rules are shared with <see cref="Catchup"/> so both surfaces agree on what
/// "unsupported connection allowance" means.
/// </summary>
public sealed class ConnectionBudget
{
    // A self-restraint ceiling. Provider allowances are at most a few connections;
    // more than this locally is a leak, not a legitimate allowance.
    public const int MaxLocalHeld = 16;

    private readonly object _sync = new();
    private readonly List<StreamLease> _leases = [];
    private readonly Func<DateTimeOffset> _clock;

    public ConnectionBudget() : this(() => DateTimeOffset.UtcNow)
    {
    }

    public ConnectionBudget(Func<DateTimeOffset> clock) => _clock = clock;

    public IReadOnlyList<StreamLease> Leases
    {
        get { lock (_sync) return _leases.ToArray(); }
    }
    public int LocalHeld
    {
        get { lock (_sync) return _leases.Count; }
    }

    /// <summary>
    /// True when the provider reported a usable allowance. A false result is the single
    /// shared definition of "unsupported connection allowance": it covers a missing,
    /// blank, non-numeric, zero or negative count, and a negative active count.
    /// </summary>
    public static bool TryParseAllowance(AccountProfile? profile, out int active, out int max)
    {
        active = 0;
        max = 0;
        if (profile is null) return false;
        if (string.IsNullOrWhiteSpace(profile.MaxConnections) || string.IsNullOrWhiteSpace(profile.ActiveConnections)) return false;
        if (!int.TryParse(profile.MaxConnections.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max) || max <= 0) return false;
        if (!int.TryParse(profile.ActiveConnections.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out active) || active < 0) return false;
        return true;
    }

    /// <summary>
    /// Decides whether one more local stream fits. A replaced stream returns the caller's
    /// own slot, so the provider-active count is reduced by one exactly as archive
    /// playback already does.
    /// </summary>
    public ConnectionBudgetDecision Evaluate(AccountProfile? profile, bool replacingCurrentConnection = false)
    {
        lock (_sync)
            return EvaluateLocked(profile, replacingCurrentConnection);
    }

    private ConnectionBudgetDecision EvaluateLocked(AccountProfile? profile, bool replacingCurrentConnection)
    {
        if (profile is null)
            return new ConnectionBudgetDecision(false, ConnectionBudgetStatus.NoAccount,
                "No account information is available, so the connection allowance cannot be checked.", 0, 0, LocalHeld, LocalHeld + 1);
        if (!TryParseAllowance(profile, out var active, out var max))
            return new ConnectionBudgetDecision(false, ConnectionBudgetStatus.ConnectionUnknown,
                "The provider did not report a supported connection allowance. This capture can continue only after you confirm the account allows it.",
                -1, -1, LocalHeld, LocalHeld + (replacingCurrentConnection ? 0 : 1), RequiresConfirmation: true);
        // Local connections this app expects to hold once the request is honoured.
        var required = replacingCurrentConnection ? Math.Max(0, LocalHeld - 1) : LocalHeld + 1;
        if (Math.Max(0, active - (replacingCurrentConnection ? 1 : 0)) >= max)
            return new ConnectionBudgetDecision(false, ConnectionBudgetStatus.ConnectionLimit,
                "The provider's connection allowance is already in use.", active, max, LocalHeld, required);
        if (required > MaxLocalHeld)
            return new ConnectionBudgetDecision(false, ConnectionBudgetStatus.LocalLimit,
                "This app is already holding more streams than a recording should need; stop one before recording another.",
                active, max, LocalHeld, required);
        return new ConnectionBudgetDecision(true, ConnectionBudgetStatus.Available,
            "A connection slot is available.", active, max, LocalHeld, required);
    }

    /// <summary>
    /// Takes one local slot. <paramref name="replacing"/> hands back a lease this budget
    /// still holds as part of the same operation, so a swapped stream never needs a
    /// second slot. A lease that is not held is treated as an additional stream. A
    /// confirmation-required decision is honoured only when the caller has explicitly
    /// accepted the unreported allowance; otherwise nothing is taken.
    /// </summary>
    public StreamLease? Acquire(AccountProfile? profile, StreamLeaseKind kind, string purpose,
        StreamLease? replacing = null, bool acceptUnknownConnection = false)
    {
        lock (_sync)
        {
            var swaps = replacing is not null && _leases.Any(lease => string.Equals(lease.Id, replacing.Id, StringComparison.Ordinal));
            var decision = EvaluateLocked(profile, swaps);
            if (!decision.IsAllowed && !(decision.RequiresConfirmation && acceptUnknownConnection)) return null;
            // The provider's active count is a snapshot from before this acquisition.
            // Reserve from the remaining allowance while holding the same lock as insertion.
            if (decision.IsAllowed && !swaps && decision.ProviderMax - decision.ProviderActive - _leases.Count <= 0)
                return null;
            if (swaps) ReleaseLocked(replacing!.Id);
            var lease = new StreamLease(Guid.NewGuid().ToString("N"), kind, purpose ?? string.Empty, _clock());
            _leases.Add(lease);
            return lease;
        }
    }

    /// <summary>Releases a held slot. A repeated or unknown release is refused, so it can never free a second slot.</summary>
    public bool Release(StreamLease? lease) => lease is not null && Release(lease.Id);

    public bool Release(string? leaseId)
    {
        if (string.IsNullOrEmpty(leaseId)) return false;
        lock (_sync) return ReleaseLocked(leaseId);
    }

    private bool ReleaseLocked(string leaseId)
    {
        var index = _leases.FindIndex(lease => string.Equals(lease.Id, leaseId, StringComparison.Ordinal));
        if (index < 0) return false;
        _leases.RemoveAt(index);
        return true;
    }

    /// <summary>Releases every matching slot, for example on cancel or shutdown. Returns how many were released.</summary>
    public int ReleaseWhere(Func<StreamLease, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_sync) return _leases.RemoveAll(lease => predicate(lease));
    }

    public int ReleaseAll()
    {
        lock (_sync)
        {
            var released = _leases.Count;
            _leases.Clear();
            return released;
        }
    }

    public int Held(StreamLeaseKind kind)
    {
        lock (_sync) return _leases.Count(lease => lease.Kind == kind);
    }
}
