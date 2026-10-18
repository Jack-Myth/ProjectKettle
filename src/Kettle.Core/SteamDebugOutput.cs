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
    private readonly List<char[]> protectedValues = [];
    private readonly StringBuilder pending = new();
    private readonly TerminalTextNormalizer normalizer = new();
    private bool disposed;
    public void Protect(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return;
        lock (sync)
        {
            if (!disposed)
                protectedValues.Add(value.ToArray());
        }
    }

    public void Write(string chunk) => WriteNormalized(normalizer.Feed(chunk));
    internal void WriteNormalized(string chunk)
    {
        lock (sync)
        {
            if (disposed)
                return;
            var visible = new StringBuilder();
            foreach (var c in chunk)
            {
                if (c == '\b')
                {
                    if (pending.Length > 0)
                        pending.Length--;
                    continue;
                }

                pending.Append(c);
                while (pending.Length > 0)
                {
                    bool prefix = false, complete = false;
                    foreach (var secret in protectedValues)
                    {
                        if (pending.Length > secret.Length)
                            continue;
                        bool matches = true;
                        for (int i = 0; i < pending.Length; i++)
                            if (pending[i] != secret[i])
                            {
                                matches = false;
                                break;
                            }

                        if (!matches)
                            continue;
                        prefix = true;
                        if (pending.Length == secret.Length)
                        {
                            complete = true;
                            break;
                        }
                    }

                    if (complete)
                    {
                        visible.Append("●●●");
                        pending.Clear();
                        break;
                    }

                    if (prefix)
                        break;
                    visible.Append(pending[0]);
                    pending.Remove(0, 1);
                }

                // Bound a single observer notification even for an unusually large source chunk.
                if (visible.Length >= 8192)
                {
                    output.Append(visible.ToString());
                    visible.Clear();
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
            pending.Clear();
            foreach (var secret in protectedValues)
                Array.Clear(secret);
            protectedValues.Clear();
        }
    }
}
