using System.Runtime.InteropServices;
using System.Text;
using Kettle.Core;

internal static class ConsoleFixture
{
    public static void Run(bool fresh = false)
    {
        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input, out var mode))
        {
            input = CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
            if (!GetConsoleMode(input, out mode))
                Environment.Exit(1000 + Marshal.GetLastWin32Error());
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.Write("Steam>");
        Read(input);
        Console.Write("Logging in using username/password.\r\nPassword:");
        SetConsoleMode(input, mode & ~4u);
        Read(input);
        SetConsoleMode(input, mode);
        Console.Error.Write(fresh ? "Steam Guard code:" : "\r\nSteam Guard code:");
        Read(input);
        if (fresh)
        {
            Console.Write("Logging in user 'fixture_user' to Steam Public...OK\r\nWaiting for client config...\r\nERROR! Package Info request returned error Timeout\r\nOK\r\nWaiting for user info...");
            Thread.Sleep(350);
            Console.Write("OK\r\nSteam>");
        }
        else
            Console.Write("\r\n中文连接状态\r\nLogging in user 'fixture_user' to Steam Public...OK\r\nWaiting for user info...OK\r\nSteam>");
        while (true)
        {
            var command = Read(input).Trim();
            if (command == "spawn_owned_fixture")
            {
                var helper = new System.Diagnostics.ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                foreach (var argument in new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Threading.Thread]::Sleep(1500)"
                }

                )
                    helper.ArgumentList.Add(argument);
                System.Diagnostics.Process.Start(helper)?.Dispose();
                Console.Write("\r\nFixture child started\r\nSteam>");
            }
            else if (command is "emit_wrapped_notice_fixture" or "emit_wide_notice_fixture")
            {
                const string start = "Game process removed: AppID 1030300 \"", end = "\", ProcID 42152 ";
                bool wide = command == "emit_wide_notice_fixture";
                var notice = start + new string('x', (wide ? ConPtyTerminal.Columns - 4 : 236) - start.Length - end.Length) + end;
                var marker = wide ? "WIDE_FIXTURE" : "WRAP_FIXTURE";
                Console.Write("\r\n" + marker + "_BEGIN\r\nSteam>" + notice + "\r\n" + marker + "_END\r\nSteam>");
            }
            else if (command.StartsWith("find __kettle_boundary_"))
                Console.Write("\r\nConVars:\r\nCommands:\r\nSteam>");
            else if (command.StartsWith("app_status "))
                Console.Write("\r\n" + StatusFixture.Body(uint.Parse(command[11..]), 4200) + "\r\nSteam>");
            else if (command == "app_info_print 46")
            {
                Thread.Sleep(700);
                Console.Write("\r\n\"46\" { \"common\" { \"name\" \"Command complete\" } }\r\nSteam>");
            }
            else if (command.StartsWith("app_info_print "))
                Console.Write("\r\n\"" + command.Split(' ')[1] + "\" { \"common\" { \"name\" \"Command complete\" } }\r\nSteam>");
            else if (command.Length == 0)
                Console.Write("\r\nSteam>");
            else if (command == "info")
                Console.Write("\r\nPersona name: Fixture Player\r\nSteamID: 76561198012345678\r\nSteam>");
            else
                Console.Write("\r\nCommand complete\r\nSteam>");
        }
    }

    private static string Read(nint input)
    {
        var buffer = new char[512];
        if (!ReadConsoleW(input, buffer, 512, out var count, 0))
            Environment.Exit(91);
        var value = new string(buffer, 0, (int)count);
        Array.Clear(buffer);
        return value;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleW(nint handle, [Out] char[] buffer, uint count, out uint read, nint control);
}
