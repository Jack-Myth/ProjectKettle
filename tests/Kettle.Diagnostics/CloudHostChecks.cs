using System.Diagnostics;
using Kettle.Core;

internal static class CloudHostChecks
{
    internal static void Fixture(string mode)
    {
        Console.Error.Write(new string('x', 256 * 1024));
        Console.Error.WriteLine("stderr-tail-fixture");
        if (mode == "exit") { Environment.ExitCode = 7; return; }
        if (mode == "reject")
        {
            Console.WriteLine("""{"Ready":false,"Error":"fixture initialization failure"}""");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine("""{"Ready":true,"RemoteRoot":"fixture","CloudEnabled":true}""");
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (line == "quit")
            {
                if (mode == "hang") Thread.Sleep(Timeout.Infinite);
                return;
            }
            if (mode == "request-exit") { Environment.ExitCode = 7; return; }
            Console.WriteLine(mode switch
            {
                "result-error" => """{"Result":9,"ResultName":"k_EResultFileNotFound","Payload":""}""",
                "host-error" => """{"Ready":false,"Error":"Exception: RPC timeout or oversized result"}""",
                _ => """{"Result":1,"Payload":"AQID"}"""
            });
        }
    }

    internal static async Task Run(Action<bool, string> check)
    {
        foreach (var mode in new[] { "ok", "reject", "exit", "hang", "result-error", "host-error", "request-exit" })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--cloud-host-fixture");
            start.ArgumentList.Add(mode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            CloudHostClient client;
            try { client = await CloudHostClient.StartAsync(start, timeout.Token); }
            catch (IOException e) when (mode is "reject" or "exit")
            {
                check(mode != "reject" || e.Message.Contains("fixture initialization failure"), "initialization reason preserved");
                continue;
            }
            check(mode is not ("reject" or "exit"), "failed helper cannot be accepted as ready");
            check(client.CloudEnabled && client.RemoteRoot == "fixture", "ready handshake survives noisy stderr");
            if (mode is "result-error" or "host-error" or "request-exit")
            {
                try
                {
                    await client.CallAsync("Cloud.ClientFileDownload#1", [], timeout.Token);
                    throw new Exception("Failed RPC was accepted");
                }
                catch (IOException e)
                {
                    check(mode switch
                    {
                        "result-error" => e.Message.Contains("Result=9") && e.Message.Contains("k_EResultFileNotFound"),
                        "host-error" => e.Message.Contains("RPC timeout or oversized result"),
                        _ => e.Message.Contains("EOF")
                    }, "RPC failure retains the actual reason: " + mode);
                }
                try { await client.DisposeAsync(); }
                catch (IOException) when (mode == "request-exit") { }
                continue;
            }
            check((await client.CallAsync("fixture", [], timeout.Token)).SequenceEqual(new byte[] { 1, 2, 3 }), "RPC payload parsed");
            try
            {
                await client.DisposeAsync();
                check(mode == "ok" && client.LastExitCode == 0, "normal helper exit");
            }
            catch (IOException) when (mode == "hang")
            {
                check(client.ExitForced, "hung helper is terminated without terminating the parent");
            }
        }
        var trace = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "diagnostics", "cloudhost-client-latest.log"));
        check(trace.Contains("stderr-tail-fixture") && trace.Length < 96 * 1024, "stderr evidence is retained within the log limit");
    }
}
