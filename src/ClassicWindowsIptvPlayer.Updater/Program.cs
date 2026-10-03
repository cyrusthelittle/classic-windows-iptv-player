using ClassicWindowsIptvPlayer.Core;
using System.Text.Json;
if (args.Length != 2 || args[0] is not ("--apply" or "--recover")) return 2;
UpdateJob? job = null;
try
{
    var jobPath = Path.GetFullPath(args[1]); AuthenticatedUpdate.RejectLinks(jobPath);
    if (new FileInfo(jobPath).Length > 16384) throw new InvalidDataException("Invalid update job size.");
    job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllBytes(jobPath)) ?? throw new InvalidDataException("Invalid update job.");
    UpdateInstaller.Run(job, args[0] == "--recover");
    if (args[0] == "--recover") { using var restored = UpdateInstaller.Launch(job); return 0; }
    var journal = Path.Combine(job.InstallDirectory, UpdateInstaller.PendingMarker);
    var info = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(journal)) ?? throw new InvalidDataException("Invalid installed journal.");
    var work = info.WorkDirectory; var health = Path.Combine(work, "ready.txt");
    try
    {
        // This process is an app bootstrap/native probe, with no interactive window or stream.
        using var probe = UpdateInstaller.Launch(job, health);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!probe.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!probe.HasExited)
        {
            probe.Kill(); probe.WaitForExit(5000);
            throw new IOException("The updated binary's noninteractive startup probe timed out.");
        }
        if (probe.ExitCode != 0 || !File.Exists(health) || File.ReadAllText(health) != "ready") throw new IOException("The updated app failed its startup probe. The previous version will be restored.");
        using (var installLease = new InstallationLease(job.InstallDirectory))
        using (var dataLease = AppDataLease.Acquire(job.DataRoot))
            File.Move(journal, Path.Combine(work, "installed-journal.json"));
        File.WriteAllText(Path.Combine(work, "result.txt"), "Update installed; noninteractive WPF/native bootstrap checked. Backups retained.");
        using var app = UpdateInstaller.Launch(job);
        return 0;
    }
    catch
    {
        using (var installLease = new InstallationLease(job.InstallDirectory))
        using (var dataLease = AppDataLease.Acquire(job.DataRoot))
        {
            // The marker may have moved only after a healthy probe; retain a blocking pointer if rollback fails.
            if (!File.Exists(journal)) File.Copy(Path.Combine(work, "installed-journal.json"), journal, false);
            UpdateInstaller.Recover(job, journal);
        }
        throw;
    }
}
catch (Exception error)
{
    if (job is not null)
    {
        try
        {
            UpdateInstaller.ValidateJob(job);
            File.WriteAllText(Path.Combine(job.StageDirectory, "update-result.txt"), "Update failed: " + error.Message + "\nApplication backups and saved data were retained.");
            if (!File.Exists(Path.Combine(job.InstallDirectory, UpdateInstaller.PendingMarker))) { using var restored = UpdateInstaller.Launch(job); }
        }
        catch { }
    }
    return 1;
}
