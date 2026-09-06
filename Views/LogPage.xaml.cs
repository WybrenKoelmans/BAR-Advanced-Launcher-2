using BAR_Advanced_Launcher_2.ViewModels;

namespace BAR_Advanced_Launcher_2.Views;

public abstract class LogPageBase : ViewModelPage<LogViewModel>;

public sealed partial class LogPage : LogPageBase
{
    public LogPage() => InitializeComponent();
}
