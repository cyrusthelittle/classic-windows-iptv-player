using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace ClassicWindowsIptvPlayer.Core;

public sealed record UpdateJob(string StageDirectory, string InstallDirectory, string DataRoot, int ParentProcessId, string CurrentVersion, string Tag);
public sealed record UpdateBackup(string Path, bool Existed, string? Sha256);
public sealed record UpdateJournal(string WorkDirectory, UpdateBackup[] Files);

/// <summary>Invoked only by the external helper after the parent has exited; never writes saved data.</summary>
public static class UpdateInstaller
{
    public const string PendingMarker = ".update-pending.json";
    public const string HealthVariable = "CYRUS_IPTV_UPDATE_HEALTH";
    public static int Run(UpdateJob job, bool recoverOnly = false)
    {
        using var verificationDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ValidateJob(job);
        if (job.ParentProcessId > 0)
        {
            try
            {
                using var parent = Process.GetProcessById(job.ParentProcessId);
                if (!parent.WaitForExit(120000)) throw new IOException("The app did not finish closing. The update was not installed.");
            }
            catch (ArgumentException) { } // Parent has already exited.
        }
        // Mutex ownership is thread-affine: helper intentionally stays synchronous.
        using var installLease = new InstallationLease(job.InstallDirectory);
        using var dataLease = AppDataLease.Acquire(job.DataRoot);
        var pending = Path.Combine(job.InstallDirectory, PendingMarker);
        if (File.Exists(pending)) Recover(job, pending);
        if (recoverOnly) return 0;
        var manifest = AuthenticatedUpdate.VerifyManifest(File.ReadAllBytes(Path.Combine(job.StageDirectory, AuthenticatedUpdate.ManifestName)),
            File.ReadAllBytes(Path.Combine(job.StageDirectory, AuthenticatedUpdate.SignatureName)), job.Tag, Version.Parse(job.CurrentVersion));
        var work = Path.Combine(job.StageDirectory, "apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var extracted = Path.Combine(work, "payload"); Directory.CreateDirectory(extracted);
        // Hold package open against mutation/deletion until all authenticated bytes have been privately extracted.
        using (var package = new FileStream(Path.Combine(job.StageDirectory, manifest.Package), FileMode.Open, FileAccess.Read, FileShare.Read))
            AuthenticatedUpdate.VerifyAndExtract(package, manifest, extracted, verificationDeadline.Token);
        var backupRoot = Path.Combine(work, "backup"); Directory.CreateDirectory(backupRoot);
        var backups = new List<UpdateBackup>();
        foreach (var file in manifest.Files)
        {
            var target = CheckedTarget(job, file.Path);
            var exists = File.Exists(target);
            string? hash = null;
            if (exists)
            {
                var backup = AuthenticatedUpdate.ResolvePath(backupRoot, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, false);
                using var stream = File.OpenRead(backup); hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            backups.Add(new UpdateBackup(file.Path, exists, hash));
        }
        // Backups are complete before the durable marker allows any installed file mutation.
        var journal = new UpdateJournal(work, backups.ToArray());
        WriteDurable(pending, JsonSerializer.SerializeToUtf8Bytes(journal));
        try
        {
            foreach (var file in manifest.Files)
            {
                var target = CheckedTarget(job, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var source = AuthenticatedUpdate.ResolvePath(extracted, file.Path);
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != file.Size || !Convert.ToHexString(AuthenticatedUpdate.HashStream(input, verificationDeadline.Token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The extracted update was changed before installation.");
                input.Position = 0;
                var temporary = target + ".update-new";
                AuthenticatedUpdate.RejectLinks(temporary);
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                File.Move(temporary, target, true);
            }
            // Startup must see a complete install. Retain marker as rollback journal elsewhere during health check.
            // Retain the durable marker throughout the noninteractive startup probe.
        }
        catch
        {
            Recover(job, pending);
            throw;
        }
        return 0;
    }
    public static Process Launch(UpdateJob job, string? healthPath = null)
    {
        var start = new ProcessStartInfo(Path.Combine(job.InstallDirectory, AuthenticatedUpdate.ApplicationExe)) { UseShellExecute = false, WorkingDirectory = job.InstallDirectory };
        start.Environment[AppDataPaths.OverrideVariable] = job.DataRoot;
        if (healthPath is not null) start.Environment[HealthVariable] = healthPath;
        else start.Environment.Remove(HealthVariable);
        return Process.Start(start) ?? throw new IOException("The app could not be restarted.");
    }
    public static void Recover(UpdateJob job, string journalPath)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(journalPath)) ?? throw new InvalidDataException("Invalid update recovery journal.");
        var stage = Path.GetFullPath(job.StageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(journal.WorkDirectory).StartsWith(stage, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery backup escaped the update stage.");
        AuthenticatedUpdate.RejectLinks(journal.WorkDirectory);
        foreach (var file in journal.Files)
        {
            var target = CheckedTarget(job, file.Path);
            if (!file.Existed) continue;
            using var stream = File.OpenRead(AuthenticatedUpdate.ResolvePath(Path.Combine(journal.WorkDirectory, "backup"), file.Path));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The recovery backup checksum did not match. Retain the backup and repair the installation manually.");
        }
        foreach (var file in journal.Files)
        {
            var target = CheckedTarget(job, file.Path);
            if (file.Existed) RestoreLockedBackup(AuthenticatedUpdate.ResolvePath(Path.Combine(journal.WorkDirectory, "backup"), file.Path), target, file.Sha256);
            else if (File.Exists(target)) File.Delete(target);
            if (File.Exists(target + ".update-new")) File.Delete(target + ".update-new");
        }
        File.Delete(journalPath);
    }
    private static void RestoreLockedBackup(string backup, string target, string? expectedHash)
    {
        using var input = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The recovery backup changed before restoration.");
        input.Position = 0;
        var temporary = target + ".update-new";
        AuthenticatedUpdate.RejectLinks(temporary);
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        File.Move(temporary, target, true);
    }
    private static string CheckedTarget(UpdateJob job, string relative)
    {
        var target = AuthenticatedUpdate.ResolvePath(job.InstallDirectory, relative);
        var data = Path.GetFullPath(job.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        // Portable data root equals install root; signed application allowlist excludes its data files.
        if (!data.Equals(Path.GetFullPath(job.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            (target.Equals(data, StringComparison.OrdinalIgnoreCase) || target.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("An application file overlaps the saved-data root.");
        return target;
    }
    public static void ValidateJob(UpdateJob job)
    {
        foreach (var root in new[] { job.StageDirectory, job.InstallDirectory, job.DataRoot })
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new InvalidDataException("Update directories must exist and be absolute.");
            AuthenticatedUpdate.RejectLinks(root);
        }
        var expectedStages = Path.Combine(Path.GetFullPath(job.DataRoot), "updates") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(job.StageDirectory).StartsWith(expectedStages, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update stage must be inside the pinned data root's updates folder.");
        if (!Version.TryParse(job.CurrentVersion, out _) || job.ParentProcessId < 0) throw new InvalidDataException("Invalid update job.");
        AuthenticatedUpdate.ValidateTag(job.Tag);
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        AuthenticatedUpdate.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
}
