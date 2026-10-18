using System.Text;

namespace Kettle.Core;
// Lossless KeyValues 1 document: ordered/duplicate nodes, quoted or bare tokens,
// escapes, nested objects, comments and optional condition suffixes. Editing a
// parsed node removes its source span; all other source text stays untouched.
public sealed class KeyValuesDocument
{
    public sealed record Node(string Key, string? Value, IReadOnlyList<Node>? Children, int Start, int End, bool Conditional);
    public string Text
    {
        get;
    }
    public IReadOnlyList<Node> Children
    {
        get;
    }

    private KeyValuesDocument(string text, IReadOnlyList<Node> children)
    {
        Text = text;
        Children = children;
    }

    public static KeyValuesDocument Parse(string text)
    {
        if (text.Length > 8 * 1024 * 1024)
            throw new InvalidDataException(L.Text(TextId.KeyValuesConfigurationIsTooLarge));
        var reader = new Reader(text);
        return new(text, reader.Object(false, 0));
    }

    public string Remove(IEnumerable<Node> nodes)
    {
        var result = new StringBuilder(Text);
        int previous = Text.Length;
        foreach (var node in nodes.OrderByDescending(x => x.Start))
        {
            if (node.Start < 0 || node.End > previous || node.End <= node.Start)
                throw new InvalidDataException(L.Text(TextId.InvalidKeyValuesNodeRange));
            result.Remove(node.Start, node.End - node.Start);
            previous = node.Start;
        }

        return result.ToString();
    }

    private sealed class Reader(string text)
    {
        private int at;
        private InvalidDataException Error() => new(L.Text(TextId.InvalidKeyValuesFormatAtPosition) + at + L.Text(TextId.TheOriginalConfigurationWasNotRebuilt));
        private void Trivia()
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at]) || at == 0 && text[at] == '\uFEFF')
                {
                    at++;
                    continue;
                }

                if (text.AsSpan(at).StartsWith("//"))
                {
                    while (at < text.Length && text[at] != '\n')
                        at++;
                    continue;
                }

                if (text.AsSpan(at).StartsWith("/*"))
                {
                    int end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw Error();
                    at = end + 2;
                    continue;
                }

                break;
            }
        }

        private string Token()
        {
            Trivia();
            if (at == text.Length || text[at] is '{' or '}' or '[' or ']')
                throw Error();
            var value = new StringBuilder();
            if (text[at] == '"')
            {
                at++;
                while (at < text.Length)
                {
                    char c = text[at++];
                    if (c == '"')
                        return value.ToString();
                    if (c == '\\')
                    {
                        if (at == text.Length)
                            throw Error();
                        char escaped = text[at++];
                        if (escaped is '\\' or '"')
                            value.Append(escaped);
                        else if (escaped is 'n' or 'r' or 't')
                            value.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : '\t');
                        else
                            value.Append('\\').Append(escaped);
                    }
                    else
                    {
                        if (c == '\0')
                            throw Error();
                        value.Append(c);
                    }
                }

                throw Error();
            }

            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('{' or '}' or '[' or ']' or '"'))
            {
                if (text[at] == '\0' || text.AsSpan(at).StartsWith("//") || text.AsSpan(at).StartsWith("/*"))
                    break;
                value.Append(text[at++]);
            }

            if (value.Length == 0)
                throw Error();
            return value.ToString();
        }

        internal IReadOnlyList<Node> Object(bool nested, int depth)
        {
            if (depth > 128)
                throw Error();
            var nodes = new List<Node>();
            while (true)
            {
                Trivia();
                if (at == text.Length)
                {
                    if (nested)
                        throw Error();
                    return nodes;
                }

                if (text[at] == '}')
                {
                    if (!nested)
                        throw Error();
                    at++;
                    return nodes;
                }

                int start = at;
                string key = Token();
                // Directives could import another file; never resolve or silently ignore them.
                if (key.StartsWith('#'))
                    throw new InvalidDataException(L.Text(TextId.LibraryConfigurationContainsKeyValuesDirectivesAndCannotBeCleaned));
                Trivia();
                IReadOnlyList<Node>? children = null;
                string? value = null;
                if (at < text.Length && text[at] == '{')
                {
                    at++;
                    children = Object(true, depth + 1);
                }
                else
                    value = Token();
                int end = at;
                Trivia();
                bool conditional = false;
                if (at < text.Length && text[at] == '[')
                {
                    conditional = true;
                    at++;
                    while (at < text.Length && text[at] != ']')
                    {
                        if (text[at] is '\n' or '\r' or '[')
                            throw Error();
                        at++;
                    }

                    if (at == text.Length)
                        throw Error();
                    at++;
                    end = at;
                }

                nodes.Add(new(key, value, children, start, end, conditional));
            }
        }
    }
}
