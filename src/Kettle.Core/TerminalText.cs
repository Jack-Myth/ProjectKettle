using System.Text;

namespace Kettle.Core;
// One normalization policy. Callers can retain an instance across arbitrary reads.
internal sealed class TerminalTextNormalizer
{
    private int escape;
    private bool cr;
    public bool IncompleteControl => escape != 0;

    public string Feed(string chunk)
    {
        var text = new StringBuilder(chunk.Length);
        foreach (char c in chunk)
        {
            if (escape == 1)
            {
                escape = c == '[' ? 2 : c == ']' ? 3 : 0;
                continue;
            }

            if (escape == 2)
            {
                if (c is >= '@' and <= '~')
                {
                    if (c is 'H' or 'f')
                    {
                        text.Append('\n');
                        cr = false;
                    }

                    escape = 0;
                }

                continue;
            }

            if (escape == 3)
            {
                if (c == '\a')
                    escape = 0;
                else if (c == '\x1b')
                    escape = 4;
                continue;
            }

            if (escape == 4)
            {
                escape = c == '\\' ? 0 : 3;
                continue;
            }

            if (c == '\x1b')
            {
                escape = 1;
                continue;
            }

            if (c == '\r')
            {
                text.Append('\n');
                cr = true;
                continue;
            }

            if (c == '\n' && cr)
            {
                cr = false;
                continue;
            }

            cr = false;
            if (c >= ' ' || c is '\n' or '\t' or '\b')
                text.Append(c);
        }

        return text.ToString();
    }

    public void Reset()
    {
        escape = 0;
        cr = false;
    }

    public static void Append(StringBuilder target, string normalized)
    {
        foreach (char c in normalized)
            if (c == '\b')
            {
                if (target.Length > 0)
                    target.Length--;
            }
            else
                target.Append(c);
    }
}

internal readonly record struct ConsolePrompt(int Start, int Length);
internal static class ConsolePrompts
{
    public static ConsolePrompt[] Groups(string text)
    {
        var groups = new List<ConsolePrompt>();
        foreach (var prompt in Find(text))
        {
            if (groups.Count > 0 && string.IsNullOrWhiteSpace(text[(groups[^1].Start + groups[^1].Length)..prompt.Start]) && !text[(groups[^1].Start + groups[^1].Length)..prompt.Start].Contains('\n'))
                groups[^1] = new(groups[^1].Start, prompt.Start + prompt.Length - groups[^1].Start);
            else
                groups.Add(prompt);
        }

        return groups.ToArray();
    }

    public static ConsolePrompt[] Find(string text)
    {
        var prompts = new List<ConsolePrompt>();
        for (int line = 0; line < text.Length;)
        {
            int at = line;
            while (at < text.Length)
            {
                while (at < text.Length && text[at] is ' ' or '\t')
                    at++;
                if (!text.AsSpan(at).StartsWith("Steam>", StringComparison.Ordinal))
                    break;
                prompts.Add(new(at, 6));
                at += 6;
            }

            int next = text.IndexOf('\n', at);
            if (next < 0)
                break;
            line = next + 1;
        }

        return prompts.ToArray();
    }

    public static string Remove(string text)
    {
        var result = new StringBuilder(text);
        foreach (var prompt in Find(text).Reverse())
            result.Remove(prompt.Start, prompt.Length);
        return result.ToString();
    }

    public static bool IsNoise(string text) => string.IsNullOrWhiteSpace(Remove(text));
}
