using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;
using Steamworks;

// Isolated x86 host for the already-verified legacy ABI. No credentials, registry writes or file mutations.
class Program
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer
    {
        MaxJsonLength = 16 * 1024 * 1024
    };
    class Request
    {
        public string Method { get; set; }
        public string Payload { get; set; }
    }

    static readonly string[] Methods =
    {
        "Cloud.GetAppFileChangelist#1",
        "Cloud.GetSingleFileInfo#1",
        "Cloud.ClientFileDownload#1",
        "Cloud.BeginAppUploadBatch#1",
        "Cloud.ClientBeginFileUpload#1",
        "Cloud.ClientCommitFileUpload#1",
        "Cloud.CompleteAppUploadBatchBlocking#1"
    };
    static int Main(string[] args)
    {
        bool initialized = false;
        try
        {
            if (args.Length != 3)
                return 2;
            uint app = uint.Parse(args[0]);
            string expectedRoot = Path.GetFullPath(args[1]);
            uint account = uint.Parse(args[2]);
            Environment.SetEnvironmentVariable("SteamAppID", app.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", app.ToString());
            initialized = SteamAPI.Init();
            if (!initialized || !SteamUser.BLoggedOn() || (uint)SteamUser.GetSteamID().GetAccountID() != account)
                throw new Exception("Steam context/account mismatch");
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().First(m => m.ModuleName.Equals("steamclient.dll", StringComparison.OrdinalIgnoreCase));
            if (!Path.GetFullPath(module.FileName).Equals(Path.Combine(expectedRoot, "steamclient.dll"), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Wrong Steam client context");
            string folder;
            if (!SteamUser.GetUserDataFolder(out folder, 4096))
                throw new Exception("User data folder unavailable");
            string expectedApp = Path.Combine(expectedRoot, "userdata", account.ToString(), app.ToString());
            if (!Path.GetFullPath(folder).StartsWith(expectedApp + "\\", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected application data folder");
            if (SteamClient.GetISteamUnifiedMessages(SteamAPI.GetHSteamUser(), SteamAPI.GetHSteamPipe(), "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION001") == IntPtr.Zero)
                throw new Exception("Unified Messages unavailable");
            Console.WriteLine(Json.Serialize(new { Ready = true, RemoteRoot = Path.Combine(expectedApp, "remote"), Account = account, CloudEnabled = SteamRemoteStorage.IsCloudEnabledForAccount() && SteamRemoteStorage.IsCloudEnabledForApp() }));
            Console.Out.Flush();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line == "quit")
                    break;
                try
                {
                    var request = Json.Deserialize<Request>(line);
                    if (!Methods.Contains(request.Method))
                        throw new Exception("RPC not allowed");
                    var bytes = Convert.FromBase64String(request.Payload);
                    if (bytes.Length > 4 * 1024 * 1024)
                        throw new Exception("RPC too large");
                    var handle = SteamUnifiedMessages.SendMethod(request.Method, bytes, (uint)bytes.Length, 0);
                    uint size = 0;
                    EResult result = 0;
                    bool ready = false;
                    try
                    {
                        if (handle.Equals(default(ClientUnifiedMessageHandle)))
                            throw new Exception("RPC unavailable");
                        var watch = Stopwatch.StartNew();
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
                        if (size > 0 && !SteamUnifiedMessages.GetMethodResponseData(handle, response, size, false))
                            throw new Exception("RPC read failed");
                        Console.WriteLine(Json.Serialize(new { Result = (int)result, Payload = Convert.ToBase64String(response) }));
                    }
                    finally
                    {
                        SteamUnifiedMessages.ReleaseMethod(handle);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(Json.Serialize(new { Error = e.GetType().Name }));
                }

                Console.Out.Flush();
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(Json.Serialize(new { Ready = false, Error = e.Message }));
            return 1;
        }
        finally
        {
            if (initialized)
                SteamAPI.Shutdown();
        }
    }
}
