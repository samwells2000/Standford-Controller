using System.Net;
using System.Net.Sockets;
using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>What a parameter needs from the connected instrument session.</summary>
public interface IInstrumentContext
{
    Dg645Client Client { get; }

    IDialogService Dialogs { get; }

    /// <summary>Logs a failed operation and surfaces it in the session's status line and error list.</summary>
    void ReportError(string context, Exception exception);

    /// <summary>Called after any successful write, e.g. to redraw the timing diagram.</summary>
    void NotifySettingsChanged();

    /// <summary>Ends the session, e.g. after the LAN interface was reset.</summary>
    void RequestDisconnect(string reason);

    /// <summary>Settings may have been changed behind the pages' backs (e.g. from the terminal).</summary>
    void NotifyExternalChange();

    /// <summary>
    /// Starts a change that the caller records in the audit trail itself: commands sent on this async flow until the
    /// scope is disposed are not recorded again as raw commands.
    /// </summary>
    IDisposable BeginChange();

    /// <summary>Adds a record about this instrument to the audit trail, with the user's reason for changes.</summary>
    void Audit(string action, string? setting = null, string? oldValue = null, string? newValue = null, string? details = null);
}

public sealed record ChoiceOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Base for anything that reads or writes the instrument and can show an error inline.</summary>
public abstract class ParameterViewModel(IInstrumentContext context, string label) : ObservableObject
{
    /// <summary>Shown in place of a setting the instrument's firmware does not have.</summary>
    public const string UnsupportedText = "Not available on this instrument's firmware";

    private string? _error;
    private bool _isBusy;
    private bool _isUnsupported;

    protected IInstrumentContext Context { get; } = context;

    protected Dg645Client Client => Context.Client;

    public string Label { get; } = label;

    /// <summary>Tooltip text explaining the setting and its remote command.</summary>
    public string? Hint { get; init; }

    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The instrument's firmware does not have this setting (reading it gave error 111).</summary>
    public bool IsUnsupported
    {
        get => _isUnsupported;
        private set
        {
            if (SetProperty(ref _isUnsupported, value))
            {
                OnPropertyChanged(nameof(Placeholder));
                OnPropertyChanged(nameof(ToolTipText));
                if (value)
                {
                    OnUnsupported();
                }
            }
        }
    }

    /// <summary>Shown inside a greyed-out field the firmware does not have.</summary>
    public string? Placeholder => IsUnsupported ? "Not available" : null;

    /// <summary>The setting's explanation, or why it is greyed out.</summary>
    public string? ToolTipText => IsUnsupported ? UnsupportedText : Hint;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public abstract Task RefreshAsync();

    /// <summary>The firmware turned out not to have this setting: forget any value shown.</summary>
    protected virtual void OnUnsupported()
    {
    }

    /// <summary>
    /// Runs an instrument operation, capturing any failure as <see cref="Error"/>. When <paramref name="reading"/> the
    /// setting, a firmware without the command marks it <see cref="IsUnsupported"/> instead of failing.
    /// </summary>
    protected async Task<bool> RunAsync(Func<Task> action, bool reading = false)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
            if (reading)
            {
                IsUnsupported = false;
            }

