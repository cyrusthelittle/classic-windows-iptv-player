using System.Windows;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class FeedbackWindow : Window
{
    private readonly Func<string, bool, Task<FeedbackDeliveryResult>> _send;
    private bool _sending;

    public FeedbackWindow(Func<string, bool, Task<FeedbackDeliveryResult>> send, string? initialMessage = null)
    {
        InitializeComponent();
        _send = send ?? throw new ArgumentNullException(nameof(send));
        MessageBox.Text = initialMessage ?? string.Empty;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text.Trim();
        if (message.Length == 0)
        {
            StatusText.Text = "Write a message before sending.";
            MessageBox.Focus();
            return;
        }
        if (_sending) return;

        _sending = true;
        SendButton.IsEnabled = false;
        StatusText.Text = "Sending message…";
        try
        {
            var result = await _send(message, IncludeLogsCheckBox.IsChecked == true);
            StatusText.Text = result.StatusMessage;
            if (result.Success)
                DialogResult = true;
            else if (result.WillRetry)
                StatusText.Text += " It is queued for retry.";
        }
        catch
        {
            StatusText.Text = "Could not complete the request. Your message is still in this box; press Send to retry.";
        }
        finally
        {
            _sending = false;
            if (IsVisible) SendButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
