using ClassicWindowsIptvPlayer.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>Collects user choices for one schedule; it never connects to or tunes a provider stream.</summary>
public partial class ScheduledRecordingEditorWindow : Window
{
    private const int MaxPaddingMinutes = 60;
    private readonly string _accountId;
    private readonly string _channelId;
    private readonly string _channelName;
    private readonly string _programmeTitle;
    private readonly DateTimeOffset _programmeStartUtc;
    private readonly DateTimeOffset _programmeEndUtc;

    public ScheduledRecordingJob? ScheduledJob { get; private set; }

    public ScheduledRecordingEditorWindow(
        string accountId,
        string channelId,
        string channelName,
        string programmeTitle,
        DateTimeOffset programmeStartUtc,
        DateTimeOffset programmeEndUtc,
        string defaultOutputDirectory)
    {
        InitializeComponent();
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Account ID is required.", nameof(accountId));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel ID is required.", nameof(channelId));
        if (programmeStartUtc.Offset != TimeSpan.Zero || programmeEndUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Programme start and end must be UTC.");
        if (programmeEndUtc <= programmeStartUtc) throw new ArgumentException("Programme end must be after start.");

        _accountId = accountId;
        _channelId = channelId;
        _channelName = channelName ?? string.Empty;
        _programmeTitle = programmeTitle ?? string.Empty;
        _programmeStartUtc = programmeStartUtc;
        _programmeEndUtc = programmeEndUtc;

        for (var minute = 0; minute <= MaxPaddingMinutes; minute += 5)
        {
            PrePaddingBox.Items.Add(minute);
            PostPaddingBox.Items.Add(minute);
        }
        // Make the initial capture window explicit and ensure both selectors show
        // a valid choice as soon as the editor opens.
        PrePaddingBox.SelectedItem = 0;
        PostPaddingBox.SelectedItem = 0;

        var localStart = programmeStartUtc.ToLocalTime();
        var localEnd = programmeEndUtc.ToLocalTime();
        ProgrammeText.Text = string.IsNullOrWhiteSpace(_programmeTitle) ? "(Untitled programme)" : _programmeTitle;
        ChannelText.Text = string.IsNullOrWhiteSpace(_channelName) ? "Channel" : _channelName;
        ProgrammeTimeText.Text = $"{localStart.ToString("f", CultureInfo.CurrentCulture)} – {localEnd.ToString("t", CultureInfo.CurrentCulture)} ({TimeZoneInfo.Local.StandardName})";

        var safeName = MakeFileName(string.IsNullOrWhiteSpace(_programmeTitle) ? _channelName : _programmeTitle);
        if (!string.IsNullOrWhiteSpace(defaultOutputDirectory))
            DestinationBox.Text = Path.Combine(defaultOutputDirectory, $"{safeName}-{localStart:yyyyMMdd-HHmm}.ts");
        UpdateCaptureWindow();
        PrePaddingBox.SelectionChanged += Padding_SelectionChanged;
        PostPaddingBox.SelectionChanged += Padding_SelectionChanged;
        DestinationBox.TextChanged += Destination_TextChanged;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(DestinationBox.Text)) BrowseButton.Focus();
            else ScheduleButton.Focus();
        };
    }

    private static string MakeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Where(ch => !invalid.Contains(ch) && !char.IsControl(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "recording" : safe;
    }

    private int SelectedMinutes( System.Windows.Controls.ComboBox box) =>
        box.SelectedItem is int minutes && minutes is >= 0 and <= MaxPaddingMinutes ? minutes : 0;

    private void Padding_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateCaptureWindow();

    private void UpdateCaptureWindow()
    {
        if (CaptureWindowText is null || PrePaddingBox is null || PostPaddingBox is null) return;
        var start = _programmeStartUtc.AddMinutes(-SelectedMinutes(PrePaddingBox)).ToLocalTime();
        var end = _programmeEndUtc.AddMinutes(SelectedMinutes(PostPaddingBox)).ToLocalTime();
        CaptureWindowText.Text = $"Planned capture: {start.ToString("g", CultureInfo.CurrentCulture)} – {end.ToString("t", CultureInfo.CurrentCulture)}. This window is padded; the programme itself runs { _programmeStartUtc.ToLocalTime():t}–{_programmeEndUtc.ToLocalTime():t}.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose scheduled recording file",
            Filter = "MPEG transport stream (*.ts)|*.ts",
            DefaultExt = ".ts",
            AddExtension = true,
            FileName = Path.GetFileName(DestinationBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(DestinationBox.Text))
                ? Path.GetDirectoryName(DestinationBox.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FileName;
    }

    private void Destination_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ScheduleButton.IsEnabled = true;
        ValidationText.Text = string.Empty;
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        var destination = DestinationBox.Text.Trim();
        if (!Path.IsPathFullyQualified(destination) || !string.Equals(Path.GetExtension(destination), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            ValidationText.Text = "Choose a fully qualified output path ending in .ts.";
            DestinationBox.Focus();
            return;
        }

        var prePadding = TimeSpan.FromMinutes(SelectedMinutes(PrePaddingBox));
        var postPadding = TimeSpan.FromMinutes(SelectedMinutes(PostPaddingBox));
        ScheduledRecordingJob job;
        try
        {
            job = new ScheduledRecordingJob
            {
                Id = Guid.NewGuid().ToString("N"),
                AccountId = _accountId,
                ChannelId = _channelId,
                ChannelName = _channelName,
                ProgrammeTitle = _programmeTitle,
                ProgrammeStartUtc = _programmeStartUtc,
                ProgrammeEndUtc = _programmeEndUtc,
                PrePadding = prePadding,
                PostPadding = postPadding,
                RequestedCaptureStartUtc = _programmeStartUtc - prePadding,
                RequestedCaptureEndUtc = _programmeEndUtc + postPadding,
                DestinationPath = destination,
                Status = ScheduledRecordingStatus.Scheduled
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            ValidationText.Text = "The padding would put the capture window outside the supported date range.";
            return;
        }

        var validation = ScheduledRecordingPolicy.Validate(job);
        if (!validation.IsValid)
        {
            ValidationText.Text = validation.Reason;
            return;
        }

        ScheduledJob = job;
        DialogResult = true;
    }
}
