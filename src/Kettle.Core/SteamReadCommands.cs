using System.Text.RegularExpressions;

namespace Kettle.Core;

public static class AppsRunningCommand
{
    public static async Task<AppsRunningSnapshot> ReadAsync(ISteamMetadataCommands channel, TimeSpan timeout, CancellationToken token = default)
    {
        string response = await channel.QueryMetadataAsync(["apps_running"], timeout, token);
        try
        {
            SteamResponseErrors.ThrowIfError("apps_running", response);
            return AppsRunningSnapshot.Parse(response);
        }
        catch (SteamCommandExecutionException error)
        {
            if (channel is SteamSession session)
                session.ReportExecutionFailure(error);
            throw;
        }
        catch (InvalidDataException error)
        {
            if (channel is SteamSession session)
                session.ReportParseFailure(error);
            throw;
        }
    }
}

public static class InstalledAppsCommand
{
    public static async Task<Game[]> ReadAsync(ISteamMetadataCommands channel, CancellationToken token = default)
    {
        string response = await channel.QueryMetadataAsync(["apps_installed"], TimeSpan.FromSeconds(30), token);
        try
        {
            SteamResponseErrors.ThrowIfError("apps_installed", response);
            return SteamInstalledLibrary.ParseInstalled(response);
        }
        catch (SteamCommandExecutionException error)
        {
            if (channel is SteamSession session)
                session.ReportExecutionFailure(error);
            throw;
        }
        catch (InvalidDataException error)
        {
            var failure = new InvalidDataException("apps_installed 响应解析失败：" + error.Message, error);
            if (channel is SteamSession session)
                session.ReportParseFailure(failure);
            throw failure;
        }
    }
}

public static class LibraryFoldersCommand
{
    public static async Task<SteamVolume[]> ReadAsync(ISteamMetadataCommands channel, CancellationToken token = default)
    {
        string response = await channel.QueryMetadataAsync(["library_folder_list"], TimeSpan.FromSeconds(30), token);
        try
        {
            SteamResponseErrors.ThrowIfError("library_folder_list", response);
            return SteamInstalledLibrary.ParseVolumes(response);
        }
        catch (SteamCommandExecutionException error)
        {
            if (channel is SteamSession session)
                session.ReportExecutionFailure(error);
            throw;
        }
        catch (InvalidDataException error)
        {
            var failure = new InvalidDataException("library_folder_list 响应解析失败：" + error.Message, error);
            if (channel is SteamSession session)
                session.ReportParseFailure(failure);
            throw failure;
        }
    }
}

public sealed record AppInfoResponse(string Vdf, ValveData Data);
public static class AppInfoCommand
{
    public static async Task<AppLaunchMetadata> ReadAsync(uint id, ISteamMetadataCommands channel, CancellationToken token = default)
    {
        string response = await channel.QueryMetadataAsync([$"app_info_print {id}"], TimeSpan.FromSeconds(30), token);
        try
        {
            SteamResponseErrors.ThrowIfError($"app_info_print {id}", response);
            return AppLaunchMetadata.Read(id, response);
        }
        catch (SteamCommandExecutionException error)
        {
            if (channel is SteamSession session)
                session.ReportExecutionFailure(error);
            throw;
        }
        catch (InvalidDataException error)
        {
            if (channel is SteamSession session)
                session.ReportParseFailure(error);
            throw;
        }
    }

