using System;
using System.Collections.Generic;
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

/// <summary>The "pick and play" page (PLAN.md §7).</summary>
public sealed partial class LaunchViewModel : ObservableObject
{
    private readonly IInstallationContext _installation;
    private readonly IArchiveCatalog _catalog;
    private readonly IProfileStore _profiles;
    private readonly IStartScriptStore _scripts;
    private readonly ILaunchService _launcher;
    private readonly ISettingsService _settings;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<LaunchViewModel> _logger;

    /// <summary>The load currently running, shared by overlapping callers.</summary>
    private Task? _loading;

    /// <summary>Suppresses persistence while the bound collections are being rebuilt.</summary>
    private bool _isSyncing;

    /// <summary>
    /// The page as it stood when the current profile was loaded, which is what the dirty
    /// marker compares against.
    ///
    /// Deliberately not the stored profile itself. A profile leaves <c>MenuName</c> and
    /// <c>ScriptFileName</c> null to mean "whatever the page has selected", and the
    /// pickers then default to their first entry — so comparing against the stored profile
    /// makes an untouched Chobby profile show up as edited the moment it loads.
    /// </summary>
    private LaunchProfile? _baseline;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string InstallPath { get; set; }

    /// <summary>Set when no install validated; drives the InfoBar on the page.</summary>
    [ObservableProperty]
    public partial string? InstallProblem { get; set; }

    /// <summary>Why the current selection cannot be launched, or null when it can.</summary>
    [ObservableProperty]
    public partial string? LaunchProblem { get; set; }

    /// <summary>The command the Launch button would run, shown as a preview.</summary>
    [ObservableProperty]
    public partial string? CommandPreview { get; set; }

