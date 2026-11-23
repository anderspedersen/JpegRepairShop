using System.ComponentModel;
using System.Windows;
using JpegRepair.Core;

namespace JpegRepairShop;

/// <summary>
/// Progress for "Fix all": shows what has been fixed so far and lets the user pause or stop. Both take effect at once:
/// the searches stop at their next checkpoint (<see cref="Gate"/>, set on the document while "Fix all" runs), and
/// between steps <see cref="WaitWhilePausedAsync"/> waits. Closing the window counts as stopping. The window is not
/// modal, so the picture can be zoomed and panned while it runs or is paused.
/// </summary>
public partial class FixAllWindow : Window
{
    private bool _finished;
    private TaskCompletionSource? _paused;   // set while paused; completed on resume or stop
    private TaskCompletionSource<bool>? _stripeChoice;   // set while asking whether to fix stripes

    public FixAllWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Whether the last step (stripes between damaged MCUs) runs after the first part. The user can change it at any
    /// time while the first part runs.
    /// </summary>
    public bool FixStripes
    {
        get => StripesCheck.IsChecked == true;
        set => StripesCheck.IsChecked = value;
    }

    /// <summary>
    /// Called when the first part is done and <see cref="FixStripes"/> is off: says so and waits for the user to choose
    /// "Fine-tune" (or tick the box): true; or "Close" (or close the window): false.
    /// </summary>
    public Task<bool> AskForStripesAsync()
    {
        _stripeChoice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StatusText.Text = "Alignment and colours done. Fine-tune the stretches between damaged areas too?";
        Progress.IsIndeterminate = false;
        PauseButton.Visibility = Visibility.Collapsed;
        StripesButton.Visibility = Visibility.Visible;
        StopButton.Content = "Close";
        StripesButton.Focus();
        return _stripeChoice.Task;
    }

    private void OnStripesClick(object sender, RoutedEventArgs e) => ChooseStripes(true);

    private void OnStripesChecked(object sender, RoutedEventArgs e)
    {
        if (_stripeChoice != null)
            ChooseStripes(true);
    }

    private void ChooseStripes(bool fix)
    {
        if (_stripeChoice is not { } choice)
            return;
        _stripeChoice = null;
        if (fix)
        {
            FixStripes = true;
            StripesButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Visible;
            StopButton.Content = "Stop";
            Progress.IsIndeterminate = true;
            StatusText.Text = "Fine-tuning the stretches between damaged areas…";
        }

        choice.TrySetResult(fix);
    }

    /// <summary>Pause/stop control shared with the searches.</summary>
    public WorkGate Gate { get; } = new();

    public CancellationToken Token => Gate.Token;

    public bool IsPaused => _paused != null;

    /// <summary>Waits while the user has paused (returns at once otherwise). Call between steps.</summary>
    public async Task WaitWhilePausedAsync()
    {
        if (_paused is not { } paused)
            return;
        await paused.Task;
    }

    public void ShowSearching(int fixes)
    {
        if (!IsPaused && !Token.IsCancellationRequested)
            StatusText.Text = fixes == 0 ? "Searching for damage…" : "Searching for more damage…";
    }

    public void ShowSearchingStrips(int strips)
    {
        if (!IsPaused && !Token.IsCancellationRequested)
            StatusText.Text = "Fine-tuning the stretches between damaged areas…";
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_paused is { } paused)
        {
            _paused = null;
            PauseButton.Content = "Pause";
            StatusText.Text = "Resuming…";
            Progress.IsIndeterminate = true;
            Gate.Resume();
            paused.TrySetResult();
            return;
        }

        _paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gate.Pause();
        PauseButton.Content = "Resume";
        StatusText.Text = "Paused. The picture shows the fixes so far; you can zoom and pan it.";
        Progress.IsIndeterminate = false;
    }

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
        e.Cancel = true; // closes by itself as soon as the running search has stopped
    }

    private void RequestStop()
    {
        if (_stripeChoice != null)
        {
            // Asked about the stripes: "Close" means no, and the window closes.
            ChooseStripes(false);
            StopButton.IsEnabled = false;
            return;
        }

        Gate.Stop();
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StatusText.Text = "Stopping…";
        Progress.IsIndeterminate = true;
        var paused = _paused;
        _paused = null;
        paused?.TrySetResult(); // a paused run goes on to the stop check
    }
}
