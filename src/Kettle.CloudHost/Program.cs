using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Steamworks;

// Isolated x86 Native AOT host. Keep the existing Steam native ABI.
class Program
{
    static readonly string[] Methods =
    {
        "Cloud.GetAppFileChangelist#1",
        "Cloud.GetSingleFileInfo#1",
        "Cloud.ClientFileDownload#1",
        "Cloud.ClientDeleteFile#1",
        "Cloud.BeginAppUploadBatch#1",
        "Cloud.ClientBeginFileUpload#1",
        "Cloud.ClientCommitFileUpload#1",
        "Cloud.CompleteAppUploadBatchBlocking#1"
    };
    static int Main(string[] args)
    {
        bool initialized = false;
        int exitCode = 0;
        try
        {
            if (args is ["--self-test"])
                return SelfTest();
            if (args.Length != 3)
                return 2;
            HostTrace.Start();
            uint app = uint.Parse(args[0]);
            string expectedRoot = Path.GetFullPath(args[1]);
            uint account = uint.Parse(args[2]);
            Environment.SetEnvironmentVariable("SteamAppID", app.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", app.ToString());
            HostTrace.Write("SteamAPI.Init begin");
            initialized = SteamAPI.Init();
            HostTrace.Write("SteamAPI.Init returned " + initialized);
            HostTrace.Write("account validation begin");
            if (!initialized || !SteamUser.BLoggedOn() || (uint)SteamUser.GetSteamID().GetAccountID() != account)
                throw new Exception("Steam context/account mismatch");
            HostTrace.Write("client module validation begin");
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().First(m => m.ModuleName.Equals("steamclient.dll", StringComparison.OrdinalIgnoreCase));
            if (!Path.GetFullPath(module.FileName).Equals(Path.Combine(expectedRoot, "steamclient.dll"), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Wrong Steam client context");
            string folder;
            HostTrace.Write("GetUserDataFolder begin");
            if (!SteamUser.GetUserDataFolder(out folder, 4096))
                throw new Exception("User data folder unavailable");
            string expectedApp = Path.Combine(expectedRoot, "userdata", account.ToString(), app.ToString());
            if (!Path.GetFullPath(folder).StartsWith(expectedApp + "\\", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected application data folder");
            HostTrace.Write("UnifiedMessages interface begin");
            if (SteamClient.GetISteamUnifiedMessages(SteamAPI.GetHSteamUser(), SteamAPI.GetHSteamPipe(), "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION001") == IntPtr.Zero)
                throw new Exception("Unified Messages unavailable");
            HostTrace.Write("cloud enabled query begin");
            Console.WriteLine(JsonSerializer.Serialize(new ReadyResponse(true, Path.Combine(expectedApp, "remote"), account, SteamRemoteStorage.IsCloudEnabledForAccount() && SteamRemoteStorage.IsCloudEnabledForApp()), HostJson.Default.ReadyResponse));
            Console.Out.Flush();
            HostTrace.Write("ready");
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line == "quit")
                    break;
                try
                {
                    var request = JsonSerializer.Deserialize(line, HostJson.Default.Request) ?? throw new InvalidDataException("Missing request");
                    if (!Methods.Contains(request.Method))
                        throw new Exception("RPC not allowed");
                    var bytes = Convert.FromBase64String(request.Payload);
                    if (bytes.Length > 4 * 1024 * 1024)
                        throw new Exception("RPC too large");
                    HostTrace.Write("SendMethod " + request.Method);
                    var handle = SteamUnifiedMessages.SendMethod(request.Method, bytes, (uint)bytes.Length, 0);
                    if (handle.Equals(default(ClientUnifiedMessageHandle)))
                        throw new Exception("RPC unavailable");
                    uint size = 0;
                    EResult result = 0;
                    bool ready = false;
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        HostTrace.Write("waiting: RunCallbacks / GetMethodResponseInfo");
                        while (watch.ElapsedMilliseconds < 20000)
                        {
                            SteamAPI.RunCallbacks();
                            if (SteamUnifiedMessages.GetMethodResponseInfo(handle, out size, out result))
                            {
                                ready = true;
                                break;
                            }

                            Thread.Sleep(20);
                        }

                        if (!ready || size > 8 * 1024 * 1024)
                            throw new Exception("RPC timeout or oversized result");
                        var response = new byte[size];
                        HostTrace.Write("GetMethodResponseData size=" + size + " result=" + (int)result);
                        if (size > 0 && !SteamUnifiedMessages.GetMethodResponseData(handle, response, size, false))
                            throw new Exception("RPC read failed");
                        Console.WriteLine(JsonSerializer.Serialize(new MethodResponse((int)result, Convert.ToBase64String(response), result.ToString()), HostJson.Default.MethodResponse));
                    }
                    finally
                    {
                        HostTrace.Write("ReleaseMethod begin");
                        SteamUnifiedMessages.ReleaseMethod(handle);
                        HostTrace.Write("ReleaseMethod complete");
                    }
                }
                catch (Exception e)
                {
                    HostTrace.Write("request failed: " + e);
                    Console.WriteLine(JsonSerializer.Serialize(new ErrorResponse(false, e.GetType().Name + ": " + e.Message), HostJson.Default.ErrorResponse));
                }

                Console.Out.Flush();
            }

            HostTrace.Write("request loop ended");
        }
        catch (Exception e)
        {
            HostTrace.Write("host failed: " + e);
            Console.WriteLine(JsonSerializer.Serialize(new ErrorResponse(false, e.Message), HostJson.Default.ErrorResponse));
            exitCode = 1;
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    HostTrace.Write("SteamAPI.Shutdown begin");
                    SteamAPI.Shutdown();
                    HostTrace.Write("SteamAPI.Shutdown complete");
                }
                catch (Exception e)
                {
                    HostTrace.Write("shutdown failed: " + e);
                    exitCode = 1;
                }
            }
        }
        HostTrace.Write("exit=" + exitCode);
        return exitCode;
    }

