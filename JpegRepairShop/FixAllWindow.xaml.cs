using System.ComponentModel;
using System.Windows;

namespace JpegRepairShop;

/// <summary>
/// Progress for "Fix all": shows what has been fixed so far and lets the user stop. Stopping takes effect after the
/// search that is running; closing the window counts as stopping.
/// </summary>
public partial class FixAllWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _finished;

    public FixAllWindow()
    {
        InitializeComponent();
    }

    public CancellationToken Token => _cancel.Token;

    public void ShowSearching(int fixes) =>
        StatusText.Text = Token.IsCancellationRequested
            ? "Stopping after the current search…"
            : fixes == 0 ? "Searching for damage…" : "Searching for more damage…";

    public void AddFix(int fixes, string description)
    {
        CountText.Text = fixes == 1 ? "1 fix applied." : $"{fixes} fixes applied.";
        Log.Items.Add($"{fixes}. {description}");
        Log.ScrollIntoView(Log.Items[^1]);
    }

    /// <summary>Allows the window to close and closes it.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => RequestStop();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished)
            return;
        RequestStop();
        e.Cancel = true; // closes by itself when the running search is done
    }

    private void RequestStop()
    {
        _cancel.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping after the current search…";
    }
}
