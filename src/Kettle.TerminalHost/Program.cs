using System.Diagnostics;
using System.Runtime.InteropServices;

// No credentials pass through argv. This broker only repairs inherited std handles inside its ConPTY.
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1 || !OperatingSystem.IsWindows())
            return 80;
        var input = CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
        var output = CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
        if (input == -1 || output == -1)
            return 81;
        try
        {
            if (!SetHandleInformation(input, 1, 1) || !SetHandleInformation(output, 1, 1) || !SetStdHandle(-10, input) || !SetStdHandle(-11, output) || !SetStdHandle(-12, output))
                return 82;
            var start = new ProcessStartInfo(args[0])
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(args[0])!
            };
            foreach (var argument in args.Skip(1))
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start);
            if (child is null)
                return 83;
            child.WaitForExit();
            return child.ExitCode;
        }
        catch
        {
            return 84;
        }
        finally
        {
            CloseHandle(input);
            CloseHandle(output);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int kind, nint handle);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
