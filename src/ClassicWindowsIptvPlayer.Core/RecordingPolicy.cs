using System.Globalization;
using System.Text;

namespace ClassicWindowsIptvPlayer.Core;

public enum RecordingContainerKind { Mp4 = 0, Ts, Mkv }

public enum RecordingFailure
{
    None = 0,
    UnsupportedSource,
    UnsupportedContainer,
    NoDestination,
    DestinationMissing,
    DestinationNotWritable,
    LowDiskSpace,
    NameCollision,
    ConnectionLimit,
    ConnectionUnknown,
    NoAccount
}

public enum RecordingFilenameStatus { Ok = 0, Renamed, Failed }

public enum RecordingSpaceStatus { Ok = 0, Low, Unknown }

public sealed record RecordingDestination(string Folder, bool IsValid, RecordingFailure Failure, string Message, VolumeSpace Space);
public sealed record RecordingSpaceCheck(long AvailableBytes, long RequiredBytes, RecordingSpaceStatus Status, string Message);
public sealed record RecordingFilename(string Value, RecordingFilenameStatus Status);

/// <summary>Free space on the volume that would hold a file, read once by the caller.</summary>
public sealed record VolumeSpace(bool IsKnown, long AvailableBytes)
{
    public static readonly VolumeSpace Unknown = new(false, 0);
}

/// <summary>
/// What already exists at the destination: the captures this account still believes it
/// is writing, and a probe of the destination folder. A file that one of those unfinished
/// captures explains is continued; any other existing file is never replaced.
/// </summary>
public sealed record RecordingTargets(IReadOnlyList<RecordingEntry> Unfinished, Func<string, bool> FileExists)
{
    public static readonly RecordingTargets None = new([], _ => false);
}

/// <summary>A validated capture request. Nothing here touches the disk.</summary>
public sealed record RecordingPlan
{
    public string DestinationFolder { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public RecordingContainerKind Container { get; init; }
    public DateTimeOffset RequestedStartUtc { get; init; }
    public DateTimeOffset RequestedStopUtc { get; init; }

    /// <summary>When the capture actually began, which can differ from the requested start.</summary>
    public DateTimeOffset? ActualStartUtc { get; init; }

    public RecordedProgrammeMetadata Programme { get; init; } = RecordedProgrammeMetadata.None;
    public long RequiredBytes { get; init; }

    /// <summary>Set when a file with this name already exists and must be continued rather than replaced.</summary>
    public string? ConflictingFileName { get; init; }

    /// <summary>The unfinished index row this capture continues, if any.</summary>
    public RecordingEntry? Existing { get; init; }

    /// <summary>
    /// Binds a plan to the unfinished index row it continues. The row supplies the file
    /// name and the originally requested times, so a restarted capture of the same file
    /// appears once in the recordings list rather than twice. The index stores no folder,
    /// so the destination always comes from the current plan; only an unfinished row whose
    /// name matches the file this plan chose can be continued.
    /// </summary>
    public static RecordingPlan Create(RecordingPlan plan, RecordingEntry? existing)
    {
        if (plan.ConflictingFileName is null || existing is null) return plan with { Existing = null };
        return plan with
        {
            Existing = existing,
            FileName = existing.FileName,
            FilePath = Path.Combine(plan.DestinationFolder, existing.FileName),
            RequestedStartUtc = existing.RequestedStartUtc,
            RequestedStopUtc = existing.RequestedStopUtc,
            Programme = existing.Programme.HasProgramme ? existing.Programme : plan.Programme
        };
    }
}

public sealed record RecordingDecision(bool IsAllowed, RecordingFailure Failure, string Message, RecordingPlan? Plan,
    ConnectionBudgetDecision Connection);

/// <summary>
/// Everything the recording surface must decide before capture starts: where the file
/// goes, whether the drive has room, what the file is called, whether the account
/// allowance covers one more stream, and what guide text is kept with the capture.
///
/// Storage policy, stated once. A capture needs at least 512 MiB free on the chosen
/// volume and keeps a 256 MiB margin above the estimate so the volume cannot be filled
/// by the capture itself. Free space is read from the volume that holds the destination
/// folder, not from the volume the app was installed on. An unreadable volume is
/// reported as unknown and allowed with that warning attached, because refusing every
/// recording on a network or unmounted drive would be as unhelpful as recording blind.
///
/// No method here performs I/O; the caller supplies the directory and volume facts.
/// </summary>
public static class RecordingPolicy
{
    public const long MinFreeBytes = 512L * 1024 * 1024;
    public const long SafetyMarginBytes = 256L * 1024 * 1024;

    // An hour of a high-bitrate stream is roughly 1.8 GB, so 48 hours is already more
    // than any single capture can claim. A longer open-ended capture is not refused; it
    // simply is not allowed to demand an unlimited amount of free space.
    public const long MinEstimateBytes = 64L * 1024 * 1024;
    public const int MaxEstimateHours = 48;
    public const double AssumedBitrateKbps = 4000;

