using CommunityToolkit.Mvvm.ComponentModel;

namespace BAR_Advanced_Launcher_2.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    // A partial property cannot carry an initializer under C# 13, so defaults live
    // in the constructor throughout.
    public HistoryViewModel() => Header = "History";

    [ObservableProperty]
    public partial string Header { get; set; }
}
