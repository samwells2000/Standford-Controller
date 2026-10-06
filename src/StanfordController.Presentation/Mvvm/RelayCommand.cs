using System.Windows.Input;

namespace StanfordController.Presentation.Mvvm;

public interface IRaiseCanExecuteChanged
{
    void RaiseCanExecuteChanged();
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand, IRaiseCanExecuteChanged
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand, IRaiseCanExecuteChanged
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(Convert(parameter)) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute(Convert(parameter));
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}

/// <summary>An async command that disables itself while running and never lets exceptions escape to the UI thread.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    : ICommand, IRaiseCanExecuteChanged
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        IsRunning = true;
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            if (onError is null)
            {
                throw;
            }

            onError(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
