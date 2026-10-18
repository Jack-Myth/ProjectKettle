using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;
/// <summary>One owned SteamCMD lifetime. No transcript, credential cache, logging, or automatic credential persistence.</summary>
public sealed class SteamSession(Func<ISteamTerminal>? terminalFactory = null, bool checkConflicts = true, TimeSpan? timeout = null, TimeSpan? recoveryTimeout = null, string? diagnosticDirectory = null, SteamDebugStream? debugOutput = null) : IAsyncDisposable, ISteamMetadataCommands
{
    private readonly Queue<SteamCommandOutputDiagnostic> outputDiagnostics = new();
    public SteamCommandOutputDiagnostic[] RecentCommandDiagnostics
    {
        get
        {
            lock (outputDiagnostics)
                return outputDiagnostics.ToArray();
        }
    }

    public string CommandDiagnosticsPath => Path.Combine(diagnosticDirectory ?? Path.Combine(AppContext.BaseDirectory, "data", "diagnostics"), "steam-commands-latest.json");

    private SteamCommandOutputDiagnostic Remember(CommandResponseCapture capture, string stage, string? payload)
    {
        var evidence = capture.Diagnostic(stage) with
        {
            Payload = payload is null ? null : CommandOutputText.Limit(payload)
        };
        lock (outputDiagnostics)
        {
            outputDiagnostics.Enqueue(evidence);
            while (outputDiagnostics.Count > 8)
                outputDiagnostics.Dequeue();
        }

        return evidence;
    }

