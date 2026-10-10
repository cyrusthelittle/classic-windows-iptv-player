using System.Windows;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostButton();
    }

    private void UpdateTopmostButton()
    {
        TopmostButton.Content = Topmost ? "On top ✓" : "Not on top";
        TopmostButton.ToolTip = Topmost
            ? "The player stays above other windows. Click to turn this off."
            : "Click to keep the player above other windows.";
    }
}
