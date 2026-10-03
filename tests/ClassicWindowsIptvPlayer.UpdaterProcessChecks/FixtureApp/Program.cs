using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

// Executed only from the harness's invented install, never against normal data.
var root = Environment.GetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT");
if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Path.GetFileName(root).StartsWith("fixture-data-")) return 20;
var health = Environment.GetEnvironmentVariable("CYRUS_IPTV_UPDATE_HEALTH");
var state = new ConfigStore().Load();
var valid = state.Account.Password == "fixture-password" && state.SelectedLibrary.FavoriteIds.Contains("fixture-favorite") &&
    state.SelectedLibrary.Recent.Any(p => p.ItemKey == "fixture-history") && state.DarkMode && Directory.Exists(state.RecordingFolder) &&
    new ConfigStore().LoadChannelCache("fixture")?.Any(p => p.Id == "fixture-favorite") == true;
File.WriteAllText(Path.Combine(root, "relaunch-probe.json"), JsonSerializer.Serialize(new { Root = AppDataPaths.Root, Valid = valid }));
if (!valid) return 21;
if (health is not null)
{
    File.WriteAllText(health, "ready");
    // Fixture-only deliberate post-ack crash proves readiness alone does not pass the helper's probe.
    return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixture-marker.dll"))[0] == 8 ? 23 : 0;
}
Thread.Sleep(5000);
return 0;
