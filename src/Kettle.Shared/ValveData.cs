using System.Text.RegularExpressions;

namespace Kettle.Shared;

public sealed class ValveData
{
    public string? Value
    {
        get; private init;
    }
    public Dictionary<string, ValveData> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValveData? this[string key] => Children.GetValueOrDefault(key);
    public static ValveData Parse(string text, bool rejectDuplicates = false)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]").Select(m => m.Value is "{" or "}" ? m.Value : m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")).ToArray();
        int position = 0;
        ValveData Read(bool nested)
        {
            var node = new ValveData();
            while (position < tokens.Length)
            {
                var key = tokens[position++];
                if (key == "}")
                {
                    if (!nested)
                        throw new InvalidDataException();
                    return node;
                }

                if (key == "{" || position == tokens.Length)
                    throw new InvalidDataException();
                var value = tokens[position++];
                if (rejectDuplicates && node.Children.ContainsKey(key))
                    throw new InvalidDataException("Duplicate metadata key: " + key);
                node.Children[key] = value == "{" ? Read(true) : new ValveData
                {
                    Value = value
                };
            }

            if (nested)
                throw new InvalidDataException("Incomplete app metadata.");
            return node;
        }

        return Read(false);
    }
}

