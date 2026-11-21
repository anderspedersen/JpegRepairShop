using System.Windows;
using System.Windows.Input;
using JpegRepairShop.ViewModels;

namespace JpegRepairShop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // "Open with…" / drag onto the exe: open the first command-line argument.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            await _viewModel.OpenPathAsync(args[1]);
        ImageView.Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardChanges())
            e.Cancel = true;
    }

    /// <summary>Mouse wheel over a DC value nudges it up or down; with Shift held, 10 steps per notch.</summary>
    private void OnDcWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DcChannel channel })
        {
            int steps = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            channel.Nudge(e.Delta > 0 ? steps : -steps);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            await _viewModel.OpenPathAsync(paths[0]);
            ImageView.Focus();
        }
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        ImageView.ZoomToFit();
        ImageView.Focus();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        ImageView.ZoomToActualSize();
        ImageView.Focus();
    }
}
