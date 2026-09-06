using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>
/// The infolog viewer (PLAN.md §5.8): pick a run, see what it was and how it ended, read
/// its log filtered, and get its start script back out.
/// </summary>
public sealed partial class InfologViewModel : ObservableObject
{
    /// <summary>
    /// The section filter's "no filter" entry. A sentinel string rather than a null item,
    /// because a <c>ComboBox</c> cannot distinguish a null selection from no selection.
    /// </summary>
    public const string AnySection = "(all sections)";

    /// <summary>
    /// How many logs the picker offers. This install has 773; a developer wants the last
    /// few days, and the rest are reachable through the folder button.
    /// </summary>
    private const int MaxFilesShown = 200;

    /// <summary>
    /// Typing in the search box triggers a re-read of the file, so keystrokes are
    /// coalesced. Long enough to swallow a word, short enough not to feel laggy.
    /// </summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How often the selected log is checked for growth while the page is open. The
    /// engine writes in bursts, not continuously, so this is a compromise between feeling
    /// live and stat-ing a file nobody is watching.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IInfologCatalog _catalog;
    private readonly IInfologParser _parser;
    private readonly IStartScriptRecovery _recovery;
    private readonly ILaunchService _launcher;
    private readonly IStartScriptStore _scripts;
    private readonly IInstallationContext _installation;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<InfologViewModel> _logger;

    private Task? _loading;

    /// <summary>Cancels the read a new filter or file selection has superseded.</summary>
    private CancellationTokenSource? _read;

    /// <summary>True while the code, not the user, is changing the file selection.</summary>
    private bool _isSyncing;

    /// <summary>
    /// Suppresses the automatic re-read while several filter properties are set together.
    /// Without it, "Problems only" would start three reads and then a fourth.
    /// </summary>
    private bool _suppressReads;

    /// <summary>Polls the selected file for growth while the page is open (<see cref="OnActivated"/>).</summary>
    private Timer? _pollTimer;

    /// <summary>Where a live tail should resume from, and what file it belongs to.</summary>
    private InfologTailCursor? _tailCursor;

    private string? _tailPath;

    /// <summary>
    /// Running totals behind <see cref="LinesStatus"/>, kept in step with every tail so the
    /// count is right without re-scanning the file to get it.
    /// </summary>
    private int _totalMatchCount;

    private int _totalLineCount;

    /// <summary>True once a live tail has evicted a line to stay within <see cref="InfologRead.LineLimit"/>.</summary>
    private bool _trimmedForLive;

    /// <summary>
    /// Guards against a tail and a filter-driven read racing each other onto the same
    /// file. Only the tail path checks it — a filter change always wins, and a stale tail
    /// response is caught separately by <see cref="_generation"/>.
    /// </summary>
    private int _tailBusy;

    /// <summary>
    /// Bumped by every full read. A tail response is dropped if this has moved on since
    /// the tail was requested — the file or filter changed underneath it, and the read
    /// that caused that change already replaced what it would have appended to.
    /// </summary>
    private int _generation;

    /// <summary>Raised just before <see cref="Lines"/> is cleared for a fresh (non-live) read.</summary>
    public event EventHandler? LinesReloading;

    /// <summary>Raised once a fresh read has repopulated <see cref="Lines"/>.</summary>
    public event EventHandler? LinesReloaded;

    /// <summary>Raised after a live tail appends lines without touching what was already shown.</summary>
    public event EventHandler<LinesAppendedEventArgs>? LinesAppended;

    public InfologViewModel(
        IInfologCatalog catalog,
        IInfologParser parser,
        IStartScriptRecovery recovery,
        ILaunchService launcher,
        IStartScriptStore scripts,
        IInstallationContext installation,
        IShellService shell,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<InfologViewModel> logger)
    {
        _catalog = catalog;
        _parser = parser;
        _recovery = recovery;
        _launcher = launcher;
        _scripts = scripts;
        _installation = installation;
        _shell = shell;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = logger;

        // A partial property cannot carry an initializer under C# 13, so defaults live
        // in the constructor throughout.
        MinimumSeverity = InfologSeverity.Info;
        SelectedSection = AnySection;
        Status = string.Empty;
        LinesStatus = string.Empty;
        IsLive = true;
        Sections.Add(AnySection);

        _installation.Changed += (_, _) => _dispatcher.Post(() => _ = RefreshAsync());
    }

