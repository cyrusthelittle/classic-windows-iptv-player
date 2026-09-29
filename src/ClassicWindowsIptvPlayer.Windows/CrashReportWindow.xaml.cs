using System.Windows;

namespace ClassicWindowsIptvPlayer.Windows;

public enum CrashReportChoice { Later, DontSend, Sent }

public sealed record FeedbackDeliveryResult(bool Success, string StatusMessage, bool WillRetry = false);

public partial class CrashReportWindow : Window
{
    private readonly Func<string, string?, Task<FeedbackDeliveryResult>> _send;
    private bool _sending;

    public CrashReportChoice Choice { get; private set; } = CrashReportChoice.Later;

    public CrashReportWindow(string redactedLog, Func<string, string?, Task<FeedbackDeliveryResult>> send)
    {
        InitializeComponent();
        _send = send ?? throw new ArgumentNullException(nameof(send));
        LogPreview.Text = redactedLog ?? string.Empty;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        _sending = true;
        SendButton.IsEnabled = false;
        StatusText.Text = "Sending report…";
        try
        {
            var result = await _send(LogPreview.Text, NullIfEmpty(MessageBox.Text));
            StatusText.Text = result.StatusMessage;
            if (result.Success)
            {
                Choice = CrashReportChoice.Sent;
                DialogResult = true;
            }
            else if (result.WillRetry)
            {
                StatusText.Text += " It is queued for retry.";
            }
        }
        catch
        {
            StatusText.Text = "Could not send the report. It is still saved on this device and available to retry.";
        }
        finally
        {
            _sending = false;
            if (IsVisible) SendButton.IsEnabled = true;
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Choice = CrashReportChoice.Later;
        DialogResult = false;
    }

    private void DontSend_Click(object sender, RoutedEventArgs e)
    {
        Choice = CrashReportChoice.DontSend;
        DialogResult = false;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
