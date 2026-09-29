namespace ClassicWindowsIptvPlayer.Core;

public enum ScheduledRecordingStatus
{
    Scheduled = 0,
    Recording,
    Completed,
    Missed,
    Conflict,
    Failed,
    Cancelled
}

public enum ScheduledRecordingRecovery
{
    None = 0,
    ResumeIfWindowOpen,
    MissedWindowExpired
}

/// <summary>A single account-scoped programme capture request. Contains no provider URL or credentials.</summary>
public sealed record ScheduledRecordingJob
{
    public string Id { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;
    public string ProgrammeTitle { get; init; } = string.Empty;
    public DateTimeOffset ProgrammeStartUtc { get; init; }
    public DateTimeOffset ProgrammeEndUtc { get; init; }
    public TimeSpan PrePadding { get; init; }
    public TimeSpan PostPadding { get; init; }
    public DateTimeOffset RequestedCaptureStartUtc { get; init; }
    public DateTimeOffset RequestedCaptureEndUtc { get; init; }
    public string DestinationPath { get; init; } = string.Empty;
    public string? RecordingId { get; init; }
    public ScheduledRecordingStatus Status { get; init; } = ScheduledRecordingStatus.Scheduled;
    public string Reason { get; init; } = string.Empty;

    public bool IsLive => Status is ScheduledRecordingStatus.Scheduled or ScheduledRecordingStatus.Recording or ScheduledRecordingStatus.Conflict;
}

public sealed record ScheduledRecordingValidation(bool IsValid, string Reason)
{
    public static ScheduledRecordingValidation Valid { get; } = new(true, string.Empty);
}

public sealed record ScheduledRecordingRecoveryResult(ScheduledRecordingJob Job, ScheduledRecordingRecovery Classification);

/// <summary>Pure deterministic validation, overlap classification and restart recovery.</summary>
public static class ScheduledRecordingPolicy
{
    public static ScheduledRecordingValidation Validate(ScheduledRecordingJob? job)
    {
        if (job is null) return new(false, "Job is missing.");
        if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.AccountId) || string.IsNullOrWhiteSpace(job.ChannelId))
            return new(false, "Job, account and channel IDs are required.");
        if (job.ProgrammeStartUtc.Offset != TimeSpan.Zero || job.ProgrammeEndUtc.Offset != TimeSpan.Zero ||
            job.RequestedCaptureStartUtc.Offset != TimeSpan.Zero || job.RequestedCaptureEndUtc.Offset != TimeSpan.Zero)
            return new(false, "Programme and capture timestamps must be UTC.");
        if (job.ProgrammeEndUtc <= job.ProgrammeStartUtc) return new(false, "Programme end must be after programme start.");
        if (job.PrePadding < TimeSpan.Zero || job.PostPadding < TimeSpan.Zero) return new(false, "Padding cannot be negative.");
        DateTimeOffset expectedStart;
        DateTimeOffset expectedEnd;
        try
        {
            expectedStart = job.ProgrammeStartUtc - job.PrePadding;
            expectedEnd = job.ProgrammeEndUtc + job.PostPadding;
        }
        catch (ArgumentOutOfRangeException) { return new(false, "Padding places the capture window outside the supported date range."); }
        if (job.RequestedCaptureStartUtc != expectedStart || job.RequestedCaptureEndUtc != expectedEnd)
            return new(false, "Requested capture window must exactly match programme time plus padding.");
        if (string.IsNullOrWhiteSpace(job.DestinationPath) || !Path.IsPathFullyQualified(job.DestinationPath))
            return new(false, "Destination path must be a fully qualified path.");
        if (LooksSecretLike(job.DestinationPath) || (job.RecordingId is not null && LooksSecretLike(job.RecordingId)))
            return new(false, "Destination path and recording ID must not contain URLs or credentials.");
        if (!Enum.IsDefined(job.Status)) return new(false, "Job status is invalid.");
        return ScheduledRecordingValidation.Valid;
    }

    private static bool LooksSecretLike(string value) =>
        value.Contains("://", StringComparison.Ordinal) ||
        value.Contains("?", StringComparison.Ordinal) ||
        value.Contains("#", StringComparison.Ordinal) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("username", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("credential", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ScheduledRecordingJob> ClassifyConflicts(IEnumerable<ScheduledRecordingJob> jobs)
    {
        var valid = jobs.Select(job => (Job: job, Validation: Validate(job)))
            .Where(item => item.Validation.IsValid)
            .Select(item => item.Job)
            .ToArray();
        var conflicted = new HashSet<ScheduledRecordingJob>();
        var activeByAccount = valid.Where(job => job.IsLive)
            .GroupBy(job => job.AccountId, StringComparer.OrdinalIgnoreCase);
        foreach (var accountJobs in activeByAccount)
        {
            var ordered = accountJobs
                .OrderBy(job => job.RequestedCaptureStartUtc)
                .ThenBy(job => job.RequestedCaptureEndUtc)
                .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(job => job.Id, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length < 2) continue;

            // A connected interval component is a conflict group: every interval in
            // a component of size > 1 overlaps at least one other interval. Track the
            // furthest end rather than comparing each interval with every active one.
            var componentStart = 0;
            var componentMaxEnd = ordered[0].RequestedCaptureEndUtc;
            void MarkComponent(int start, int end)
            {
                if (end - start < 2) return;
                for (var index = start; index < end; index++)
                {
                    var job = ordered[index];
                    if (job.Status != ScheduledRecordingStatus.Recording)
                        conflicted.Add(job);
                }
            }

            for (var index = 1; index < ordered.Length; index++)
            {
                var current = ordered[index];
                if (current.RequestedCaptureStartUtc < componentMaxEnd)
                {
                    if (current.RequestedCaptureEndUtc > componentMaxEnd)
                        componentMaxEnd = current.RequestedCaptureEndUtc;
                    continue;
                }

                MarkComponent(componentStart, index);
                componentStart = index;
                componentMaxEnd = current.RequestedCaptureEndUtc;
            }
            MarkComponent(componentStart, ordered.Length);
        }
        return valid.Select(job => conflicted.Contains(job)
            ? job with { Status = ScheduledRecordingStatus.Conflict, Reason = "Capture window overlaps another active job for this account." }
            : job.Status == ScheduledRecordingStatus.Conflict
                ? job with { Status = ScheduledRecordingStatus.Scheduled, Reason = string.Empty }
                : job).ToArray();
    }

    public static ScheduledRecordingRecoveryResult Recover(ScheduledRecordingJob job, DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Recovery time must be UTC.", nameof(nowUtc));
        if (job.Status != ScheduledRecordingStatus.Recording)
            return new(job, ScheduledRecordingRecovery.None);
        if (nowUtc < job.RequestedCaptureEndUtc)
            return new(job with { Status = ScheduledRecordingStatus.Scheduled, Reason = "Recovered after restart; capture may resume while its window is open." },
                ScheduledRecordingRecovery.ResumeIfWindowOpen);
        return new(job with { Status = ScheduledRecordingStatus.Missed, Reason = "App restarted after the requested capture window ended." },
            ScheduledRecordingRecovery.MissedWindowExpired);
    }
}

/// <summary>Versioned account-scoped durable job index.</summary>
public sealed class ScheduledRecordingIndex
{
    public const int CurrentVersion = 1;
    public const int MaxJobs = 2000;
    public int Version { get; set; } = CurrentVersion;
    public List<ScheduledRecordingJob> Jobs { get; set; } = [];

    public void Normalize()
    {
        Jobs ??= [];
        Jobs = Jobs.Where(job => job is not null)
            .Select(job => job with { Reason = AppLogger.SanitizeText(job.Reason).Trim() })
            .GroupBy(job => job.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.Last())
            .OrderBy(job => job.RequestedCaptureStartUtc).TakeLast(MaxJobs).ToList();
    }

    public ScheduledRecordingJob? Find(string accountId, string id) => Jobs.FirstOrDefault(job =>
        string.Equals(job.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(job.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool Add(ScheduledRecordingJob job)
    {
        if (!ScheduledRecordingPolicy.Validate(job).IsValid || Jobs.Any(existing =>
            string.Equals(existing.AccountId, job.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Id, job.Id, StringComparison.OrdinalIgnoreCase))) return false;
        Jobs.Add(job);
        Jobs = [.. ScheduledRecordingPolicy.ClassifyConflicts(Jobs)];
        Normalize();
        return true;
    }

    public IReadOnlyList<ScheduledRecordingRecoveryResult> RecoverActive(DateTimeOffset nowUtc)
    {
        var results = new List<ScheduledRecordingRecoveryResult>();
        for (var i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].Status != ScheduledRecordingStatus.Recording) continue;
            var result = ScheduledRecordingPolicy.Recover(Jobs[i], nowUtc);
            Jobs[i] = result.Job;
            results.Add(result);
        }
        return results;
    }
}
