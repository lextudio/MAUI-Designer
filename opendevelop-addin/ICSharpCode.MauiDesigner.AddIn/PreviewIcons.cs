using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using MAUIDesigner.Preview;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// 16×16 device icons from the Visual Studio 2026 Image Library, drawn from the same path geometry
/// as <c>Phone.xaml</c> / <c>PhoneLandscape.xaml</c> / <c>Computer.xaml</c>. Copied as geometry
/// rather than referenced as resources because the addin ships on its own and cannot rely on
/// OpenDevelop's Resources/VS2026 tree being present. The fill follows the theme's
/// <c>Foreground</c> brush, like every OpenDevelop icon.
/// </summary>
static class PreviewIcons
{
    // Images/Phone.xaml
    const string PhoneGeometry =
        "M7 12C6.72386 12 6.5 12.2239 6.5 12.5C6.5 12.7761 6.72386 13 7 13H9C9.27614 13 9.5 12.7761 9.5 12.5C9.5 12.2239 9.27614 12 9 12H7ZM5.75 1C4.7835 1 4 1.7835 4 2.75V13.25C4 14.2165 4.7835 15 5.75 15H10.25C11.2165 15 12 14.2165 12 13.25V2.75C12 1.7835 11.2165 1 10.25 1H5.75ZM5 2.75C5 2.33579 5.33579 2 5.75 2H10.25C10.6642 2 11 2.33579 11 2.75V13.25C11 13.6642 10.6642 14 10.25 14H5.75C5.33579 14 5 13.6642 5 13.25V2.75Z";

    // Images/PhoneLandscape.xaml
    const string PhoneLandscapeGeometry =
        "M12 9C12 9.27614 12.2239 9.5 12.5 9.5C12.7761 9.5 13 9.27614 13 9L13 7C13 6.72386 12.7761 6.5 12.5 6.5C12.2239 6.5 12 6.72386 12 7L12 9ZM1 10.25C1 11.2165 1.7835 12 2.75 12L13.25 12C14.2165 12 15 11.2165 15 10.25L15 5.75C15 4.7835 14.2165 4 13.25 4L2.75 4C1.7835 4 1 4.7835 1 5.75L1 10.25ZM2.75 11C2.33579 11 2 10.6642 2 10.25L2 5.75C2 5.33579 2.33579 5 2.75 5L13.25 5C13.6642 5 14 5.33579 14 5.75L14 10.25C14 10.6642 13.6642 11 13.25 11L2.75 11Z";

    // Images/Computer.xaml (a monitor and a laptop, two paths)
    const string ComputerMonitorGeometry =
        "M2.5 1H6.5C7.33 1 8 1.67 8 2.5V3H7V2.5C7 2.22 6.78 2 6.5 2H2.5C2.22 2 2 2.22 2 2.5V12.5C2 12.78 2.22 13 2.5 13H4.59C4.53 13.16 4.5 13.32 4.5 13.5C4.5 13.68 4.53 13.84 4.59 14H2.5C1.67 14 1 13.33 1 12.5V2.5C1 1.67 1.67 1 2.5 1Z";

    const string ComputerLaptopGeometry =
        "M6 4H12C13.103 4 14 4.897 14 6V10C14 11.103 13.103 12 12 12H11V13H12C12.276 13 12.5 13.224 12.5 13.5C12.5 13.776 12.276 14 12 14H6C5.724 14 5.5 13.776 5.5 13.5C5.5 13.224 5.724 13 6 13H7V12H6C4.897 12 4 11.103 4 10V6C4 4.897 4.897 4 6 4ZM8 13H10V12H8V13ZM12 11C12.551 11 13 10.552 13 10V6C13 5.448 12.551 5 12 5H6C5.449 5 5 5.448 5 6V10C5 10.552 5.449 11 6 11H12Z";

    /// <summary>The icon for a target: a phone (portrait or landscape) or the macOS computer.</summary>
    public static FrameworkElement For(PreviewPlatform platform, PreviewOrientation orientation) =>
        platform == PreviewPlatform.MacOSAppKit
            ? Draw(ComputerMonitorGeometry, ComputerLaptopGeometry)
            : Draw(orientation == PreviewOrientation.Landscape ? PhoneLandscapeGeometry : PhoneGeometry);

    static FrameworkElement Draw(params string[] geometries)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        foreach (var geometry in geometries)
        {
            var path = new Path
            {
                Data = Geometry.Parse(geometry),
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
            };
            // The code equivalent of {DynamicResource Foreground}: colours follow the IDE theme.
            if (System.Windows.Application.Current?.TryFindResource("Foreground") is Brush foreground)
                path.Fill = foreground;
            else
                path.Fill = SystemColors.ControlTextBrush;
            canvas.Children.Add(path);
        }

        return new Viewbox { Width = 16, Height = 16, Child = canvas };
    }
}