    // ---- state ------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Whether the selected log is followed for growth while it is open. On by default;
    /// a user mid-investigation can turn it off to stop the view moving under them
    /// without losing their place, then back on to pick up whatever was written meanwhile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    // Every command gated on HasFile has to be listed here, or it stays disabled from
    // startup: CanExecute is only re-evaluated when something tells it to.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowProblemsOnlyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyShownLinesCommand))]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial InfologFile? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    [NotifyPropertyChangedFor(nameof(FailureText))]
    [NotifyPropertyChangedFor(nameof(RunDescription))]
    public partial InfologSummary? Summary { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RerunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyPropertyChangedFor(nameof(HasRecoveredScript))]
    public partial RecoveredScript? Recovered { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    public partial ScriptComparison? Comparison { get; set; }

    [ObservableProperty]
    public partial string LinesStatus { get; set; }

    // ---- filter -----------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    public partial InfologSeverity MinimumSeverity { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    public partial string? SelectedSection { get; set; }

    public ObservableCollection<InfologFile> Files { get; } = new();

    public ObservableCollection<InfologLine> Lines { get; } = new();

    public ObservableCollection<string> Sections { get; } = new();

    public ObservableCollection<DiffLine> DiffLines { get; } = new();

    public InfologSeverity[] SeverityLevels { get; } =
    {
        InfologSeverity.Info,
        InfologSeverity.Warning,
        InfologSeverity.Error,
        InfologSeverity.Fatal,
    };

    public bool HasFile => SelectedFile is not null;

    public bool HasSummary => Summary is not null;

    public bool HasFailure => Summary?.Failure is not null;

    public bool HasRecoveredScript => Recovered is { HasScript: true };

    public bool HasComparison => Comparison is not null;

    public bool HasInstallation => _installation.Current is not null;

    public string FailureText => Summary?.Failure is { } failure
        ? failure.Caption is { Length: > 0 } caption
            ? $"{caption}: {failure.Message}"
            : failure.Message
        : string.Empty;

    /// <summary>
    /// What the run was, from the game's own <c>infologVersionTags</c> line where it got
    /// far enough to write one, and from the engine's banner where it did not.
    /// </summary>
    public string RunDescription
    {
        get
        {
            if (Summary is not { } summary)
            {
                return string.Empty;
            }

            var parts = new List<string>(5);

            if (summary.TagMap is { Length: > 0 })
            {
                parts.Add(summary.TagMap);
            }

            if (summary.TagGame is { Length: > 0 })
            {
                parts.Add(summary.TagGame);
            }

            if (summary.TagLobby is { Length: > 0 })
            {
                parts.Add(summary.TagLobby);
            }

            if (summary.EngineFolder is { Length: > 0 } folder)
            {
                parts.Add("engine " + folder);
            }
            else if (summary.EngineVersion is { Length: > 0 } version)
            {
                parts.Add(version);
            }

            parts.Add(summary.IsIsolated ? "isolated" : "not isolated");

            return string.Join(" · ", parts);
        }
    }

    // ---- loading ----------------------------------------------------------------

    [RelayCommand]
    private Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        await _installation.InitializeAsync();
        await RefreshAsync();
    }

    /// <summary>
    /// Re-lists the logs and shows the newest, which is the run that just finished — the
    /// question this page exists to answer.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            string? keep = SelectedFile?.Path;
            IReadOnlyList<InfologFile> found = await Task.Run(() => _catalog.Discover());

            _isSyncing = true;
            try
            {
                Files.Clear();
                foreach (InfologFile file in found.Take(MaxFilesShown))
                {
                    Files.Add(file);
                }
            }
            finally
            {
                _isSyncing = false;
            }

            InfologFile? next = keep is null
                ? Files.FirstOrDefault()
                : Files.FirstOrDefault(f => string.Equals(f.Path, keep, StringComparison.OrdinalIgnoreCase))
                  ?? Files.FirstOrDefault();

