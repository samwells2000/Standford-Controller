using System.Collections.ObjectModel;
using System.Windows.Input;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

public enum TerminalLineKind
{
    Command,
    Response,
    Error,
    Info,
}

public sealed record TerminalLine(TerminalLineKind Kind, string Text)
{
    public string Prefix => Kind switch
    {
        TerminalLineKind.Command => ">",
        TerminalLineKind.Response => "<",
        TerminalLineKind.Error => "!",
        _ => "#",
    };
}

/// <summary>A command console for sending any remote command by hand, with a searchable command reference.</summary>
public sealed class TerminalViewModel : ParameterViewModel
{
    private readonly List<string> _history = [];
    private int _historyIndex;
    private string _input = string.Empty;
    private bool _checkErrors = true;
    private string _filter = string.Empty;
    private CommandInfo? _selectedCommand;

    public TerminalViewModel(IInstrumentContext context)
        : base(context, "Terminal")
    {
        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(_input));
        ClearCommand = new RelayCommand(Lines.Clear);
        HistoryUpCommand = new RelayCommand(() => MoveHistory(-1));
        HistoryDownCommand = new RelayCommand(() => MoveHistory(1));
        InsertCommand = new RelayCommand<CommandInfo>(Insert);
        Lines.Add(new TerminalLine(TerminalLineKind.Info,
            "Type any DG645 command and press Enter. Queries end in '?', e.g. *IDN? or DLAY?2. Separate several commands with ';'."));
    }

    public ObservableCollection<TerminalLine> Lines { get; } = [];

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value ?? string.Empty))
            {
                (SendCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Read LERR? after each command so rejected commands are reported.</summary>
    public bool CheckErrors
    {
        get => _checkErrors;
        set => SetProperty(ref _checkErrors, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(FilteredCommands));
            }
        }
    }

    public IEnumerable<CommandInfo> FilteredCommands => string.IsNullOrWhiteSpace(_filter)
        ? CommandCatalog.All
        : CommandCatalog.All.Where(c =>
            c.Syntax.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
            c.Description.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
            c.Category.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase));

    public CommandInfo? SelectedCommand
    {
        get => _selectedCommand;
        set => SetProperty(ref _selectedCommand, value);
    }

    public ICommand SendCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand HistoryUpCommand { get; }

    public ICommand HistoryDownCommand { get; }

    public ICommand InsertCommand { get; }

    public override Task RefreshAsync() => Task.CompletedTask;

    public async Task SendAsync()
    {
        var line = _input.Trim();
        if (line.Length == 0)
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != line)
        {
            _history.Add(line);
        }

        _historyIndex = _history.Count;
        Input = string.Empty;
        Lines.Add(new TerminalLine(TerminalLineKind.Command, line));

        await RunAsync(async () =>
        {
            var result = await Client.ExecuteRawAsync(line, _checkErrors);
            foreach (var response in result.Responses)
            {
                Lines.Add(new TerminalLine(TerminalLineKind.Response, response));
            }

            if (result.TimedOut)
            {
                Lines.Add(new TerminalLine(TerminalLineKind.Error, "No reply within the timeout."));
            }

            foreach (var code in result.Errors)
            {
                Lines.Add(new TerminalLine(TerminalLineKind.Error, $"Error {code}: {Dg645Names.DescribeError(code)}"));
            }

            if (result.Responses.Count == 0 && result.Errors.Count == 0 && !result.TimedOut)
            {
                Lines.Add(new TerminalLine(TerminalLineKind.Info, _checkErrors ? "OK" : "Sent"));
            }

        });

        if (Error is not null)
        {
            Lines.Add(new TerminalLine(TerminalLineKind.Error, Error));
        }

        while (Lines.Count > 2000)
        {
            Lines.RemoveAt(0);
        }
    }

    private void MoveHistory(int delta)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    private void Insert(CommandInfo? command)
    {
        if (command is not null)
        {
            Input = command.Example;
        }
    }
}
