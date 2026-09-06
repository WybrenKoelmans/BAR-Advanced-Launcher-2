using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// One element of a start script. The document is kept as an ordered tree rather than
/// a dictionary so that a round trip preserves the order the engine (or Chobby) wrote
/// things in, and so that keys this app does not model survive an edit untouched
/// (PLAN.md §5.7: "parse → model → write must reproduce a semantically identical file").
/// </summary>
public abstract class StartScriptNode;

/// <summary>A <c>key=value;</c> line.</summary>
public sealed class StartScriptValue : StartScriptNode
{
    public StartScriptValue(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; set; }

    public string Value { get; set; }

    public override string ToString() => $"{Key}={Value};";
}

/// <summary>
/// A <c>//</c> line. Real engine- and Chobby-written scripts contain none, but a
/// hand-edited one may, and dropping it on save would be a silent edit.
/// </summary>
public sealed class StartScriptComment : StartScriptNode
{
    public StartScriptComment(string text) => Text = text;

    /// <summary>The comment body, without the leading slashes.</summary>
    public string Text { get; set; }

    public override string ToString() => $"// {Text}";
}

/// <summary>A <c>[name] { ... }</c> block. Nests to arbitrary depth.</summary>
public sealed class StartScriptSection : StartScriptNode
{
    public StartScriptSection(string name) => Name = name;

    public string Name { get; set; }

    /// <summary>Children in document order: values, comments and nested sections.</summary>
    public List<StartScriptNode> Nodes { get; } = new();

    public IEnumerable<StartScriptValue> Values => Nodes.OfType<StartScriptValue>();

    public IEnumerable<StartScriptSection> Sections => Nodes.OfType<StartScriptSection>();

    // Keys are matched case-insensitively throughout: real scripts on this machine use
    // both "startrectleft" (Chobby) and "StartRectLeft" (an older hand-written one).

    public StartScriptValue? FindValue(string key) =>
        Values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => FindValue(key)?.Value;

    /// <summary>Sets a key, adding it at the end when absent. A null value removes it.</summary>
    public void SetString(string key, string? value)
    {
        if (value is null)
        {
            RemoveValue(key);
            return;
        }

        StartScriptValue? existing = FindValue(key);

        if (existing is null)
        {
            Nodes.Add(new StartScriptValue(key, value));
        }
        else
        {
            existing.Value = value;
        }
    }

    public bool RemoveValue(string key)
    {
        StartScriptValue? existing = FindValue(key);
        return existing is not null && Nodes.Remove(existing);
    }

    public int? GetInt(string key) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    public void SetInt(string key, int? value) =>
        SetString(key, value?.ToString(CultureInfo.InvariantCulture));

    public float? GetFloat(string key) =>
        float.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    public void SetFloat(string key, float? value) =>
        SetString(key, value?.ToString("0.########", CultureInfo.InvariantCulture));

    /// <summary>Reads an engine boolean, which is written as 0 or 1.</summary>
    public bool? GetBool(string key) => GetInt(key) switch
    {
        null => null,
        0 => false,
        _ => true,
    };

    public void SetBool(string key, bool? value) => SetInt(key, value is null ? null : value.Value ? 1 : 0);

    public StartScriptSection? Section(string name) =>
        Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public StartScriptSection GetOrAddSection(string name)
    {
        StartScriptSection? existing = Section(name);

        if (existing is not null)
        {
            return existing;
        }

        var created = new StartScriptSection(name);
        Nodes.Add(created);
        return created;
    }

    public bool RemoveSection(string name)
    {
        StartScriptSection? existing = Section(name);
        return existing is not null && Nodes.Remove(existing);
    }

    /// <summary>
    /// Nested sections named <paramref name="prefix"/> followed by a number — the
    /// <c>player0</c>, <c>ai1</c>, <c>team2</c>, <c>allyteam0</c> convention — ordered by
    /// that number rather than by document order, which is arbitrary in real files.
    /// </summary>
    public IReadOnlyList<(int Index, StartScriptSection Section)> IndexedSections(string prefix) =>
        Sections
            .Select(s => (Ok: TryParseIndexedName(s.Name, prefix, out int index), Index: index, Section: s))
            .Where(t => t.Ok)
            .OrderBy(t => t.Index)
            .Select(t => (t.Index, t.Section))
            .ToArray();

    /// <summary>
    /// Splits <c>allyteam10</c> into "allyteam" + 10. Guards against <c>allyteam</c>
    /// matching the <c>team</c> prefix, which a naive StartsWith would accept.
    /// </summary>
    public static bool TryParseIndexedName(string name, string prefix, out int index)
    {
        index = 0;

        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(
            name.AsSpan(prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out index);
    }

    /// <summary>Next free index for a <paramref name="prefix"/> family, so adds never collide.</summary>
    public int NextIndex(string prefix)
    {
        IReadOnlyList<(int Index, StartScriptSection Section)> existing = IndexedSections(prefix);
        return existing.Count == 0 ? 0 : existing[^1].Index + 1;
    }

    public override string ToString() => $"[{Name}] ({Nodes.Count} nodes)";
}

/// <summary>
/// A whole start script. The root is a nameless section so that every lookup helper is
/// written once; a real file has a single <c>[game]</c> block at the root.
/// </summary>
public sealed class StartScriptDocument
{
    public StartScriptSection Root { get; } = new(string.Empty);

    /// <summary>The <c>[game]</c> block, created if the document does not have one.</summary>
    public StartScriptSection Game => Root.GetOrAddSection("game");

    /// <summary>The <c>[game]</c> block, or null — used to tell a real script from an empty one.</summary>
    public StartScriptSection? FindGame() => Root.Section("game");
}
