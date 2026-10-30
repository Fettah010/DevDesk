using Microsoft.UI.Xaml;

namespace DevDesk.ViewModels;

/// <summary>How a measured value compares with its budget. Colour only — no verdict words.</summary>
public enum PerfTone
{
    /// <summary>Nothing measured yet, or no budget applies to this number.</summary>
    Muted,

    Within,
    Over,
}

/// <summary>
/// One line of the Diagnostics performance card. The three visibility properties
/// exist so the DataTemplate can pick a themed brush without a converter: WinUI has
/// no data triggers, and a brush built in this class would be a light/dark guess
/// made outside the theme system (the same mistake that crashed pages with an
/// invented <c>TextFillColor*</c> key).
/// </summary>
public sealed record PerfRow(string Label, string ValueText, string BudgetText, PerfTone Tone)
{
    public Visibility MutedVisibility =>
        Tone == PerfTone.Muted ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WithinVisibility =>
        Tone == PerfTone.Within ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OverVisibility =>
        Tone == PerfTone.Over ? Visibility.Visible : Visibility.Collapsed;
}
