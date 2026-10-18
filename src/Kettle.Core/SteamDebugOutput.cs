using System.Text;

namespace Kettle.Core;
// Opt-in, memory-only console observation. It never sends console input or writes files.
public sealed class SteamDebugOutput
{
    public const int Capacity = 256 * 1024;
    private readonly object sync = new();
    private readonly StringBuilder text = new();
    private long version;
    public SteamDebugStream CreateStream(string name)
    {
        Append("\n--- " + name + " ---\n");
        return new(this);
    }

    internal void Append(string value)
    {
        if (value.Length == 0)
            return;
        lock (sync)
        {
            if (value.Length >= Capacity)
            {
                text.Clear();
                text.Append(value.AsSpan(value.Length - Capacity));
            }
            else
            {
                int excess = text.Length + value.Length - Capacity;
                if (excess > 0)
                    text.Remove(0, excess);
                text.Append(value);
            }

            version++;
        }
    }

    public string? ReadSince(ref long seen)
    {
        lock (sync)
        {
            if (seen == version)
                return null;
            seen = version;
            return text.ToString();
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            text.Clear();
            version++;
        }
    }
}

public sealed class SteamDebugStream(SteamDebugOutput output) : IDisposable
{
    private readonly object sync = new();
    private readonly TerminalTextNormalizer normalizer = new();
    private string prefix = "";
    private bool ordinary, hideArguments, disposed;

    public void Write(string chunk) => WriteNormalized(normalizer.Feed(chunk));

    internal void WriteNormalized(string chunk)
    {
        lock (sync)
        {
            if (disposed)
                return;
            var visible = new StringBuilder();
            foreach (char c in chunk)
            {
                if (c == '\n')
                {
                    visible.Append(prefix).Append(c);
                    prefix = "";
                    ordinary = hideArguments = false;
                    continue;
                }
                if (hideArguments)
                    continue;
                if (ordinary)
                {
                    if (c != '\b')
                        visible.Append(c);
                    continue;
                }
                if (prefix.Length == 0 && c is ' ' or '\t')
                {
                    visible.Append(c);
                    continue;
                }
                if (prefix.Equals("login", StringComparison.OrdinalIgnoreCase) && c is ' ' or '\t')
                {
                    visible.Append(L.Text(TextId.LoginArgumentsHidden));
                    prefix = "";
                    hideArguments = true;
                    continue;
                }
                prefix += c;
                if (prefix.Equals("Steam>", StringComparison.OrdinalIgnoreCase))
                {
                    visible.Append(prefix);
                    prefix = "";
                }
                else if (!"login".StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                         !"Steam>".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    visible.Append(prefix);
                    prefix = "";
                    ordinary = true;
                }
            }
            output.Append(visible.ToString());
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            prefix = "";
        }
    }
}