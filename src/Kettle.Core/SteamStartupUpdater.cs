using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record SteamUpdateProgress(int? Percent, string Text);
// One parser per stream: never mix partial UTF-8 characters or CR refresh lines.
public sealed class SteamUpdateProgressParser(Action<SteamUpdateProgress> publish)
{
    private readonly StringBuilder line = new();
    private readonly TerminalTextNormalizer normalizer = new();
    public void Feed(string text, bool finished = false) => FeedNormalized(normalizer.Feed(text), finished);
    internal void FeedNormalized(string text, bool finished = false)
    {
        foreach (char c in text)
        {
            if (c is '\r' or '\n')
            {
                Flush();
                continue;
            }

            if (c == '\b')
            {
                if (line.Length > 0)
                    line.Length--;
                continue;
            }

            if (c >= ' ' || c == '\t')
                line.Append(c);
            if (line.Length > 65536)
                line.Clear();
        }

        if (finished)
            Flush();
    }

    private void Flush()
    {
        string value = line.ToString();
        line.Clear();
        var match = Regex.Match(value, @"^\s*\[\s*(?:(\d{1,3})\s*%|(----))\s*\][ \t]?(.*)$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return;
        int? percent = null;
        if (match.Groups[1].Success)
        {
            if (!int.TryParse(match.Groups[1].Value, out var number) || number is < 0 or > 100)
                return;
            percent = number;
        }

        publish(new(percent, match.Groups[3].Value));
    }
}

public interface ISteamStartupUpdater
{
    Task RunAsync(string executable, Action<SteamUpdateProgress> progress, CancellationToken cancellationToken = default);
}

public interface ISteamStartupProcess
{
    void EnsureIdle();
    Task<UpdateProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, string, bool> output, CancellationToken token);
}

public sealed class SteamStartupUpdater(SteamDebugOutput? debugOutput = null, ISteamStartupProcess? process = null) : ISteamStartupUpdater
{
    public async Task RunAsync(string executable, Action<SteamUpdateProgress> progress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = SteamCmdPath.Resolve(executable, Environment.CurrentDirectory);
        if (!path.IsValid)
            throw new IOException("未找到 SteamCMD，请检查程序位置后重试。");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path.FullPath.ToUpperInvariant())));
        using var ownership = new Semaphore(1, 1, "Local\\Kettle.SteamCMD." + hash);
        if (!ownership.WaitOne(0))
            throw new IOException("SteamCMD 正在使用中，请退出后重试。");
        try
        {
            var runner = process ?? new WindowsSteamStartupProcess();
            runner.EnsureIdle();
            var cleanup = SteamLibraryCleanup.CleanOwned(path.FullPath);
            if (cleanup.RemovedEntries > 0)
                progress(new(null, "启动前已清理 0 号库零大小条目：" + string.Join("、", cleanup.Files.SelectMany(x => x.AppIds).Distinct())));
            if (cleanup.RecycledManifests.Length > 0)
                progress(new(null, "已将 0 号库空安装 ACF 移入回收站：" + string.Join("、", cleanup.RecycledManifests.Select(Path.GetFileName))));
            foreach (string skipped in cleanup.Skipped)
                progress(new(null, "启动前清理已跳过：" + skipped));
            var parsers = new[]
            {
                new SteamUpdateProgressParser(progress),
                new SteamUpdateProgressParser(progress)
            };
            using var stdout = debugOutput?.CreateStream("SteamCMD 更新");
            using var stderr = debugOutput?.CreateStream("SteamCMD 更新 stderr");
            var normalizers = new[]
            {
                new TerminalTextNormalizer(),
                new TerminalTextNormalizer()
            };
            cancellationToken.ThrowIfCancellationRequested();
            var result = await runner.RunAsync(path.FullPath, ["+quit"], (stream, text, finished) =>
            {
                var normalized = normalizers[stream].Feed(text);
                (stream == 0 ? stdout : stderr)?.WriteNormalized(normalized);
                parsers[stream].FeedNormalized(normalized, finished);
            }, cancellationToken);
            if (!result.Success)
                throw new IOException("SteamCMD 更新未完成，请重试。");
        }
        finally
        {
            ownership.Release();
        }
    }
}

internal sealed class WindowsSteamStartupProcess : ISteamStartupProcess
{
    public void EnsureIdle()
    {
        foreach (string name in new[]
        {
            "steamcmd",
            "steam"
        }

        )
        {
            var candidates = System.Diagnostics.Process.GetProcessesByName(name);
            bool occupied = candidates.Length > 0;
            foreach (var candidate in candidates)
                candidate.Dispose();
            if (occupied)
                throw new IOException("Steam 正在使用中，请正常退出后重试。");
        }
    }

    public Task<UpdateProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, string, bool> output, CancellationToken token) => WindowsUpdateProcess.RunAsync(executable, arguments, output, pseudoConsole: true, cancellationToken: token);
}
