using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JpegRepairShop;

/// <summary>
/// Asks how many MCU rows above a section "Fix section colours" compares its colours with. Shown modally;
/// <see cref="Rows"/> holds the answer when it returns true.
/// </summary>
public partial class SectionColoursWindow : Window
{
    private readonly int _max;

    /// <param name="rows">Number shown at first.</param>
    /// <param name="max">Most rows that can be chosen (the rows above the section).</param>
    /// <param name="sectionFirst">First MCU of the section, for the text.</param>
    public SectionColoursWindow(int rows, int max, int sectionFirst)
    {
        InitializeComponent();
        _max = Math.Max(1, max);
        Rows = Math.Clamp(rows, 1, _max);
        IntroText.Text = $"The colours of the section from MCU {sectionFirst} are matched to the good MCUs in the rows above it.";
        RangeText.Text = $"(1–{_max})";
        RowsBox.Text = Rows.ToString();
        Loaded += (_, _) =>
        {
            RowsBox.Focus();
            RowsBox.SelectAll();
        };
    }

    /// <summary>The chosen number of rows.</summary>
    public int Rows { get; private set; }

    private bool TryRead(out int rows) =>
        int.TryParse(RowsBox.Text.Trim(), out rows) && rows >= 1 && rows <= _max;

    private void OnRowsChanged(object sender, TextChangedEventArgs e)
    {
        if (OkButton != null)
            OkButton.IsEnabled = TryRead(out _);
    }

    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        int step = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
        if (step == 0)
            return;
        int current = TryRead(out int rows) ? rows : Rows;
        RowsBox.Text = Math.Clamp(current + step, 1, _max).ToString();
        RowsBox.SelectAll();
        e.Handled = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out int rows))
            return;
        Rows = rows;
        DialogResult = true;
    }
}
