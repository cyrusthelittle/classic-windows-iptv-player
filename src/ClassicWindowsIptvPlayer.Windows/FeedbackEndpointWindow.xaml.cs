using System.Windows;
using ClassicWindowsIptvPlayer.Core;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class FeedbackEndpointWindow : Window
{
    public string? Endpoint { get; private set; }

    public FeedbackEndpointWindow(string? currentEndpoint)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(currentEndpoint))
            EndpointTextBox.Text = currentEndpoint;
        EndpointTextBox.TextChanged += (_, _) => ValidationText.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var value = EndpointTextBox.Text.Trim();
        if (!AppState.TryValidateFeedbackEndpoint(value, out var endpoint))
        {
            ValidationText.Text = "Enter a valid absolute HTTPS URL without credentials (maximum 2048 characters).";
            return;
        }

        Endpoint = endpoint!.AbsoluteUri;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Endpoint = null;
        DialogResult = true;
    }
}
