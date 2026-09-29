namespace ClassicWindowsIptvPlayer.Core;

public enum RecordingOutcome
{
    /// <summary>Prepared or in progress. The app has not yet learned how the capture ended.</summary>
    Recording = 0,
    Completed,
    Stopped,
    Failed,
    DiskFull,
    /// <summary>Nothing usable was written and the partial file was removed.</summary>
    Discarded
}

public enum RecordingStopReason { None = 0, User, RequestedEndUtc, ProviderEnded, Error, DiskFull, Shutdown, Superseded, Restarted }

/// <summary>
/// The guide entry as it read when the capture started, so the recordings list keeps
/// the programme after the guide feed has moved on. A window without a title is
/// treated as no programme at all, which is the honest answer for an unmapped channel.
/// </summary>
public sealed record RecordedProgrammeMetadata
{
    public static readonly RecordedProgrammeMetadata None = new();

    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset Stop { get; init; }

    public bool HasProgramme => Stop > Start && !string.IsNullOrWhiteSpace(Title);
    public string DisplayTitle => HasProgramme ? Title : "Live channel capture";

    public static RecordedProgrammeMetadata From(EpgProgramme? programme) => programme is null || programme.Stop <= programme.Start
        ? None
        : new RecordedProgrammeMetadata
        {
            Title = programme.Title.Trim(), Description = programme.Description.Trim(),
            Category = programme.Category.Trim(), Start = programme.Start, Stop = programme.Stop
        };
}

/// <summary>
/// One capture. It deliberately holds no source URL, username, password or token: the
/// recordings list identifies a file by account, channel key and file name, and any later
/// re-tune resolves the channel from the current provider catalog exactly as the app
/// already re-resolves a recent item. The captured file is the playable artefact, so a
/// protected index never becomes a place a provider credential can leak from.
/// </summary>
public sealed record RecordingEntry
{
    public string Id { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>Stable provider-independent key used for per-account name collisions.</summary>
    public string ChannelKey { get; init; } = string.Empty;

    public DateTimeOffset RequestedStartUtc { get; init; }
    public DateTimeOffset RequestedStopUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StoppedUtc { get; init; }
    public string FileName { get; init; } = string.Empty;
    /// <summary>Local output path only; never a provider URL. Used to reopen a capture after restart.</summary>
    public string FilePath { get; init; } = string.Empty;
    public RecordingContainerKind Container { get; init; }
    public long ByteSize { get; init; }
    public RecordingOutcome Outcome { get; init; } = RecordingOutcome.Recording;
    public RecordingStopReason StopReason { get; init; } = RecordingStopReason.None;

    /// <summary>Free text from the capture layer. It is sanitized before it is stored.</summary>
    public string Note { get; init; } = string.Empty;

    public RecordedProgrammeMetadata Programme { get; init; } = RecordedProgrammeMetadata.None;

    public string SearchKey => ChannelName + " " + Programme.Title;
    public bool IsActive => Outcome == RecordingOutcome.Recording;
    public string DisplayName => Programme.HasProgramme ? Programme.Title : ChannelName;
    public TimeSpan RequestedDuration => RequestedStopUtc > RequestedStartUtc ? RequestedStopUtc - RequestedStartUtc : TimeSpan.Zero;
    public TimeSpan RecordedDuration => StartedUtc is { } start && StoppedUtc is { } stop && stop > start ? stop - start : TimeSpan.Zero;
}

/// <summary>How a capture ended. Recorded by the caller that owns the capture process.</summary>
public sealed record RecordingFinish(DateTimeOffset StoppedUtc, RecordingOutcome Outcome, RecordingStopReason StopReason,
    long ByteSize, string Note);

/// <summary>
/// Account-scoped list of captures, with its own schema version because the existing
/// caches carry none. An index file is only ever written from here, so a secret can
/// only enter it through <see cref="RecordingEntry"/>, which has no field for one.
/// </summary>
public sealed class RecordingIndex
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 2000;

    public int Version { get; set; } = CurrentVersion;
    public List<RecordingEntry> Entries { get; set; } = [];

