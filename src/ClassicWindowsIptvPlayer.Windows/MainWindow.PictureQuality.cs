using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using System.Windows;
using System.Windows.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    // Picture engine: a second LibVLC instance that carries every picture option
    // (deinterlacing, upscaling, HDR and the CPU filters). It is rebuilt whenever the
    // options change, so changes apply without restarting the app.
    private readonly SemaphoreSlim _pictureEngineGate = new(1, 1);
    private string _pictureEngineKey = string.Empty;
    // Names of the LibVLC video filters this runtime has (filled when the player engine starts).
    private HashSet<string> _availableVideoFilters = new(StringComparer.OrdinalIgnoreCase);
    // The full LibVLC start-up arguments, reused by the enhancement engine (Step 36).
    private string[] _libVlcArgs = Array.Empty<string>();
    private int _superResolutionUnsupportedReported;
    private int _rtxHdrUnsupportedReported;

    // The choices the user made (shown as checkmarks). Kept even while "Turn all off" is selected.
    private PictureEnhancementSettings GetChosenPictureSettings() => PictureEnhancementOptions.Normalize(
        _state.DeinterlaceMode, _state.UpscaleMode, _state.RtxHdrEnabled, _state.DeblockQuality,
        _state.DenoiseLevel, _state.DebandEnabled, _state.SharpenLevel, false);

    // What actually plays: every option at its default while "Turn all off" is selected.
    private PictureEnhancementSettings GetPictureSettings() => _state.PictureEnhancementsOff
        ? PictureEnhancementOptions.Normalize("default", "linear", false, 0, 0, false, 0, false)
        : GetChosenPictureSettings();

    private void PictureMenu_Opened(object sender, RoutedEventArgs e) => UpdatePictureCheckmarks();

    private void UpdatePictureCheckmarks()
    {
        var settings = GetChosenPictureSettings();
        PicturesOffMenuItem.IsChecked = _state.PictureEnhancementsOff;
        PicturesOnMenuItem.Visibility = _state.PictureEnhancementsOff ? Visibility.Visible : Visibility.Collapsed;
        CheckOnly(DeinterlaceMenu, "deinterlace:" + settings.DeinterlaceMode);
        CheckOnly(UpscalingMenu, "upscale:" + settings.UpscaleMode);
        RtxHdrMenuItem.IsChecked = settings.RtxHdr;
        CheckOnly(DeblockMenu, "deblock:" + settings.DeblockQuality.ToString(CultureInfo.InvariantCulture));
        CheckOnly(DenoiseMenu, "denoise:" + settings.DenoiseLevel.ToString(CultureInfo.InvariantCulture));
        DebandMenuItem.IsChecked = settings.Deband;
        CheckOnly(SharpenMenu, "sharpen:" + settings.SharpenLevel.ToString(CultureInfo.InvariantCulture));
        SetFilterAvailability(DeblockMenu, "postproc", "Reduce blockiness (deblocking, CPU)",
            "Reduce blockiness: needs VLC 3 (64-bit) installed");
        SetFilterAvailability(DenoiseMenu, "hqdn3d", "Reduce noise (CPU)", "Reduce noise: not available in this VLC");
        SetFilterAvailability(DebandMenuItem, "gradfun", "Smooth color banding (CPU)", "Smooth color banding: not available in this VLC");
        SetFilterAvailability(SharpenMenu, "sharpen", "Sharpen (CPU)", "Sharpen: not available in this VLC");
        // While "Turn all off" is selected only "Turn back on" can be used; the saved choices stay visible.
        var enhancementsOn = !_state.PictureEnhancementsOff;
        DeinterlaceMenu.IsEnabled = enhancementsOn;
        UpscalingMenu.IsEnabled = enhancementsOn;
        RtxHdrMenuItem.IsEnabled = enhancementsOn;
        PicturesOffMenuItem.IsEnabled = enhancementsOn;
    }

    private void SetFilterAvailability(MenuItem item, string module, string availableHeader, string missingHeader)
    {
        var available = _availableVideoFilters.Contains(module);
        item.IsEnabled = available && !_state.PictureEnhancementsOff;
        item.Header = available ? availableHeader : missingHeader;
    }

    private static void CheckOnly(MenuItem parent, string selectedTag)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            item.IsCheckable = true;
            item.IsChecked = string.Equals(item.Tag as string, selectedTag, StringComparison.Ordinal);
        }
    }

    // One handler for every Picture menu item. Tag format: "group:value" for choices,
    // a single word for on/off items.
    private void PictureOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        var value = parts.Length == 2 ? parts[1] : string.Empty;
        switch (parts[0])
        {
            case "deinterlace": _state.DeinterlaceMode = value; break;
            case "upscale": _state.UpscaleMode = value; break;
            case "rtxhdr": _state.RtxHdrEnabled = !_state.RtxHdrEnabled; break;
            case "deblock": _state.DeblockQuality = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "denoise": _state.DenoiseLevel = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "deband": _state.DebandEnabled = !_state.DebandEnabled; break;
            case "sharpen": _state.SharpenLevel = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "alloff": _state.PictureEnhancementsOff = true; break;
            case "allon": _state.PictureEnhancementsOff = false; break;
            default:
                return;
        }
        _store.Save(_state);
        UpdatePictureCheckmarks();
        _ = ApplyPictureSettingsAsync(restartStream: true);
        if (_state.PictureEnhancementsOff && parts[0] is not ("alloff" or "allon"))
            StatusText.Text = "Choice saved. Picture enhancements are turned off; use \"Turn picture enhancements back on\" to use your choices.";
    }

    // Builds the picture engine for the current settings and swaps it in. The channel
    // that is playing is reopened only when the engine actually changed.
    private async Task ApplyPictureSettingsAsync(bool restartStream)
    {
        await _pictureEngineGate.WaitAsync();
        try
        {
            if (_isShuttingDown || _tuner is null) return;
            var settings = GetPictureSettings();
            var arguments = PictureEnhancementOptions.BuildEngineArgs(settings)
                .Concat(PictureEnhancementOptions.BuildCpuFilterArgs(settings, _availableVideoFilters)).ToList();
            var key = string.Join(" ", arguments);
            if (key == _pictureEngineKey) return;

            LibVLC? engine = null;
            if (arguments.Count > 0)
            {
                try
                {
                    var engineArguments = _libVlcArgs.Concat(arguments).ToArray();
                    engine = await Task.Run(() => new LibVLC(engineArguments));
                    if (_isShuttingDown)
                    {
                        engine.Dispose();
                        return;
                    }
                    engine.Log += OnLibVlcLog;
                    AppLogger.Info("Picture engine started: " + key);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Picture engine failed to start; playing without picture options.", ex);
                    StatusText.Text = "Picture options could not be started on this PC; playing without them.";
                    engine = null;
                    key = string.Empty;
                }
            }
            else AppLogger.Info("Picture engine off: all picture options are at their defaults.");

            var previous = _filteredLibVlc;
            var abandonedBefore = _tuner.AbandonedPlayerCount;
            _pictureEngineKey = key;
            _filteredLibVlc = engine;
            _tuner.FilteredLibVlc = engine;
            _tuner.CpuEnhancementsOnlyBelowFullHd = false;
            _tuner.ResetEnhancementFallbacks();

            var restarted = false;
            if (restartStream && _recordingService?.ActiveCount is null or 0)
                restarted = _tuner.TryRestartCurrentLive();
            if (restartStream)
                StatusText.Text = restarted
                    ? "Picture setting applied. Reopening the channel with it."
                    : "Picture setting applied.";

            // The old engine may still own the player that is being replaced.
            if (previous is not null && restarted) _ = DisposePictureEngineLaterAsync(previous, abandonedBefore);
        }
        finally { _pictureEngineGate.Release(); }
    }

    private async Task DisposePictureEngineLaterAsync(LibVLC engine, int abandonedBefore)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (_isShuttingDown || _tuner is null) return;
        if (_tuner.AbandonedPlayerCount != abandonedBefore)
        {
            AppLogger.Warn("Old picture engine kept alive: a player on it could not be stopped.");
            return;
        }
        engine.Log -= OnLibVlcLog;
        await Task.Run(() => BoundedNativeCall.Run(engine.Dispose, TimeSpan.FromSeconds(5),
            ex => AppLogger.Warn("Old picture engine dispose failed. " + AppLogger.SanitizeText(ex.Message))));
    }

    // Called from OnLibVlcLog on LibVLC threads.
    private void CheckPictureFeatureLog(string module, string message)
    {
        if (message.StartsWith("turning VSR", StringComparison.OrdinalIgnoreCase) &&
            _libVlcLogFilter.ShouldLog(module + ": " + message, Environment.TickCount64, out _))
            AppLogger.Info("libvlc " + module + ": " + message);

        if (message.Contains("Super Resolution", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            ReportUnsupportedPictureFeature(ref _superResolutionUnsupportedReported,
                "AI Super Resolution is not supported by this graphics card or driver; standard upscaling is used.", message);

        if (message.Contains("True HDR", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             message.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)))
            ReportUnsupportedPictureFeature(ref _rtxHdrUnsupportedReported,
                "SDR to HDR is not available on this PC: it needs an NVIDIA RTX graphics card with RTX Video HDR turned on and an HDR screen.", message);
    }

    private void ReportUnsupportedPictureFeature(ref int reported, string userMessage, string libVlcMessage)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        AppLogger.Warn("Picture feature unavailable: " + libVlcMessage);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isShuttingDown) StatusText.Text = userMessage;
        }));
    }
}
