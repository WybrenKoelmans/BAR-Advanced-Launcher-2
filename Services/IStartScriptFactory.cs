using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// One of the shapes a new script can start from (PLAN.md §5.7). The four are taken from
/// the names in the old launcher's library — <c>vs_ai.txt</c>, <c>ai_ai.txt</c>,
/// <c>godless_vs_barb.txt</c> — which show what is actually played on this machine.
/// </summary>
public sealed record StartScriptTemplate(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>
/// What a template needs filled in. Everything is optional: the factory falls back to
/// values that produce a runnable script, so "New script" never asks a question first.
/// </summary>
public sealed class StartScriptTemplateOptions
{
    /// <summary>Defaults to the literal <c>Beyond All Reason $VERSION</c> (PLAN.md §2.3).</summary>
    public string? GameType { get; set; }

    public string? MapName { get; set; }

    public string? PlayerName { get; set; }

    /// <summary>AI archive name, e.g. <c>BARb</c>. Defaults to the engine's built-in AI.</summary>
    public string? AiShortName { get; set; }

    public string? AiVersion { get; set; }
}

/// <summary>Builds a start script from a template.</summary>
public interface IStartScriptFactory
{
    IReadOnlyList<StartScriptTemplate> Templates { get; }

    /// <summary>
    /// Builds the document for a template. An unknown id falls back to the empty
    /// sandbox rather than throwing, so a stale id in the UI cannot crash a create.
    /// </summary>
    StartScriptModel Create(string templateId, StartScriptTemplateOptions? options = null);

    /// <summary>A file name that suits the template, before uniquing.</summary>
    string SuggestFileName(string templateId);
}
