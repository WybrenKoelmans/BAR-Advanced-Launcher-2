using System;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services.Logging;

/// <summary>
/// One line in the in-app log pane. The display members are pre-formatted strings
/// because <c>x:Bind</c> is strongly typed and will not coerce a <see cref="LogLevel"/>
/// or call a formatting overload for a <c>TextBlock.Text</c>.
/// </summary>
public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };

    /// <summary>Category without its namespace, which is all the pane has room for.</summary>
    public string ShortCategory
    {
        get
        {
            int dot = Category.LastIndexOf('.');
            return dot >= 0 && dot < Category.Length - 1 ? Category[(dot + 1)..] : Category;
        }
    }

    public override string ToString() =>
        $"{TimeText} [{LevelText}] {ShortCategory}: {Message}" +
        (Exception is null ? string.Empty : Environment.NewLine + Exception);
}