    // The full NTFS limit is 255 UTF-16 units, but the capture layer also appends
    // suffixes, so the readable part is kept well below it.
    public const int MaxNameLength = 80;
    public const int MaxCollisionAttempts = 1000;
    public const string DefaultFolderName = "Recordings";

    public static readonly IReadOnlyList<RecordingContainerKind> SupportedContainers =
        [RecordingContainerKind.Mp4, RecordingContainerKind.Ts, RecordingContainerKind.Mkv];

    private static readonly char[] IllegalFileNameCharacters =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] ReservedFileNames =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static string Extension(RecordingContainerKind container) => container switch
    {
        RecordingContainerKind.Mp4 => ".mp4",
        RecordingContainerKind.Ts => ".ts",
        RecordingContainerKind.Mkv => ".mkv",
        _ => string.Empty
    };

    /// <summary>Why a channel cannot be captured, or null when it can.</summary>
    public static string? DescribeSource(Channel? channel)
    {
        if (channel is null) return "No channel was selected.";
        // Movies and series are bounded files. Capturing them is a download, and catch-up
        // is the provider's answer for past live programming, not a local capture.
        if (channel.MediaKind != MediaKind.Live)
            return "Only live channels can be captured directly. Movies and series can be recorded after they start, or through provider catch-up where it is advertised.";
        if (string.IsNullOrWhiteSpace(channel.Url)) return "This channel has no playable stream address.";
        return null;
    }

