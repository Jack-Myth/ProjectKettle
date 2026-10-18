namespace Kettle.Core;

public sealed record LaunchOptions(string? SteamCmd, bool Preview, bool Library, bool Help, string? Capture, bool ExitAfterCapture)
{
    public bool DiagnoseLaunch
    {
        get; init;
    }
    public bool Debug
    {
        get; init;
    }

    public static LaunchOptions Parse(string[] args)
    {
        string? steam = null, capture = null;
        bool preview = false, library = false, help = false, exit = false, diagnose = false, debug = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value(string flag)
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                    throw new ArgumentException($"{flag} 需要一个非空路径参数。");
                return args[i];
            }

            switch (args[i])
            {
                case "--debug":
                    debug = true;
                    break;
                case "--diagnose-launch":
                    diagnose = true;
                    break;
                case "--steamcmd":
                    if (steam is not null)
                        throw new ArgumentException("--steamcmd 不能重复指定。");
                    steam = Value("--steamcmd");
                    break;
                case "--preview":
                    preview = true;
                    break;
                case "--library":
                    library = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--capture":
                    capture = Value("--capture");
                    break;
                case "--exit-after-capture":
                    exit = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}。使用 --help 查看帮助。");
            }
        }

        if (exit && capture is null)
            throw new ArgumentException("--exit-after-capture 需要 --capture。");
        if (diagnose && preview)
            throw new ArgumentException("启动诊断需要真实登录，不能与 --preview 合用。");
        return new(steam, preview, library, help, capture, exit)
        {
            DiagnoseLaunch = diagnose,
            Debug = debug
        };
    }

    public const string Usage = """
    Kettle — Avalonia 初版
    Kettle.exe [--steamcmd <steamcmd.exe 或目录>] [--library | --preview]
      --steamcmd  显式相对路径以当前工作目录为基准，支持带空格的引号路径。
                  未指定时读取便携设置，否则默认 <程序目录>/../steamcmd.exe。
      --library   兼容参数；仍进入正常登录流程，不会绕过账户认证。
      --preview   直接打开明确标记的演示游戏库，不访问 SteamCMD 或真实库。
      --help      显示帮助。
      --debug     启动时打开独立 Debug 窗口，实时查看 SteamCMD 输出。
      --diagnose-launch  登录后显示临时 LB 启动诊断面板，手动点击才执行。
      --capture <png> [--exit-after-capture]  开发验证：保存当前窗口渲染。
    配置：<程序目录>/data/settings.json；不存储密码或令牌。
    """;
}

public sealed record PathStatus(string FullPath, bool IsValid, string Message);
public static class SteamCmdPath
{
    public static string Default(string appDirectory) => Path.GetFullPath(Path.Combine(appDirectory, "..", "steamcmd.exe"));
    public static PathStatus Resolve(string? input, string relativeBase)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new("", false, "请填写 SteamCMD 路径。");
        try
        {
            var full = Path.GetFullPath(input.Trim(), relativeBase);
            if (Directory.Exists(full))
                full = Path.Combine(full, "steamcmd.exe");
            if (!string.Equals(Path.GetFileName(full), "steamcmd.exe", StringComparison.OrdinalIgnoreCase))
                return new(full, false, "请选择 steamcmd.exe 文件，或包含它的目录。");
            return File.Exists(full) ? new(full, true, "SteamCMD 已就绪 · 路径有效") : new(full, false, "未找到 steamcmd.exe，请检查路径。");
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new("", false, "路径无效或无法访问。");
        }
    }
}
