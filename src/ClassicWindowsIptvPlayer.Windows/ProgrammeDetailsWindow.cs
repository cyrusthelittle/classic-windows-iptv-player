using ClassicWindowsIptvPlayer.Core;
using System;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace ClassicWindowsIptvPlayer.Windows;

internal sealed class ProgrammeDetailsWindow : Window
{
    public ProgrammeDetailsWindow(Channel channel, EpgProgramme programme, Action? watchArchive)
    {
        Title = programme.Title;
        Width = 460;
        Height = 320;
        MinWidth = 300;
        MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = channel.Name, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = $"{programme.Start.LocalDateTime:f} – {programme.Stop.LocalDateTime:t}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        panel.Children.Add(new TextBlock { Text = programme.Category, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = programme.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var availability = Catchup.Evaluate(channel, programme, DateTimeOffset.UtcNow);
        if (availability.Failure != CatchupFailure.Unsupported && watchArchive is not null)
        {
            var watch = new Button { Name = "WatchArchiveButton", Content = availability.IsStartOver ? "Start Over" : "Watch archive", IsEnabled = availability.IsAvailable, Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(watch, watch.Content.ToString());
            panel.Children.Add(watch);
            panel.Children.Add(new TextBlock { Text = availability.IsAvailable ? "Availability and connection allowance will be checked with the provider." : availability.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
            watch.Click += (_, _) => { DialogResult = true; watchArchive(); };
        }
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Padding = new Thickness(10, 5, 10, 5) };
        panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
