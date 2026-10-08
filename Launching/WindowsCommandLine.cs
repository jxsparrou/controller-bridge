using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SBridge.Launching;

internal static class WindowsCommandLine
{
    // Encode argument tokens, not argv[0] and not a cmd.exe command/script.
    // COM activation needs a string; Win32 launching uses BCL ArgumentList instead.
    public static string Join(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(argument => QuoteArgument(argument)));

    public static string QuoteArgument(string argument, bool forceQuotes = false)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("Windows arguments cannot contain NUL.", nameof(argument));
        if (!forceQuotes && argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return argument;

        var result = new StringBuilder().Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        // Trailing backslashes must not escape the closing quote.
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public static string FirstArgument(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return "";
        int position = 0;
        while (position < commandLine.Length && IsSeparator(commandLine[position])) position++;
        return ReadArgument(commandLine, ref position);
    }

    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.Contains('\0')) throw new ArgumentException("Windows command lines cannot contain NUL.", nameof(commandLine));
        var result = new List<string>();
        int position = 0;
        while (position < commandLine.Length)
        {
            while (position < commandLine.Length && IsSeparator(commandLine[position])) position++;
            if (position < commandLine.Length) result.Add(ReadArgument(commandLine, ref position));
        }
        return result.AsReadOnly();
    }

    private static string ReadArgument(string commandLine, ref int position)
    {
        bool quoted = false;
        var result = new StringBuilder();
        while (position < commandLine.Length)
        {
            if (!quoted && IsSeparator(commandLine[position])) break;
            int backslashes = 0;
            while (position < commandLine.Length && commandLine[position] == '\\') { backslashes++; position++; }
            if (position < commandLine.Length && commandLine[position] == '"')
            {
                result.Append('\\', backslashes / 2);
                if (backslashes % 2 != 0) result.Append('"');
                else if (quoted && position + 1 < commandLine.Length && commandLine[position + 1] == '"')
                {
                    result.Append('"');
                    position++;
                }
                else quoted = !quoted;
                position++;
            }
            else
            {
                result.Append('\\', backslashes);
                if (position == commandLine.Length || (!quoted && IsSeparator(commandLine[position]))) break;
                result.Append(commandLine[position++]);
            }
        }
        return result.ToString();
    }

    // CRT/Windows token delimiters are space and tab, not Unicode whitespace.
    // A nonbreaking space is legal filename data and must remain in settings keys.
    private static bool IsSeparator(char character) => character is ' ' or '\t';
}
