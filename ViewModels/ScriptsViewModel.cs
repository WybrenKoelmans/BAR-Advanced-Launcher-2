using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>
/// The script library and its raw editor (PLAN.md §5.7). The form view over
/// <see cref="StartScriptModel"/> is Phase 5; this page is the raw text editor plus the
/// file operations, with the parser wired up so a broken script is reported inline rather
/// than discovered when the engine refuses to start.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly ILogger<ScriptsViewModel> _logger;
    private readonly IStartScriptStore _store;
    private readonly IStartScriptSerializer _serializer;
    private readonly IStartScriptFactory _factory;
    private readonly ILaunchService _launcher;
    private readonly IInstallationContext _installation;
    private readonly ISettingsService _settings;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;

    /// <summary>
    /// Shared by the page's Loaded event and the refresh command, which overlap on
    /// startup — the same in-flight guard the Launch page uses.
    /// </summary>
    private Task? _loading;

    /// <summary>Suppresses the dirty flag while the editor is being filled in code.</summary>
    private bool _isPopulatingEditor;

    public ScriptsViewModel(
        ILogger<ScriptsViewModel> logger,
        IStartScriptStore store,
        IStartScriptSerializer serializer,
        IStartScriptFactory factory,
        ILaunchService launcher,
        IInstallationContext installation,
        ISettingsService settings,
        IShellService shell,
        IDialogService dialogs)
    {
        _logger = logger;
        _store = store;
        _serializer = serializer;
        _factory = factory;
        _launcher = launcher;
        _installation = installation;
        _settings = settings;
        _shell = shell;
        _dialogs = dialogs;

        Header = "Scripts";
        EditorText = string.Empty;
        Status = string.Empty;
        ParseError = string.Empty;

        UseIsolation = settings.Current.ScriptsUseIsolation;

        foreach (StartScriptTemplate template in _factory.Templates)
        {
            Templates.Add(template);
        }
    }

    [ObservableProperty]
    public partial string Header { get; set; }

    /// <summary>
    /// Whether this page's Launch passes <c>--isolation</c>, which limits the games and
    /// maps scanner to the install. Persisted through <c>settings.json</c> rather than a
    /// profile: launching from the editor is "run this file", not "run my setup".
    /// </summary>
    [ObservableProperty]
    public partial bool UseIsolation { get; set; }

    public ObservableCollection<StartScriptFile> Scripts { get; } = new();

    public ObservableCollection<StartScriptTemplate> Templates { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedScriptPath))]
    public partial StartScriptFile? SelectedScript { get; set; }

    /// <summary>The raw text of the selected script, as edited.</summary>
    [ObservableProperty]
    public partial string EditorText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyPropertyChangedFor(nameof(EditorHeader))]
    public partial bool IsDirty { get; set; }

    /// <summary>The parse error for the current editor text, or empty when it is valid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParseError))]
    public partial string ParseError { get; set; }

    /// <summary>The result of the last file operation, shown under the list.</summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    public bool HasSelection => SelectedScript is not null;

    public bool HasParseError => ParseError.Length > 0;

    public string SelectedScriptPath => SelectedScript?.FullPath ?? string.Empty;

    public string LibraryPath => _store.LibraryPath;

    public string EditorHeader => SelectedScript is null
        ? "No script selected"
        : IsDirty ? SelectedScript.DisplayName + " •" : SelectedScript.DisplayName;

    partial void OnUseIsolationChanged(bool value)
    {
        if (_settings.Current.ScriptsUseIsolation == value)
        {
            return;
        }

        _settings.Current.ScriptsUseIsolation = value;
        _ = _settings.SaveAsync();
    }

    // ---- loading --------------------------------------------------------------

    [RelayCommand]
    private Task LoadAsync() =>
        _loading is { IsCompleted: false } inFlight ? inFlight : _loading = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            IReadOnlyList<StartScriptFile> scripts = await _store.ListAsync();
            string? previous = SelectedScript?.FileName;

            SyncScripts(scripts);

            // Keep the selection across a refresh where the file still exists, so saving
            // does not bounce the user back to the top of the list.
            StartScriptFile? restored = previous is null
                ? null
                : Scripts.FirstOrDefault(s => string.Equals(s.FileName, previous, StringComparison.OrdinalIgnoreCase));

            SelectedScript = restored ?? Scripts.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the script library.");
            Status = "Could not read the script library.";
        }
    }

    /// <summary>
    /// Refills the list only when it actually changed: rebuilding an ObservableCollection
    /// makes the bound ListView push a null selection back through SelectedItem.
    /// </summary>
    private void SyncScripts(IReadOnlyList<StartScriptFile> scripts)
    {
        if (Scripts.SequenceEqual(scripts))
        {
            return;
        }

        Scripts.Clear();

        foreach (StartScriptFile script in scripts)
        {
            Scripts.Add(script);
        }
    }

    partial void OnSelectedScriptChanged(StartScriptFile? value)
    {
        _ = LoadEditorAsync(value);
    }

    private async Task LoadEditorAsync(StartScriptFile? script)
    {
        if (script is null)
        {
            SetEditorText(string.Empty);
            return;
        }

        string? text = await _store.ReadTextAsync(script.FileName);

        if (text is null)
        {
            SetEditorText(string.Empty);
            Status = $"Could not read {script.FileName}.";
            return;
        }

        SetEditorText(text);
    }

    private void SetEditorText(string text)
    {
        _isPopulatingEditor = true;

        try
        {
            EditorText = text;
            IsDirty = false;
        }
        finally
        {
            _isPopulatingEditor = false;
        }

        Validate();
    }

    partial void OnEditorTextChanged(string value)
    {
        if (_isPopulatingEditor)
        {
            return;
        }

        IsDirty = true;
        Validate();
    }

    /// <summary>
    /// Parses what is in the editor so a mistake shows up while typing. The text is never
    /// rewritten from the parse — a valid-but-unusual script must survive editing byte for
    /// byte unless the user asks for it to be tidied.
    /// </summary>
    private void Validate()
    {
        if (SelectedScript is null && EditorText.Length == 0)
        {
            ParseError = string.Empty;
            return;
        }

        StartScriptParseResult result = _serializer.Parse(EditorText);
        ParseError = result.Success ? string.Empty : result.Error!.ToString();
    }

    // ---- file operations ------------------------------------------------------

    [RelayCommand]
    private async Task NewAsync(StartScriptTemplate? template)
    {
        template ??= Templates.FirstOrDefault();

        if (template is null)
        {
            return;
        }

        StartScriptModel model = _factory.Create(template.Id, BuildTemplateOptions());
        string content = _serializer.Write(model.Document);

        StartScriptFile? created = await _store.CreateAsync(_factory.SuggestFileName(template.Id), content);

        if (created is null)
        {
            Status = "Could not create the script.";
            return;
        }

        await LoadAsync();
        SelectedScript = Scripts.FirstOrDefault(s => s.FileName == created.FileName) ?? SelectedScript;
        Status = $"Created {created.DisplayName} from '{template.Name}'.";
    }

    /// <summary>
    /// Seeds a new script from what the user is actually working with, so a template is
    /// runnable without editing: the map and game come from the archive catalog by way of
    /// the settings the Launch page already keeps.
    /// </summary>
    private StartScriptTemplateOptions BuildTemplateOptions() => new()
    {
        PlayerName = string.IsNullOrWhiteSpace(_settings.Current.PlayerName)
            ? Environment.UserName
            : _settings.Current.PlayerName,

        // Null leaves the factory's own defaults in place, which are known-good values
        // rather than blanks.
        MapName = _settings.Current.SelectedMapName,
        GameType = _settings.Current.SelectedGameName,
    };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DuplicateAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        StartScriptFile? copy = await _store.DuplicateAsync(script.FileName);

        if (copy is null)
        {
            Status = "Could not duplicate the script.";
            return;
        }

        await LoadAsync();
        SelectedScript = Scripts.FirstOrDefault(s => s.FileName == copy.FileName) ?? SelectedScript;
        Status = $"Duplicated as {copy.DisplayName}.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        string? name = await _dialogs.PromptForTextAsync(
            "Rename script",
            "New name for this start script:",
            script.DisplayName,
            "Rename",
            candidate => _store.ValidateName(candidate).Error);

        if (name is null || string.Equals(name, script.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        StartScriptFile? renamed = await _store.RenameAsync(script.FileName, name);

        if (renamed is null)
        {
            await _dialogs.ShowMessageAsync(
                "Could not rename",
                $"'{name}' could not be used. A script by that name may already exist.");
            return;
        }

        // Point the selection at the new name before the list reloads, or the restore
        // logic looks for a file that is no longer there.
        SelectedScript = renamed;
        await LoadAsync();
        Status = $"Renamed to {renamed.DisplayName}.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            "Delete script",
            $"Send '{script.DisplayName}' to the recycle bin?",
            "Delete");

        if (!confirmed)
        {
            return;
        }

        if (!_store.Delete(script.FileName))
        {
            Status = $"Could not delete {script.DisplayName}.";
            return;
        }

        SelectedScript = null;
        await LoadAsync();
        Status = $"Deleted {script.DisplayName}. It is in the recycle bin.";
    }

    private bool CanSave => IsDirty && SelectedScript is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        // A script that does not parse will not load, so saying so before writing is
        // more useful than letting the engine reject it later. It is a warning rather
        // than a block: a work in progress is still worth keeping.
        if (HasParseError)
        {
            bool anyway = await _dialogs.ConfirmAsync(
                "This script does not parse",
                $"{ParseError}\n\nSave it anyway? The engine will not be able to load it as it stands.",
                "Save anyway");

            if (!anyway)
            {
                return;
            }
        }

        StartScriptFile? saved = await _store.SaveTextAsync(script.FileName, EditorText);

        if (saved is null)
        {
            Status = $"Could not save {script.DisplayName}.";
            return;
        }

        IsDirty = false;
        SelectedScript = saved;
        await LoadAsync();
        Status = $"Saved {saved.DisplayName}.";
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task RevertAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        await LoadEditorAsync(script);
        Status = $"Reverted {script.DisplayName}.";
    }

    /// <summary>
    /// Rewrites the editor from the parsed document, normalising indentation. Separate
    /// from saving so that formatting is never applied behind the user's back.
    /// </summary>
    [RelayCommand]
    private void Format()
    {
        StartScriptParseResult result = _serializer.Parse(EditorText);

        if (!result.Success)
        {
            Status = "Cannot tidy a script that does not parse.";
            return;
        }

        string formatted = _serializer.Write(result.Document);

        if (string.Equals(formatted, EditorText, StringComparison.Ordinal))
        {
            Status = "Already tidy.";
            return;
        }

        EditorText = formatted;
        Status = "Tidied. Save to keep it.";
    }

    // ---- shell ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenInEditor()
    {
        if (SelectedScript is { } script && !_shell.OpenFile(script.FullPath))
        {
            Status = "No editor is registered for .txt files.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal()
    {
        if (SelectedScript is { } script)
        {
            _shell.RevealInExplorer(script.FullPath);
        }
    }

    [RelayCommand]
    private void OpenLibraryFolder()
    {
        // The folder is created lazily, so it may not exist before the first script.
        System.IO.Directory.CreateDirectory(_store.LibraryPath);
        _shell.OpenFolder(_store.LibraryPath);
    }

    // ---- launching ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task LaunchAsync()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        if (IsDirty)
        {
            bool save = await _dialogs.ConfirmAsync(
                "Unsaved changes",
                "The engine reads the file from disk, so it would run the last saved version. Save first?",
                "Save and launch");

            if (!save)
            {
                return;
            }

            await SaveAsync();

            if (IsDirty)
            {
                return;
            }
        }

        // A one-off profile rather than a saved one: launching from here is "run this
        // file", not "run my configured setup".
        var profile = new LaunchProfile
        {
            Id = "scripts-page",
            Name = script.DisplayName,
            Mode = LaunchMode.Script,
            ScriptFileName = script.FileName,
            UseIsolation = UseIsolation,
        };

        LaunchResult result = await _launcher.LaunchAsync(profile, PreferredEngine());

        Status = result.Success
            ? $"Launched {script.DisplayName} (pid {result.Instance!.ProcessId})."
            : result.Error ?? "The launch failed.";

        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Could not launch", Status);
        }
    }

    /// <summary>
    /// The engine the Launch page is set to. Null falls through to the launch service's
    /// own "newest complete build" choice rather than failing.
    /// </summary>
    private EngineBuild? PreferredEngine()
    {
        string? name = _settings.Current.SelectedEngineName;

        return string.IsNullOrWhiteSpace(name)
            ? null
            : _installation.Engines.FirstOrDefault(
                e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
