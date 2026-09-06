using System;
using System.Collections.Generic;
using System.Globalization;

namespace BAR_Advanced_Launcher_2.Services;

public enum DiffKind
{
    Unchanged,

    /// <summary>Present on the left (the stored copy) only.</summary>
    Removed,

    /// <summary>Present on the right (the recovered copy) only.</summary>
    Added,
}

/// <summary>One line of a diff, carrying whichever line numbers apply to it.</summary>
public sealed record DiffLine(DiffKind Kind, string Text, int? LeftNumber, int? RightNumber)
{
    public string Marker => Kind switch
    {
        DiffKind.Removed => "−",
        DiffKind.Added => "+",
        _ => " ",
    };

    public string LeftText => LeftNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string RightText => RightNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public override string ToString() => Marker + Text;
}

/// <summary>The whole comparison, so the UI does not have to count the lines itself.</summary>
public sealed record TextDiffResult
{
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();

    public int Added { get; init; }

    public int Removed { get; init; }

    public bool Identical => Added == 0 && Removed == 0;

    /// <summary>
    /// True when one side was longer than <see cref="TextDiff.MaxLines"/> and the diff was
    /// reduced to "every line replaced". Start scripts are tens of lines, so this only
    /// happens to something that is not one.
    /// </summary>
    public bool WasTooLarge { get; init; }

    public string SummaryText => Identical
        ? "identical"
        : $"{Added} added, {Removed} removed";
}

/// <summary>
/// Line diff for the start-script comparison of PLAN.md §5.8.
///
/// A plain longest-common-subsequence walk: the inputs are start scripts of a few dozen
/// lines, so the quadratic table costs nothing and the result is exact, which a
/// heuristic diff would not be.
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// Above this the table stops being free, so the diff degrades to a wholesale
    /// replacement rather than allocating a million cells for something that is not a
    /// start script.
    /// </summary>
    public const int MaxLines = 2_000;

    public static TextDiffResult Compare(string? left, string? right)
    {
        string[] leftLines = SplitLines(left);
        string[] rightLines = SplitLines(right);

        if (leftLines.Length > MaxLines || rightLines.Length > MaxLines)
        {
            return TooLarge(leftLines, rightLines);
        }

        int[,] lengths = new int[leftLines.Length + 1, rightLines.Length + 1];

        for (int i = leftLines.Length - 1; i >= 0; i--)
        {
            for (int j = rightLines.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(leftLines[i], rightLines[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var lines = new List<DiffLine>(Math.Max(leftLines.Length, rightLines.Length));
        int added = 0;
        int removed = 0;
        int x = 0;
        int y = 0;

        while (x < leftLines.Length && y < rightLines.Length)
        {
            if (string.Equals(leftLines[x], rightLines[y], StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffKind.Unchanged, leftLines[x], x + 1, y + 1));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                lines.Add(new DiffLine(DiffKind.Removed, leftLines[x], x + 1, null));
                removed++;
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Added, rightLines[y], null, y + 1));
                added++;
                y++;
            }
        }

        for (; x < leftLines.Length; x++)
        {
            lines.Add(new DiffLine(DiffKind.Removed, leftLines[x], x + 1, null));
            removed++;
        }

        for (; y < rightLines.Length; y++)
        {
            lines.Add(new DiffLine(DiffKind.Added, rightLines[y], null, y + 1));
            added++;
        }

        return new TextDiffResult { Lines = lines, Added = added, Removed = removed };
    }

    private static TextDiffResult TooLarge(string[] leftLines, string[] rightLines)
    {
        var lines = new List<DiffLine>(leftLines.Length + rightLines.Length);

        for (int i = 0; i < leftLines.Length; i++)
        {
            lines.Add(new DiffLine(DiffKind.Removed, leftLines[i], i + 1, null));
        }

        for (int i = 0; i < rightLines.Length; i++)
        {
            lines.Add(new DiffLine(DiffKind.Added, rightLines[i], null, i + 1));
        }

        return new TextDiffResult
        {
            Lines = lines,
            Added = rightLines.Length,
            Removed = leftLines.Length,
            WasTooLarge = true,
        };
    }

    /// <summary>
    /// Splits on any line ending and drops a single trailing blank, so a file that ends
    /// with a newline does not read as differing from one that does not.
    /// </summary>
    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        return lines.Length > 0 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
    }
}
