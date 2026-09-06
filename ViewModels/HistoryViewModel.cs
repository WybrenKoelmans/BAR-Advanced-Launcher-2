using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>
/// Past launches, replay, and the jump to a run's log (PLAN.md §6.1, §7).
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ILaunchHistoryStore _history;
    private readonly ILaunchService _launcher;
    private readonly IInfologCatalog _logs;
    private readonly IInstallationContext _installation;
    private readonly INavigationService _navigation;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<HistoryViewModel> _logger;

    private Task? _loading;

    public HistoryViewModel(
        ILaunchHistoryStore history,
        ILaunchService launcher,
        IInfologCatalog logs,
        IInstallationContext installation,
        INavigationService navigation,
        IShellService shell,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _launcher = launcher;
        _logs = logs;
        _installation = installation;
        _navigation = navigation;
        _shell = shell;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = logger;

        Status = string.Empty;

        // Launches are recorded from the launch service, including from a process-exit
        // callback on a pool thread, so this arrives off the UI thread.
        _history.Changed += (_, _) => _dispatcher.Post(Sync);
    }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplayCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommandLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedCommandLine))]
    public partial LaunchRecord? SelectedRecord { get; set; }

    /// <summary>
    /// Set when the selected run's start script is no longer what it was. Computed once
    /// per selection rather than from a property getter, because working it out means
    /// reading the file.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptDrift))]
    public partial string? ScriptDriftText { get; set; }

    public bool HasScriptDrift => !string.IsNullOrEmpty(ScriptDriftText);

    public ObservableCollection<LaunchRecord> Records { get; } = new();

    public bool HasSelection => SelectedRecord is not null;

    public bool HasRecords => Records.Count > 0;

    public string SelectedCommandLine => SelectedRecord?.CommandLineText ?? string.Empty;

    partial void OnSelectedRecordChanged(LaunchRecord? value) => _ = UpdateScriptDriftAsync(value);

    /// <summary>
    /// Checks whether the selected run's start script still says what it said at the time.
    ///
    /// A replay reads the file, because that is what the engine does — so a script edited
    /// since is a real difference between "replay this run" and "run something else under
    /// the same name", and the only honest thing is to say which one the button will do.
    /// </summary>
    private async Task UpdateScriptDriftAsync(LaunchRecord? record)
    {
        if (record is not { ScriptPath: { Length: > 0 } path, ScriptSnapshot: { } snapshot })
        {
            ScriptDriftText = null;
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                ScriptDriftText = "The start script this run used is gone, so a replay will fail.";
                return;
            }

            string current = await File.ReadAllTextAsync(path);

            ScriptDriftText = TextDiff.Compare(snapshot, current).Identical
                ? null
                : "The start script has been edited since this run, so a replay will not reproduce it exactly.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not compare the stored script snapshot for {Path}.", path);
            ScriptDriftText = null;
        }
    }

    [RelayCommand]
    private Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        await _installation.InitializeAsync();
        await _history.LoadAsync();
        Sync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _history.LoadAsync();
        Sync();
    }

    private void Sync()
    {
        string? keep = SelectedRecord?.Id;

        Records.Clear();
        foreach (LaunchRecord record in _history.Records)
        {
            Records.Add(record);
        }

        SelectedRecord = keep is null
            ? null
            : Records.FirstOrDefault(record => record.Id == keep);

        Status = Records.Count switch
        {
            0 => "Nothing launched yet.",
            1 => "1 launch.",
            _ => $"{Records.Count} launches.",
        };

        OnPropertyChanged(nameof(HasRecords));
        ClearCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Launches a past run again, through the same validation a fresh launch gets: the
    /// stored profile is re-resolved against the current install, so a moved install or a
    /// deleted engine is reported rather than producing a command line that cannot work.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReplayAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        LaunchResult result = await _launcher.LaunchAsync(record.Profile, null, record.ScriptPath);

        Status = result.Success
            ? $"Replaying {record.ProfileName} (pid {result.Instance!.ProcessId})."
            : result.Error ?? "The replay failed.";

        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Could not replay", Status);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyCommandLineAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        Status = await _shell.SetClipboardTextAsync(record.CommandLineText)
            ? "Command line copied to the clipboard."
            : "Could not copy the command line.";
    }

    /// <summary>
    /// Opens this run's log in the viewer.
    ///
    /// The log is found by write time rather than by the path the record stored: the
    /// engine writes one <c>infolog.txt</c> per write directory and rotates the previous
    /// one aside on its next start, so the stored path stops being this run's log as soon
    /// as anything else launches.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenLogAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        InfologFile? file = _logs.FindForRun(record.StartedAt, record.ExitedAt);

        if (file is null)
        {
            Status = "No log could be matched to this run.";
            await _dialogs.ShowMessageAsync(
                "No log found",
                "No engine log was written in this run's time window. It may have been cleaned up, "
                + "or the run may have failed before the engine opened its log.");
            return;
        }

        // Handed over as a navigation parameter rather than by reaching into the other
        // page's view model, so the two pages stay independent.
        if (!_navigation.NavigateTo(PageKeys.Infolog, file))
        {
            Status = "Could not open the Infolog page.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealScript()
    {
        if (SelectedRecord?.ScriptPath is not { Length: > 0 } path)
        {
            Status = "This run used no start script.";
            return;
        }

        if (!_shell.RevealInExplorer(path))
        {
            Status = $"Could not reveal {path}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        await _history.RemoveAsync(record.Id);
        Sync();
    }

    [RelayCommand(CanExecute = nameof(HasRecords))]
    private async Task ClearAsync()
    {
        bool confirmed = await _dialogs.ConfirmAsync(
            "Clear launch history",
            $"Forget all {Records.Count} recorded launches? The scripts and profiles themselves are not touched.",
            "Clear");

        if (!confirmed)
        {
            return;
        }

        await _history.ClearAsync();
        Sync();
    }
}