    public static AppInfoResponse Parse(uint id, string output)
    {
        // A received response is parsed once here, after transport has released its queue.
        string value = output.Trim();
        string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string date = @"(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)[ \t]+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[ \t]+\d{1,2}[ \t]+\d{2}:\d{2}:\d{2}[ \t]+\d{4}";
        value = Regex.Replace(value, @"\AAppID[ \t]*:[ \t]*" + key + @",[ \t]*change number[ \t]*:[ \t]*\d+(?:/\d+)?(?:,[ \t]*last change[ \t]*:[ \t]*" + date + @")?[ \t]*\r?\n\s*", "", RegexOptions.CultureInvariant);
        var root = Regex.Match(value, @"\A""" + key + @"""\s*\{");
        if (!root.Success)
            throw Error(id, "未找到对应 AppID 的元数据对象，或响应头格式不支持。");
        int depth = 0, end = -1;
        bool quoted = false, escaped = false;
        for (int i = value.IndexOf('{', root.Index); i < value.Length; i++)
        {
            char c = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quoted && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
                continue;
            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
            {
                end = i + 1;
                break;
            }
        }

        if (end < 0 || !string.IsNullOrWhiteSpace(value[end..]))
            throw Error(id, "元数据对象不完整或包含无法识别的额外内容。");
        try
        {
            var data = ValveData.Parse(value, rejectDuplicates: true);
            if (data[key] is null)
                throw Error(id, "缺少请求的元数据对象。");
            return new(value, data);
        }
        catch (InvalidDataException error)
        {
            throw Error(id, "VDF 元数据解析失败：" + error.Message);
        }
    }

    private static InvalidDataException Error(uint id, string reason) => new($"app_info_print {id} 响应解析失败：{reason}");
}

public static class AppStopCommand
{
    public static async Task ExecuteAsync(SteamSession channel, uint id, TimeSpan timeout, CancellationToken token = default)
    {
        if (id == 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        Parse(id, await channel.SendCommandAsync($"app_stop {id} 0", timeout, token));
    }

    public static void Parse(uint id, string response)
    {
        SteamResponseErrors.ThrowIfError($"app_stop {id} 0", response);
        if (!string.IsNullOrWhiteSpace(response))
            throw new InvalidDataException($"app_stop {id} 0 响应解析失败：SteamCMD 返回了无法确认的停止结果；仍需检查运行状态。");
    }
}

public static class AppLaunchCommand
{
    public static async Task<string> ExecuteAsync(SteamSession channel, uint id, TimeSpan timeout, bool legacyNativeWait, CancellationToken token = default)
    {
        if (id == 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (!legacyNativeWait)
        {
            var output = await channel.SendCommandAsync($"app_launch {id}", timeout, token);
            Validate(channel, id, output);
            return output;
        }

        // This legacy opt-in observation belongs to the launch handler, not framing.
        // The ordinary production monitor uses the normal finite response timeout.
        var pids = new HashSet<int>();
        void Observe(GameRuntimeEvent value)
        {
            if (value.AppId != id || value.IsAuxiliary)
                return;
            lock (pids)
            {
                if (value.Kind is GameEventKind.Added or GameEventKind.Updated)
                    pids.Add(value.ProcessId);
                else if (value.Kind == GameEventKind.Removed)
                    pids.Remove(value.ProcessId);
            }
        }

        bool Alive()
        {
            lock (pids)
                foreach (int pid in pids)
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (!process.HasExited)
                            return true;
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return true;
                    }
                }

            return false;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        channel.GameEventReceived += Observe;
        var response = channel.SendCommandAsync($"app_launch {id}", Timeout.InfiniteTimeSpan, requestCancellation.Token);
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (!response.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (Alive())
                    deadline = DateTime.UtcNow.AddSeconds(8);
                if (DateTime.UtcNow >= deadline)
                {
                    requestCancellation.Cancel();
                    throw new TimeoutException($"app_launch {id} 响应等待超时；运行状态未确认。");
                }

                await Task.WhenAny(response, Task.Delay(20, token));
            }

            var output = await response;
            Validate(channel, id, output);
            return output;
        }
        finally
        {
            if (!response.IsCompleted)
                requestCancellation.Cancel();
            channel.GameEventReceived -= Observe;
        }
    }

    private static void Validate(SteamSession channel, uint id, string response)
    {
        try
        {
            Parse(id, response);
        }
        catch (SteamCommandExecutionException error)
        {
            channel.ReportExecutionFailure(error);
            throw;
        }
        catch (InvalidDataException error)
        {
            channel.ReportParseFailure(error);
            throw;
        }
    }

    public static void Parse(uint id, string response)
    {
        var failed = Regex.Match(response, @"(?im)^[ \t]*(?:ERROR![ \t]*)?AppId[ \t]+" + id + @"[ \t]+launch failed(?:[ \t]*\(([^\r\n]*)\))?[^\r\n]*");
        var error = Regex.Match(response, @"(?im)^[ \t]*ERROR(?:!|:|\b)[ \t]*([^\r\n]*)");
        if (failed.Success || error.Success)
        {
            string reason = failed.Success && failed.Groups[1].Success ? failed.Groups[1].Value.Trim() : error.Success ? error.Groups[1].Value.Trim() : "SteamCMD 报告启动失败";
            if (reason.Length > 512)
                reason = reason[..512];
            throw new GameLaunchRejectedException($"app_launch {id} 执行失败：{reason}");
        }

        if (!string.IsNullOrWhiteSpace(response))
            throw new InvalidDataException($"app_launch {id} 响应解析失败：已收到完整输出，但无法识别启动结果。");
    }
}
