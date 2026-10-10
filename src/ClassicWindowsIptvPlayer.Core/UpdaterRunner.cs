using System.Text.Json;

namespace ClassicWindowsIptvPlayer.Core;

/// <summary>Copies the helper and its shared runtime out of the installation before replacing files.</summary>
public static class UpdaterRunner
{
    public static void CopyFiles(string installDirectory, string runnerDirectory)
    {
        const string assembly = "ClassicWindowsIptvPlayer.Updater";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AuthenticatedUpdate.HelperExe,
            assembly + ".dll",
            assembly + ".deps.json",
            assembly + ".runtimeconfig.json"
        };
        var depsPath = AuthenticatedUpdate.ResolvePath(installDirectory, assembly + ".deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        var targetName = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new InvalidDataException("The updater dependency manifest has no runtime target.");
        var target = deps.RootElement.GetProperty("targets").GetProperty(targetName);
        foreach (var library in target.EnumerateObject())
        {
            // Published managed/native assets are flattened into the application directory.
            // Resource assemblies retain their locale directory.
            foreach (var groupName in new[] { "runtime", "native", "resources" })
            {
                if (!library.Value.TryGetProperty(groupName, out var assets)) continue;
                foreach (var asset in assets.EnumerateObject())
                {
                    if (asset.Name.EndsWith("/_._", StringComparison.Ordinal)) continue;
                    var name = Path.GetFileName(asset.Name);
                    if (groupName == "resources")
                    {
                        var locale = asset.Value.GetProperty("locale").GetString();
                        if (string.IsNullOrWhiteSpace(locale)) throw new InvalidDataException("Invalid updater resource locale.");
                        name = locale + "/" + name;
                    }
                    files.Add(name);
                }
            }
        }

        // Validate the complete set before copying or starting the helper. A missing runtime
        // must leave the current player running rather than close it for an unusable update.
        var copies = files.Select(name => (
            Source: AuthenticatedUpdate.ResolvePath(installDirectory, name),
            Destination: AuthenticatedUpdate.ResolvePath(runnerDirectory, name))).ToArray();
        foreach (var copy in copies)
            if (!File.Exists(copy.Source)) throw new IOException("An updater dependency is missing: " + Path.GetFileName(copy.Source));
        foreach (var copy in copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Destination)!);
            File.Copy(copy.Source, copy.Destination, overwrite: false);
        }
    }
}
