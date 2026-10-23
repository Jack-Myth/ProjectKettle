using System.Diagnostics;
using System.Text.Json;
namespace Kettle.Core;
// Local file operations are delegated to the worker; the frontend never reads saves.
public static class CloudHostTasks
{
    private static async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start CloudHost.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var result = await output;
        var details = await error;
        if (process.ExitCode != 0)
        {
            SaveError? failure = null;
            try { failure = JsonSerializer.Deserialize(details, CoreSessionJson.Default.SaveError); }
            catch (JsonException) { }
            throw new IOException("CloudHost: " + CloudMessages.Format(failure, details.Trim()));
        }
        return result.Trim();
    }
    public static async Task<BackupRetentionReport[]> MaintainAsync(string appDirectory)
    {
        var reports = JsonSerializer.Deserialize(await RunAsync(["--maintain-backups", appDirectory], CancellationToken.None),
            CoreSessionJson.Default.BackupRetentionReportArray) ?? throw new IOException("Missing backup maintenance result.");
        return reports.Select(report => report with { Error = report.Failure is null ? report.Error : CloudMessages.Format(report.Failure, report.Error ?? "") }).ToArray();
    }
    public static Task<string> BackupAsync(string source, string destination, CancellationToken token) =>
        RunAsync(["--backup", source, destination], token);
}