            return true;
        }
        catch (Dg645UnsupportedCommandException) when (reading)
        {
            IsUnsupported = true;
            return false;
        }
        catch (Exception ex)
        {
            Error = DescribeError(ex);
            Context.ReportError(Label, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Writes to the instrument as one change in the audit trail. When <paramref name="write"/> succeeds, the change of
    /// <paramref name="setting"/> (this setting's label by default) from <paramref name="before"/> to what
    /// <paramref name="after"/> returns once <paramref name="readBack"/> has shown the instrument's value is recorded,
    /// and the session is told settings changed. The read-back runs even if the write fails. Returns true on success.
    /// </summary>
    protected async Task<bool> ChangeAsync(string? before, Func<Task> write, Func<Task> readBack, Func<string?> after,
        string? setting = null, Action? written = null)
    {
        var succeeded = false;
        using (Context.BeginChange())
        {
            await RunAsync(async () =>
            {
                try
                {
                    await write();
                    succeeded = true;
                }
                finally
                {
                    try
                    {
                        await readBack();
                    }
                    catch
                    {
                        // Keep the original error.
                    }
                }
            });
        }

        if (succeeded)
        {
            written?.Invoke();
            Context.Audit(AuditActions.Changed, setting ?? Label, before, after());
            Context.NotifySettingsChanged();
        }

        return succeeded;
    }

    /// <summary>Runs an instrument action (a reset, a test, a mode change) as one audited step; see <see cref="ChangeAsync"/>.</summary>
    protected async Task<bool> ActAsync(Func<Task> action, string auditAction, string? setting = null, Func<string?>? details = null)
    {
        bool succeeded;
        using (Context.BeginChange())
        {
            succeeded = await RunAsync(action);
        }

        if (succeeded)
        {
            Context.Audit(auditAction, setting, details: details?.Invoke());
        }

        return succeeded;
    }

    public static string DescribeError(Exception exception) => exception switch
    {
        Dg645Exception dg => $"{dg.ErrorDescription} (error {dg.ErrorCode})",
        Dg645UnsupportedCommandException => UnsupportedText,
        TimeoutException => "No reply from the instrument",
        _ => exception.Message,
    };
}

public delegate bool ValueParser<T>(string text, out T value, out string? error);

/// <summary>The part of a text-box setting the views bind to, whatever its value type.</summary>
public abstract class TextParameterViewModel(IInstrumentContext context, string label) : ParameterViewModel(context, label)
{
    public abstract string Text { get; set; }

    public abstract bool IsReadOnly { get; }

    /// <summary>The text differs from the value last read from the instrument.</summary>
    public abstract bool IsDirty { get; }

    public abstract ICommand CommitCommand { get; }

    public abstract ICommand RevertCommand { get; }
}

/// <summary>
/// A text-box backed setting: the user edits <see cref="Text"/>, then <see cref="CommitCommand"/>
/// (Enter or leaving the field) validates, writes, and reads back what the instrument actually accepted.
/// </summary>
public sealed class EditableParameterViewModel<T> : TextParameterViewModel
    where T : notnull
{
    private readonly Func<CancellationToken, Task<T>> _read;
    private readonly Func<T, CancellationToken, Task>? _write;
    private readonly Func<T, string> _format;
    private readonly ValueParser<T> _parse;
    private string _text = string.Empty;
    private string _committedText = string.Empty;
    private T? _value;
    private bool _hasValue;

    public EditableParameterViewModel(
        IInstrumentContext context,
        string label,
        Func<CancellationToken, Task<T>> read,
        Func<T, CancellationToken, Task>? write,
        Func<T, string> format,
        ValueParser<T> parse)
        : base(context, label)
    {
        _read = read;
        _write = write;
        _format = format;
        _parse = parse;
        CommitCommand = new AsyncRelayCommand(CommitAsync);
        RevertCommand = new RelayCommand(Revert);
    }

    public event EventHandler? ValueChanged;

    public override string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public override bool IsReadOnly => _write is null;

    public override bool IsDirty => !IsReadOnly && !string.Equals(_text.Trim(), _committedText, StringComparison.Ordinal);

    public T? Value => _value;

    public bool HasValue => _hasValue;

    /// <summary>The value last read from the instrument, as text (whatever is being typed).</summary>
    public string ValueText => _committedText;

    public override ICommand CommitCommand { get; }

    public override ICommand RevertCommand { get; }

    public override Task RefreshAsync() => RunAsync(async () => SetValue(await _read(CancellationToken.None)), reading: true);

    public void SetValue(T value)
    {
        _value = value;
        _hasValue = true;
        _committedText = _format(value);
        Text = _committedText;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(IsDirty));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CommitAsync()
    {
        if (!IsDirty || _write is null)
        {
            return;
        }

        if (!_parse(Text.Trim(), out var value, out var parseError))
        {
            Error = parseError;
            return;
        }

        // Show what the instrument really holds afterwards, even if it rejected or rounded the value.
        var before = _hasValue ? _committedText : null;
        await ChangeAsync(before, () => _write(value, CancellationToken.None), TryReadBackAsync, () => _committedText);
    }

    public void Revert()
    {
        Text = _committedText;
        Error = null;
    }

    protected override void OnUnsupported()
    {
        _value = default;
        _hasValue = false;
        _committedText = string.Empty;
        Text = string.Empty;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(IsDirty));
    }

    private async Task TryReadBackAsync()
    {
        try
        {
            SetValue(await _read(CancellationToken.None));
        }
        catch
        {
            // Keep the original error.
        }
    }
}

/// <summary>A setting chosen from a fixed list (combo box). Selecting an option writes it immediately.</summary>
public sealed class ChoiceParameterViewModel(
    IInstrumentContext context,
    string label,
    IReadOnlyList<ChoiceOption> options,
    Func<CancellationToken, Task<int>> read,
    Func<int, CancellationToken, Task> write) : ParameterViewModel(context, label)
{
    private ChoiceOption? _selected;
    private bool _updating;

    public event EventHandler? ValueChanged;

    public IReadOnlyList<ChoiceOption> Options { get; } = options;

    public ChoiceOption? Selected
    {
        get => _selected;
        set
        {
            if (value is null || Equals(value, _selected))
            {
                return;
            }

            var previous = _selected;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedValue));
            if (!_updating)
            {
                _ = ApplyAsync(value.Value, previous?.Label);
            }
        }
    }

    public int? SelectedValue => _selected?.Value;

    public override Task RefreshAsync() => RunAsync(async () => SetValue(await read(CancellationToken.None)), reading: true);

    public void SetValue(int value)
    {
        _updating = true;
        try
        {
            var option = Options.FirstOrDefault(o => o.Value == value);
            if (option is null)
            {
                Error = $"The instrument reported an unexpected value ({value}).";
            }

            _selected = option;
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SelectedValue));
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _updating = false;
        }
    }

    private async Task ApplyAsync(int value, string? before)
    {
        // Let the combo box finish its own selection change before we possibly revert it.
        await Task.Yield();
        await ChangeAsync(before,
            () => write(value, CancellationToken.None),
            async () => SetValue(await read(CancellationToken.None)),
            () => _selected?.Label,
            written: () => ValueChanged?.Invoke(this, EventArgs.Empty));
    }
}

