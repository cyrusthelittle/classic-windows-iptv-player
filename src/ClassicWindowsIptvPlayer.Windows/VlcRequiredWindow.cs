using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace ClassicWindowsIptvPlayer.Windows;

public sealed class VlcRequiredWindow : Window
{
    public string? ChosenDirectory { get; private set; }

    public VlcRequiredWindow()
    {
        Title = "VLC is needed for playback";
        Width = 530; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "The player could not load a compatible 64-bit VLC 3 installation or the VLC libraries in the app folder.\n\nInstall 64-bit VLC from VideoLAN, then choose Retry. If VLC is in a custom folder, choose that folder instead.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(buttons, "Download VLC", () =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.videolan.org/vlc/download-windows.html") { UseShellExecute = true }); }
            catch { System.Windows.MessageBox.Show(this, "The download page could not be opened. Visit videolan.org to download 64-bit VLC.", "Download VLC"); }
        });
        AddButton(buttons, "Choose folder", () =>
        {
            var picker = new OpenFolderDialog { Title = "Choose the VLC folder containing libvlc.dll and plugins" };
            if (picker.ShowDialog(this) == true) { ChosenDirectory = picker.FolderName; DialogResult = true; }
        });
        AddButton(buttons, "Retry", () => DialogResult = true).IsDefault = true;
        AddButton(buttons, "Cancel", () => DialogResult = false).IsCancel = true;
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static Button AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(4) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }
}
