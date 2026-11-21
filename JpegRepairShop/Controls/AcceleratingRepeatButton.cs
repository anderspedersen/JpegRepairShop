using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace JpegRepairShop.Controls;

/// <summary>
/// A <see cref="RepeatButton"/> whose steps grow the longer it is held: 1 step per repeat for the first second,
/// then 2, 5 and 10. The current step count is passed to the command as its parameter (an int).
/// Growing the step instead of the repeat rate keeps the number of re-renders per second constant.
/// </summary>
public sealed class AcceleratingRepeatButton : RepeatButton
{
    private static readonly (double Seconds, int Steps)[] Stages = [(0, 1), (1.0, 2), (2.0, 5), (3.0, 10)];

    private readonly Stopwatch _held = new();

    public AcceleratingRepeatButton()
    {
        CommandParameter = 1;
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        if (IsPressed)
            _held.Restart();
        else
            _held.Reset();
    }

    protected override void OnClick()
    {
        double seconds = _held.Elapsed.TotalSeconds;
        int steps = 1;
        foreach (var (start, count) in Stages)
        {
            if (seconds >= start)
                steps = count;
        }

        CommandParameter = steps;
        base.OnClick();
    }
}