    public static RecordingDestination ValidateDestination(string? folder)
    {
        var text = (folder ?? string.Empty).Trim();
        if (text.Length == 0)
            return new RecordingDestination(string.Empty, false, RecordingFailure.NoDestination,
                $"Choose a recording folder, for example {DefaultFolderName} in Documents.", VolumeSpace.Unknown);
        string full;
        try { full = Path.GetFullPath(text); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RecordingDestination(text, false, RecordingFailure.DestinationMissing,
                "That is not a usable folder path.", VolumeSpace.Unknown);
        }
        var space = ReadVolumeSpace(full);
        if (!Directory.Exists(full))
            return new RecordingDestination(full, false, RecordingFailure.DestinationMissing,
                "The recording folder does not exist. Choose an existing folder.", space);
        try
        {
            if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReadOnly))
                return new RecordingDestination(full, false, RecordingFailure.DestinationNotWritable,
                    "The recording folder is read-only.", space);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RecordingDestination(full, false, RecordingFailure.DestinationNotWritable,
                "The recording folder cannot be written to.", space);
        }
        return new RecordingDestination(full, true, RecordingFailure.None, "The recording folder is ready.", space);
    }

    public static long EstimateBytes(TimeSpan duration)
    {
        // An open-ended capture still needs the floor; nothing may start on zero space.
        if (duration <= TimeSpan.Zero) return MinEstimateBytes;
        var hours = Math.Min(duration.TotalHours, MaxEstimateHours);
        return (long)Math.Ceiling(hours * 3600d * AssumedBitrateKbps * 1000d / 8d) + MinEstimateBytes;
    }

    public static RecordingSpaceCheck CheckFreeSpace(RecordingDestination destination, TimeSpan duration)
    {
        // A capture must never start below a usable floor, no matter how short it is.
        var required = Math.Max(MinFreeBytes, EstimateBytes(duration));
        var needed = required + SafetyMarginBytes;
        if (!destination.Space.IsKnown)
            return new RecordingSpaceCheck(-1, needed, RecordingSpaceStatus.Unknown,
                "The free space of the recording drive could not be read. Choose a folder on a local drive of this PC.");
        var available = destination.Space.AvailableBytes;
        return available < needed
            ? new RecordingSpaceCheck(available, needed, RecordingSpaceStatus.Low,
                $"Only {FormatBytes(available)} is free on the recording drive. This capture needs at least {FormatBytes(needed)} including a safety margin. Free space or choose another drive.")
            : new RecordingSpaceCheck(available, needed, RecordingSpaceStatus.Ok,
                $"{FormatBytes(available)} is free on the recording drive.");
    }

    public static string SanitizeFileName(string? value)
    {
        var builder = new StringBuilder((value ?? string.Empty).Trim());
        for (var i = 0; i < builder.Length; i++)
            if (Array.IndexOf(IllegalFileNameCharacters, builder[i]) >= 0) builder[i] = '_';
        var text = builder.ToString().TrimEnd(' ', '.');
        // A name made only of replacements still tells the user nothing.
        if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) return "Channel";
        var stem = text.Split('.')[0];
        if (ReservedFileNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) text = "_" + text;
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd(' ', '.') : text;
    }

    /// <summary>The name a capture gets when nothing at the destination blocks it.</summary>
    public static string BaseFileName(string? channelName, DateTimeOffset startUtc, RecordingContainerKind container)
    {
        var extension = Extension(container);
        if (string.IsNullOrEmpty(extension)) throw new InvalidOperationException("Unsupported recording container.");
        return SanitizeFileName(startUtc.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + " " + channelName) + extension;
    }

    /// <summary>
    /// Builds a name that cannot overwrite an existing recording. A file already on
    /// disk is continued or removed by the caller; the naming never replaces it.
    /// </summary>
    public static RecordingFilename ChooseFileName(string? folder, string? channelName, DateTimeOffset startUtc,
        RecordingContainerKind container, Func<string, bool>? exists = null)
    {
        var extension = Extension(container);
        if (string.IsNullOrEmpty(extension)) throw new InvalidOperationException("Unsupported recording container.");
        var baseName = BaseFileName(channelName, startUtc, container);
        var folderPath = string.IsNullOrWhiteSpace(folder) ? string.Empty : folder;
        for (var attempt = 1; attempt <= MaxCollisionAttempts; attempt++)
        {
            var name = attempt == 1 ? baseName : baseName[..^extension.Length] + $" ({attempt})" + extension;
            if (exists is null || !exists(Path.Combine(folderPath, name))) return new RecordingFilename(name, attempt == 1 ? RecordingFilenameStatus.Ok : RecordingFilenameStatus.Renamed);
        }
        throw new InvalidOperationException("No unused recording file name was available.");
    }

    public static RecordingDecision Evaluate(AccountProfile? profile, string? destinationFolder, Channel? channel,
        TimeSpan duration, DateTimeOffset startUtc, RecordingContainerKind container, EpgProgramme? programme,
        ConnectionBudget budget, bool replacingCurrentConnection = false, RecordingTargets? targets = null,
        VolumeSpace? space = null)
    {
        var connection = budget.Evaluate(profile, replacingCurrentConnection);
        var source = DescribeSource(channel);
        if (source is not null) return new RecordingDecision(false, RecordingFailure.UnsupportedSource, source, null, connection);
        var channelName = channel!.Name;
        if (!SupportedContainers.Contains(container))
            return new RecordingDecision(false, RecordingFailure.UnsupportedContainer,
                "This recording container is not supported. Use MP4, TS or MKV.", null, connection);
        var destination = ValidateDestination(destinationFolder);
        if (!destination.IsValid) return new RecordingDecision(false, destination.Failure, destination.Message, null, connection);
        // The caller may supply the volume facts when the destination folder has to be
        // created first, or when it is reporting a destination that is not mounted yet.
        var prepared = space is null ? destination : destination with { Space = space };
        var checkedSpace = CheckFreeSpace(prepared, duration);
        if (checkedSpace.Status == RecordingSpaceStatus.Low)
            return new RecordingDecision(false, RecordingFailure.LowDiskSpace, checkedSpace.Message, null, connection);
        // An existing file this account is still recording into is continued, not renamed
        // around. Any other existing file is stepped over rather than replaced.
        targets ??= RecordingTargets.None;
        var baseName = BaseFileName(channelName, startUtc, container);
        var continuing = targets.FileExists(Path.Combine(prepared.Folder, baseName)) &&
            targets.Unfinished.Any(entry => string.Equals(entry.FileName, baseName, StringComparison.OrdinalIgnoreCase));
        var fileName = continuing ? new RecordingFilename(baseName, RecordingFilenameStatus.Ok) :
            ChooseFileName(prepared.Folder, channelName, startUtc, container, targets.FileExists);
        var filePath = Path.Combine(prepared.Folder, fileName.Value);
        var plan = new RecordingPlan
        {
            DestinationFolder = prepared.Folder, ChannelName = channelName, FileName = fileName.Value, FilePath = filePath,
            Container = container, RequestedStartUtc = startUtc, RequestedStopUtc = startUtc + (duration > TimeSpan.Zero ? duration : TimeSpan.Zero),
            ActualStartUtc = null, Programme = RecordedProgrammeMetadata.From(programme),
            RequiredBytes = checkedSpace.RequiredBytes, ConflictingFileName = continuing ? baseName : null
        };
        // An unreported allowance is a choice, not a pass: the caller has to confirm it
        // before the capture starts, and the message says so either way.
        return new RecordingDecision(true, RecordingFailure.None, checkedSpace.Message, plan, connection);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < 4) { value /= 1024; unit++; }
        var name = unit switch { 1 => "KB", 2 => "MB", 3 => "GB", _ => "TB" };
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + name;
    }

    // The volume that would hold a file written to this folder. Only existing paths
    // are inspected, so a folder that is about to be created still resolves its volume.
    // Every failure here is reported as unknown space rather than as no space.
    internal static VolumeSpace ReadVolumeSpace(string folder)
    {
        try
        {
            var probe = new DirectoryInfo(Path.GetFullPath(folder));
            while (probe is not null && !probe.Exists) probe = probe.Parent;
            var root = probe is null ? null : Path.GetPathRoot(probe.FullName);
            if (string.IsNullOrEmpty(root)) return VolumeSpace.Unknown;
            var volume = new DriveInfo(root);
            return volume.IsReady ? new VolumeSpace(true, volume.AvailableFreeSpace) : VolumeSpace.Unknown;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return VolumeSpace.Unknown;
        }
    }
}
