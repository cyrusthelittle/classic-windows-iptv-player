using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CyrusIptv.Windows;

internal static class IconFactory
{
    public const string Menu = "M3,6 H21 V8 H3 Z M3,11 H21 V13 H3 Z M3,16 H21 V18 H3 Z";
    public const string Previous = "M6,5 H8 V19 H6 Z M18,5 L9,12 L18,19 Z";
    public const string Play = "M8,5 L19,12 L8,19 Z";
    public const string Pause = "M7,5 H10 V19 H7 Z M14,5 H17 V19 H14 Z";
    public const string Stop = "M7,7 H17 V17 H7 Z";
    public const string Next = "M16,5 H18 V19 H16 Z M6,5 L15,12 L6,19 Z";
    public const string Volume = "M4,9 H8 L13,5 V19 L8,15 H4 Z M16,9 C17,10.5 17,13.5 16,15 L18,16.5 C20,13.8 20,10.2 18,7.5 Z";
    public const string Mute = "M4,9 H8 L13,5 V19 L8,15 H4 Z M16,8 L18,10 L20,8 L21.5,9.5 L19.5,11.5 L21.5,13.5 L20,15 L18,13 L16,15 L14.5,13.5 L16.5,11.5 L14.5,9.5 Z";
    public const string FullScreen = "M5,5 H11 V7 H7 V11 H5 Z M13,5 H19 V11 H17 V7 H13 Z M5,13 H7 V17 H11 V19 H5 Z M17,17 V13 H19 V19 H13 V17 Z";
    public const string Restart = "M12,5 C8.7,5 6,7.7 6,11 H3 L7,15 L11,11 H8 C8,8.8 9.8,7 12,7 C14.8,7 17,9.2 17,12 C17,14.8 14.8,17 12,17 C10.4,17 9,16.3 8.1,15.3 L6.7,16.7 C8,18.1 9.9,19 12,19 C15.9,19 19,15.9 19,12 C19,8.1 15.9,5 12,5 Z";
    public const string Test = "M12,3 C7,3 3,7 3,12 C3,17 7,21 12,21 C17,21 21,17 21,12 C21,7 17,3 12,3 Z M10.5,14.8 L7.8,12.1 L9.2,10.7 L10.5,12 L15.6,7.5 L17,9 Z";
    public const string Copy = "M8,7 H18 V21 H8 Z M6,3 H16 V5 H8 C6.9,5 6,5.9 6,7 V17 H4 V5 C4,3.9 4.9,3 6,3 Z";
    public const string Star = "M12,3 L14.7,8.5 L20.8,9.4 L16.4,13.7 L17.5,19.8 L12,16.9 L6.5,19.8 L7.6,13.7 L3.2,9.4 L9.3,8.5 Z";
    public const string StarHollow = "M12,6.6 L13.9,10.4 L18.1,11 L15,14 L15.8,18.2 L12,16.2 L8.2,18.2 L9,14 L5.9,11 L10.1,10.4 Z M12,3 L9.3,8.5 L3.2,9.4 L7.6,13.7 L6.5,19.8 L12,16.9 L17.5,19.8 L16.4,13.7 L20.8,9.4 L14.7,8.5 Z";
    public const string Folder = "M3,6 H9 L11,8 H21 V19 H3 Z M5,10 V17 H19 V10 Z";
    public const string Tv = "M4,6 H20 V17 H4 Z M6,8 V15 H18 V8 Z M9,20 H15 V18 H9 Z";
    public const string Cinema = "M4,6 H20 V18 H4 Z M6,8 H8 V10 H6 Z M10,8 H12 V10 H10 Z M14,8 H16 V10 H14 Z M18,8 H20 V10 H18 Z M6,14 H8 V16 H6 Z M10,14 H12 V16 H10 Z M14,14 H16 V16 H14 Z M18,14 H20 V16 H18 Z";
    public const string Exit = "M6,5 H14 V7 H8 V17 H14 V19 H6 Z M14,9 L17,12 L14,15 V13 H10 V11 H14 Z";

    public static Viewbox Create(string geometry, double size = 18)
    {
        var path = new Path
        {
            Data = Geometry.Parse(geometry),
            Stretch = Stretch.Uniform
        };
        // Follow the active theme so icons stay visible on dark buttons.
        path.SetResourceReference(Shape.FillProperty, "Text0Brush");
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path
        };
    }

    public static StackPanel Labeled(string label, string geometry)
    {
        var text = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text0Brush");
        return new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Create(geometry, 15),
                text
            }
        };
    }
}
