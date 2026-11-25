using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpegRepair.Core;
using Microsoft.Win32;

namespace JpegRepairShop.ViewModels;

/// <param name="McuIndex">MCU the issue refers to (position in the original decode), or -1 for file-level warnings.</param>
public sealed record IssueItem(int McuIndex, string Message)
{
    public string Location => McuIndex < 0 ? "File" : $"MCU {McuIndex}";
}

/// <summary>
/// One colour channel (Y, Cb or Cr) in the DC editor: the DC values of the selected MCU's blocks for that
/// channel, how far they were moved from the decoded values, and commands to move them from the selected
/// MCU onwards.
/// </summary>
public sealed partial class DcChannel(MainViewModel owner, int component, string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>DC value of each block of this channel in the selected MCU, e.g. "23  25".</summary>
    [ObservableProperty]
    private string _values = "–";

    /// <summary>Change from the originally decoded value, e.g. "+3", or empty if unchanged.</summary>
    [ObservableProperty]
    private string _change = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IncreaseCommand), nameof(DecreaseCommand))]
    private bool _isAvailable;

    /// <param name="steps">Number of DC steps (the buttons pass more the longer they are held).</param>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void Increase(int? steps) => owner.NudgeDc(component, Math.Max(1, steps ?? 1));

    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void Decrease(int? steps) => owner.NudgeDc(component, -Math.Max(1, steps ?? 1));

    /// <summary>For the mouse wheel.</summary>
    public void Nudge(int steps)
    {
        if (IsAvailable)
            owner.NudgeDc(component, steps);
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(SelectedInfo), nameof(HoverInfo), nameof(ShowWelcome), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    private JpegDocument? _document;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowWelcome))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedInfo))]
    [NotifyCanExecuteChangedFor(nameof(InsertBlankCommand), nameof(RemoveMcuCommand), nameof(FixColorsCommand), nameof(ToggleBadCommand))]
    private int _selectedMcu = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverInfo))]
    private int _hoverMcu = -1;

    [ObservableProperty]
    private bool _showGrid = true;

    /// <summary>Show bad MCUs (filled and outlined) and the other problem markings on the picture.</summary>
    [ObservableProperty]
    private bool _showBadMcus;

    /// <summary>Outline the section around the selected MCU.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownSection))]
    private bool _showSection;

    /// <summary>The section around the selected MCU (good MCUs in read order, see JpegDocument.FindSection), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedInfo), nameof(ShownSection))]
    [NotifyCanExecuteChangedFor(nameof(FixSectionColorsCommand))]
    private McuSection? _section;

    public McuSection? ShownSection => ShowSection ? Section : null;

    /// <summary>MCUs that look bad (2+ rugged edges to their neighbours, one of them above or below).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(SelectedInfo), nameof(HoverInfo))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBadCommand))]
    private BadMcuMap? _badMcus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyCanExecuteChangedFor(nameof(InsertBlankCommand), nameof(RemoveMcuCommand), nameof(FixColorsCommand),
        nameof(FixSectionColorsCommand), nameof(FixAllCommand), nameof(UndoCommand), nameof(RedoCommand),
        nameof(ToggleBadCommand), nameof(FillBadCommand), nameof(ReloadCommand))]
    private bool _isBusy;

    /// <summary>Text shown while <see cref="IsBusy"/>.</summary>
    [ObservableProperty]
    private string _busyText = "Loading…";

    [ObservableProperty]
    private IssueItem? _selectedIssue;

    /// <summary>Incremented after every edit; the image view and previews re-render when it changes.</summary>
    [ObservableProperty]
    private int _revision;

    /// <summary>Feedback from the last action, shown in the status bar.</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Shown when the header is broken but the file may still hold image data: offers to borrow a header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BorrowHeaderCommand))]
    private bool _canBorrowHeader;

    // True after a header was borrowed and not yet saved (the document itself is unedited, but the file isn't).
    private bool _unsavedHeaderFix;

    public ObservableCollection<IssueItem> Issues { get; } = [];

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    private bool HasUnsavedChanges => Document?.IsModified == true || _unsavedHeaderFix;

    public ObservableCollection<DcChannel> DcChannels { get; } = [];

    public string Title
    {
        get
        {
            if (FilePath == null)
                return "JPEG Repair Shop";
            string modified = HasUnsavedChanges ? "* " : "";
            return $"{modified}{Path.GetFileName(FilePath)} - JPEG Repair Shop";
        }
    }

    public bool HasError => ErrorMessage != null;

    public bool ShowWelcome => Document == null && ErrorMessage == null && !IsBusy;

    public string UndoToolTip => Document?.UndoDescription is { } d ? $"Undo: {d} (Ctrl+Z)" : "Undo (Ctrl+Z)";

    public string RedoToolTip => Document?.RedoDescription is { } d ? $"Redo: {d} (Ctrl+Y)" : "Redo (Ctrl+Y)";

    public string Summary
    {
        get
        {
            if (Document is not { } doc)
                return "";

            var frame = doc.Frame;
            int ok = 0, corrupt = 0, missing = 0, blank = 0, filled = 0;
            for (int i = 0; i < frame.McuCount; i++)
            {
                switch (doc.GetStatus(i))
                {
                    case McuStatus.Ok: ok++; break;
                    case McuStatus.Corrupt: corrupt++; break;
                    case McuStatus.Blank: blank++; break;
                    case McuStatus.Filled: filled++; break;
                    default: missing++; break;
                }
            }

            string text = $"{frame.Width} × {frame.Height}   ·   {SamplingName(frame)}   ·   " +
                          $"MCU {frame.McuWidth}×{frame.McuHeight}, {frame.McusPerRow}×{frame.McuRows} = {frame.McuCount}   ·   " +
                          $"OK {ok}   ·   corrupt {corrupt}   ·   missing {missing}";
            if (blank > 0)
                text += $"   ·   blank {blank}";
            if (filled > 0)
                text += $"   ·   filled {filled}";
            if (doc.OverflowCount > 0)
                text += $"   ·   overflow {doc.OverflowCount}";
            if (BadMcus != null)
            {
                text += $"   ·   looks bad {BadMcus.Count}";
                if (BadMcus.MarkCount > 0)
                    text += $" (marked by hand: {BadMcus.MarkedBadCount} bad, {BadMcus.MarkedNotBadCount} not bad)";
            }
            if (doc.File.RestartInterval > 0)
                text += $"   ·   restart every {doc.File.RestartInterval} MCUs";
            return text;
        }
    }

    // DC values are shown in the DC editor.
    public string SelectedInfo
    {
        get
        {
            string text = Describe(SelectedMcu, includeDc: false);
            if (text.Length > 0 && Section is { } section)
                text += $"\nSection: MCUs {section.First}–{section.Last} ({section.Count} MCUs)";
            return text;
        }
    }

    public string HoverInfo => Describe(HoverMcu, includeDc: true);

    // ---- Files -------------------------------------------------------------------------------------------

    public async Task OpenPathAsync(string path)
    {
        if (IsBusy)
        {
            StatusMessage = "Wait until the current task (e.g. Fix all) is done, or stop it.";
            return;
        }

        if (Directory.Exists(path))
        {
            StatusMessage = "That is a folder; open a JPEG file instead.";
            return;
        }

        if (!ConfirmDiscardChanges())
            return;

        await LoadAsync(path);
    }

    /// <summary>Asks before throwing away unsaved edits. Returns false if the user cancels.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (Document == null || !HasUnsavedChanges)
            return true;

        var answer = MessageBox.Show(
            $"{Path.GetFileName(FilePath)} has unsaved repairs. Save them first?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => SaveAsInternal(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open JPEG",
            Filter = "JPEG images|*.jpg;*.jpeg;*.jpe;*.jfif|All files|*.*",
        };
        if (FilePath != null)
            dialog.InitialDirectory = Path.GetDirectoryName(FilePath);

        if (dialog.ShowDialog() == true)
            await OpenPathAsync(dialog.FileName);
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task Reload()
    {
        if (ConfirmDiscardChanges())
            await LoadAsync(FilePath!);
    }

    private bool CanReload() => FilePath != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveAs() => SaveAsInternal();

    private bool CanSave() => Document != null;

    private bool SaveAsInternal()
    {
        if (Document is not { } doc || FilePath == null)
            return false;

        var dialog = new SaveFileDialog
        {
            Title = "Save repaired JPEG",
            Filter = "JPEG image|*.jpg",
            InitialDirectory = Path.GetDirectoryName(FilePath),
            FileName = Path.GetFileNameWithoutExtension(FilePath) + "_fixed.jpg",
        };
        if (dialog.ShowDialog() != true)
            return false;

        try
        {
            File.WriteAllBytes(dialog.FileName, doc.Save());
            _unsavedHeaderFix = false;
            StatusMessage = $"Saved {Path.GetFileName(dialog.FileName)}";
            OnPropertyChanged(nameof(Title));
            return true;
        }
        catch (Exception ex) when (ex is JpegEncodeException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ---- Repairs -----------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertBlank()
    {
        long ms = Timed(() => Document!.InsertBlank(SelectedMcu));
        StatusMessage = $"Inserted a blank MCU at {SelectedMcu}; everything after it moved right. ({ms} ms)";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveMcu()
    {
        long ms = Timed(() => Document!.Remove(SelectedMcu));
        StatusMessage = $"Removed MCU {SelectedMcu}; everything after it moved left. ({ms} ms)";
    }

    [RelayCommand(CanExecute = nameof(CanFixColors))]
    private void FixColors()
    {
        var doc = Document!;
        int[] deltas = [];
        long ms = Timed(() => deltas = doc.AutoFixColors(SelectedMcu));
        var names = ComponentNames(doc.Frame);
        StatusMessage = deltas.Any(d => d != 0)
            ? $"Colour shift corrected from MCU {SelectedMcu}: " +
              string.Join(", ", deltas.Select((d, c) => $"{names[c]} {d:+0;-0;0}")) + $" DC steps. ({ms} ms)"
            : $"No colour shift found between MCU {SelectedMcu}'s row and the row above.";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var doc = Document!;
        StatusMessage = $"Undone: {doc.UndoDescription}";
        doc.Undo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var doc = Document!;
        StatusMessage = $"Redone: {doc.RedoDescription}";
        doc.Redo();
    }

    /// <summary>Runs an action (an edit, including the re-render it triggers) and returns how long it took.</summary>
    private static long Timed(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.ElapsedMilliseconds;
    }

    private bool HasSelection() => !IsBusy && Document is { } doc && SelectedMcu >= 0 && SelectedMcu < doc.Frame.McuCount;

    private bool CanFixColors() => HasSelection() && SelectedMcu >= Document!.Frame.McusPerRow;

    private bool CanFixSectionColors() => HasSelection() && Section is { First: > 0 };

    /// <summary>Rows above a section that "Fix section colours" compares with; asked each time, the last answer remembered.</summary>
    private int _sectionReferenceRows = JpegDocument.SectionHistogramRows;

    /// <summary>
    /// Asks how many rows above the selected section to compare with, then matches the section's colours to the good MCUs
    /// there (see JpegDocument.FixSectionColours).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFixSectionColors))]
    private void FixSectionColors()
    {
        var doc = Document!;
        if (Section is not { } shown)
            return;
        int rowsAbove = Math.Max(1, (shown.First + doc.Frame.McusPerRow - 1) / doc.Frame.McusPerRow);
        var dialog = new SectionColoursWindow(_sectionReferenceRows, rowsAbove, shown.First)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
            return;
        _sectionReferenceRows = dialog.Rows;
        int rows = dialog.Rows;

        (McuSection Section, int[] Deltas)? result = null;
        long ms = Timed(() => result = doc.FixSectionColours(SelectedMcu, rows));
        if (result is not { } fixedSection)
        {
            StatusMessage = "No good MCUs before this section to compare its colours with.";
            return;
        }

        var (section, deltas) = fixedSection;
        var names = ComponentNames(doc.Frame);
        StatusMessage = deltas.Any(d => d != 0)
            ? $"Section colours corrected from MCU {section.First} (compared with {rows} rows above): " +
              string.Join(", ", deltas.Select((d, c) => $"{names[c]} {d:+0;-0;0}")) + $" DC steps. ({ms} ms)"
            : $"The colours of section {section.First}–{section.Last} already match the {rows} rows before it.";
    }

    private bool CanUndo() => !IsBusy && Document?.CanUndo == true;

    private bool CanRedo() => !IsBusy && Document?.CanRedo == true;

    private bool CanFixAll() => !IsBusy && Document is { } doc && doc.Frame.McuRows > 1;

    /// <summary>
    /// Whether "Fix all" goes on with fine-tuning (the stripes, its last, optional step); off at first, then the last
    /// choice is remembered.
    /// </summary>
    private bool _fixAllStripes;

    /// <summary>Most fixes "Fix all" applies before giving up (a safety net; real pictures need far fewer).</summary>
    private const int MaxAutomaticFixes = 1000;

    /// <summary>
    /// Repairs the whole picture: repeatedly finds the worst damage (misalignment, or a colour shift on its own) and
    /// fixes it (inserts or removes MCUs and fixes the colours), until nothing more is found. Then, as the last step,
    /// fixes the stripes of the MCU stream (short good stripes moved and/or colour-corrected, bad stripes colour-corrected,
    /// see JpegDocument.FindNextStripFix), top to bottom, each as soon as it is found. The stripes are optional: a box in
    /// the window (changeable while the first part runs); if it is off, the window asks when the first part is done. Shows progress in a window that can stop it. Every fix, and every strip,
    /// is its own undo step. The window is not modal and has a pause button: while it runs, the picture can be zoomed and
    /// panned, but nothing can be edited (IsBusy).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFixAll))]
    private async Task FixAll()
    {
        var doc = Document!;
        var window = new FixAllWindow { Owner = Application.Current.MainWindow, FixStripes = _fixAllStripes };
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        var watch = Stopwatch.StartNew();
        int fixes = 0, strips = 0;
        string reason = "nothing more found";
        bool stopped = false;
        PerfLog.Begin($"Fix all: {Path.GetFileName(FilePath ?? "")}, {doc.Frame.Width}×{doc.Frame.Height}, " +
                      $"{doc.Frame.McusPerRow}×{doc.Frame.McuRows} MCUs");
        Task WaitWhilePausedAsync() => window.WaitWhilePausedAsync();

        window.Loaded += async (_, _) =>
        {
            // The searches pause and stop at their own checkpoints (see JpegDocument.Gate), not only between steps.
            doc.Gate = window.Gate;
            try
            {
                var applied = new HashSet<string>();
                while (true)
                {
                    await WaitWhilePausedAsync();
                    if (window.Token.IsCancellationRequested)
                    {
                        reason = "stopped";
                        stopped = true;
                        break;
                    }

                    if (fixes >= MaxAutomaticFixes)
                    {
                        reason = $"stopped after {MaxAutomaticFixes} fixes";
                        stopped = true;
                        break;
                    }

                    window.ShowSearching(fixes);
                    var searchStart = Stopwatch.GetTimestamp();
                    var result = await Task.Run(doc.FindMisalignment);
                    PerfLog.Add("Alignment: search", Stopwatch.GetElapsedTime(searchStart));
                    if (result == null)
                        break;
                    if (window.Token.IsCancellationRequested)
                    {
                        reason = "stopped";
                        stopped = true;
                        break;
                    }

                    // The same fix at the same place again would go round in circles.
                    string key = $"{result.Index}:{result.Shift}:{string.Join(",", result.ColourFix ?? Array.Empty<int>())}";
                    if (!applied.Add(key))
                    {
                        reason = "the same fix came up again";
                        break;
                    }

                    int[] colourFix;
                    using (PerfLog.Time("Alignment: apply fix (incl. render)"))
                        colourFix = doc.AlignAndFixColors(result.Index, result.Shift);
                    SelectedMcu = result.Index;
                    fixes++;
                    window.AddFix(fixes, DescribeFix(doc, result, colourFix));
                }

                // Last step: stripes of the MCU stream, in one pass from the top of the picture down. Each search looks
                // for the first stripe (from the one holding the end of the last fix on) that needs a fix; that fix is
                // applied at once (its own undo step) and the pass goes on below it, so every stripe is judged against
                // rows above it that are already fixed. Every search detects the bad MCUs afresh (each fix re-renders,
                // which drops the cached detection); stripes whose surroundings haven't changed come from the
                // document's cache.
                PerfLog.Line($"Alignment phase done: {fixes} fixes, {reason}");

                // The stripes are optional: on if the box is ticked (it can be changed while the first part runs);
                // otherwise the window asks now, and the user can still go on with them.
                bool doStripes = !stopped && !window.Token.IsCancellationRequested &&
                                 (window.FixStripes || await window.AskForStripesAsync());
                var doneStrips = new HashSet<string>();
                int from = 0;
                long passStart = Stopwatch.GetTimestamp();
                while (doStripes && !stopped)
                {
                    await WaitWhilePausedAsync();
                    if (window.Token.IsCancellationRequested || fixes >= MaxAutomaticFixes)
                    {
                        reason = window.Token.IsCancellationRequested ? "stopped" : $"stopped after {MaxAutomaticFixes} fixes";
                        break;
                    }

                    window.ShowSearchingStrips(strips);
                    int start = from;
                    StripFix? strip;
                    using (PerfLog.Time("Strips: find next fix"))
                        strip = await Task.Run(() => doc.FindNextStripFix(start, doneStrips, window.Token));
                    if (strip == null)
                        break;

                    from = strip.RegionLast + 1;
                    doneStrips.Add(strip.Key);
                    using (PerfLog.Time("Strips: apply fix (incl. render)"))
                        doc.ApplyStripFix(strip);
                    SelectedMcu = strip.First + strip.Shift;
                    fixes++;
                    strips++;
                    window.AddFix(fixes, $"{doc.DescribeStripFix(strip)}; seams {strip.CostBefore:0.0} → {strip.CostAfter:0.0} levels");
                }

                if (doStripes)
                    PerfLog.Line($"Strip pass done: {strips} fixes, {Stopwatch.GetElapsedTime(passStart).TotalSeconds:0.00} s");
            }
            catch (Exception ex) when (WorkGate.IsCancellation(ex))
            {
                reason = "stopped";
            }
            catch (Exception ex)
            {
                reason = "failed: " + ex.Message;
            }
            finally
            {
                _fixAllStripes = window.FixStripes;
                doc.Gate = null;
                PerfLog.Line($"Done: {fixes} fixes ({strips} strips), {reason}; paused {window.Gate.PausedTime.TotalSeconds:0.0} s");
                window.Finish();
            }
        };

        IsBusy = true;
        BusyText = "";
        try
        {
            window.Show();
            await closed.Task;
        }
        finally
        {
            IsBusy = false;
            BusyText = "Loading…";
        }

        string summary = fixes switch
        {
            0 => "no fixes applied",
            1 => "1 fix applied (undo with Ctrl+Z)",
            _ => $"{fixes} fixes applied (each is a separate undo step)",
        };
        if (strips > 0)
            summary += strips == 1 ? ", 1 of them fine-tuning" : $", {strips} of them fine-tuning";
        StatusMessage = $"Fix all: {summary}; {reason}. ({watch.Elapsed.TotalSeconds:0.0} s)";

        if (PerfLog.Enabled)
            new PerfLogWindow(PerfLog.Report()) { Owner = Application.Current.MainWindow }.Show();
    }

    // ---- Filling -------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills every bad, corrupt and blank MCU from the picture around it (exemplar-based inpainting), as one undo
    /// step. Runs on a worker thread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFillBad))]
    private Task FillBad() => FillAsync(Document!.FindFillTargets());

    private bool CanFillBad() => !IsBusy && Document != null;

    private async Task FillAsync(IReadOnlyList<int> list)
    {
        var doc = Document!;
        if (list.Count == 0)
        {
            StatusMessage = "Nothing to fill: no bad, corrupt or blank MCUs.";
            return;
        }

        IsBusy = true;
        BusyText = list.Count == 1 ? "Filling 1 MCU…" : $"Filling {list.Count} MCUs…";
        try
        {
            var watch = Stopwatch.StartNew();
            var plan = await Task.Run(() => doc.PrepareFill(list));
            int filled = doc.ApplyFill(plan);
            StatusMessage = filled == 1
                ? $"Filled MCU {plan.Indices[0]} from the picture around it. ({watch.Elapsed.TotalSeconds:0.0} s)"
                : $"Filled {filled} MCUs from the picture around them, as one undo step. ({watch.Elapsed.TotalSeconds:0.0} s)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or JpegFormatException)
        {
            StatusMessage = "Fill failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "Loading…";
        }
    }

    private static string DescribeFix(JpegDocument doc, MisalignmentResult result, IReadOnlyList<int> colourFix)
    {
        var frame = doc.Frame;
        int shift = result.Shift;
        string where = $"MCU {result.Index} (column {result.Index % frame.McusPerRow}, row {result.Index / frame.McusPerRow})";
        string colours = colourFix.Any(d => d != 0)
            ? $"colours fixed (DC {string.Join(", ", colourFix.Select(d => d.ToString("+0;-0;0")))})"
            : "colours already matched";
        if (result.Estimate is { } estimate && shift != 0)
        {
            string what = shift > 0 ? $"inserted {shift} blank MCU(s)" : $"removed {-shift} MCU(s)";
            string how = result.FromLineStarts ? " (found from where lines start; the row above was unusable)" : "";
            return $"Misalignment at {where}: {what}, fit {estimate.FitWithoutShift:P0} → {estimate.Fit:P0}{how}; {colours}";
        }

        return $"Colour shift at {where}: {colours}";
    }

    internal void NudgeDc(int component, int delta)
    {
        if (!HasSelection())
            return;

        var doc = Document!;
        var deltas = new int[doc.Frame.Components.Count];
        deltas[component] = delta;
        // Consecutive nudges at the same MCU become one undo step.
        doc.AdjustDc(SelectedMcu, deltas, mergeWithPrevious: true);
        StatusMessage = doc.UndoDescription ?? "";
    }

    // ---- Overriding bad-MCU detection ------------------------------------------------------------------

    /// <summary>True if the selected MCU can be marked by hand (decoded without errors, or flagged by detection).</summary>
    public bool CanMarkSelected => Document is { } doc && (doc.CanMark(SelectedMcu, McuMark.Bad) || doc.CanMark(SelectedMcu, McuMark.NotBad));

    /// <summary>True if the selected MCU can be marked as bad (decoded without errors).</summary>
    public bool CanMarkSelectedBad => Document?.CanMark(SelectedMcu, McuMark.Bad) == true;

    /// <summary>True if the selected MCU can be marked as not bad (detection flags it, or it has a decoding error).</summary>
    public bool CanMarkSelectedNotBad => Document?.CanMark(SelectedMcu, McuMark.NotBad) == true;

    /// <summary>The user's mark on the selected MCU (Auto: detection decides).</summary>
    public McuMark SelectedMark
    {
        get => BadMcus?.Mark(SelectedMcu) ?? McuMark.Auto;
        set
        {
            if (!IsBusy && Document is { } doc && value != SelectedMark && doc.SetMark(SelectedMcu, value))
            {
                StatusMessage = value switch
                {
                    McuMark.Bad => $"MCU {SelectedMcu} marked as bad: it ends sections and is left out of colour fixes.",
                    McuMark.NotBad => $"MCU {SelectedMcu} marked as not bad: it counts as good for sections and colour fixes.",
                    _ => $"MCU {SelectedMcu} is no longer marked; detection decides.",
                };
            }

            NotifyMarkProperties();
        }
    }

    // For the three radio buttons.
    public bool SelectedMarkAuto
    {
        get => SelectedMark == McuMark.Auto;
        set
        {
            if (value)
                SelectedMark = McuMark.Auto;
        }
    }

    public bool SelectedMarkBad
    {
        get => SelectedMark == McuMark.Bad;
        set
        {
            if (value)
                SelectedMark = McuMark.Bad;
        }
    }

    public bool SelectedMarkNotBad
    {
        get => SelectedMark == McuMark.NotBad;
        set
        {
            if (value)
                SelectedMark = McuMark.NotBad;
        }
    }

    private void NotifyMarkProperties()
    {
        OnPropertyChanged(nameof(CanMarkSelected));
        OnPropertyChanged(nameof(CanMarkSelectedBad));
        OnPropertyChanged(nameof(CanMarkSelectedNotBad));
        OnPropertyChanged(nameof(SelectedMark));
        OnPropertyChanged(nameof(SelectedMarkAuto));
        OnPropertyChanged(nameof(SelectedMarkBad));
        OnPropertyChanged(nameof(SelectedMarkNotBad));
        ToggleBadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Flips whether the selected MCU counts as bad: marks it as bad or not bad, or removes the mark when detection
    /// already gives the wanted answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleBad))]
    private void ToggleBad()
    {
        if (BadMcus is not { } map || Document is not { } doc)
            return;

        // A decoding error counts as bad; the only mark that changes that is "not bad".
        if (doc.GetStatus(SelectedMcu) == McuStatus.Corrupt)
        {
            SelectedMark = map.Mark(SelectedMcu) == McuMark.NotBad ? McuMark.Auto : McuMark.NotBad;
            return;
        }

        bool detected = map.IsDetected(SelectedMcu);
        SelectedMark = map.IsBad(SelectedMcu)
            ? detected ? McuMark.NotBad : McuMark.Auto
            : detected ? McuMark.Auto : McuMark.Bad;
    }

    private bool CanToggleBad() => !IsBusy && CanMarkSelected;

    partial void OnBadMcusChanged(BadMcuMap? value) => NotifyMarkProperties();

    private void OnMarksChanged(object? sender, EventArgs e) => RefreshBadMcus();

    private void RefreshDcChannels()
    {
        var doc = Document;
        bool selected = HasSelection();
        bool decoded = selected && SelectedMcu < doc!.Count && doc.GetStatus(SelectedMcu) != McuStatus.Missing;

        foreach (var channel in DcChannels)
            channel.IsAvailable = selected;

        if (!decoded)
        {
            foreach (var channel in DcChannels)
            {
                channel.Values = "–";
                channel.Change = "";
            }

            return;
        }

        var layout = doc!.Layout;
        bool blank = doc[SelectedMcu].IsBlank;
        for (int c = 0; c < DcChannels.Count; c++)
        {
            var values = new List<int>();
            int change = 0;
            for (int b = 0; b < layout.BlocksPerMcu; b++)
            {
                if (layout.Blocks[b].ComponentIndex != c)
                    continue;
                if (values.Count == 0)
                    change = doc.GetDcChange(SelectedMcu, b);
                values.Add(doc.GetDc(SelectedMcu, b));
            }

            DcChannels[c].Values = blank ? "blank" : string.Join("  ", values);
            DcChannels[c].Change = change == 0 ? "" : change.ToString("+0;-0");
        }
    }

    partial void OnSelectedMcuChanged(int value)
    {
        NotifyMarkProperties();
        RefreshDcChannels();
        RefreshSection();
    }

    private void RefreshSection() => Section = Document is { } doc && SelectedMcu >= 0 ? doc.FindSection(SelectedMcu) : null;

    /// <summary>Re-runs bad-MCU detection (after loading and every edit, undo and redo).</summary>
    private void RefreshBadMcus()
    {
        var watch = Stopwatch.StartNew();
        BadMcus = Document?.FindBadMcus();
        RefreshSection();
        Debug.WriteLine($"Bad-MCU detection: {watch.ElapsedMilliseconds} ms");
    }

    private void OnDocumentEdited(object? sender, int firstMcu)
    {
        RefreshBadMcus();
        Revision++;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedInfo));
        OnPropertyChanged(nameof(HoverInfo));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RefreshDcChannels();
    }

    partial void OnDocumentChanged(JpegDocument? oldValue, JpegDocument? newValue)
    {
        if (oldValue != null)
        {
            oldValue.Changed -= OnDocumentEdited;
            oldValue.MarksChanged -= OnMarksChanged;
        }

        if (newValue != null)
        {
            newValue.Changed += OnDocumentEdited;
            newValue.MarksChanged += OnMarksChanged;
        }


        DcChannels.Clear();
        if (newValue != null)
        {
            var names = ComponentNames(newValue.Frame);
            for (int c = 0; c < names.Length; c++)
                DcChannels.Add(new DcChannel(this, c, names[c]));
        }

        RefreshDcChannels();
        RefreshBadMcus();

        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        InsertBlankCommand.NotifyCanExecuteChanged();
        RemoveMcuCommand.NotifyCanExecuteChanged();
        FixColorsCommand.NotifyCanExecuteChanged();
        FixSectionColorsCommand.NotifyCanExecuteChanged();
        FixAllCommand.NotifyCanExecuteChanged();
        FillBadCommand.NotifyCanExecuteChanged();
        NotifyMarkProperties();
    }

    partial void OnSelectedIssueChanged(IssueItem? value)
    {
        if (value != null && Document is { } doc && value.McuIndex >= 0 && value.McuIndex < doc.Frame.McuCount)
            SelectedMcu = value.McuIndex;
    }

    // ---- Loading ------------------------------------------------------------------------------------------

    private async Task LoadAsync(string path)
    {
        IsBusy = true;
        SelectedIssue = null;
        SelectedMcu = -1;
        HoverMcu = -1;
        StatusMessage = "";
        CanBorrowHeader = false;
        ErrorDetail = null;
        _unsavedHeaderFix = false;
        try
        {
            // Decode and do the first full render on a worker thread so the window stays responsive.
            var (doc, decodeMs, renderMs) = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var loaded = JpegDocument.Load(path);
                long decoded = watch.ElapsedMilliseconds;
                loaded.EnsureRendered();
                loaded.FindBadMcus(); // cached; the UI thread then gets it for free
                return (loaded, decoded, watch.ElapsedMilliseconds - decoded);
            });

            ShowDocument(path, doc, []);
            StatusMessage = $"Decoded in {decodeMs} ms, rendered in {renderMs} ms.";
        }
        catch (Exception ex) when (ex is JpegFormatException or IOException or UnauthorizedAccessException)
        {
            FilePath = path;
            Document = null;
            Issues.Clear();
            ErrorMessage = $"Cannot open {Path.GetFileName(path)}\n\n{ex.Message}";

            // A broken header (not an unsupported format) in a file that may still hold image data:
            // offer to borrow the header from another picture.
            if (ex is JpegFormatException and not JpegNotSupportedException && await Task.Run(() => MightHoldImageData(path)))
            {
                CanBorrowHeader = true;
                ErrorDetail = BorrowHeaderExplanation;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowDocument(string path, JpegDocument doc, IEnumerable<string> notes)
    {
        ErrorMessage = null;
        ErrorDetail = null;
        CanBorrowHeader = false;
        FilePath = path;
        Document = doc;

        Issues.Clear();
        foreach (var note in notes)
            Issues.Add(new IssueItem(-1, note));
        foreach (var warning in doc.File.Warnings)
            Issues.Add(new IssueItem(-1, warning));
        foreach (var issue in doc.Scan.Issues)
            Issues.Add(new IssueItem(issue.McuIndex, issue.Message));
        OnPropertyChanged(nameof(Title));
    }

    private static bool MightHoldImageData(string path)
    {
        try
        {
            return HeaderTransplant.MightContainImageData(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Borrowing a header from other pictures ---------------------------------------------------------------

    private const string BorrowHeaderExplanation =
        "The header — the part before the image data that describes the picture size, colour layout and " +
        "compression tables — is damaged or missing, but the file is big enough to still hold image data.\n\n" +
        "You can borrow the header from another picture. For the best result, choose pictures:\n" +
        "  •  from the same camera or phone,\n" +
        "  •  with the same image size, quality and orientation (portrait/landscape) settings,\n" +
        "  •  ideally taken around the same time.\n\n" +
        "You can select several. They are tried in name order, and the first one whose image data starts at " +
        "exactly the same position as in this file is used.";

    private sealed record ReferenceSearch(
        byte[] Damaged,
        int Tried,
        List<string> Unusable,
        (string Path, byte[] Bytes, HeaderCandidate Candidate)? SameOffset,
        (string Path, byte[] Bytes, HeaderCandidate Candidate)? OtherOffset,
        (string Path, byte[] Bytes, HeaderCandidate Candidate)? FirstUsable);

    [RelayCommand(CanExecute = nameof(CanBorrowHeader))]
    private async Task BorrowHeader()
    {
        string damagedPath = FilePath!;
        var dialog = new OpenFileDialog
        {
            Title = "Choose one or more pictures from the same camera, taken with the same settings",
            Filter = "JPEG images|*.jpg;*.jpeg;*.jpe;*.jfif|All files|*.*",
            Multiselect = true,
            InitialDirectory = Path.GetDirectoryName(damagedPath),
        };
        if (dialog.ShowDialog() != true)
            return;

        var references = dialog.FileNames
            .Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(damagedPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (references.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var search = await Task.Run(() => SearchReferences(damagedPath, references));
            await ApplyReferenceAsync(damagedPath, search);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Borrow header", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static ReferenceSearch SearchReferences(string damagedPath, List<string> references)
    {
        var damaged = File.ReadAllBytes(damagedPath);
        var unusable = new List<string>();
        (string, byte[], HeaderCandidate)? other = null, firstUsable = null;
        int tried = 0;

        foreach (var path in references)
        {
            tried++;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unusable.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            var candidate = HeaderTransplant.Evaluate(damaged, bytes);
            switch (candidate.Match)
            {
                case HeaderMatch.SameOffset:
                    return new ReferenceSearch(damaged, tried, unusable, (path, bytes, candidate), other, firstUsable ?? (path, bytes, candidate));
                case HeaderMatch.OtherOffset:
                    other ??= (path, bytes, candidate);
                    firstUsable ??= (path, bytes, candidate);
                    break;
                case HeaderMatch.None:
                    firstUsable ??= (path, bytes, candidate);
                    break;
                default:
                    unusable.Add($"{Path.GetFileName(path)}: {candidate.Message}");
                    break;
            }
        }

        return new ReferenceSearch(damaged, tried, unusable, null, other, firstUsable);
    }

    private async Task ApplyReferenceAsync(string damagedPath, ReferenceSearch search)
    {
        string name = Path.GetFileName(damagedPath);
        byte[]? combined = null;
        string? note = null;

        if (search.SameOffset is { } same)
        {
            combined = HeaderTransplant.Combine(same.Bytes, search.Damaged, same.Candidate);
            note = $"Header borrowed from {Path.GetFileName(same.Path)}: its image data starts at the same position " +
                   $"(offset 0x{same.Candidate.DamagedScanOffset:X}). Save to keep the repaired file.";
        }
        else if (search.OtherOffset is { } other && Ask(
                     $"None of the {search.Tried} picture(s) has its image data starting at the same position as in {name}.\n\n" +
                     $"{Path.GetFileName(other.Path)} has the same start-of-scan header, but at another position " +
                     $"(0x{other.Candidate.DamagedScanOffset:X} in {name}, 0x{other.Candidate.ReferenceScanOffset:X} in the reference). " +
                     "That happens when the pictures' EXIF data differ in size. But almost all JPEGs share this header, " +
                     "so this is only a weak match.\n\nTry it anyway?"))
        {
            combined = HeaderTransplant.Combine(other.Bytes, search.Damaged, other.Candidate);
            note = $"Header borrowed from {Path.GetFileName(other.Path)}, joined at the matching start-of-scan header " +
                   $"(offset 0x{other.Candidate.DamagedScanOffset:X}; weak match). Save to keep the repaired file.";
        }
        else if (search.FirstUsable is { } first && first.Candidate.ReferenceScanOffset < search.Damaged.Length && Ask(
                     (search.OtherOffset == null
                         ? $"None of the {search.Tried} picture(s) matches: {name} has no start-of-scan header where they expect one.\n\n"
                         : "") +
                     $"Try anyway, assuming the image data starts at the same position as in {Path.GetFileName(first.Path)} " +
                     $"(offset 0x{first.Candidate.ReferenceScanOffset:X})? This only works if the damaged header had exactly the same size."))
        {
            combined = HeaderTransplant.CombineAtSameOffset(first.Bytes, search.Damaged, first.Candidate);
            note = $"Header borrowed from {Path.GetFileName(first.Path)} without a start-of-scan check " +
                   $"(joined at offset 0x{first.Candidate.ReferenceScanOffset:X}). Save to keep the repaired file.";
        }
        else if (search.FirstUsable == null)
        {
            MessageBox.Show(
                "None of the selected files can be used as a reference:\n\n" + string.Join("\n", search.Unusable),
                "Borrow header", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (combined == null || note == null)
            return;

        var repaired = combined;
        try
        {
            var doc = await Task.Run(() =>
            {
                var decoded = JpegDocument.Decode(repaired);
                decoded.EnsureRendered();
                return decoded;
            });
            ShowDocument(damagedPath, doc, [note]);
            _unsavedHeaderFix = true;
            OnPropertyChanged(nameof(Title));
            StatusMessage = note;
        }
        catch (JpegFormatException ex)
        {
            MessageBox.Show($"Even with the borrowed header the file can't be decoded:\n\n{ex.Message}",
                "Borrow header", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool Ask(string question) =>
        MessageBox.Show(question, "Borrow header", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ---- Text helpers ---------------------------------------------------------------------------------------

    private string Describe(int index, bool includeDc)
    {
        if (Document is not { } doc || index < 0 || index >= doc.Frame.McuCount)
            return "";

        var frame = doc.Frame;
        string text = $"MCU {index}  (column {index % frame.McusPerRow}, row {index / frame.McusPerRow})\n";
        if (index >= doc.Count || doc.GetStatus(index) == McuStatus.Missing)
            return text + "Missing (not decoded)";

        var mcu = doc[index];
        if (mcu.IsBlank)
            return text + "Blank (inserted)";

        if (mcu.Status == McuStatus.Filled && mcu.IsInserted)
        {
            text += "Filled (made up from the picture around it), in place of an inserted blank MCU";
        }
        else
        {
            int fileOffset = doc.File.Entropy.ToSourceOffset((int)(mcu.BitOffset >> 3));
            text += $"{mcu.Status}, originally MCU {mcu.SourceIndex}, {mcu.BitLength} bits at file offset 0x{fileOffset:X}";
            if (mcu.Status == McuStatus.Filled)
                text += "\nContents made up from the picture around it";
        }
        if (BadMcus is { } bad)
        {
            string edges = DescribeEdges(bad.RuggedEdges(index));
            text += bad.Mark(index) switch
            {
                McuMark.Bad when bad.IsDetected(index) => $"\nMarked as bad (detected too: rugged edge {edges})",
                McuMark.Bad => "\nMarked as bad (detection finds it fine)",
                McuMark.NotBad when !bad.IsDetected(index) => "\nMarked as not bad: its decoded data is used despite the decoding error",
                McuMark.NotBad => $"\nMarked as not bad (detected: rugged edge {edges})",
                _ when bad.IsBad(index) => $"\nLooks bad: rugged edge {edges}",
                _ => "",
            };
        }
        if (!includeDc)
            return text;
        text += "\n";

        // DC value of every block, grouped by component: the first thing to look at for colour shifts.
        var names = ComponentNames(frame);
        var layout = doc.Layout;
        var parts = new List<string>();
        for (int c = 0; c < frame.Components.Count; c++)
        {
            var values = new List<int>();
            for (int b = 0; b < layout.BlocksPerMcu; b++)
            {
                if (layout.Blocks[b].ComponentIndex == c)
                    values.Add(doc.GetDc(index, b));
            }

            parts.Add($"{names[c]} {string.Join(" ", values)}");
        }

        return text + "DC  " + string.Join("   ", parts);
    }

    private static string DescribeEdges(byte edges)
    {
        var names = new List<string>(4);
        if ((edges & BadMcuMap.Top) != 0) names.Add("above");
        if ((edges & BadMcuMap.Right) != 0) names.Add("right");
        if ((edges & BadMcuMap.Bottom) != 0) names.Add("below");
        if ((edges & BadMcuMap.Left) != 0) names.Add("left");
        return string.Join(", ", names);
    }

    private static string[] ComponentNames(FrameHeader frame) => frame.Components.Count switch
    {
        1 => ["Y"],
        3 => ["Y", "Cb", "Cr"],
        _ => frame.Components.Select(c => $"C{c.Id}").ToArray(),
    };

    private static string SamplingName(FrameHeader frame)
    {
        if (frame.Components.Count == 1)
            return "grayscale";

        var luma = frame.Components[0];
        bool chromaFull = frame.Components.Skip(1).All(c => c.H == 1 && c.V == 1);
        return (luma.H, luma.V, chromaFull) switch
        {
            (1, 1, true) => "4:4:4",
            (2, 1, true) => "4:2:2",
            (2, 2, true) => "4:2:0",
            (1, 2, true) => "4:4:0",
            (4, 1, true) => "4:1:1",
            _ => string.Join(" ", frame.Components.Select(c => $"{c.H}x{c.V}")),
        };
    }
}
