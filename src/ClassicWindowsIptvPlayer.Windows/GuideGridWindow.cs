using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace ClassicWindowsIptvPlayer.Windows;

internal sealed class GuideGridWindow : Window
{
    private readonly EpgGuide _guide;
    private readonly IReadOnlyList<Channel> _channels;
    private readonly Func<Channel, string?> _mapping;
    private readonly int _offsetMinutes;
    private readonly Channel? _playing;
    private readonly Channel? _selected;
    private readonly DatePicker _date = new() { Width = 145, Margin = new Thickness(4, 0, 4, 0) };
    private readonly TextBox _search = new() { Width = 190, Margin = new Thickness(8, 0, 4, 0),
        ToolTip = "Search titles, descriptions and categories across the loaded guide" };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 5, 8, 5), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _ruler = new() { Margin = new Thickness(218, 2, 8, 2), FontWeight = FontWeights.SemiBold };
    private readonly ListBox _rows = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    private readonly System.Windows.Threading.DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTimeOffset _windowStart;
    private bool _updatingDate;
    private readonly Action<Channel, EpgProgramme>? _watchArchive;
    private readonly Action<Channel, EpgProgramme>? _scheduleRecording;

    public GuideGridWindow(EpgGuide guide, IReadOnlyList<Channel> channels, Channel? selected, Channel? playing,
        Func<Channel, string?> mapping, int offsetMinutes, bool stale, Action<Channel, EpgProgramme>? watchArchive = null,
        Action<Channel, EpgProgramme>? scheduleRecording = null)
    {
        _guide = guide;
        _channels = channels.Where(channel => channel.MediaKind == MediaKind.Live).ToArray();
        _mapping = mapping;
        _offsetMinutes = offsetMinutes;
        _playing = playing;
        _selected = selected;
        _watchArchive = watchArchive;
        _scheduleRecording = scheduleRecording;
        _status.Tag = stale ? "Saved guide is stale. Refresh from the main window. " : "";
        Title = "Programme guide";
        Width = 980;
        Height = 650;
        MinWidth = 580;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        AddButton(bar, "Now", (_, _) => MoveToInstant(DateTimeOffset.Now));
        AddButton(bar, "Today", (_, _) => MoveToLocal(DateTime.Today));
        AddButton(bar, "◀ 2 hours", (_, _) => MoveToInstant(_windowStart.AddHours(-2)));
        AddButton(bar, "2 hours ▶", (_, _) => MoveToInstant(_windowStart.AddHours(2)));
        bar.Children.Add(new TextBlock { Text = "Date", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        bar.Children.Add(_date);
        _date.SelectedDateChanged += (_, _) =>
        {
            if (!_updatingDate && _date.SelectedDate is { } value)
                MoveToLocal(value.Date.Add(_windowStart.LocalDateTime.TimeOfDay));
        };
        bar.Children.Add(new TextBlock { Text = "Search programmes", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_search);
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Render(); };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        AddButton(bar, "Clear", (_, _) => _search.Clear());

        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        DockPanel.SetDock(_ruler, Dock.Top);
        root.Children.Add(_ruler);
        VirtualizingPanel.SetIsVirtualizing(_rows, true);
        VirtualizingPanel.SetVirtualizationMode(_rows, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_rows, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_rows, ScrollBarVisibility.Auto);
        _rows.ItemTemplate = BuildTemplate();
        _rows.SelectionChanged += (_, _) =>
        {
            if (_rows.SelectedItem is GuideRow row && row.Programmes.Count > 0)
                _rows.Tag = row.Programmes[0];
        };
        _rows.PreviewKeyDown += RowsKeyDown;
        root.Children.Add(_rows);
        Content = root;
        var range = guide.DateRange();
        var first = range.First?.LocalDateTime.Date;
        var last = range.Last?.LocalDateTime.Date;
        _windowStart = DateTimeOffset.Now;
        if (first.HasValue && _windowStart.LocalDateTime.Date < first.Value) MoveToLocal(first.Value);
        if (last.HasValue && _windowStart.LocalDateTime.Date > last.Value) MoveToLocal(last.Value);
        if (selected is not null)
        {
            Loaded += (_, _) =>
            {
                var index = _channels.ToList().FindIndex(channel => SameChannel(channel, selected));
                if (index >= 0 && index < _rows.Items.Count) _rows.ScrollIntoView(_rows.Items[index]);
            };
        }
        Render();
    }

    private static void AddButton(Panel panel, string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(7, 3, 7, 3) };
        button.Click += action;
        panel.Children.Add(button);
    }

    private void MoveToLocal(DateTime local)
    {
        _windowStart = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
        Render();
    }

    private void MoveToInstant(DateTimeOffset instant)
    {
        _windowStart = instant;
        Render();
    }

    private void Render()
    {
        var day = _windowStart.LocalDateTime.Date;
        _updatingDate = true;
        _date.SelectedDate = day;
        _updatingDate = false;
        var search = _search.Text.Trim();
        if (search.Length > 0)
        {
            var matches = GuideSearchRows.Create(_guide, _channels, search, _mapping, _offsetMinutes,
                (channel, programme) => new GuideRow(channel, channel.Name + Marker(channel), [programme], programme.Start, programme.Stop));
            _rows.ItemsSource = matches;
            _status.Text = (string)_status.Tag + $"{matches.Count:N0} programme matches across the loaded guide. Select a programme to open details and schedule it.";
            return;
        }

        // DateTimeOffset conversion uses each instant's local UTC offset, so the repeated
        // hour at a DST fallback remains two distinct programmes.
        var start = _windowStart.ToUniversalTime();
        var end = start.AddHours(2);
        _ruler.Text = string.Join("        ", Enumerable.Range(0, 5)
            .Select(index => start.AddMinutes(index * 30).ToLocalTime().ToString("HH:mm zzz")));
        _rows.ItemsSource = new LazyList<GuideRow>(_channels.Count, index =>
        {
            var channel = _channels[index];
            return new GuideRow(channel, channel.Name + Marker(channel),
                _guide.GetProgrammes(channel, start, end, _mapping(channel), _offsetMinutes), start, end);
        });
        _status.Text = (string)_status.Tag + $"{start.LocalDateTime:g} to {end.LocalDateTime:g} · {_channels.Count:N0} channels. " +
            "Select a programme to view details and choose Schedule this programme. Up/Down: channel; Left/Right: two hours; Enter: programme details. Selected and playing channels are labeled separately.";
    }

    private static bool SameChannel(Channel left, Channel? right) => right is not null &&
        ItemIdentity.For(left).Equals(ItemIdentity.For(right), StringComparison.OrdinalIgnoreCase);

    private string Marker(Channel channel) =>
        (SameChannel(channel, _selected) ? "  [Selected]" : "") +
        (SameChannel(channel, _playing) ? "  [Playing]" : "");

    private DataTemplate BuildTemplate()
    {
        var template = new DataTemplate(typeof(GuideRow));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(GuideRow.ChannelName)));
        name.SetValue(FrameworkElement.WidthProperty, 210.0);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        row.AppendChild(name);
        var programmes = new FrameworkElementFactory(typeof(ItemsControl));
        programmes.SetValue(FrameworkElement.WidthProperty, 720.0);
        programmes.SetValue(FrameworkElement.HeightProperty, 36.0);
        programmes.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(GuideRow.Slots)));
        var itemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(Canvas)));
        itemsPanel.VisualTree.SetValue(FrameworkElement.WidthProperty, 720.0);
        itemsPanel.VisualTree.SetValue(FrameworkElement.HeightProperty, 36.0);
        programmes.SetValue(ItemsControl.ItemsPanelProperty, itemsPanel);
        var containerStyle = new Style(typeof(ContentPresenter));
        containerStyle.Setters.Add(new Setter(Canvas.LeftProperty, new System.Windows.Data.Binding(nameof(GuideSlot.Left))));
        programmes.SetValue(ItemsControl.ItemContainerStyleProperty, containerStyle);
        var itemTemplate = new DataTemplate(typeof(GuideSlot));
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        button.SetValue(FrameworkElement.HeightProperty, 32.0);
        button.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding(nameof(GuideSlot.Width)));
        button.SetValue(System.Windows.Controls.Control.PaddingProperty, new Thickness(6, 3, 6, 3));
        button.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(GuideSlot.Label)));
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding(nameof(GuideSlot.Label)));
        button.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(GuideSlot.ToolTip)));
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler(ShowDetails));
        itemTemplate.VisualTree = button;
        programmes.SetValue(ItemsControl.ItemTemplateProperty, itemTemplate);
        row.AppendChild(programmes);
        template.VisualTree = row;
        return template;
    }

    private void ShowDetails(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GuideSlot slot } button) return;
        var programme = slot.Programme;
        OpenProgramme(slot.Channel, programme);
    }

    private void OpenProgramme(Channel channel, EpgProgramme programme)
    {
        Action? watch = _watchArchive is null ? null : () =>
        {
            Close();
            _watchArchive(channel, programme);
        };
        Action? schedule = _scheduleRecording is null ? null : () => _scheduleRecording(channel, programme);
        new ProgrammeDetailsWindow(channel, programme, watch, schedule) { Owner = this }.ShowDialog();
    }

    private void RowsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) { MoveToInstant(_windowStart.AddHours(-2)); e.Handled = true; }
        else if (e.Key == Key.Right) { MoveToInstant(_windowStart.AddHours(2)); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button &&
            _rows.SelectedItem is GuideRow row && row.Programmes.Count > 0)
        {
            var programme = row.Programmes[0];
            OpenProgramme(row.Channel, programme);
            e.Handled = true;
        }
    }

    private sealed record GuideRow(Channel Channel, string ChannelName, IReadOnlyList<EpgProgramme> Programmes,
        DateTimeOffset WindowStart, DateTimeOffset WindowEnd)
    {
        public IReadOnlyList<GuideSlot> Slots => Programmes.Select(programme =>
        {
            var span = (WindowEnd - WindowStart).TotalSeconds;
            var left = Math.Max(0, (programme.Start - WindowStart).TotalSeconds / span * 720);
            var right = Math.Min(720, (programme.Stop - WindowStart).TotalSeconds / span * 720);
            return new GuideSlot(Channel, programme, left, Math.Max(2, right - left),
                $"{programme.Start.LocalDateTime:t} {programme.Title}", programme.Description);
        }).ToArray();
    }
    private sealed record GuideSlot(Channel Channel, EpgProgramme Programme, double Left, double Width, string Label, string ToolTip);
}