/// <summary>An on/off setting (check box or toggle). Changing it writes immediately.</summary>
public sealed class ToggleParameterViewModel(
    IInstrumentContext context,
    string label,
    Func<CancellationToken, Task<bool>> read,
    Func<bool, CancellationToken, Task> write) : ParameterViewModel(context, label)
{
    private bool _isChecked;
    private bool _updating;

    public event EventHandler? ValueChanged;

    /// <summary>Optional question asked before applying a new value; return false to cancel.</summary>
    public Func<bool, Task<bool>>? ConfirmChange { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == _isChecked)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
            if (!_updating)
            {
                _ = ApplyAsync(value);
            }
        }
    }

    public override Task RefreshAsync() => RunAsync(async () => SetValue(await read(CancellationToken.None)), reading: true);

    public void SetValue(bool value)
    {
        _updating = true;
        try
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _updating = false;
        }
    }

    private async Task ApplyAsync(bool value)
    {
        await Task.Yield();
        if (ConfirmChange is not null && !await ConfirmChange(value))
        {
            SetValue(!value);
            return;
        }

        await ChangeAsync(OnOff(!value),
            () => write(value, CancellationToken.None),
            async () => SetValue(await read(CancellationToken.None)),
            () => OnOff(_isChecked));
    }

    private static string OnOff(bool on) => on ? "On" : "Off";
}

/// <summary>Factories for the common kinds of editable parameter.</summary>
public static class Param
{
    public static EditableParameterViewModel<double> Number(
        IInstrumentContext context,
        string label,
        Quantity quantity,
        Func<CancellationToken, Task<double>> read,
        Func<double, CancellationToken, Task>? write,
        double? min = null,
        double? max = null,
        string? hint = null) =>
        new(context, label, read, write, v => EngineeringUnits.Format(v, quantity), NumberParser(quantity, min, max)) { Hint = hint };

    public static EditableParameterViewModel<long> Integer(
        IInstrumentContext context,
        string label,
        Func<CancellationToken, Task<long>> read,
        Func<long, CancellationToken, Task>? write,
        long? min = null,
        long? max = null,
        string? hint = null) =>
        new(context, label, read, write, v => v.ToString(System.Globalization.CultureInfo.InvariantCulture), IntegerParser(min, max)) { Hint = hint };

    public static EditableParameterViewModel<string> IpAddress(
        IInstrumentContext context,
        string label,
        Func<CancellationToken, Task<string>> read,
        Func<string, CancellationToken, Task>? write,
        string? hint = null) =>
        new(context, label, read, write, v => v.Trim(), ParseIpAddress) { Hint = hint };

    public static ValueParser<double> NumberParser(Quantity quantity, double? min, double? max) =>
        (string text, out double value, out string? error) =>
        {
            if (!EngineeringUnits.TryParse(text, quantity, out value))
            {
                error = quantity switch
                {
                    Quantity.Time => $"\"{text}\" is not a time. Examples: 1.5 us, 250 ns, 2 ms, 1e-6.",
                    Quantity.Frequency => $"\"{text}\" is not a frequency. Examples: 1 kHz, 10 MHz, 50.",
                    Quantity.Voltage => $"\"{text}\" is not a voltage. Examples: 2.5, -800 mV.",
                    _ => $"\"{text}\" is not a number.",
                };
                return false;
            }

            if (value < min || value > max)
            {
                error = $"Must be between {EngineeringUnits.Format(min ?? double.NaN, quantity)} and {EngineeringUnits.Format(max ?? double.NaN, quantity)}.";
                return false;
            }

            error = null;
            return true;
        };

    public static ValueParser<long> IntegerParser(long? min, long? max) =>
        (string text, out long value, out string? error) =>
        {
            value = 0;
            if (!EngineeringUnits.TryParse(text, Quantity.Integer, out var number) || Math.Abs(number) > long.MaxValue / 2.0)
            {
                error = $"\"{text}\" is not a whole number.";
                return false;
            }

            value = (long)Math.Round(number);
            if (value < min || value > max)
            {
                error = $"Must be between {min} and {max}.";
                return false;
            }

            error = null;
            return true;
        };

    private static bool ParseIpAddress(string text, out string value, out string? error)
    {
        if (IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') == 3)
        {
            value = address.ToString();
            error = null;
            return true;
        }

        value = text;
        error = $"\"{text}\" is not an IPv4 address (e.g. 192.168.1.20).";
        return false;
    }
}
