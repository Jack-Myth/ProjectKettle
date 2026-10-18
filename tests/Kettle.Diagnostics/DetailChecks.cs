using Kettle;
using Kettle.Core;
using System.Net;
using System.Text;
using System.Threading.Channels;

internal static class DetailChecks
{
    public static async Task Run(Action<bool, string> check, string root, string executable)
    {
        check(AppStatusReader.Parse(Body(635940, 13717425525), 635940).SizeOnDiskBytes == 13717425525, "real size-on-disk format retains exact 64-bit bytes rather than rounded depot totals");
        check(AppStatusReader.Parse(Body(42, null), 42).SizeOnDiskBytes is null && AppStatusReader.Parse(Body(42, 0), 42).SizeOnDiskBytes == 0, "missing size is unknown while zero bytes is a known value");
        check(AppStatusReader.FormatBytes(8207380004) == "7.64 GiB" && AppStatusReader.FormatBytes(0) == "0 B", "size display uses accurate binary units and preserves raw bytes separately");
        try
        {
            AppStatusReader.Parse(Body(43, 1), 42);
            throw new Exception("Mismatched app accepted");
        }
        catch (InvalidDataException)
        {
            check(true, "wrong application response cannot supply selected game size");
        }

        var terminal = new AsyncStatusTerminal();
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(executable, "detail_fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var first = AppStatusReader.ReadAsync(42, session);
        var next = session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3));
        await Task.Delay(170);
        check(!first.IsCompleted && !next.IsCompleted, "unfinished asynchronous app_status retains the queue until a prompt is received");
        check((await first).SizeOnDiskBytes == 4200 && (await next).Contains("Test Persona"), "complete delayed UserConfig response drains before next command");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = AppStatusReader.ReadAsync(42, session, cancellation.Token);
            await SessionChecks.Until(() => terminal.ActiveStatus);
            cancellation.Cancel();
            try
            {
                await cancelled;
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                check(session.IsAuthenticated, "cancel selection promptly without logging out SteamCMD");
            }

            check((await AppStatusReader.ReadAsync(43, session)).SizeOnDiskBytes == 4300, "cancelled asynchronous status is drained before a new selected app query");
        }

        var statuses = new ControlledStatuses();
        var images = new EmptyHeaders();
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(session, "detail_fixture", executable), action => action(), Path.Combine(root, "details-vm"), images, statuses);
        await SessionChecks.Until(() => vm.Games.Count == 2 && !vm.Busy);
        vm.CloseSettingsCommand.Execute(null);
        await SessionChecks.Until(() => statuses.Requested.Contains(42));
        await SessionChecks.Until(() => images.Requested.Contains(42));
        vm.SelectedGame = vm.Games.Single(g => g.AppId == "43");
        await SessionChecks.Until(() => statuses.Requested.Contains(43));
        statuses.Complete(43, 8207380004, false);
        await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 8207380004);
        statuses.Complete(42, 13717425525, true);
        await Task.Delay(60);
        check(vm.SelectedGame?.AppId == "43" && vm.SelectedSizeOnDiskBytes == 8207380004 && vm.DetailSize == "7.64 GiB", "late response for previous selection cannot overwrite new game's raw size or display");
        check(!vm.SelectedNeedsUpdate && vm.RunLabel == "运行", "late update result for previous selection cannot disable current game");
        images.CompletePrevious();
        await Task.Delay(50);
        check(!vm.HasHeader && vm.SelectedGame?.AppId == "43", "late previous-game image cannot replace current selection placeholder");
        statuses.Failure = new TimeoutException();
        vm.SelectedGame = vm.Games.Single(g => g.AppId == "42");
        await SessionChecks.Until(() => vm.DetailMessage.Contains("超时"));
        check(vm.SelectedSizeOnDiskBytes is null && vm.DetailSize == "未获取", "timeout clears prior game's size and presents an explicit unknown state");
        check(images.Requests <= 3 && !vm.HasHeader, "artwork failure has a placeholder and only selected applications are requested");
        await HeaderChecks(check, root);
    }

    internal static string Body(uint id, ulong? bytes) => $"AppID {id} (Synthetic game):\r\n - install state: {(bytes.HasValue ? "Fully Installed," : "uninstalled")}\r\n" + (bytes.HasValue ? $" - size on disk: {bytes} bytes, BuildID 1\r\n" : "") + " - user config: \"UserConfig\"\r\n{\r\n \"nested\" { \"quoted\" \"brace } in value\" }\r\n}\r\n";
    private static async Task HeaderChecks(Action<bool, string> check, string root)
    {
        var handler = new HeaderHandler();
        var home = Path.Combine(root, "header-cache");
        var service = new SteamHeaderService(home, new HttpClient(handler));
        var first = await service.LoadAsync(42);
        var second = await service.LoadAsync(42);
        check(first is { Cached: false } && second is { Cached: true } && handler.Requests == 2, "official store header_image and CDN are fetched asynchronously once, then portable cache avoids network");
        check(File.Exists(Path.Combine(home, "data", "images", "headers", "42.img")), "header cache stays under application data directory");
        handler.Fail = true;
        File.SetLastWriteTimeUtc(Path.Combine(home, "data", "images", "headers", "42.img"), DateTime.UtcNow.AddDays(-8));
        check(await service.LoadAsync(42) is { Cached: true }, "offline artwork lookup can show an existing public image cache");
        check(await service.LoadAsync(43) is null, "missing cache and failed official request return placeholder without blocking library");
        handler.Fail = false;
        handler.Untrusted = true;
        check(await service.LoadAsync(44) is null, "untrusted image hosts are rejected rather than downloaded");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await service.LoadAsync(45, cancelled.Token);
            throw new Exception("Header cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            check(true, "image download observes selection cancellation");
        }
    }
}

