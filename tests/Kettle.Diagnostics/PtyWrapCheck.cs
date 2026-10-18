using System.Text;
using System.Text.Json;
using Kettle.Core;

internal static class PtyWrapCheck
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        await using var terminal = new ConPtyTerminal();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var text = new StringBuilder();
        await terminal.StartAsync(Environment.ProcessPath!, ["-globaluser"], lifetime.Token);
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in terminal.ReadAsync(lifetime.Token))
                lock (text)
                    text.Append(chunk);
        });
        string Current()
        {
            lock (text)
                return text.ToString();
        }

        await SessionChecks.Until(() => Current().Contains("Steam>"));
        await terminal.WriteLineAsync("login fixture".AsMemory(), lifetime.Token);
        await SessionChecks.Until(() => Current().Contains("Password:"));
        await terminal.WriteLineAsync("fixture-password".AsMemory(), lifetime.Token);
        await SessionChecks.Until(() => Current().Contains("Guard code:"));
        await terminal.WriteLineAsync("fixture-code".AsMemory(), lifetime.Token);
        await SessionChecks.Until(() => Current().Contains("Waiting for user info...OK"));
        foreach (var wide in new[]
        {
            false,
            true
        }

        )
        {
            var marker = wide ? "WIDE_FIXTURE" : "WRAP_FIXTURE";
            await terminal.WriteLineAsync((wide ? "emit_wide_notice_fixture" : "emit_wrapped_notice_fixture").AsMemory(), lifetime.Token);
            await SessionChecks.Until(() => Current().Contains(marker + "_END"));
            await Task.Delay(150);
            var all = Current();
            int begin = all.IndexOf(marker + "_BEGIN", StringComparison.Ordinal);
            if (begin < 0)
                throw new IOException("Generated marker missing");
            int start = begin + (marker + "_BEGIN").Length;
            int end = all.IndexOf(marker + "_END", start, StringComparison.Ordinal);
            var raw = all[start..end]; // Generated payload only; exclude all mock login traffic.
            var normalized = raw.Replace("\x1b[K", "").Replace("\r", "");
            await File.WriteAllTextAsync(Path.Combine(output, wide ? "generated-wide-raw.json" : "generated-raw.json"), JsonSerializer.Serialize(new
            {
                Columns = ConPtyTerminal.Columns,
                NoticeCharacters = wide ? ConPtyTerminal.Columns - 4 : 236,
                PromptCharacters = 6,
                Raw = raw,
                ContainsBackspace = raw.Contains('\b'),
                ContainsCrLf = raw.Contains("\r\n"),
                RealSteam = false,
                GeneratedDataOnly = true
            }));
            if (!wide && !normalized.Contains("ProcID 42152 "))
                throw new IOException("Former 240-edge notice still splits its PID");
            if (wide && !normalized.Contains("ProcID 4215\n2"))
                throw new IOException("Expected native 4096-edge wrap not observed");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Columns = ConPtyTerminal.Columns,
                Wide = wide,
                RawCharacters = raw.Length,
                FullPidUnsplit = normalized.Contains("ProcID 42152 "),
                ContainsBackspace = raw.Contains('\b')
            }));
        }

        await terminal.StopAsync();
        lifetime.Cancel();
        try
        {
            await reader;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
