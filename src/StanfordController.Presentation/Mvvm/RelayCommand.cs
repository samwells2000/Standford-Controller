using System.Windows.Input;

namespace StanfordController.Presentation.Mvvm;

public interface IRaiseCanExecuteChanged
{
    void RaiseCanExecuteChanged();
}

/// <summary>A command that can say why it is unavailable, shown when the user hovers over its greyed-out control.</summary>
public interface IExplainUnavailable
{
    /// <summary>Why the command cannot run right now, or null while it can (or when there is nothing useful to say).</summary>
    string? UnavailableReason { get; }
}

internal static class CommandReasons
{
    public const string Running = "Already in progress. Wait for it to finish.";
}

/// <param name="unavailableReason">Why <paramref name="canExecute"/> is false, for the greyed-out control's tooltip.</param>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null, Func<string?>? unavailableReason = null)
    : ICommand, IRaiseCanExecuteChanged, IExplainUnavailable
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public string? UnavailableReason => CanExecute(null) ? null : unavailableReason?.Invoke();

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null, Func<string?>? unavailableReason = null)
    : ICommand, IRaiseCanExecuteChanged, IExplainUnavailable
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(Convert(parameter)) ?? true;

    /// <summary>The reason given when the command is greyed out; the parameter is not known here, so it is not re-checked.</summary>
    public string? UnavailableReason => unavailableReason?.Invoke();

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
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null,
    Func<string?>? unavailableReason = null)
    : ICommand, IRaiseCanExecuteChanged, IExplainUnavailable
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

    public string? UnavailableReason => _isRunning ? CommandReasons.Running
        : canExecute?.Invoke() == false ? unavailableReason?.Invoke() : null;

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

/// <summary>An async command with a parameter; disables itself while running.</summary>
public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute, Func<T?, bool>? canExecute = null, Func<string?>? unavailableReason = null)
    : ICommand, IRaiseCanExecuteChanged, IExplainUnavailable
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(Convert(parameter)) ?? true);

    /// <summary>The reason given when the command is greyed out; the parameter is not known here, so it is not re-checked.</summary>
    public string? UnavailableReason => _isRunning ? CommandReasons.Running : unavailableReason?.Invoke();

    public async void Execute(object? parameter) => await ExecuteAsync(Convert(parameter));

    public async Task ExecuteAsync(T? parameter)
    {
        if (_isRunning || !(canExecute?.Invoke(parameter) ?? true))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}
