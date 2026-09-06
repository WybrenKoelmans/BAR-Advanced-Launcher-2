using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// A hand-written scanner for the start script format. It is character-based rather than
/// line-based because the two real specimens on this machine differ in layout — the
/// engine's own <c>bar_debug_launcher_script.txt</c> indents four spaces and Chobby's
/// <c>_script.txt</c> does not — and a hand-edited file may put a whole block on one line.
/// </summary>
public sealed class StartScriptSerializer : IStartScriptSerializer
{
    private const int IndentWidth = 4;

    /// <summary>Both real specimens on this machine are CRLF, as is everything the engine writes.</summary>
    private const string NewLine = "\r\n";

    public StartScriptParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // An empty file is a valid empty document, not an error: a newly created
            // script is empty for the moment between creation and the first save.
            return StartScriptParseResult.Ok(new StartScriptDocument());
        }

        var document = new StartScriptDocument();
        var scanner = new Scanner(text);

        // The section currently being filled. The root is the bottom of this stack and
        // is never popped.
        var stack = new Stack<StartScriptSection>();
        stack.Push(document.Root);

        while (true)
        {
            scanner.SkipWhitespace();

            if (scanner.AtEnd)
            {
                break;
            }

            char c = scanner.Peek();

            if (c == '/' && scanner.PeekAt(1) == '/')
            {
                stack.Peek().Nodes.Add(new StartScriptComment(scanner.ReadLineComment()));
                continue;
            }

            if (c == '[')
            {
                if (!TryReadSection(scanner, stack, out StartScriptParseResult? failure))
                {
                    return failure;
                }

                continue;
            }

            if (c == '{')
            {
                // A brace with no section header before it. The engine would treat this
                // as an anonymous block; saying so is more useful than guessing.
                return StartScriptParseResult.Fail(scanner.Line, "'{' here does not follow a [section] header.");
            }

            if (c == '}')
            {
                scanner.Read();

                if (stack.Count == 1)
                {
                    return StartScriptParseResult.Fail(scanner.Line, "'}' closes a section that was never opened.");
                }

                stack.Pop();
                continue;
            }

            if (!TryReadAssignment(scanner, stack.Peek(), out StartScriptParseResult? assignmentFailure))
            {
                return assignmentFailure;
            }
        }

        if (stack.Count > 1)
        {
            StartScriptSection unclosed = stack.Peek();
            return StartScriptParseResult.Fail(
                scanner.Line,
                $"[{unclosed.Name}] is never closed — a '}}' is missing.");
        }

        return StartScriptParseResult.Ok(document);
    }

    private static bool TryReadSection(
        Scanner scanner,
        Stack<StartScriptSection> stack,
        [NotNullWhen(false)] out StartScriptParseResult? failure)
    {
        int headerLine = scanner.Line;
        scanner.Read(); // '['

        string name = scanner.ReadUntil(']', '\n').Trim();

        if (scanner.AtEnd || scanner.Peek() != ']')
        {
            failure = StartScriptParseResult.Fail(headerLine, "A section header is missing its ']'.");
            return false;
        }

        scanner.Read(); // ']'

        if (name.Length == 0)
        {
            failure = StartScriptParseResult.Fail(headerLine, "A section header has no name.");
            return false;
        }

        // The opening brace is normally on the next line, but allow it on the same one.
        scanner.SkipWhitespaceAndComments();

        if (scanner.AtEnd || scanner.Peek() != '{')
        {
            failure = StartScriptParseResult.Fail(headerLine, $"[{name}] is not followed by a '{{'.");
            return false;
        }

        scanner.Read(); // '{'

        var section = new StartScriptSection(name);
        stack.Peek().Nodes.Add(section);
        stack.Push(section);

        failure = null;
        return true;
    }

    private static bool TryReadAssignment(
        Scanner scanner,
        StartScriptSection target,
        [NotNullWhen(false)] out StartScriptParseResult? failure)
    {
        int line = scanner.Line;

        // A key runs to the '=', but stop at a newline, a brace or a ';' so a malformed
        // line is reported where it went wrong rather than swallowing the rest of the
        // file — and so "ishost;" is named as 'ishost', not as 'ishost;'.
        string key = scanner.ReadUntil('=', '\n', '{', '}', ';').Trim();

        if (scanner.AtEnd || scanner.Peek() != '=')
        {
            failure = StartScriptParseResult.Fail(
                line,
                key.Length == 0
                    ? "Expected a key, a [section] or a brace here."
                    : $"'{key}' is not followed by an '='.");
            return false;
        }

        scanner.Read(); // '='

        // Values run to the terminating ';'. A newline also ends one, so a file missing a
        // semicolon still parses rather than eating the following key.
        string value = scanner.ReadUntil(';', '\n').Trim();

        if (!scanner.AtEnd && scanner.Peek() == ';')
        {
            scanner.Read();
        }

        if (key.Length == 0)
        {
            failure = StartScriptParseResult.Fail(line, "A value has no key before its '='.");
            return false;
        }

        target.Nodes.Add(new StartScriptValue(key, value));
        failure = null;
        return true;
    }

    public string Write(StartScriptDocument document)
    {
        var builder = new StringBuilder();
        WriteNodes(builder, document.Root.Nodes, depth: 0);
        return builder.ToString();
    }

    private static void WriteNodes(StringBuilder builder, IReadOnlyList<StartScriptNode> nodes, int depth)
    {
        string prefix = new(' ', depth * IndentWidth);

        foreach (StartScriptNode node in nodes)
        {
            switch (node)
            {
                case StartScriptValue value:
                    builder.Append(prefix).Append(value.Key).Append('=').Append(value.Value).Append(';').Append(NewLine);
                    break;

                case StartScriptComment comment:
                    builder.Append(prefix).Append("// ").Append(comment.Text).Append(NewLine);
                    break;

                case StartScriptSection section:
                    builder.Append(prefix).Append('[').Append(section.Name).Append(']').Append(NewLine);
                    builder.Append(prefix).Append('{').Append(NewLine);
                    WriteNodes(builder, section.Nodes, depth + 1);
                    builder.Append(prefix).Append('}').Append(NewLine);
                    break;
            }
        }
    }

    /// <summary>Tracks a line number alongside the cursor so errors can point at one.</summary>
    private sealed class Scanner
    {
        private readonly string _text;
        private int _position;

        public Scanner(string text)
        {
            _text = text;
            Line = 1;
        }

        public int Line { get; private set; }

        public bool AtEnd => _position >= _text.Length;

        public char Peek() => _text[_position];

        public char PeekAt(int offset) =>
            _position + offset < _text.Length ? _text[_position + offset] : '\0';

        public char Read()
        {
            char c = _text[_position++];

            if (c == '\n')
            {
                Line++;
            }

            return c;
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Peek()))
            {
                Read();
            }
        }

        /// <summary>Used between a section header and its brace, where a comment may sit.</summary>
        public void SkipWhitespaceAndComments()
        {
            while (true)
            {
                SkipWhitespace();

                if (AtEnd || Peek() != '/' || PeekAt(1) != '/')
                {
                    return;
                }

                ReadLineComment();
            }
        }

        /// <summary>Consumes <c>// ...</c> through to the end of the line, returning its text.</summary>
        public string ReadLineComment()
        {
            Read(); // '/'
            Read(); // '/'

            var builder = new StringBuilder();

            while (!AtEnd && Peek() != '\n')
            {
                builder.Append(Read());
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// Reads up to but not including the first of <paramref name="stops"/>. The stop
        /// character is left in place so the caller decides what it means.
        /// </summary>
        public string ReadUntil(params char[] stops)
        {
            var builder = new StringBuilder();

            while (!AtEnd && Array.IndexOf(stops, Peek()) < 0)
            {
                builder.Append(Read());
            }

            return builder.ToString();
        }
    }
}
