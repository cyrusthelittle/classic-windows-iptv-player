using System.IO;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>Candidate ordering and on-disk checks, without loading native code.</summary>
public static class VlcRuntimeLocator
{
    public static string? Select(IEnumerable<string> installedDirectories, string applicationDirectory,
        Func<string, bool> probe, string? chosenDirectory = null)
    {
        var candidates = (chosenDirectory is null ? Enumerable.Empty<string>() : new[] { chosenDirectory })
            .Concat(installedDirectories).Concat(new[]
            {
                Path.Combine(applicationDirectory, "libvlc", "win-x64"),
                Path.Combine(applicationDirectory, "libvlc"),
                applicationDirectory
            });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            try
            {
                var path = Path.GetFullPath(candidate);
                if (seen.Add(path) && IsCandidate(path) && probe(path)) return path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* An unusable installation must not suppress the local fallback. */ }
        }
        return null;
    }

    public static bool IsCandidate(string directory)
    {
        try
        {
            return IsX64Library(Path.Combine(directory, "libvlc.dll")) &&
                IsX64Library(Path.Combine(directory, "libvlccore.dll")) &&
                Directory.Exists(Path.Combine(directory, "plugins")) &&
                Directory.EnumerateFiles(Path.Combine(directory, "plugins"), "*.dll", SearchOption.AllDirectories).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static bool IsX64Library(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
        stream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 24) return false;
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) return false;
        stream.Position = offset + 22;
        return (reader.ReadUInt16() & 0x2000) != 0; // IMAGE_FILE_DLL
    }
}
