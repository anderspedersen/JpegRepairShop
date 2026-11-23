using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JpegRepairShop;

/// <summary>Shows the performance log (JpegRepair.Core.PerfLog) after "Fix all", with a button to copy it.</summary>
public sealed class PerfLogWindow : Window
{
    public PerfLogWindow(string text)
    {
        Title = "Performance log";
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };

        var copy = new Button { Content = "Copy", MinWidth = 80, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(text);
        var close = new Button { Content = "Close", MinWidth = 80, Padding = new Thickness(8, 3, 8, 3), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        Content = root;
    }
}
