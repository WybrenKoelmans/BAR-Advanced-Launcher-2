using System.Collections.ObjectModel;
using System.Linq;
using BAR_Advanced_Launcher_2.Services;
using BAR_Advanced_Launcher_2.Services.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>The app log pane. Engine infolog tailing arrives in Phase 7.</summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly IAppLogSink _sink;
    private readonly IUiDispatcher _dispatcher;

    [ObservableProperty]
    public partial LogLevel MinimumLevel { get; set; }

    public ObservableCollection<AppLogEntry> Entries { get; } = new();

    public LogViewModel(IAppLogSink sink, IUiDispatcher dispatcher)
    {
        _sink = sink;
        _dispatcher = dispatcher;
        MinimumLevel = LogLevel.Information;

        Reload();
        _sink.EntryAdded += (_, entry) => _dispatcher.Post(() =>
        {
            if (entry.Level >= MinimumLevel)
            {
                Entries.Add(entry);
            }
        });
    }

    partial void OnMinimumLevelChanged(LogLevel value) => Reload();

    private void Reload()
    {
        Entries.Clear();
        foreach (AppLogEntry entry in _sink.Entries.Where(e => e.Level >= MinimumLevel))
        {
            Entries.Add(entry);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _sink.Clear();
        Entries.Clear();
    }
}
