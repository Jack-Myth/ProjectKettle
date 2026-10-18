using Kettle.Core;
using System.Threading.Channels;

internal static partial class ChannelRecoveryChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var terminal = new RecoveryTerminal();
        await using var session = new SteamSession(() => terminal, false, recoveryTimeout: TimeSpan.FromSeconds(2));
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var slow = session.QueryMetadataAsync(["app_info_print 99"], TimeSpan.FromMilliseconds(80));
        await SessionChecks.Until(() => terminal.SlowSent);
        var queued = session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
        bool timedOut = false;
        try
        {
            await slow;
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        check(timedOut && session.CommandChannel.State == CommandChannelState.WaitingForLateResponse, "timeout reports waiting recovery instead of silently poisoning session");
        bool rejected = false;
        try
        {
            await queued.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (SteamCommandChannelException)
        {
            rejected = true;
        }

        check(rejected && terminal.InfoReads == 0, "already queued command is released with recoverable error without being sent");
        check(session.CommandChannel.Commands.SequenceEqual(new[] { "app_info_print 99" }), "channel diagnostics preserve first failed command descriptors without raw output");
        terminal.CompleteSlow();
        await SessionChecks.Until(() => session.CommandChannel.CanRead);
        var status = await AppStatusReader.ReadAsync(42, session);
        check(status.SizeOnDiskBytes == 4200 && session.IsAuthenticated && !terminal.Stopped, "late complete response restores app_status without relogin or process termination");
        var absent = new RecoveryTerminal();
        await using var hard = new SteamSession(() => absent, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await hard.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => hard.IsAuthenticated);
        try
        {
            await hard.QueryMetadataAsync(["app_info_print 99"], TimeSpan.FromMilliseconds(60));
        }
        catch (TimeoutException)
        {
        }

        await SessionChecks.Until(() => hard.CommandChannel.NeedsReconnect);
        absent.CompleteSlow();
        await Task.Delay(200);
        check(hard.CommandChannel.NeedsReconnect && hard.IsAuthenticated && !absent.Stopped, "reply after recovery deadline cannot silently clear lost protocol synchronization");
        rejected = false;
        try
        {
            await AppStatusReader.ReadAsync(42, hard);
        }
        catch (SteamCommandChannelException)
        {
            rejected = true;
        }

        check(rejected && absent.StatusReads == 0, "unrecoverable session rejects new reads immediately and requires explicit reconnect");
    }

    private sealed class RecoveryTerminal : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SlowSent, Stopped;
        public int InfoReads, StatusReads;
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            output.Writer.TryWrite("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public void CompleteSlow() => output.Writer.TryWrite("\"99\" { \"common\" { \"name\" \"late metadata complete\" } }\r\nSteam>");
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            if (line.IsEmpty)
            {
                output.Writer.TryWrite("\r\nSteam>");
                return Task.CompletedTask;
            }

            foreach (var command in line.ToString().Split('\r'))
            {
                if (command.StartsWith("login "))
                {
                    output.Writer.TryWrite("Logged in OK\r\nSteam>");
                    continue;
                }

                output.Writer.TryWrite(command + "\r\n");
                if (command == "app_info_print 99")
                {
                    SlowSent = true;
                    continue;
                }

                if (command.StartsWith("app_status "))
                {
                    StatusReads++;
                    var id = uint.Parse(command[11..]);
                    output.Writer.TryWrite("Steam>\r\n" + StatusFixture.Body(id, id * 100));
                }
                else if (command == "apps_installed")
                    output.Writer.TryWrite("2 apps installed :\r\nAppID 42 : \"Game A\" : C:\\Fixture\\A\r\nAppID 43 : \"Game B\" : C:\\Fixture\\B\r\nSteam>");
                else if (command == "library_folder_list")
                    output.Writer.TryWrite("Index 0, ContentID 1, Path \"C:\\Fixture\", Label \"\", Apps 2, Mounted yes\r\nSteam>");
                else
                {
                    if (command == "info")
                        InfoReads++;
                    output.Writer.TryWrite("Persona name: Fixture\r\nSteamID: 76561198012345678\r\nSteam>");
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
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