    private void SaveCommandFailure(string stage, Exception error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CommandDiagnosticsPath)!);
            // One bounded report, replaced atomically; failures to save never replace the original error.
            var json = KettleJson.Serialize(new CommandFailureReport(stage, error.GetType().Name, DateTime.UtcNow, RecentCommandDiagnostics));
            var temporary = CommandDiagnosticsPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, CommandDiagnosticsPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    internal void ReportParseFailure(Exception error) => SaveCommandFailure("response-parse-failed", error);
    internal void ReportExecutionFailure(Exception error) => SaveCommandFailure("command-execution-failed", error);
    private readonly ISteamTerminal terminal = terminalFactory?.Invoke() ?? new ConPtyTerminal();
    private SteamLoginHandler? login;
    private readonly TerminalTextNormalizer terminalText = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim inputGate = new(1, 1), commandGate = new(1, 1);
    private readonly object sync = new();
    private Semaphore? ownership;
    private bool ownershipHeld, loginSent, authenticated, ready;
    private Task? disposal;
    private string username = "";
    private Task? receive;
    private CommandResponseCapture? metadata;
    private readonly ConsoleOutputBuffer idleOutput = new(rolling: true);
    private readonly ConsoleOutputBuffer commandOutput = new();
    private int idlePromptsObserved;
    private void ClearIdle()
    {
        idleOutput.Clear();
        idlePromptsObserved = 0;
    }

    private bool hasCommandBoundary;
    private bool metadataBlocked;
    private CancellationTokenSource queueInvalidation = new();
    private readonly List<CancellationTokenSource> oldQueueTokens = [];
    private Task? metadataDrain;
    public CommandChannelStatus CommandChannel { get; private set; } = CommandChannelStatus.Ready();

    public event Action<CommandChannelStatus>? CommandChannelChanged;
    public event Action<SteamCommandBoundaryTrace>? CommandBoundaryObserved;
    private void BlockChannel(IReadOnlyList<string> commands, bool needsReconnect)
    {
        CancellationTokenSource cancel;
        lock (sync)
        {
            metadataBlocked = true;
            cancel = queueInvalidation;
            CommandChannel = new(needsReconnect ? CommandChannelState.ReconnectRequired : CommandChannelState.WaitingForLateResponse, needsReconnect ? "SteamCMD 的上一条响应无法确认，读取已暂停。本地存档与未完成同步记录保留，请按当前操作的提示恢复连接。" : "上一条 SteamCMD 响应超时，正在等待完整响应以安全恢复。可继续浏览，请查看当前操作的恢复提示。", commands.ToArray(), DateTime.UtcNow)
            {
                AppsRunningResponseTail = commands.Count == 1 && commands[0] == "apps_running" && metadata is not null ? CommandOutputText.Limit(metadata.DiagnosticTail()) : null,
                LaunchResponseTail = commands.Count == 1 && commands[0].StartsWith("app_launch ", StringComparison.Ordinal) && metadata is not null ? CommandOutputText.Limit(metadata.DiagnosticTail()) : null
            };
        }

        cancel.Cancel();
        CommandChannelChanged?.Invoke(CommandChannel);
    }

    private void RestoreChannel()
    {
        lock (sync)
        {
            metadataBlocked = false;
            oldQueueTokens.Add(queueInvalidation);
            queueInvalidation = new();
            CommandChannel = CommandChannelStatus.Ready();
        }

        CommandChannelChanged?.Invoke(CommandChannel);
    }

    private async Task AcquireCommand(CancellationToken token)
    {
        CancellationToken invalidated;
        lock (sync)
        {
            if (metadataBlocked)
                throw new SteamCommandChannelException(CommandChannel);
            invalidated = queueInvalidation.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, invalidated);
        try
        {
            await commandGate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var status = CommandChannel;
            if (status.CanRead)
                throw new InvalidOperationException("此前命令已取消，通道现已恢复，请重试当前操作。");
            throw new SteamCommandChannelException(status);
        }

        if (metadataBlocked)
        {
            commandGate.Release();
            throw new SteamCommandChannelException(CommandChannel);
        }
    }

    private readonly GameEventParser gameEvents = new();
    private readonly HashSet<int> observedGameProcesses = [];
    public SessionProcessSafety InspectDisconnectSafety()
    {
        lock (observedGameProcesses)
        {
            foreach (var pid in observedGameProcesses.ToArray())
            {
                try
                {
                    using var game = Process.GetProcessById(pid);
                    if (!game.HasExited)
                        return new(false, "SteamCMD 报告过的游戏进程仍在运行（PID " + pid + "），请正常退出后再试。");
                }
                catch (ArgumentException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return new(false, "无法验证已观察到的游戏进程，未断开连接。");
                }
            }
        }

        return terminal.InspectProcesses();
    }

    private GameRun? activeGame;
    public event Action<GameRuntimeEvent>? GameEventReceived;
    public bool GameCommandActive => activeGame is not null;

    private sealed class GameRun(uint appId)
    {
        public uint AppId = appId;
        public HashSet<int> Processes = [];
        public bool Started, Removed;
        public TaskCompletionSource StartedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private CancellationTokenRegistration cancellation;
    public event Action<LoginUpdate>? Updated;
    public LoginUpdate Current { get; private set; } = new(LoginState.Idle, "输入 Steam 账户名以继续。");
    public bool IsAuthenticated => authenticated;
    public LibraryCleanupResult? StartupCleanup
    {
        get; private set;
    }

    private void Update(LoginState state, string message) => EnsureLogin().Update(state, message);
    private SteamLoginHandler EnsureLogin() => login ??= new SteamLoginHandler(PublishLogin, OnPrompt, timeout);
    private void PublishLogin(LoginUpdate update)
    {
        lock (sync)
        {
            Current = update;
            if (update.State == LoginState.Authenticated)
            {
                authenticated = true;
                ready = false;
                awaitingAuthenticatedPrompt = true;
                ClearIdle();
            }
        }

        Updated?.Invoke(update);
    }

    private void OnPrompt()
    {
        lock (sync)
        {
            if (authenticated && awaitingAuthenticatedPrompt)
            {
                ClearIdle();
                awaitingAuthenticatedPrompt = false;
            }

            ready = true;
        }

        if (activeGame is { Removed: true } ended && ended.Processes.Count == 0)
            ended.Finished.TrySetResult();
    }

    public Task StartAsync(string executable, string account, CancellationToken cancellationToken = default) => StartAsync(executable, account, ReadOnlyMemory<char>.Empty, ReadOnlyMemory<char>.Empty, cancellationToken);
    public async Task StartAsync(string executable, string account, ReadOnlyMemory<char> password, ReadOnlyMemory<char> guardCode, CancellationToken cancellationToken = default)
    {
        if (Current.State != LoginState.Idle)
            throw new InvalidOperationException("请为重试创建新会话。");
        if (!Regex.IsMatch(account, @"\A[A-Za-z0-9_]{2,64}\z"))
        {
            Update(LoginState.Failed, "账户名格式不正确。");
            return;
        }

        if (!SteamLoginHandler.ValidInput(password.Span) || !SteamLoginHandler.ValidInput(guardCode.Span, 16))
        {
            Update(LoginState.Failed, "密码或令牌码含不支持的控制字符或长度超限，请重新输入。");
            return;
        }

        EnsureLogin().ConfigureInitialInput(password.Span, guardCode.Span);
        username = account;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkConflicts)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())));
                ownership = new Semaphore(1, 1, "Local\\Kettle.SteamCMD." + hash);
                ownershipHeld = ownership.WaitOne(0);
                if (!ownershipHeld || HasConflict(executable))
                {
                    Update(LoginState.Failed, "SteamCMD 正在使用中，请关闭已有会话后重试。");
                    await DisposeTerminal();
                    return;
                }

                StartupCleanup = SteamLibraryCleanup.CleanOwned(executable);
            }

            Update(LoginState.Starting, "正在连接…");
            cancellation = cancellationToken.Register(() => _ = CancelAsync());
            await terminal.StartAsync(executable, ["-globaluser"], lifetime.Token);
            receive = ReceiveAsync();
            _ = Watchdog();
        }
        catch (OperationCanceledException)
        {
            await CancelAsync();
        }
        catch (SteamLibraryCleanupException error)
        {
            Update(LoginState.Failed, error.Message);
            await DisposeTerminal();
        }
        catch
        {
            Update(LoginState.Failed, "无法启动 SteamCMD，请检查程序位置后重试。");
            await DisposeTerminal();
        }
    }

    public static bool HasConflict(string executable)
    {
        foreach (var process in Process.GetProcessesByName("steamcmd"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (!process.HasExited)
                        return true;
                }
            }
        }

        return false;
    }

    private bool awaitingAuthenticatedPrompt;
    private async Task ReceiveAsync()
    {
        try
        {
            await foreach (var chunk in terminal.ReadAsync(lifetime.Token))
            {
                var normalized = terminalText.Feed(chunk);
                debugOutput?.WriteNormalized(normalized);
                foreach (var gameEvent in gameEvents.FeedNormalized(normalized))
                {
                    if (!gameEvent.IsAuxiliary)
                        lock (observedGameProcesses)
                        {
                            if (gameEvent.Kind is GameEventKind.Added or GameEventKind.Updated)
                                observedGameProcesses.Add(gameEvent.ProcessId);
                        }

                    var run = activeGame;
                    if (run is not null && run.AppId == gameEvent.AppId && !gameEvent.IsAuxiliary)
                    {
                        if (gameEvent.Kind is GameEventKind.Added or GameEventKind.Updated)
                        {
                            run.Processes.Add(gameEvent.ProcessId);
                            run.Started = true;
                            run.Removed = false;
                            run.StartedSignal.TrySetResult();
                        }

                        if (gameEvent.Kind == GameEventKind.Removed)
                        {
                            run.Processes.Remove(gameEvent.ProcessId);
                            run.Removed = run.Started && run.Processes.Count == 0;
                        }

                        if (gameEvent.Kind == GameEventKind.LaunchFailed)
                        {
                            run.StartedSignal.TrySetException(new IOException("SteamCMD 未能启动游戏。"));
                            run.Finished.TrySetException(new IOException("SteamCMD 未能启动游戏。"));
                        }
                    }

                    GameEventReceived?.Invoke(gameEvent);
                }

                lock (sync)
                {
                    if (metadata is { } capture)
                        capture.AppendNormalized(normalized, chunk, terminalText.IncompleteControl);
                    else if (authenticated)
                        idleOutput.AppendNormalized(normalized, chunk, terminalText.IncompleteControl);
                }

                if (!authenticated || awaitingAuthenticatedPrompt)
                {
                    EnsureLogin().FeedNormalized(normalized);
                    if (IsTerminal(Current.State))
                        return;
                    if (ready && !loginSent)
                    {
                        loginSent = true;
                        ready = false;
                        EnsureLogin().BeginLogin();
                        var command = EnsureLogin().CreateLoginCommand(username);
                        try
                        {
                            await Send(command, lifetime.Token);
                        }
                        finally
                        {
                            Array.Clear(command);
                        }
                    }

                    // The authentication handler selects the matching one-shot input.
                    // Only values omitted from the native login parameters remain here.
                    if (EnsureLogin().TakeInitialInput() is { } initial)
                    {
                        try
                        {
                            await SubmitAsync(initial, lifetime.Token);
                        }
                        finally
                        {
                            Array.Clear(initial);
                        }
                    }
                }
                else
                {
                    lock (sync)
                    {
                        if (metadata is null)
                        {
                            int count = ConsolePrompts.Groups(GameNoticeFilter.Process(idleOutput.Text).Cleaned).Length;
                            if (count > idlePromptsObserved)
                            {
                                idlePromptsObserved = count;
                                OnPrompt();
                            }
                        }
                    }
                }
            }

            if (!lifetime.IsCancellationRequested && !IsTerminal(Current.State))
            {
                authenticated = false;
                Update(LoginState.Exited, "连接已断开，请重新登录。");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!lifetime.IsCancellationRequested)
            {
                authenticated = false;
                Update(LoginState.Exited, "连接已断开，请重试。");
            }
        }
        finally
        {
            login?.Reset();
            debugOutput?.Dispose();
            activeGame?.Finished.TrySetException(new IOException("游戏连接已中断，结束状态未确认。"));
            activeGame?.StartedSignal.TrySetException(new IOException("游戏连接已中断。"));
            await DisposeTerminal();
        }
    }

    public async Task SubmitAsync(ReadOnlyMemory<char> response, CancellationToken cancellationToken = default)
    {
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (!Current.NeedsInput || response.IsEmpty)
                return;
            if (!SteamLoginHandler.ValidInput(response.Span))
            {
                Update(Current.State, "输入含不支持的控制字符或长度超限，请重新输入。");
                return;
            }

            // Only the response currently requested is sent, never saved or included in a command/exception.
            EnsureLogin().PrepareInput(response.Span);
            ready = false;
            Update(LoginState.Connecting, "正在登录…");
            await terminal.WriteLineAsync(response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync();
        }
        catch
        {
            Update(LoginState.Failed, "暂时无法完成登录，请重试。");
            await CancelOwned();
        }
        finally
        {
            inputGate.Release();
        }
    }

    private async Task Send(ReadOnlyMemory<char> value, CancellationToken token)
    {
        await inputGate.WaitAsync(token);
        try
        {
            await terminal.WriteLineAsync(value, token);
        }
        finally
        {
            inputGate.Release();
        }
    }

    private async Task SendOrdinary(string command, CommandResponseCapture capture, CancellationToken token)
    {
        await inputGate.WaitAsync(token);
        try
        {
            await terminal.WriteLineAsync(command.AsMemory(), token);
            await terminal.WriteLineAsync(ReadOnlyMemory<char>.Empty, lifetime.Token);
            capture.MarkBlankSent();
        }
        finally
        {
            inputGate.Release();
        }
    }

    // Future commands reuse this authenticated session and are serialized until a real prompt returns.
    // This API deliberately does not return raw output; add typed parsers for each future command.
    public async Task ExecuteCommandAsync(string command, TimeSpan commandTimeout, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['\r', '\n', '\x1b']) >= 0 || Regex.IsMatch(command, @"^\s*(login|quit|exit|set_steam_guard_code|app_stop|app_uninstall|app_update|force_install_dir)\b", RegexOptions.IgnoreCase))
            throw new ArgumentException("该命令必须由会话生命周期或登录流程处理。");
        await CaptureCommandAsync([command], commandTimeout, token);
    }

    public async Task RunGameAsync(uint appId, Action<bool> progress, CancellationToken token = default)
    {
        await AcquireCommand(token);
        var run = new GameRun(appId);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ready && IsAuthenticated && DateTime.UtcNow < deadline)
                await Task.Delay(20, token);
            if (!IsAuthenticated || !ready || metadataBlocked)
                throw new IOException("SteamCMD 尚未就绪，请重新登录后重试。");
            token.ThrowIfCancellationRequested();
            activeGame = run;
            ready = false;
            await Send($"app_run {appId}".AsMemory(), CancellationToken.None);
            if (await Task.WhenAny(run.StartedSignal.Task, Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token)) != run.StartedSignal.Task)
                progress(false);
            await run.StartedSignal.Task;
            progress(true);
            await run.Finished.Task;
        }
        finally
        {
            activeGame = null;
            commandGate.Release();
        }
    }

    public async Task<string> QueryMetadataAsync(IReadOnlyList<string> commands, TimeSpan commandTimeout, CancellationToken token = default)
    {
        if (commands.Count is < 1 or > 64 || commands.Any(command => !Regex.IsMatch(command, @"^(app_info_print [0-9]+(?: common)?|info|help|find (?:library|app|install)|library_folder_list|apps_installed|apps_running|app_status [0-9]+|app_update_status [0-9]+)$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("只允许读取游戏库信息。");
        var results = new List<string>();
        // A metadata batch is a sequence, never several commands written at once.
        foreach (var command in commands)
        {
            var output = await CaptureCommandAsync([command], commandTimeout, token);
            results.Add(output);
        }

        return string.Join('\n', results);
    }

    public Task StopAppCommandAsync(uint appId, CancellationToken token = default, TimeSpan? responseTimeout = null) => AppStopCommand.ExecuteAsync(this, appId, responseTimeout ?? TimeSpan.FromSeconds(12), token);
    public Task<string> LaunchAppCommandAsync(uint appId, CancellationToken token = default, TimeSpan? responseTimeout = null, bool waitForNativeLifetime = false) => AppLaunchCommand.ExecuteAsync(this, appId, responseTimeout ?? TimeSpan.FromSeconds(12), waitForNativeLifetime, token);
    internal Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken token) => CaptureCommandAsync([command], timeout, token);
    private async Task AttachWhenIdle(CommandResponseCapture capture, IReadOnlyList<string> commands, DateTime stopAt, CancellationToken token)
    {
        while (DateTime.UtcNow < stopAt)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAuthenticated)
                throw new IOException("SteamCMD 尚未登录或连接已断开。");
            lock (sync)
            {
                lock (idleOutput.Sync)
                {
                    bool settled = !awaitingAuthenticatedPrompt && (!hasCommandBoundary || DateTime.UtcNow - idleOutput.Changed >= TimeSpan.FromMilliseconds(200));
                    if (settled && ready && !idleOutput.IncompleteControl)
                    {
                        // No request owns this interval. Keep bounded evidence, but do
                        // not turn unsolicited logs into a broken command channel.
                        if (!ConsolePrompts.IsNoise(GameNoticeFilter.Process(idleOutput.Text).Cleaned))
                        {
                            var background = idleOutput.Diagnostic("<idle>", "background-output", false, "Idle");
                            lock (outputDiagnostics)
                            {
                                outputDiagnostics.Enqueue(background);
                                while (outputDiagnostics.Count > 8)
                                    outputDiagnostics.Dequeue();
                            }
                        }
                        ClearIdle();
                        metadata = capture;
                        ready = false;
                        return;
                    }
                }
            }
            await Task.Delay(20, token);
        }

        BlockChannel(commands, true);
        throw new TimeoutException("SteamCMD 尚未完成上一条输出收尾，未发送下一命令；请检查连接状态。");
    }

    private bool CompleteAndDetach(CommandResponseCapture capture, out string result)
    {
        lock (sync)
        {
            if (!capture.TryComplete(out result))
                return false;
            if (ReferenceEquals(metadata, capture))
            {
                metadata = null;
                ClearIdle();
                ready = true;
                hasCommandBoundary = true;
            }

            return true;
        }
    }

    private void Detach(CommandResponseCapture capture)
    {
        lock (sync)
        {
            if (ReferenceEquals(metadata, capture))
            {
                metadata = null;
                ClearIdle();
            }
        }
    }

    private async Task<string> CaptureCommandAsync(IReadOnlyList<string> commands, TimeSpan commandTimeout, CancellationToken token)
    {
        await AcquireCommand(token);
        var capture = new CommandResponseCapture(commands, commandOutput);
        void Trace(string stage, string? payload = null)
        {
            if (commands.Count == 1)
            {
                var evidence = Remember(capture, stage, payload);
                CommandBoundaryObserved?.Invoke(new(commands[0], stage, DateTime.UtcNow, evidence.Raw)
                {
                    Output = evidence
                });
            }
        }

        bool deferredCleanup = false, issued = false;
        var stopAt = commandTimeout == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + commandTimeout;
        try
        {
            if (metadataBlocked)
                throw new SteamCommandChannelException(CommandChannel);
            await AttachWhenIdle(capture, commands, stopAt, token);
            token.ThrowIfCancellationRequested();
            issued = true;
            await SendOrdinary(commands[0], capture, token);
            while (DateTime.UtcNow < stopAt)
            {
                if (!IsAuthenticated)
                    throw new IOException("Steam 连接已断开。");
                if (CompleteAndDetach(capture, out var result))
                {
                    Trace("boundary-complete", result);
                    token.ThrowIfCancellationRequested();
                    return result;
                }

                token.ThrowIfCancellationRequested();
                await Task.Delay(20, lifetime.Token);
            }

            BlockChannel(commands, false);
            deferredCleanup = true;
            metadataDrain = DrainMetadata(capture, commands, DateTime.UtcNow + (recoveryTimeout ?? TimeSpan.FromSeconds(30)));
            throw new TimeoutException("SteamCMD 响应超时，正在等待安全恢复；请查看当前操作的恢复提示。");
        }
        catch (TimeoutException error)
        {
            Trace("command-timeout");
            SaveCommandFailure("command-timeout", error);
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            if (issued)
            {
                deferredCleanup = true;
                metadataDrain = DrainMetadata(capture, commands, stopAt == DateTime.MaxValue ? DateTime.UtcNow + (recoveryTimeout ?? TimeSpan.FromSeconds(30)) : stopAt);
            }

            throw;
        }
        catch (Exception error) when (issued && error is IOException or InvalidDataException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Trace("capture-failed");
            SaveCommandFailure("capture-failed", error);
            if (!lifetime.IsCancellationRequested)
                BlockChannel(commands, true);
            throw;
        }
        finally
        {
            if (!deferredCleanup)
            {
                Detach(capture);
                capture.Clear();
                commandGate.Release();
            }
        }
    }

    private async Task DrainMetadata(CommandResponseCapture capture, IReadOnlyList<string> commands, DateTime stopAt)
    {
        bool complete = false;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (CompleteAndDetach(capture, out _))
                {
                    complete = true;
                    return;
                }

                if (DateTime.UtcNow >= stopAt)
                {
                    if (metadataBlocked)
                    {
                        Remember(capture, "recovery-unverified", null);
                        BlockChannel(commands, true);
                        SaveCommandFailure("recovery-unverified", new TimeoutException());
                        return;
                    }

                    BlockChannel(commands, false);
                    stopAt = DateTime.UtcNow + (recoveryTimeout ?? TimeSpan.FromSeconds(30));
                }

                await Task.Delay(20, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!lifetime.IsCancellationRequested)
                BlockChannel(commands, true);
        }
        finally
        {
            var recoverThisCapture = complete && metadataBlocked && IsAuthenticated && !lifetime.IsCancellationRequested;
            Detach(capture);
            capture.Clear();
            commandGate.Release();
            if (recoverThisCapture)
                RestoreChannel();
        }
    }

    private async Task Watchdog()
    {
        try
        {
            while (!lifetime.IsCancellationRequested && !authenticated && !IsTerminal(Current.State))
            {
                await Task.Delay(100, lifetime.Token);
                if (DateTime.UtcNow <= EnsureLogin().Deadline)
                    continue;
                EnsureLogin().TimedOut();
                await CancelOwned();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsTerminal(LoginState state) => state is LoginState.Failed or LoginState.Cancelled or LoginState.TimedOut or LoginState.Exited;
    public async Task CancelAsync()
    {
        if (!IsTerminal(Current.State))
            Update(LoginState.Cancelled, "已取消登录");
        await CancelOwned();
    }

    private async Task CancelOwned()
    {
        authenticated = false;
        lifetime.Cancel();
        await DisposeTerminal();
    }

    private async Task DisposeTerminal()
    {
        login?.Reset();
        await terminal.StopAsync();
        lock (sync)
        {
            if (ownershipHeld)
            {
                ownership?.Release();
                ownershipHeld = false;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
            return new(disposal ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        await CancelOwned();
        if (receive is not null)
        {
            try
            {
                await receive.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }
        }

        if (metadataDrain is not null)
        {
            try
            {
                await metadataDrain.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        cancellation.Dispose();
        ownership?.Dispose();
        login?.Reset();
        debugOutput?.Dispose();
        username = "";
        await terminal.DisposeAsync();
    }

    public async Task LogoutAsync()
    {
        await DisposeAsync();
        Update(LoginState.Exited, "已登出");
    }
}
