using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// A resolved command line: the binary plus its arguments as separate tokens.
///
/// Tokens are kept apart all the way to <c>ProcessStartInfo.ArgumentList</c>, which
/// escapes each one for us. PLAN.md §2.4 has a real infolog where a script path was
/// concatenated unquoted and the engine read only up to the first space, logging
/// "Loading StartScript from: All" for a map beginning "All That…".
/// </summary>
public sealed record EngineCommandLine(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The command line as a user would paste it into a terminal, for the "copy full
    /// command line" button (PLAN.md §6.10). Display only — never used to start a
    /// process, because that is exactly the concatenation that caused the bug above.
    /// </summary>
    public string ToDisplayString()
    {
        var text = new StringBuilder(Quote(ExecutablePath));

        foreach (string argument in Arguments)
        {
            text.Append(' ').Append(Quote(argument));
        }

        return text.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            return value;
        }

        // Windows command-line rules: backslashes are literal unless they precede the
        // closing quote, where they must be doubled.
        var quoted = new StringBuilder("\"");
        int backslashes = 0;

        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}

/// <summary>Splits a free-text extra-arguments box into tokens.</summary>
public static class CommandLineTokenizer
{
    /// <summary>
    /// Splits on whitespace, honouring double quotes so a quoted path stays one token.
    /// Returns an empty list for null or whitespace.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;

        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                // A quote delimits but does not itself appear in the token, and an
                // empty "" is still a real, if empty, argument.
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