    public IReadOnlyList<RecordingEntry> Recent(string accountId, int limit = 50) => Entries
        .Where(entry => string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(entry => entry.StartedUtc ?? entry.RequestedStartUtc)
        .Take(Math.Max(0, limit))
        .ToList();

    public IReadOnlyList<RecordingEntry> Unfinished(string accountId) => Entries
        .Where(entry => entry.IsActive && string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public RecordingEntry? Find(string accountId, string recordingId) => Entries.Find(entry =>
        string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Id, recordingId, StringComparison.OrdinalIgnoreCase));

    public RecordingEntry? FindByFileName(string accountId, string fileName) => Entries.Find(entry =>
        string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Starts an index row for a validated plan. Re-planning the same unfinished capture
    /// replaces its row rather than adding a second one, so a restarted capture of the
    /// same channel and file appears once in the list.
    /// </summary>
    public RecordingEntry? Begin(RecordingPlan plan, string accountId, string channelId, string channelKey, string? filePath = null)
    {
        if (plan is null || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(plan.FileName)) return null;
        var entry = new RecordingEntry
        {
            Id = Guid.NewGuid().ToString("N"), AccountId = accountId, ChannelId = channelId ?? string.Empty,
            ChannelName = plan.ChannelName, ChannelKey = channelKey ?? string.Empty,
            RequestedStartUtc = plan.RequestedStartUtc, RequestedStopUtc = plan.RequestedStopUtc,
            StartedUtc = plan.ActualStartUtc, FileName = plan.FileName, FilePath = NormalizeFilePath(filePath ?? plan.FilePath), Container = plan.Container,
            Programme = plan.Programme
        };
        var previous = Entries.FindIndex(item => item.IsActive &&
            string.Equals(item.AccountId, entry.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase));
        if (previous >= 0) Entries.RemoveAt(previous);
        Entries.Insert(0, entry);
        Prune();
        return entry;
    }

    /// <summary>
    /// Records how a capture ended. Finishing twice is harmless and never rewrites a
    /// recorded outcome, so a stop that races a shutdown cannot corrupt a row.
    /// </summary>
    public RecordingEntry? Finish(RecordingEntry? entry, RecordingFinish? finish)
    {
        if (entry is null || finish is null) return null;
        if (finish.Outcome == RecordingOutcome.Recording)
            throw new ArgumentOutOfRangeException(nameof(finish), "A capture can only finish with a terminal outcome.");
        var index = Entries.FindIndex(item => string.Equals(item.AccountId, entry.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (!Entries[index].IsActive) return Entries[index];
        var stopped = finish.Outcome == RecordingOutcome.Completed && finish.StopReason == RecordingStopReason.None
            ? RecordingStopReason.RequestedEndUtc
            : finish.StopReason;
        Entries[index] = Entries[index] with
        {
            StoppedUtc = finish.StoppedUtc, Outcome = finish.Outcome, StopReason = stopped,
            ByteSize = Math.Max(0, finish.ByteSize), Note = AppLogger.SanitizeText(finish.Note).Trim()
        };
        return Entries[index];
    }

    /// <summary>Moves the displayed/recorded start to the instant media was first captured.</summary>
    public RecordingEntry? SetStarted(RecordingEntry? entry, DateTimeOffset startedUtc)
    {
        if (entry is null) return null;
        var index = Entries.FindIndex(item => string.Equals(item.AccountId, entry.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !Entries[index].IsActive) return null;
        Entries[index] = Entries[index] with { StartedUtc = startedUtc, StoppedUtc = null };
        return Entries[index];
    }

    public bool Remove(string accountId, string recordingId) => Entries.RemoveAll(entry =>
        string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Id, recordingId, StringComparison.OrdinalIgnoreCase)) > 0;

    public int RemoveFile(string accountId, string fileName) => Entries.RemoveAll(entry =>
        string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalizes anything read from disk or handed in by a caller.</summary>
    public void Normalize()
    {
        Entries ??= [];
        for (var i = 0; i < Entries.Count; i++)
        {
            var note = AppLogger.SanitizeText(Entries[i].Note).Trim();
            var filePath = NormalizeFilePath(Entries[i].FilePath);
            if (!string.Equals(note, Entries[i].Note, StringComparison.Ordinal) || !string.Equals(filePath, Entries[i].FilePath, StringComparison.Ordinal))
                Entries[i] = Entries[i] with { Note = note, FilePath = filePath };
        }
        Prune();
    }

    private static string NormalizeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.Length <= 32767 ? full : string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    // Oldest finished captures are dropped first. An unfinished capture is only removed
    // when the index is full of them, which keeps the file bounded without discarding
    // what the app still believes it is writing.
    private void Prune()
    {
        if (Entries.Count <= MaxEntries) return;
        var excess = Entries.Count - MaxEntries;
        var finished = Entries.Where(entry => !entry.IsActive).ToList();
        foreach (var entry in finished.OrderBy(entry => entry.StartedUtc ?? entry.RequestedStartUtc).Take(excess))
            Entries.Remove(entry);
        if (Entries.Count <= MaxEntries) return;
        foreach (var entry in Entries.OrderBy(entry => entry.StartedUtc ?? entry.RequestedStartUtc).Take(Entries.Count - MaxEntries).ToList())
            Entries.Remove(entry);
    }
}
