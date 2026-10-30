using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace DevDesk.ViewModels;

/// <summary>
/// The only shared view-model logic: busy state, an error message, and one
/// place that turns a failed operation into something a page can show.
/// <para>
/// <see cref="HasError"/> is derived, never stored, so the two can never
/// disagree. Cancellation is not an error: a cancelled load leaves the previous
/// state on screen without an error bar.
/// </para>
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(IdleVisibility));
            OnBusyChanged();
        }
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IdleVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetProperty(ref _errorMessage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorVisibility));
            OnErrorChanged();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Hook for derived state that depends on <see cref="IsBusy"/>.</summary>
    protected virtual void OnBusyChanged()
    {
    }

    /// <summary>Hook for derived state that depends on <see cref="HasError"/>.</summary>
    protected virtual void OnErrorChanged()
    {
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the busy overlay up. Exceptions become
    /// <see cref="ErrorMessage"/>; cancellation is swallowed.
    /// </summary>
    protected async Task RunBusyAsync(Func<Task> work)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The user (or the page) cancelled: keep whatever is on screen.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is AggregateException { InnerExceptions.Count: 1 } aggregate
                ? aggregate.InnerExceptions[0].Message
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
