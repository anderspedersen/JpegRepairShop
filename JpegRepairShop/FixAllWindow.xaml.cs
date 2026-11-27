using System.ComponentModel;
using System.Windows;
using JpegRepair.Core;

namespace JpegRepairShop;

/// <summary>
/// Progress for "Fix all": shows what has been fixed so far and lets the user pause or stop. Both take effect at
/// once: the searches stop at their next checkpoint (<see cref="Gate"/>, set on the document while it runs), and
/// between steps <see cref="WaitWhilePausedAsync"/> waits. Closing the window counts as stopping. The window is not
/// modal, so the picture can be zoomed and panned while it runs or is paused.
///
/// The search speed can be changed while it runs: after the user confirms, the run is stopped and
/// <see cref="RestartRequestedAsync"/> tells the caller to undo it and start again (<see cref="BeginRun"/>).
/// </summary>
public partial class FixAllWindow : Window
{
    private bool _finished;
    private TaskCompletionSource? _paused;   // set while paused; completed on resume or stop
    private TaskCompletionSource? _question; // set while asking whether to restart with another speed
    private bool _settingSpeed;
    private FixAllSpeed _runningSpeed;

    public FixAllWindow()
    {
        InitializeComponent();
    }

    /// <summary>Pause/stop control shared with the searches (a new one for every run).</summary>
    public WorkGate Gate { get; private set; } = new();

    /// <summary>The speed chosen in the window. Setting it doesn't ask anything (for the speed to start with).</summary>
    public FixAllSpeed Speed
    {
        get => SlowRadio.IsChecked == true ? FixAllSpeed.Slow
            : VerySlowRadio.IsChecked == true ? FixAllSpeed.VerySlow
            : FixAllSpeed.Fast;
        set
        {
            _settingSpeed = true;
            (value switch { FixAllSpeed.Slow => SlowRadio, FixAllSpeed.VerySlow => VerySlowRadio, _ => FastRadio })
                .IsChecked = true;
            _settingSpeed = false;
            _runningSpeed = value;
        }
    }

    /// <summary>True once the user has confirmed a restart with another speed.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>Waits for an open restart question to be answered, then tells whether a restart was asked for.</summary>
    public async Task<bool> RestartRequestedAsync()
    {
        if (_question is { } question)
            await question.Task;
        return RestartRequested;
    }

    /// <summary>Gets the window ready for a new run with the chosen speed (after a restart).</summary>
    public void BeginRun()
    {
        Gate = new WorkGate();
        RestartRequested = false;
        _runningSpeed = Speed;
        _paused = null;
        PauseButton.Content = "Pause";
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        SetSpeedEnabled(true);
        Log.Items.Clear();
        CountText.Text = "No fixes applied yet.";
        StatusText.Text = "Searching for damage…";
        Progress.IsIndeterminate = true;
    }

    private void OnSpeedChecked(object sender, RoutedEventArgs e)
    {
        if (_settingSpeed || _finished || Speed == _runningSpeed)
            return;

        string name = Speed switch { FixAllSpeed.Slow => "Slow", FixAllSpeed.VerySlow => "Very slow", _ => "Fast" };
        _question = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = MessageBox.Show(this, $"Start Fix all again with \u201c{name}\u201d? The fixes made so far are undone.",
            "Fix all", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            RestartRequested = true;
            StopRun("Starting again…");
        }
        else
        {
            Speed = _runningSpeed;
        }

        var question = _question;
        _question = null;
        question.TrySetResult();
    }

    private void SetSpeedEnabled(bool enabled)
    {
        FastRadio.IsEnabled = enabled;
        SlowRadio.IsEnabled = enabled;
        VerySlowRadio.IsEnabled = enabled;
    }

    public CancellationToken Token => Gate.Token;

    public bool IsPaused => _paused != null;

    /// <summary>Waits while the user has paused (returns at once otherwise). Call between steps.</summary>
    public async Task WaitWhilePausedAsync()
    {
        if (_paused is not { } paused)
            return;
        await paused.Task;
    }

    /// <summary>"Fix all" is planning: <paramref name="done"/> of <paramref name="rows"/> MCU rows searched.</summary>
    public void ShowPlanning(int done, int rows)
    {
        if (IsPaused || Token.IsCancellationRequested)
            return;
        StatusText.Text = $"Finding the best inserts and removals for the whole picture: row {done} of {rows}…";
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, rows);
        Progress.Value = done;
    }

    /// <summary>"Fix all" is applying its plan: edit <paramref name="index"/> of <paramref name="count"/>.</summary>
    public void ShowApplying(int index, int count)
    {
        if (IsPaused || Token.IsCancellationRequested)
            return;
        StatusText.Text = $"Applying the inserts and removals and fixing colours: {index} of {count}…";
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, count);
        Progress.Value = index;
    }

    /// <summary>"Fix all" is searching for colour shifts where nothing is misaligned.</summary>
    public void ShowSearchingColours()
    {
        if (IsPaused || Token.IsCancellationRequested)
            return;
        StatusText.Text = "Searching for colour shifts…";
        Progress.IsIndeterminate = true;
    }

    /// <summary>"Fix all" is looking for short stretches with a wrong colour.</summary>
    public void ShowSearchingColourStretches()
    {
        if (IsPaused || Token.IsCancellationRequested)
            return;
        StatusText.Text = "Fixing the colours of short stretches…";
        Progress.IsIndeterminate = true;
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

    private void RequestStop() => StopRun("Stopping…");

    private void StopRun(string status)
    {
        Gate.Stop();
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        SetSpeedEnabled(false);
        StatusText.Text = status;
        Progress.IsIndeterminate = true;
        var paused = _paused;
        _paused = null;
        paused?.TrySetResult(); // a paused run goes on to the stop check
    }
}
