using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private bool _videoAdjustmentsChanged;

    private void VideoAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _videoAdjustmentsChanged = false;
        var dialog = new Window
        {
            Title = "Video adjustments",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            Background = (Brush)FindResource("Bg0Brush"),
            Foreground = (Brush)FindResource("Text0Brush")
        };

        var panel = new StackPanel { Margin = new Thickness(22) };
        var sliders = new List<(string Name, Slider Control, TextBox Value)>();
        panel.Children.Add(new TextBlock
        {
            Text = "Adjust the picture while it plays. Drag a slider or type a value. Changes apply immediately and are saved when this window closes.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Text1Brush"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        AddSlider("Brightness", 0, 2, _state.VideoBrightness, value => _state.VideoBrightness = value);
        AddSlider("Contrast", 0, 2, _state.VideoContrast, value => _state.VideoContrast = value);
        AddSlider("Saturation", 0, 2, _state.VideoSaturation, value => _state.VideoSaturation = value);
        AddSlider("Hue", 0, 360, _state.VideoHue, value => _state.VideoHue = (int)Math.Round(value));
        AddSlider("Gamma", 0.5, 2, _state.VideoGamma, value => _state.VideoGamma = value);

        panel.Children.Add(new TextBlock
        {
            Text = "Neutral values: brightness, contrast, saturation and gamma 1.00; hue 0°.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text2Brush"),
            Margin = new Thickness(0, 6, 0, 16)
        });

        var buttons = new DockPanel { LastChildFill = false };
        var reset = new Button { Content = "Reset", Padding = new Thickness(14, 7, 14, 7), MinWidth = 86 };
        reset.Click += (_, _) =>
        {
            foreach (var (name, slider, valueInput) in sliders)
            {
                slider.Value = name == "Hue" ? 0 : 1;
                valueInput.Text = slider.Value.ToString(name == "Hue" ? "0" : "0.00", CultureInfo.CurrentCulture);
            }
            ApplyVideoAdjustments();
            _videoAdjustmentsChanged = true;
        };
        DockPanel.SetDock(reset, Dock.Left);
        buttons.Children.Add(reset);
        var close = new Button { Content = "Close", IsDefault = true, Padding = new Thickness(14, 7, 14, 7), MinWidth = 86 };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Closed += (_, _) =>
        {
            if (_videoAdjustmentsChanged) _store.Save(_state);
        };
        dialog.ShowDialog();

        void AddSlider(string label, double minimum, double maximum, double initial, Action<double> update)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(initial, minimum, maximum),
                TickFrequency = label == "Hue" ? 15 : 0.05,
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(slider, label);
            var valueInput = new TextBox
            {
                Text = slider.Value.ToString(label == "Hue" ? "0" : "0.00", CultureInfo.CurrentCulture),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = $"Enter a value from {minimum} to {maximum}"
            };
            AutomationProperties.SetName(valueInput, label + " value");
            void ShowValue() => valueInput.Text = slider.Value.ToString(label == "Hue" ? "0" : "0.00", CultureInfo.CurrentCulture);
            void CommitValue()
            {
                if (double.TryParse(valueInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
                    !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                    slider.Value = Math.Clamp(parsed, minimum, maximum);
                ShowValue();
            }
            valueInput.LostFocus += (_, _) => CommitValue();
            valueInput.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Enter) return;
                CommitValue();
                args.Handled = true;
            };
            slider.ValueChanged += (_, _) =>
            {
                update(slider.Value);
                ShowValue();
                ApplyVideoAdjustments();
                _videoAdjustmentsChanged = true;
            };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(valueInput, 2);
            row.Children.Add(name);
            row.Children.Add(slider);
            row.Children.Add(valueInput);
            panel.Children.Add(row);
            sliders.Add((label, slider, valueInput));
        }
    }

    private void ApplyVideoAdjustments()
    {
        var player = _mediaPlayer;
        if (player is null) return;
        try
        {
            var brightness = (float)Math.Clamp(_state.VideoBrightness, 0, 2);
            var contrast = (float)Math.Clamp(_state.VideoContrast, 0, 2);
            var saturation = (float)Math.Clamp(_state.VideoSaturation, 0, 2);
            var hue = Math.Clamp(_state.VideoHue, 0, 360);
            var gamma = (float)Math.Clamp(_state.VideoGamma, 0.5, 2);
            var neutral = brightness == 1 && contrast == 1 && saturation == 1 && hue == 0 && gamma == 1;
            if (_tuner is not null) _tuner.VideoAdjustmentsActive = !neutral;
            if (neutral)
            {
                player.SetAdjustInt(VideoAdjustOption.Enable, 0);
                return;
            }

            player.SetAdjustInt(VideoAdjustOption.Enable, 1);
            player.SetAdjustFloat(VideoAdjustOption.Brightness, brightness);
            player.SetAdjustFloat(VideoAdjustOption.Contrast, contrast);
            player.SetAdjustFloat(VideoAdjustOption.Saturation, saturation);
            player.SetAdjustInt(VideoAdjustOption.Hue, hue);
            player.SetAdjustFloat(VideoAdjustOption.Gamma, gamma);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Could not apply video adjustments: " + ex.Message);
        }
    }
}
