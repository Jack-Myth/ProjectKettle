internal static class RunningAppFixture
{
    public static string Row(uint id, int pid, string? exe = null) => $" - AppID {id}, GameID {id}, ProcessID {pid}, IP:Port 0.0.0.0:0\r\n   commandline \"\"{exe ?? "C:\\Fixture\\game.exe"}\"\"\r\n   extra info \"\"\r\n   associated PIDs ( {pid} {{parent: 5 }}, 222222 {{parent: {pid} }}, )\r\n   dwLastIsRunningCheck 12345\r\n";
}