            Status = found.Count switch
            {
                0 => HasInstallation
                    ? "No engine logs were found yet. Launch the game once and come back."
                    : "No Beyond All Reason installation is selected.",
                1 => "1 log.",
                <= MaxFilesShown => $"{found.Count} logs.",
                _ => $"Newest {MaxFilesShown} of {found.Count} logs.",
            };

            if (ReferenceEquals(next, SelectedFile))
            {
                // Same file, but it has probably grown since — the live log is the one
                // being appended to right now.
                await ShowSelectedAsync();
            }
            else
            {
                SelectedFile = next;
            }
        }
        finally
        {
            IsBusy = false;
        }

        OnPropertyChanged(nameof(HasInstallation));
    }

    partial void OnSelectedFileChanged(InfologFile? value)
    {
        if (!_isSyncing)
        {
            _ = ShowSelectedAsync();
        }
    }

    /// <summary>
    /// How long, and how often, <see cref="ShowForRunAsync"/> waits for a just-started
    /// run's log to appear on disk before giving up and showing whatever is newest.
    /// </summary>
    private static readonly TimeSpan RunLookupDelay = TimeSpan.FromSeconds(1);

    private const int RunLookupAttempts = 5;

    /// <summary>
    /// Selects the log a run just started is writing to, from the Launch page's "landed
    /// here after launching" flow. The engine takes a moment to open its log file, so
    /// this polls briefly rather than showing whatever was newest before the launch.
    /// </summary>
    public async Task ShowForRunAsync(DateTimeOffset startedAt)
    {
        await LoadAsync();

        for (int attempt = 0; attempt < RunLookupAttempts; attempt++)
        {
            if (_catalog.FindForRun(startedAt, null) is { } file)
            {
                await ShowAsync(file);
                return;
            }

            await Task.Delay(RunLookupDelay);
        }

        // The log never showed up in time — an install problem, most likely, that the
        // launch itself already reported. Falling back to the newest log rather than
        // leaving the page on whatever it last had open.
        await RefreshAsync();
    }

    /// <summary>Selects a specific log, e.g. from the History page's "open log" button.</summary>
    public async Task ShowAsync(InfologFile file)
    {
        await LoadAsync();

        InfologFile? known = Files.FirstOrDefault(
            f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            // Older than the newest MaxFilesShown, so it is not in the picker. Adding it
            // is better than silently showing something else.
            Files.Insert(0, file);
            known = file;
        }

        SelectedFile = known;
    }

    private async Task ShowSelectedAsync()
    {
        Comparison = null;
        DiffLines.Clear();

        if (SelectedFile is not { } file)
        {
            Summary = null;
            Recovered = null;
            Lines.Clear();
            LinesStatus = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            Summary = await _parser.SummariseAsync(file);
            SyncSections(Summary);

            // The recovered script depends on the summary, so it has to follow it: the
            // logged path is what decides whether Chobby's file is a fallback or the
            // answer.
            Recovered = await _recovery.RecoverAsync(Summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not summarise {Path}.", file.Path);
            Status = $"Could not read {file.FileName}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        // Through the scheduler like every other read, so that switching file again while
        // this one is still streaming cancels it rather than racing it.
        ScheduleRead(TimeSpan.Zero);
    }

    /// <summary>
    /// Rebuilds the section filter for the file just loaded, keeping the current choice
    /// when the new file also has that section — switching between two runs of the same
    /// thing should not silently drop the filter.
    /// </summary>
    private void SyncSections(InfologSummary summary)
    {
        string? previous = SelectedSection;

        // Suppressed throughout: rebuilding the list moves the selection about, and the
        // caller reads the file itself once this returns.
        _suppressReads = true;
        try
        {
            Sections.Clear();
            Sections.Add(AnySection);

            foreach (string section in summary.Sections)
            {
                Sections.Add(section);
            }

            SelectedSection = previous is not null && Sections.Contains(previous) ? previous : AnySection;
        }
        finally
        {
            _suppressReads = false;
        }
    }

    // ---- line reading -----------------------------------------------------------

    // Each of these can fire as part of a batch — "Problems only" sets three at once — so
    // they all honour the suppression flag and the batch schedules a single read itself.
    partial void OnSearchTextChanged(string? value) => ScheduleReadUnlessSuppressed(SearchDelay);

    partial void OnMinimumSeverityChanged(InfologSeverity value) => ScheduleReadUnlessSuppressed(TimeSpan.Zero);

    partial void OnSelectedSectionChanged(string? value) => ScheduleReadUnlessSuppressed(TimeSpan.Zero);

    private void ScheduleReadUnlessSuppressed(TimeSpan delay)
    {
        if (!_suppressReads)
        {
            ScheduleRead(delay);
        }
    }

    /// <summary>
    /// Coalesces filter changes into one read.
    ///
    /// Every filter is applied while streaming the file rather than over a cached list,
    /// which is what keeps a 42 MB log affordable — nothing is held but the matches. The
    /// cost is that a filter change is a re-read, hence the delay on typing.
    /// </summary>
    private void ScheduleRead(TimeSpan delay)
    {
        // Read once, here. Taking it from the source inside the lambda below would be a
        // crash: by the time that runs, a later keystroke may have replaced the source,
        // and CancellationTokenSource.Token throws once the source is disposed — on the UI
        // thread, from inside a dispatcher callback, where nothing can catch it.
        CancellationToken token = ResetRead();

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                _dispatcher.Post(() => _ = ReadLinesAsync(token));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke.
            }
        });
    }

    /// <summary>
    /// Cancels the read in flight and returns the token for the one replacing it.
    ///
    /// The superseded source is cancelled but deliberately not disposed. It holds no
    /// unmanaged resource, and the task it belongs to is still holding its token — a
    /// cancelled token stays usable, a disposed one does not.
    /// </summary>
    private CancellationToken ResetRead()
    {
        var source = new CancellationTokenSource();
        CancellationToken token = source.Token;

        Interlocked.Exchange(ref _read, source)?.Cancel();

        return token;
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task ReloadAsync() => ShowSelectedAsync();

    private InfologFilter CurrentFilter() => new()
    {
        MinimumSeverity = MinimumSeverity,
        Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        Section = SelectedSection is null or AnySection ? null : SelectedSection,
    };

    private async Task ReadLinesAsync(CancellationToken cancellationToken)
    {
        if (SelectedFile is not { } file)
        {
            return;
        }

        // Bumped so any live tail already in flight is dropped by ApplyTail rather than
        // appended onto what this read is about to replace.
        Interlocked.Increment(ref _generation);
        InfologFilter filter = CurrentFilter();

        try
        {
            InfologRead read = await _parser.ReadAsync(file, filter, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LinesReloading?.Invoke(this, EventArgs.Empty);

            Lines.Clear();
            foreach (InfologLine line in read.Lines)
            {
                Lines.Add(line);
            }

            LinesStatus = DescribeRead(read, filter);

            // A live tail resumes from here. A read superseded by a newer one before this
            // point would have its stale cursor overwritten by that one in turn, so the
            // last write always wins regardless of ordering.
            _tailPath = file.Path;
            _tailCursor = read.Cursor;
            _totalMatchCount = read.MatchCount;
            _totalLineCount = read.LineCount;
            _trimmedForLive = false;

            LinesReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer filter is already on its way.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read lines from {Path}.", file.Path);
            LinesStatus = ex.Message;

            _tailPath = null;
            _tailCursor = null;
        }
    }

    // ---- live tail ----------------------------------------------------------------

    /// <summary>
    /// Starts following the selected log for growth. Called when the page becomes
    /// current, since polling a file nobody is looking at is only waste.
    /// </summary>
    public void OnActivated() => _pollTimer ??= new Timer(_ => PollTick(), null, PollInterval, PollInterval);

    /// <summary>Stops following, so navigating away leaves nothing polling in the background.</summary>
    public void OnDeactivated()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    /// <summary>
    /// Runs on the timer's own thread pool thread, not the UI thread — everything it
    /// touches is either read-only here or safe to read without synchronisation
    /// (<see cref="SelectedFile"/> is only ever replaced by a new reference, never
    /// mutated). Only <see cref="ApplyTail"/>, posted back to the UI thread, changes state.
    /// </summary>
    private void PollTick()
    {
        if (!IsLive)
        {
            return;
        }

        if (SelectedFile is not { } file || _tailCursor is not { } cursor || _tailPath is null
            || !string.Equals(_tailPath, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        long length;
        try
        {
            length = new FileInfo(file.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (length == cursor.Position)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _tailBusy, 1, 0) != 0)
        {
            // Still working the previous tick; this one is skipped rather than queued —
            // the next tick will pick up everything written since either way.
            return;
        }

        int generation = _generation;
        InfologFilter filter = CurrentFilter();

        _ = TailAsync(file, filter, cursor, generation);
    }

    private async Task TailAsync(InfologFile file, InfologFilter filter, InfologTailCursor cursor, int generation)
    {
        try
        {
            InfologTailRead result = await _parser.TailAsync(file, filter, cursor);
            _dispatcher.Post(() => ApplyTail(file, result, generation));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tail {Path}.", file.Path);
        }
        finally
        {
            Interlocked.Exchange(ref _tailBusy, 0);
        }
    }

    /// <summary>
    /// Applies a tail response on the UI thread. Dropped rather than applied when a full
    /// read has happened since it was requested — a filter or file change means whatever
    /// this would append no longer belongs to what is on screen.
    /// </summary>
    private void ApplyTail(InfologFile file, InfologTailRead result, int generation)
    {
        if (generation != _generation
            || SelectedFile is not { } current
            || !string.Equals(current.Path, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (result.ReadError is { Length: > 0 })
        {
            // Transient — a locked file on this poll is ordinary, and the next one tries
            // again. Not worth replacing LinesStatus over.
            return;
        }

        _tailCursor = result.Cursor;

        if (result.Truncated)
        {
            // The file is shorter than what was already read: it was rotated or
            // truncated under us, and the only correct answer is to start over.
            _ = ReloadAsync();
            return;
        }

        if (result.NewLines.Count == 0)
        {
            return;
        }

        foreach (InfologLine line in result.NewLines)
        {
            if (Lines.Count >= InfologRead.LineLimit)
            {
                // Keeps the newest lines rather than the oldest: a live tail is read for
                // what is happening now, unlike a bounded one-off read of a whole file.
                Lines.RemoveAt(0);
                _trimmedForLive = true;
            }

            Lines.Add(line);
        }

        _totalMatchCount += result.NewMatchCount;
        _totalLineCount += result.NewLineCount;
        LinesStatus = DescribeCurrent(CurrentFilter());

        LinesAppended?.Invoke(this, new LinesAppendedEventArgs(result.NewLines.Count));
    }

    private string DescribeCurrent(InfologFilter filter)
    {
        string total = _totalLineCount.ToString("N0", CultureInfo.CurrentCulture);

        if (filter.IsEmpty)
        {
            return _trimmedForLive
                ? $"Showing the latest {Lines.Count:N0} of {total} lines."
                : $"{total} lines.";
        }

        string matches = _totalMatchCount.ToString("N0", CultureInfo.CurrentCulture);

        return _trimmedForLive
            ? $"Showing the latest {Lines.Count:N0} of {matches} matching lines ({total} in the file)."
            : $"{matches} of {total} lines match.";
    }

    private static string DescribeRead(InfologRead read, InfologFilter filter)
    {
        if (read.ReadError is { Length: > 0 } error)
        {
            return error;
        }

        string total = read.LineCount.ToString("N0", CultureInfo.CurrentCulture);

        if (filter.IsEmpty)
        {
            return read.Truncated
                ? $"Showing the first {read.Lines.Count:N0} of {total} lines."
                : $"{total} lines.";
        }

        string matches = read.MatchCount.ToString("N0", CultureInfo.CurrentCulture);

        return read.Truncated
            ? $"Showing the first {read.Lines.Count:N0} of {matches} matching lines ({total} in the file)."
            : $"{matches} of {total} lines match.";
    }

    private bool CanClearFilter() => MinimumSeverity != InfologSeverity.Info
                                     || !string.IsNullOrWhiteSpace(SearchText)
                                     || SelectedSection is not (null or AnySection);

    [RelayCommand(CanExecute = nameof(CanClearFilter))]
    private void ClearFilter() => SetFilter(InfologSeverity.Info);

    /// <summary>
    /// The one filter worth a button of its own: a 484k-line log with nine errors in it is
    /// the normal case, and finding them by scrolling is not.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private void ShowProblemsOnly() => SetFilter(InfologSeverity.Warning);

    /// <summary>
    /// Replaces the whole filter at once, so the three property changes cause one read
    /// rather than three cancelling each other.
    /// </summary>
    private void SetFilter(InfologSeverity minimumSeverity)
    {
        _suppressReads = true;
        try
        {
            SearchText = null;
            SelectedSection = AnySection;
            MinimumSeverity = minimumSeverity;
        }
        finally
        {
            _suppressReads = false;
        }

        ScheduleRead(TimeSpan.Zero);
    }

    // ---- start script recovery --------------------------------------------------

    /// <summary>
    /// Runs the recovered script again, reproducing the run rather than merely launching
    /// the file: the engine build and the isolation flag are taken from the log itself, so
    /// a match recovered from a run on <c>development</c> goes back to
    /// <c>development</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRecoveredScript))]
    private async Task RerunAsync()
    {
        if (Recovered is not { HasScript: true, Path: { Length: > 0 } path })
        {
            return;
        }

        EngineBuild? engine = EngineFromLog();

        var profile = new LaunchProfile
        {
            Name = "Re-run from infolog",
            Mode = LaunchMode.Script,
            EngineName = engine?.Name,
            UseIsolation = Summary?.IsIsolated ?? true,
        };

        LaunchResult result = await _launcher.LaunchAsync(profile, engine, path);

        Status = result.Success
            ? $"Re-running {System.IO.Path.GetFileName(path)} (pid {result.Instance!.ProcessId})."
            : result.Error ?? "The launch failed.";

        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Could not re-run", Status);
        }
    }

    /// <summary>
    /// The installed engine that wrote this log, matched on the folder name the log
    /// reveals through its mounted read-only data directory. Null when that build is gone,
    /// which falls back to the launch service's own newest-complete-build choice.
    /// </summary>
    private EngineBuild? EngineFromLog() =>
        Summary?.EngineFolder is { Length: > 0 } folder
            ? _installation.Engines.FirstOrDefault(e => string.Equals(e.Name, folder, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// Copies the recovered script into the library. PLAN.md §5.8 calls this the most
    /// valuable part of the feature, and it is: it makes the Chobby lobby a start-script
    /// editor.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRecoveredScript))]
    private async Task ImportAsync()
    {
        if (Recovered is not { HasScript: true } recovered)
        {
            return;
        }

        string? name = await _dialogs.PromptForTextAsync(
            "Import start script",
            "Save this run's start script in the library as:",
            recovered.SuggestedFileName,
            "Import",
            value => _scripts.ValidateName(value).Error);

        if (name is null)
        {
            return;
        }

        StartScriptFile? created = await _recovery.ImportAsync(recovered, name);

        if (created is null)
        {
            Status = "The script could not be imported.";
            await _dialogs.ShowMessageAsync("Could not import", Status);
            return;
        }

        Status = $"Imported as {created.DisplayName}.";

        // The library now has a copy, so a diff has something to compare against.
        Comparison = await _recovery.CompareWithLibraryAsync(recovered, created.FileName);
        SyncDiff();
    }

    /// <summary>
    /// Compares the recovered script with the library copy. Answers the question a
    /// re-run raises: does the file still say what it said when this ran?
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRecoveredScript))]
    private async Task DiffAsync()
    {
        if (Recovered is not { HasScript: true } recovered)
        {
            return;
        }

        if (Comparison is not null)
        {
            Comparison = null;
            DiffLines.Clear();
            return;
        }

        Comparison = await _recovery.CompareWithLibraryAsync(recovered);
        SyncDiff();
    }

    private void SyncDiff()
    {
        DiffLines.Clear();

        if (Comparison is not { Found: true } comparison)
        {
            return;
        }

        foreach (DiffLine line in comparison.Diff.Lines)
        {
            DiffLines.Add(line);
        }
    }

    // ---- shell integration ------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenInEditor()
    {
        if (SelectedFile is { } file && !_shell.OpenFile(file.Path))
        {
            Status = $"Could not open {file.Path}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Reveal()
    {
        if (SelectedFile is { } file && !_shell.RevealInExplorer(file.Path))
        {
            Status = $"Could not reveal {file.Path}.";
        }
    }

    /// <summary>
    /// Copies the lines currently shown, filter and all. The point of the filter is
    /// usually to isolate something worth pasting somewhere else, so the two belong
    /// together.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private async Task CopyShownLinesAsync()
    {
        if (Lines.Count == 0)
        {
            Status = "There are no lines to copy.";
            return;
        }

        string text = string.Join(Environment.NewLine, Lines.Select(line => line.Raw));

        Status = await _shell.SetClipboardTextAsync(text)
            ? $"Copied {Lines.Count:N0} lines to the clipboard."
            : "Could not copy the lines.";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (_installation.Current is { } installation && !_shell.OpenFolder(installation.RotatedLogsPath))
        {
            Status = "Could not open the log folder.";
        }
    }

    /// <summary>
    /// Crash triage (PLAN.md §6.11): everything someone would otherwise retype into a bug
    /// report — what ran, how it ended, and the errors — on the clipboard in one press.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSummary))]
    private async Task CopyReportAsync()
    {
        if (Summary is not { } summary)
        {
            return;
        }

        string report = await BuildReportAsync(summary);

        Status = await _shell.SetClipboardTextAsync(report)
            ? "Report copied to the clipboard."
            : "Could not copy the report.";
    }

    private async Task<string> BuildReportAsync(InfologSummary summary)
    {
        var text = new StringBuilder();

        text.AppendLine("Infolog: " + summary.File.Path);
        text.AppendLine("Written: " + summary.File.LastWriteText);
        text.AppendLine("Outcome: " + summary.HeadlineText);

        Append(text, "Engine version", summary.EngineVersion);
        Append(text, "Engine folder", summary.EngineFolder);
        Append(text, "Build", summary.BuildEnvironment);
        Append(text, "Game", summary.TagGame);
        Append(text, "Lobby", summary.TagLobby);
        Append(text, "Map", summary.TagMap);
        Append(text, "Write dir", summary.WriteDirectory);
        Append(text, "Config", summary.ConfigSource);
        text.AppendLine("Isolation: " + (summary.IsIsolated ? "on" : "off"));

        if (summary.StartScriptPath is { Length: > 0 } script)
        {
            text.AppendLine($"Start script: {script} ({summary.StartScriptState})");
        }

        if (summary.Failure is { } failure)
        {
            text.AppendLine();
            text.AppendLine($"Failure at line {failure.LineNumber}:");
            text.AppendLine(failure.Caption is { Length: > 0 } caption ? $"  {caption}" : "  (no caption)");
            text.AppendLine("  " + failure.Message.Replace("\n", "\n  "));
        }

        // Read afresh rather than reusing whatever the viewer is filtered to: a report
        // that silently reflects a section filter would be worse than no report.
        InfologRead errors = await _parser.ReadAsync(
            summary.File,
            new InfologFilter { MinimumSeverity = InfologSeverity.Error });

        if (errors.Lines.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Errors ({errors.MatchCount}):");

            foreach (InfologLine line in errors.Lines.Take(50))
            {
                text.AppendLine("  " + line.Raw);
            }

            if (errors.MatchCount > 50)
            {
                text.AppendLine($"  … and {errors.MatchCount - 50} more.");
            }
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, string label, string? value)
    {
        if (value is { Length: > 0 })
        {
            text.AppendLine($"{label}: {value}");
        }
    }
}

/// <summary>How many lines a live tail added, so a view that is not scrolled to the bottom can say so.</summary>
public sealed class LinesAppendedEventArgs(int count) : EventArgs
{
    public int Count { get; } = count;
}