    // Offline check: validate AOT, JSON and DLL loading without calling SteamAPI.Init.
    static int SelfTest()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new InvalidOperationException("Expected x86 Native AOT");
        var request = new Request(Methods[0], Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        if (JsonSerializer.Deserialize(JsonSerializer.Serialize(request, HostJson.Default.Request), HostJson.Default.Request) != request)
            throw new InvalidOperationException("JSON round trip failed");
        using var ready = JsonDocument.Parse(JsonSerializer.Serialize(new ReadyResponse(true, "fixture", 1, true), HostJson.Default.ReadyResponse));
        if (!ready.RootElement.GetProperty("Ready").GetBoolean())
            throw new InvalidOperationException("Ready response failed");
        using var reply = JsonDocument.Parse(JsonSerializer.Serialize(new MethodResponse(1, request.Payload), HostJson.Default.MethodResponse));
        if (reply.RootElement.GetProperty("Result").GetInt32() != 1 || !Convert.FromBase64String(reply.RootElement.GetProperty("Payload").GetString()!).SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new InvalidOperationException("Method response failed");
        foreach (var library in new[] { "steam_api.dll", "CSteamworks.dll" })
        {
            var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, library));
            NativeLibrary.Free(handle);
        }
        Console.WriteLine("PASS: x86 Native AOT, JSON protocol, native DLL loading. Steam was not initialized.");
        return 0;
    }
}

internal sealed record Request(string Method, string Payload);
internal sealed record ReadyResponse(bool Ready, string RemoteRoot, uint Account, bool CloudEnabled);
internal sealed record MethodResponse(int Result, string Payload, string? ResultName = null);
internal sealed record ErrorResponse(bool Ready, string Error);
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(ReadyResponse))]
[JsonSerializable(typeof(MethodResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal partial class HostJson : JsonSerializerContext { }
