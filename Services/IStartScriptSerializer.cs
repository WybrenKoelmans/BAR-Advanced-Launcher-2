using System.Diagnostics.CodeAnalysis;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Where a script failed to parse, so the raw editor can say so inline.</summary>
public sealed record StartScriptParseError(int Line, string Message)
{
    public override string ToString() => $"Line {Line}: {Message}";
}

/// <summary>Either a document or the reason there isn't one. Never both.</summary>
public sealed record StartScriptParseResult(StartScriptDocument? Document, StartScriptParseError? Error)
{
    [MemberNotNullWhen(true, nameof(Document))]
    public bool Success => Document is not null;

    public static StartScriptParseResult Ok(StartScriptDocument document) => new(document, null);

    public static StartScriptParseResult Fail(int line, string message) =>
        new(null, new StartScriptParseError(line, message));
}

/// <summary>
/// Reads and writes the engine's pseudo-INI start script format (PLAN.md §5.7):
/// nested <c>[section] { ... }</c> blocks of <c>key=value;</c> lines, arbitrary depth.
///
/// Round-trip safe: parsing a file and writing it back must produce a semantically
/// identical script. It is deliberately *not* byte-identical — the writer normalises
/// indentation — because the two real specimens on this machine disagree about
/// indentation and neither is more correct than the other.
/// </summary>
public interface IStartScriptSerializer
{
    /// <summary>Parses script text. Never throws on malformed input; reports a line instead.</summary>
    StartScriptParseResult Parse(string? text);

    /// <summary>Renders a document back to text, CRLF-terminated as the engine writes it.</summary>
    string Write(StartScriptDocument document);
}