    /// <summary>
    /// A caution about the current selection that does not stop a launch, shown as a
    /// warning InfoBar under the Launch button.
    /// </summary>
    [ObservableProperty]
    public partial string? LaunchWarning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenEngineFolderCommand))]
    public partial EngineBuild? SelectedEngine { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetDefaultProfileCommand))]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileHeader))]
    public partial LaunchProfile? SelectedProfile { get; set; }

    /// <summary>
    /// Whether the page differs from the profile as stored. Page controls are edited
    /// freely and only written on Save, so a one-off run — safe mode, say — does not
    /// quietly rewrite the profile the user launches from every day.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertProfileCommand))]
    [NotifyPropertyChangedFor(nameof(ProfileHeader))]
    public partial bool IsProfileDirty { get; set; }

    /// <summary>The result of the last profile operation, shown under the picker.</summary>
    [ObservableProperty]
    public partial string ProfileStatus { get; set; }

    /// <summary>
    /// Whether Save records the engine picker on the profile. Off leaves
    /// <see cref="LaunchProfile.EngineName"/> null, which is what keeps a profile working
    /// across an engine upgrade; on is for a profile that means a specific build, such as
    /// "reproduce the bug on 2026.06.11".
    /// </summary>
    [ObservableProperty]
    public partial bool PinEngineToProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsClientOptions))]
    [NotifyPropertyChangedFor(nameof(MenuPickerEnabled))]
    [NotifyPropertyChangedFor(nameof(ScriptPickerEnabled))]
    public partial LaunchMode SelectedMode { get; set; }

    [ObservableProperty]
    public partial MenuArchive? SelectedMenu { get; set; }

    [ObservableProperty]
    public partial StartScriptFile? SelectedScript { get; set; }

    [ObservableProperty]
    public partial string? ExtraArguments { get; set; }

    // ---- run options ----------------------------------------------------------------
    // These map one-to-one onto engine switches and, unlike the pickers above, belong to
    // the profile: a profile is how "my headless autohost" differs from "my windowed
    // debug run", so a toggle that forgot itself on the next visit would defeat the point.

    /// <summary><c>--isolation</c>. On by default, matching a fresh profile.</summary>
    [ObservableProperty]
    public partial bool UseIsolation { get; set; }

    /// <summary><c>--safemode</c>.</summary>
    [ObservableProperty]
    public partial bool UseSafeMode { get; set; }

    /// <summary><c>--only-local</c>.</summary>
    [ObservableProperty]
    public partial bool OnlyLocal { get; set; }

    /// <summary>The install's <c>PRD_*</c> downloader settings. See the profile.</summary>
    [ObservableProperty]
    public partial bool UseInstallEnvironment { get; set; }

    /// <summary><c>--window</c> / <c>--fullscreen</c> / neither.</summary>
    [ObservableProperty]
    public partial EngineWindowMode WindowMode { get; set; }

    /// <summary>Overrides <c>--write-dir</c>. Blank uses the install's data folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteDirectoryEffective))]
    public partial string? WriteDirectoryOverride { get; set; }

    /// <summary><c>--config</c>. Blank uses the write-dir's own springsettings.cfg.</summary>
    [ObservableProperty]
    public partial string? ConfigFilePath { get; set; }

    /// <summary>Where the write-dir box actually resolves to, shown under the box.</summary>
    public string WriteDirectoryEffective =>
        string.IsNullOrWhiteSpace(WriteDirectoryOverride)
            ? _installation.Current?.DataPath ?? "the install's data folder"
            : WriteDirectoryOverride;

    public EngineWindowMode[] WindowModes { get; } =
    {
        EngineWindowMode.Default,
        EngineWindowMode.Windowed,
        EngineWindowMode.Fullscreen,
    };

    /// <summary>
    /// spring-dedicated.exe registers none of the client switches, so the page greys them
    /// out rather than offering a toggle the command builder would then drop.
    /// </summary>
    public bool SupportsClientOptions => SelectedMode is not LaunchMode.Dedicated;

    /// <summary>Only <see cref="LaunchMode.Menu"/> reads <see cref="SelectedMenu"/>; grey the picker out otherwise.</summary>
    public bool MenuPickerEnabled => SelectedMode is LaunchMode.Menu;

    /// <summary>Every other mode reads <see cref="SelectedScript"/> instead of the menu.</summary>
    public bool ScriptPickerEnabled => SelectedMode is not LaunchMode.Menu;

    public bool HasProfile => SelectedProfile is not null;

    /// <summary>The shipped profile stays, or a fresh install has no way to start.</summary>
    public bool CanDeleteProfile => SelectedProfile is { IsBuiltIn: false };

    public bool CanSetDefaultProfile => SelectedProfile is { IsDefault: false };

    /// <summary>
    /// Save stays available on an unedited profile, rather than following the dirty
    /// marker. A profile that leaves the menu or script null follows whatever the page has
    /// selected, and pinning that current selection is a real thing to want — but it is
    /// not an edit, so a dirty-gated Save could never do it.
    /// </summary>
    private bool CanSaveProfile => SelectedProfile is not null;

    /// <summary>Name, whether it is pinned, and a dot while there are unsaved edits.</summary>
    public string ProfileHeader => SelectedProfile is not { } profile
        ? "No profile"
        : profile.Name
          + (profile.IsDefault ? "  (default)" : string.Empty)
          + (IsProfileDirty ? "  •" : string.Empty);

    public ObservableCollection<EngineBuild> Engines { get; } = new();

    public ObservableCollection<LaunchProfile> Profiles { get; } = new();

    public ObservableCollection<MenuArchive> Menus { get; } = new();

    public ObservableCollection<StartScriptFile> Scripts { get; } = new();

    public ObservableCollection<RunningInstance> Instances { get; } = new();

    /// <summary>The modes implemented so far. Skirmish arrives in Phase 5.</summary>
    public LaunchMode[] Modes { get; } =
    {
        LaunchMode.Menu,
        LaunchMode.Script,
        LaunchMode.Headless,
        LaunchMode.Dedicated,
    };

    public LaunchViewModel(
        IInstallationContext installation,
        IArchiveCatalog catalog,
        IProfileStore profiles,
        IStartScriptStore scripts,
        ILaunchService launcher,
        ISettingsService settings,
        IShellService shell,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<LaunchViewModel> logger)
    {
        _installation = installation;
        _catalog = catalog;
        _profiles = profiles;
        _scripts = scripts;
        _launcher = launcher;
        _settings = settings;
        _shell = shell;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = logger;

        InstallPath = "Looking for a Beyond All Reason installation…";
        ProfileStatus = string.Empty;

        // Partial properties cannot carry an initialiser, so the default that matches a
        // fresh profile is set here.
        UseIsolation = true;
        UseInstallEnvironment = true;

        _installation.Changed += (_, _) => _dispatcher.Post(() => _ = LoadAsync());
        _launcher.InstancesChanged += (_, _) => _dispatcher.Post(SyncInstances);
    }

    public bool HasInstallation => _installation.Current is not null;

    /// <summary>
    /// Shared by the page's Loaded event and IInstallationContext.Changed, which overlap
    /// on startup. Both callers are on the UI thread, so no lock is needed.
    /// </summary>
    [RelayCommand]
    private Task LoadAsync() =>
        _loading is { IsCompleted: false } inFlight ? inFlight : _loading = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
            await _installation.InitializeAsync();
            await _profiles.LoadAsync();

            if (_installation.Current is { } installation)
            {
                // Needed for the menu picker: the built-in Chobby profile resolves its
                // menu name from the catalog at launch time.
                await _catalog.LoadAsync(installation);
            }

            await SyncFromContextAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _installation.RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        LaunchProfile? profile = BuildProfileFromSelection();
        if (profile is null)
        {
            return;
        }

        LaunchResult result = await _launcher.LaunchAsync(profile, SelectedEngine);

        if (!result.Success)
        {
            LaunchProblem = result.Error;
            await _dialogs.ShowMessageAsync("Could not launch", result.Error ?? "Unknown error.");
        }
    }

    [RelayCommand]
    private async Task CopyCommandLineAsync()
    {
        if (CommandPreview is { Length: > 0 } preview)
        {
            await _shell.SetClipboardTextAsync(preview);
        }
    }

    [RelayCommand]
    private void Kill(RunningInstance? instance)
    {
        if (instance is not null)
        {
            _launcher.Kill(instance.ProcessId);
        }
    }

    [RelayCommand]
    private async Task ChangeInstallationAsync()
    {
        string? picked = await _dialogs.PickFolderAsync(settingsIdentifier: "BarInstall");
        if (picked is null)
        {
            return;
        }

        InstallationProbe probe = await _installation.SetInstallationAsync(picked);
        if (!probe.IsValid)
        {
            await _dialogs.ShowMessageAsync(
                "Not a Beyond All Reason installation",
                picked + Environment.NewLine + Environment.NewLine +
                "Rejected because " + probe.Reason + ".");
        }
    }

    [RelayCommand]
    private void OpenInstallFolder() => OpenIfPresent(_installation.Current?.RootPath);

    [RelayCommand]
    private void OpenDataFolder() => OpenIfPresent(_installation.Current?.DataPath);

    [RelayCommand]
    private void OpenScriptLibrary() => OpenIfPresent(_scripts.LibraryPath);

    [RelayCommand(CanExecute = nameof(CanOpenEngineFolder))]
    private void OpenEngineFolder() => OpenIfPresent(SelectedEngine?.Path);

    private bool CanOpenEngineFolder() => SelectedEngine is not null;

    [RelayCommand]
    private void OpenInfolog()
    {
        if (_installation.Current?.IsolatedInfologPath is { } path)
        {
            _shell.OpenFile(path);
        }
    }

    private void OpenIfPresent(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _shell.OpenFolder(path);
        }
    }

    partial void OnSelectedEngineChanged(EngineBuild? value)
    {
        UpdatePreview();

        if (_isSyncing || value is null || value.Name == _settings.Current.SelectedEngineName)
        {
            return;
        }

        _settings.Current.SelectedEngineName = value.Name;
        _ = _settings.SaveAsync();
    }

    partial void OnSelectedModeChanged(LaunchMode value) => UpdatePreview();

    partial void OnSelectedMenuChanged(MenuArchive? value) => UpdatePreview();

    partial void OnSelectedScriptChanged(StartScriptFile? value) => UpdatePreview();

    partial void OnExtraArgumentsChanged(string? value) => UpdatePreview();

    partial void OnPinEngineToProfileChanged(bool value) => UpdatePreview();

    // Toggles and the mode combo commit as they change; the text boxes only update the
    // preview here and are written to the profile on the way out (CommitRunOptions), so
    // typing an argument does not rewrite profiles.json once per character.
    partial void OnUseIsolationChanged(bool value) => CommitRunOptions();

    partial void OnUseSafeModeChanged(bool value) => CommitRunOptions();

    partial void OnOnlyLocalChanged(bool value) => CommitRunOptions();

    partial void OnUseInstallEnvironmentChanged(bool value) => CommitRunOptions();

    partial void OnWindowModeChanged(EngineWindowMode value) => CommitRunOptions();

    partial void OnWriteDirectoryOverrideChanged(string? value) => UpdatePreview();

    partial void OnConfigFilePathChanged(string? value) => UpdatePreview();

    /// <summary>
    /// Refreshes the preview and the dirty marker. Called by the toggles as they change
    /// and by the text boxes on lost focus. Nothing is persisted here — the profile is
    /// only written by Save, so the page doubles as a scratch pad for a one-off run.
    /// </summary>
    [RelayCommand]
    private void CommitRunOptions() => UpdatePreview();

    /// <summary>
    /// The run options as the page has them. Blank paths become null so the builder's
    /// "not set" test — IsNullOrWhiteSpace — and the stored JSON agree.
    /// </summary>
    private void CopyRunOptionsTo(LaunchProfile profile)
    {
        profile.UseIsolation = UseIsolation;
        profile.UseSafeMode = UseSafeMode;
        profile.OnlyLocal = OnlyLocal;
        profile.UseInstallEnvironment = UseInstallEnvironment;
        profile.WindowMode = WindowMode;
        profile.ExtraArguments = NullIfBlank(ExtraArguments);
        profile.WriteDirectoryOverride = NullIfBlank(WriteDirectoryOverride);
        profile.ConfigFilePath = NullIfBlank(ConfigFilePath);
    }

    /// <summary>
    /// Everything the page holds, written onto a profile: what to run as well as the run
    /// options. This is what Save persists, and what the dirty test compares against.
    /// </summary>
    private void ApplySelectionTo(LaunchProfile profile)
    {
        profile.Mode = SelectedMode;
        profile.MenuName = SelectedMenu?.Name;
        profile.ScriptFileName = SelectedScript?.FileName;
        profile.EngineName = PinEngineToProfile ? SelectedEngine?.Name : null;
        CopyRunOptionsTo(profile);
    }

    /// <summary>
    /// Whether the profile as stored already says what the page says. Compared field by
    /// field rather than by serialising, so a future field added to
    /// <see cref="LaunchProfile"/> that the page does not edit — <c>IsDefault</c>, for
    /// one — cannot make an untouched profile look dirty.
    /// </summary>
    /// <summary>Everything the page currently says, as a profile.</summary>
    private LaunchProfile CaptureSelection()
    {
        LaunchProfile snapshot = SelectedProfile?.Clone() ?? new LaunchProfile();
        ApplySelectionTo(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Whether two profiles would launch identically. Only the fields the Launch page
    /// edits are compared: <c>Id</c>, <c>Name</c>, <c>IsDefault</c> and <c>IsBuiltIn</c>
    /// are identity, not configuration, so pinning a profile as the default must not make
    /// it look edited.
    ///
    /// Paths and the script file name compare case-insensitively because Windows treats
    /// them that way; extra arguments do not, since the engine passes them through
    /// verbatim and a Lua identifier's case matters.
    /// </summary>
    internal static bool DescribesSameRun(LaunchProfile stored, LaunchProfile candidate) =>
        candidate.Mode == stored.Mode
        && string.Equals(candidate.MenuName, stored.MenuName, StringComparison.Ordinal)
        && string.Equals(candidate.ScriptFileName, stored.ScriptFileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.EngineName, stored.EngineName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.ExtraArguments, stored.ExtraArguments, StringComparison.Ordinal)
        && string.Equals(candidate.WriteDirectoryOverride, stored.WriteDirectoryOverride, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.ConfigFilePath, stored.ConfigFilePath, StringComparison.OrdinalIgnoreCase)
        && candidate.UseIsolation == stored.UseIsolation
        && candidate.UseSafeMode == stored.UseSafeMode
        && candidate.OnlyLocal == stored.OnlyLocal
        && candidate.UseInstallEnvironment == stored.UseInstallEnvironment
        && candidate.WindowMode == stored.WindowMode;

    private void RefreshProfileDirty() =>
        IsProfileDirty = !_isSyncing
                         && SelectedProfile is not null
                         && _baseline is { } baseline
                         && !DescribesSameRun(baseline, CaptureSelection());

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Picks the folder the run writes its infolog and config into.</summary>
    [RelayCommand]
    private async Task BrowseWriteDirectoryAsync()
    {
        if (await _dialogs.PickFolderAsync(settingsIdentifier: "BarWriteDir") is { } picked)
        {
            WriteDirectoryOverride = picked;
            CommitRunOptions();
        }
    }

    /// <summary>Picks the springsettings.cfg a run uses instead of the write-dir's own.</summary>
    [RelayCommand]
    private async Task BrowseConfigFileAsync()
    {
        if (await _dialogs.PickFileAsync(new[] { ".cfg", "*" }, settingsIdentifier: "BarConfig") is { } picked)
        {
            ConfigFilePath = picked;
            CommitRunOptions();
        }
    }

    /// <summary>Clears both path overrides, back to whatever the install supplies.</summary>
    [RelayCommand]
    private void ClearPathOverrides()
    {
        WriteDirectoryOverride = null;
        ConfigFilePath = null;
        CommitRunOptions();
    }

    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanSetDefaultProfile));
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SetDefaultProfileCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            IsProfileDirty = false;
            return;
        }

        ApplyProfileToSelection(value);

        // ApplyProfileToSelection has just made the page match, so anything left over from
        // the profile that was showing before is stale.
        IsProfileDirty = false;
        ProfileStatus = string.Empty;

        if (_isSyncing || value.Id == _settings.Current.SelectedProfileId)
        {
            return;
        }

        _settings.Current.SelectedProfileId = value.Id;
        _ = _settings.SaveAsync();
    }

    // ---- profile management ---------------------------------------------------------
    // PLAN.md §5.9. The store already had save/delete/pin; these are the commands that
    // put them on the page.

    /// <summary>Writes the page back to the selected profile.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        // Saved as a copy rather than by mutating the live instance, so a failed write
        // leaves the in-memory profile as it was.
        LaunchProfile updated = profile.Clone();
        ApplySelectionTo(updated);

        await _profiles.SaveAsync(updated);
        ReloadProfiles(updated.Id);
        ProfileStatus = $"Saved '{updated.Name}'.";
    }

    /// <summary>Keeps the current profile as it is and stores the page as a new one.</summary>
    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task SaveProfileAsAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        string? name = await PromptForProfileNameAsync("Save profile as", SuggestCopyName(profile.Name));

        if (name is null)
        {
            return;
        }

        LaunchProfile copy = profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name;

        // A copy of the shipped profile is an ordinary profile: deletable, and not the
        // pinned one until the user says so.
        copy.IsBuiltIn = false;
        copy.IsDefault = false;
        ApplySelectionTo(copy);

        await _profiles.SaveAsync(copy);
        ReloadProfiles(copy.Id);
        ProfileStatus = $"Saved as '{copy.Name}'.";
    }

    /// <summary>A profile with stock settings, rather than a copy of the current one.</summary>
    [RelayCommand]
    private async Task NewProfileAsync()
    {
        string? name = await PromptForProfileNameAsync("New profile", "New profile");

        if (name is null)
        {
            return;
        }

        LaunchProfile created = new()
        {
            Name = name,
            Mode = SelectedMode,
            MenuName = SelectedMenu?.Name,
            ScriptFileName = SelectedScript?.FileName,
        };

        await _profiles.SaveAsync(created);
        ReloadProfiles(created.Id);
        ProfileStatus = $"Created '{created.Name}'.";
    }

    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task RenameProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        string? name = await PromptForProfileNameAsync("Rename profile", profile.Name, profile.Id);

        if (name is null || string.Equals(name, profile.Name, StringComparison.Ordinal))
        {
            return;
        }

        LaunchProfile renamed = profile.Clone();
        renamed.Name = name;

        // Renaming keeps whatever is on the page, so an unsaved edit is not silently
        // committed by a rename.
        if (IsProfileDirty)
        {
            ApplySelectionTo(renamed);
        }

        await _profiles.SaveAsync(renamed);
        ReloadProfiles(renamed.Id);
        ProfileStatus = $"Renamed to '{renamed.Name}'.";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            "Delete profile",
            $"Delete '{profile.Name}'? Start scripts and engines are not touched.",
            "Delete");

        if (!confirmed)
        {
            return;
        }

        if (!await _profiles.DeleteAsync(profile.Id))
        {
            ProfileStatus = $"Could not delete '{profile.Name}'.";
            return;
        }

        // Cleared first, or ReloadProfiles restores the selection that has just gone.
        SelectedProfile = null;
        ReloadProfiles(_profiles.Default?.Id);
        ProfileStatus = $"Deleted '{profile.Name}'.";
    }

    /// <summary>Discards the page's edits and loads the profile as stored.</summary>
    [RelayCommand(CanExecute = nameof(IsProfileDirty))]
    private void RevertProfile()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        ApplyProfileToSelection(profile);
        IsProfileDirty = false;
        ProfileStatus = $"Reloaded '{profile.Name}'.";
    }

    /// <summary>Pins the profile the shell's primary button runs.</summary>
    [RelayCommand(CanExecute = nameof(CanSetDefaultProfile))]
    private async Task SetDefaultProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await _profiles.SetDefaultAsync(profile.Id);
        ReloadProfiles(profile.Id);
        ProfileStatus = $"'{profile.Name}' is now the default.";
    }

    /// <summary>
    /// Asks for a profile name, refusing a blank or one already in use. Validated on every
    /// keystroke so a clash is refused before the dialog closes.
    /// </summary>
    private Task<string?> PromptForProfileNameAsync(string title, string? initial, string? ignoreId = null) =>
        _dialogs.PromptForTextAsync(
            title,
            "Profile name:",
            initial,
            "OK",
            candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return "Enter a name.";
                }

                bool taken = _profiles.Profiles.Any(
                    p => p.Id != ignoreId && string.Equals(p.Name, candidate.Trim(), StringComparison.OrdinalIgnoreCase));

                return taken ? "A profile with that name already exists." : null;
            });

    /// <summary>"Chobby (menu)" → "Chobby (menu) copy", then "copy 2" and so on.</summary>
    private string SuggestCopyName(string name)
    {
        string candidate = name + " copy";

        for (int n = 2; Taken(candidate); n++)
        {
            candidate = $"{name} copy {n}";
        }

        return candidate;

        bool Taken(string value) => _profiles.Profiles.Any(
            p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Refills the picker from the store and selects <paramref name="selectId"/>. The
    /// store hands back new instances, so the selection is restored by id rather than by
    /// reference.
    /// </summary>
    private void ReloadProfiles(string? selectId)
    {
        bool wasSyncing = _isSyncing;
        _isSyncing = true;
        try
        {
            Profiles.Clear();
            foreach (LaunchProfile profile in _profiles.Profiles)
            {
                Profiles.Add(profile);
            }
        }
        finally
        {
            _isSyncing = wasSyncing;
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectId)
                          ?? _profiles.Default
                          ?? Profiles.FirstOrDefault();

        // Selecting the same id after a save hands back a different instance, so the
        // change handler runs and clears the marker. Re-baselining here too covers the
        // case where the collection was rebuilt with the selection unchanged.
        _baseline = CaptureSelection();
        IsProfileDirty = false;
        OnPropertyChanged(nameof(ProfileHeader));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanSetDefaultProfile));
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SetDefaultProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Copies a profile's stored choices into the pickers.</summary>
    private void ApplyProfileToSelection(LaunchProfile profile)
    {
        bool wasSyncing = _isSyncing;
        _isSyncing = true;
        try
        {
            SelectedMode = profile.Mode;
            PinEngineToProfile = profile.EngineName is { Length: > 0 };
            ExtraArguments = profile.ExtraArguments;
            UseIsolation = profile.UseIsolation;
            UseSafeMode = profile.UseSafeMode;
            OnlyLocal = profile.OnlyLocal;
            UseInstallEnvironment = profile.UseInstallEnvironment;
            WindowMode = profile.WindowMode;
            WriteDirectoryOverride = profile.WriteDirectoryOverride;
            ConfigFilePath = profile.ConfigFilePath;

            if (profile.EngineName is { Length: > 0 } engineName)
            {
                SelectedEngine = Engines.FirstOrDefault(
                    e => string.Equals(e.Name, engineName, StringComparison.OrdinalIgnoreCase)) ?? SelectedEngine;
            }

            SelectedMenu = Menus.FirstOrDefault(
                    m => string.Equals(m.Name, profile.MenuName, StringComparison.Ordinal))
                ?? Menus.FirstOrDefault();

            // A profile that names no script — the built-in Chobby one, for instance —
            // must not clear the picker, or switching to Script mode has nothing to run.
            SelectedScript = Scripts.FirstOrDefault(
                                 s => string.Equals(s.FileName, profile.ScriptFileName, StringComparison.OrdinalIgnoreCase))
                             ?? SelectedScript
                             ?? Scripts.FirstOrDefault();
        }
        finally
        {
            _isSyncing = wasSyncing;
        }

        // Taken before UpdatePreview, which is what evaluates the dirty marker against it.
        _baseline = CaptureSelection();

        UpdatePreview();
    }

    /// <summary>
    /// A throwaway profile carrying what the pickers currently say. The saved profile is
    /// the starting point; the page's controls override it for this run.
    /// </summary>
    private LaunchProfile? BuildProfileFromSelection()
    {
        LaunchProfile profile = SelectedProfile?.Clone() ?? LaunchProfile.CreateDefaultChobbyProfile();

        profile.Mode = SelectedMode;
        profile.MenuName = SelectedMenu?.Name;
        profile.ScriptFileName = SelectedScript?.FileName;
        CopyRunOptionsTo(profile);

        // Left null so the launcher uses the page's engine selection, which keeps a
        // profile working after an engine upgrade.
        profile.EngineName = null;

        return profile;
    }

    /// <summary>
    /// The folders a Beyond All Reason data directory keeps its archives in. Any one of
    /// them is enough to call a folder plausible; a fresh sandbox has none.
    /// </summary>
    private static readonly string[] ContentFolders = { "maps", "games", "packages", "pool" };

    /// <summary>
    /// Catches the one combination on this page that starts the engine and then kills it.
    ///
    /// <c>--isolation</c> limits the archive scan to the write directory and the engine
    /// folder. Point <c>--write-dir</c> at a folder with no content and the engine gets
    /// as far as the map, then exits with a content_error naming an archive it cannot
    /// resolve — which reads like a broken install rather than a launcher setting.
    /// </summary>
    internal static string? BuildWarning(LaunchProfile profile)
    {
        if (!profile.UseIsolation || string.IsNullOrWhiteSpace(profile.WriteDirectoryOverride))
        {
            return null;
        }

        string directory = profile.WriteDirectoryOverride;

        if (ContentFolders.Any(f => Directory.Exists(Path.Combine(directory, f))))
        {
            return null;
        }

        return $"Isolation is on and the write directory '{directory}' holds no maps, games, " +
               "packages or pool folder. The engine will only see archives there and in the " +
               "engine folder, so it will start and then fail to resolve the map. Either turn " +
               "isolation off or point the write directory at the install's data folder.";
    }

    private void UpdatePreview()
    {
        LaunchProfile? profile = BuildProfileFromSelection();

        if (profile is null || _installation.Current is null)
        {
            CommandPreview = null;
            LaunchProblem = null;
            LaunchWarning = null;
            return;
        }

        (EngineCommandLine? command, string? error) = _launcher.Preview(profile, SelectedEngine);

        CommandPreview = command?.ToDisplayString();
        LaunchProblem = error;
        LaunchWarning = BuildWarning(profile);

        // Every picker and toggle on the page routes through here, so this is the one
        // place the dirty marker has to be kept up to date.
        RefreshProfileDirty();
    }

    private async Task SyncFromContextAsync()
    {
        BarInstallation? current = _installation.Current;

        _isSyncing = true;
        try
        {
            if (current is null)
            {
                InstallPath = "No installation found";
                InstallProblem = DescribeProbeFailures();
                Engines.Clear();
                Menus.Clear();
                Scripts.Clear();
                SelectedEngine = null;
                OnPropertyChanged(nameof(HasInstallation));
                return;
            }

            InstallPath = current.RootPath;
            InstallProblem = _installation.Engines.Count == 0
                ? @"No engine builds were found under data\engine."
                : null;

            SyncEngines();
            SyncMenus();
            await SyncScriptsAsync();
            SyncProfiles();
        }
        finally
        {
            _isSyncing = false;
        }

        SyncInstances();
        UpdatePreview();
        OnPropertyChanged(nameof(HasInstallation));

        _logger.LogDebug(
            "Launch page bound {Engines} engine(s), {Menus} menu(s), {Scripts} script(s), {Profiles} profile(s); engine {Engine}.",
            Engines.Count,
            Menus.Count,
            Scripts.Count,
            Profiles.Count,
            SelectedEngine?.Name ?? "(none)");
    }

    private void SyncEngines()
    {
        string? wanted = SelectedEngine?.Name ?? _settings.Current.SelectedEngineName;

        // Refilling makes the bound ComboBox push null back through SelectedItem, so
        // only touch the collection when it has actually changed.
        if (!Engines.SequenceEqual(_installation.Engines))
        {
            Engines.Clear();
            foreach (EngineBuild engine in _installation.Engines)
            {
                Engines.Add(engine);
            }
        }

        SelectedEngine =
            Engines.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
            // The newest build that can do everything: a local `development` build often
            // sorts to the top while shipping only spring.exe (PLAN.md §2.2).
            ?? Engines.FirstOrDefault(e => e.Capabilities.IsComplete)
            ?? Engines.FirstOrDefault(e => e.Capabilities.HasSpring)
            ?? Engines.FirstOrDefault();
    }

    private void SyncMenus()
    {
        string? wanted = SelectedMenu?.Name;

        if (!Menus.SequenceEqual(_catalog.Index.Menus))
        {
            Menus.Clear();
            foreach (MenuArchive menu in _catalog.Index.Menus)
            {
                Menus.Add(menu);
            }
        }

        SelectedMenu = Menus.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.Ordinal))
                       ?? Menus.FirstOrDefault();
    }

    private async Task SyncScriptsAsync()
    {
        IReadOnlyList<StartScriptFile> scripts = await _scripts.ListAsync();
        string? wanted = SelectedScript?.FileName;

        Scripts.Clear();
        foreach (StartScriptFile script in scripts)
        {
            Scripts.Add(script);
        }

        SelectedScript = Scripts.FirstOrDefault(
                             s => string.Equals(s.FileName, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? Scripts.FirstOrDefault();
    }

    private void SyncProfiles() =>
        ReloadProfiles(SelectedProfile?.Id ?? _settings.Current.SelectedProfileId);

    private void SyncInstances()
    {
        Instances.Clear();
        foreach (RunningInstance instance in _launcher.Instances)
        {
            Instances.Add(instance);
        }
    }

    private string DescribeProbeFailures()
    {
        if (_installation.LastProbes.Count == 0)
        {
            return "No candidate locations were checked.";
        }

        string tried = string.Join(
            Environment.NewLine,
            _installation.LastProbes.Select(p => "• " + p.Path + " — " + p.Reason));

        return "Checked these locations:" + Environment.NewLine + tried;
    }
}
