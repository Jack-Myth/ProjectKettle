using System.Text.Json;
using System.Threading.Channels;
using Kettle.Core;

internal static class LaunchErrorChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        foreach (int chunk in new[]
        {
            1,
            7,
            4096
        }

        )
        {
            var terminal = new Terminal(chunk);
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            bool rejected = false;
            try
            {
                await session.LaunchAppCommandAsync(2806050, responseTimeout: TimeSpan.FromSeconds(3));
            }
            catch (GameLaunchRejectedException e)
            {
                rejected = e.Message.Contains("Dependency failure");
            }

            check(rejected && session.IsAuthenticated && session.CommandChannel.CanRead, "single-prompt Dependency failure ends launch without disconnect: " + chunk);
            check((await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3))).Contains("next response"), "next request works after launch rejection");
            terminal.Body = "unsupported complete launch body";
            bool malformed = false;
            try
            {
                await session.LaunchAppCommandAsync(2806050, responseTimeout: TimeSpan.FromSeconds(3));
            }
            catch (InvalidDataException)
            {
                malformed = true;
            }

            check(malformed && session.CommandChannel.CanRead, "complete unknown launch response reports parse failure and releases request");
            using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "early-prompt-app-status-2806050.json")));
            terminal.Raw = recorded.RootElement.GetProperty("Raw").GetString();
            var response = await session.QueryMetadataAsync(["app_status 2806050"], TimeSpan.FromSeconds(3));
            check(response.Contains("size on disk: 79550108673") && session.CommandChannel.CanRead, "recorded early prompt with body after it completes without trailing marker: " + chunk);
        }

        var backend = new Backend();
        var cloud = new Cloud();
        var lifecycle = new GameLifecycle(backend, cloud, Path.Combine(root, "launch-errors"), Path.Combine(root, "fixture.exe"), "fixture", () => Task.FromResult(123u));
        var game = new Game("Fixture", "12", root, "已安装", "", "");
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Failed && !lifecycle.Current.Busy && !lifecycle.BlocksSessionEnd && cloud.Finishes == 0, "explicit launch rejection restores idle controls and never uploads");
        backend.Error = new GameLaunchNotStartedException("unrecognized complete launch; empty fresh snapshot", new InvalidDataException());
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Failed && !lifecycle.BlocksSessionEnd && cloud.Finishes == 0, "parse failure confirmed unstarted permits retry without upload");
        backend.Error = null;
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Completed && cloud.Prepares == 3 && cloud.Finishes == 1, "retry creates fresh preparation and completes one confirmed game lifecycle: " + lifecycle.Current + " prepares=" + cloud.Prepares + " finishes=" + cloud.Finishes);
        backend.Error = new GameMonitoringException("query failed after running");
        backend.Started = true;
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Unknown && lifecycle.BlocksSessionEnd && cloud.Finishes == 1, "failure after observed running remains unknown and cannot upload");
    }

    private sealed class Backend : IGameBackend
    {
        public Exception? Error = new GameLaunchRejectedException("Dependency failure");
        public bool Started;
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.RemoteStorage, null, "fixture"));
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public Task RunAsync(uint id, Action<bool> progress, CancellationToken token)
        {
            if (Started || Error is null)
                progress(true);
            return Error is null ? Task.CompletedTask : Task.FromException(Error);
        }

        public Task UninstallAsync(uint id, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Cloud : IGameCloudSync
    {
        public int Prepares, Finishes;
        public Task<CloudPreparation> PrepareAsync(CloudContext c, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Prepares++;
            return Task.FromResult(new CloudPreparation(c, "fixture", true));
        }

        public Task FinishAsync(CloudPreparation c, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Finishes++;
            return Task.CompletedTask;
        }
    }

    private sealed class Terminal(int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public string Body = "ERROR! AppId 2806050 launch failed (Dependency failure)";
        public string? Raw;
        private void Emit(string text)
        {
            for (int i = 0; i < text.Length; i += chunk)
                output.Writer.TryWrite(text.Substring(i, Math.Min(chunk, text.Length - i)));
        }

        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            string c = line.ToString();
            if (c.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (c.StartsWith("app_launch"))
                Emit(c + "\r\n" + Body + "\r\nSteam>");
            else if (c.Length > 0)
                Emit(Raw ?? c + "\r\nnext response\r\nSteam>");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            output.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StopAsync();
            return ValueTask.CompletedTask;
        }
    }
}