internal sealed class AsyncStatusTerminal : ISteamTerminal
{
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    public bool ActiveStatus;
    private bool blankPending;
    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        output.Writer.TryWrite("Steam>");
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
    public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
    {
        if (line.IsEmpty)
        {
            if (ActiveStatus)
                blankPending = true;
            else
                output.Writer.TryWrite("\r\nSteam>");
            return Task.CompletedTask;
        }

        var command = line.ToString();
        output.Writer.TryWrite(command + "\r\n");
        if (command.StartsWith("login "))
            output.Writer.TryWrite("Logged in OK\r\nSteam>");
        else if (command.StartsWith("app_status "))
        {
            ActiveStatus = true; // Delayed command has not emitted a completion marker yet.
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                var id = uint.Parse(command[11..]);
                var body = DetailChecks.Body(id, id * 100ul);
                output.Writer.TryWrite("\x1b[97m" + body[..^3]);
                await Task.Delay(30);
                output.Writer.TryWrite(body[^3..] + "\x1b[m");
                ActiveStatus = false;
                if (blankPending)
                {
                    blankPending = false;
                    output.Writer.TryWrite("\r\nSteam>");
                }
            });
        }
        else
            output.Writer.TryWrite((command switch
            {
                "library_folder_list" => "Index 0, ContentID 42, Path \"C:\\Synthetic Steam\", Label \"\", Apps 2, Mounted yes",
                "apps_installed" => "2 apps installed :\r\nAppID 42 : \"First Game\" : C:\\Synthetic Steam\\first\r\nAppID 43 : \"Second Game\" : C:\\Synthetic Steam\\second",
                _ => "Persona name: Test Persona\r\nSteamID: 76561198012345678"
            }) + "\r\nSteam>");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        output.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

internal sealed class ControlledStatuses : IAppStatusProvider
{
    public HashSet<uint> Requested { get; } = [];

    private readonly Dictionary<uint, TaskCompletionSource<AppStatus>> results = [];
    public Exception? Failure;
    public Task<AppStatus> ReadAsync(uint id, ISteamMetadataCommands commands, CancellationToken token)
    {
        Requested.Add(id);
        if (Failure is not null)
            return Task.FromException<AppStatus>(Failure);
        var source = new TaskCompletionSource<AppStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        results[id] = source;
        return source.Task;
    }

    public void Complete(uint id, ulong bytes, bool? needsUpdate = null) => results[id].TrySetResult(new(id, bytes, needsUpdate));
}

internal sealed class EmptyHeaders : IGameHeaderProvider
{
    public int Requests;
    public HashSet<uint> Requested { get; } = [];

    private readonly TaskCompletionSource<HeaderAsset?> previous = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<HeaderAsset?> LoadAsync(uint id, CancellationToken token)
    {
        Requests++;
        Requested.Add(id);
        return id == 42 && Requests == 1 ? previous.Task : Task.FromResult<HeaderAsset?>(null);
    }

    public void CompletePrevious() => previous.TrySetResult(new([0xff, 0xd8, 0xff, 0xe0, 0, 1, 2, 3], false));
}

internal sealed class DetailsOwned : IOwnedLibraryProvider
{
    public OwnedLibrarySnapshot? ReadCache(string account) => null;
    public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(new OwnedLibrarySnapshot([new(42, "First Game", AppKind.Game, true), new(43, "Second Game", AppKind.Game, true)], true, true, DateTime.UtcNow, "synthetic fixture"));
}

internal sealed class HeaderHandler : HttpMessageHandler
{
    public int Requests;
    public bool Fail, Untrusted;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests++;
        if (Fail)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var id = System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, @"appids=(\d+)").Groups[1].Value;
        var content = request.RequestUri.Host == "store.steampowered.com" ? (HttpContent)new StringContent($"{{\"{id}\":{{\"success\":true,\"data\":{{\"header_image\":\"https://{(Untrusted ? "untrusted.example" : "shared.akamai.steamstatic.com")}/header.jpg\"}}}}}}", Encoding.UTF8, "application/json") : new ByteArrayContent([0xff, 0xd8, 0xff, 0xe0, 0, 1, 2, 3]);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
