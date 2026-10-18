using Kettle.Core;

internal static class CloudDirectoryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        static byte[] File(string name, uint state = 0, bool reupload = false) => CloudWire.Message((1, name), (2, new byte[20]), (3, 123ul), (4, 0u), (5, state), (9, reupload));
        var rpc = new Rpc();
        rpc.Responses.Enqueue(CloudWire.Message((1, 10ul), (4, "folder/"), (2, File("a.dat")), (2, File("b.dat"))));
        rpc.Responses.Enqueue(CloudWire.Message((1, 11ul), (3, true), (4, "folder/"), (2, File("a.dat", 2)), (2, File("c.dat", reupload: true))));
        rpc.Responses.Enqueue(CloudWire.Message((1, 12ul), (3, true), (4, "folder/"), (2, File("b.dat", 1))));
        var api = new CloudTransfer(42, rpc);
        var full = await api.ListAsync(default);
        check(full.Files.Select(f => f.Name).SequenceEqual(new[] { "folder/a.dat", "folder/b.dat" }), "RemoteStorage prefix expansion preserves nested names");
        var delta = await api.ListAsync(default);
        check(delta.Files.Length == 2 && delta.Files.Any(f => f.Name == "folder/b.dat") && delta.Files.Any(f => f.Name == "folder/c.dat" && f.ReuploadRequested) && delta.DeletedNames.SequenceEqual(new[] { "folder/a.dat" }), "delta merges unchanged files and explicit deletion/reupload markers");
        var forgotten = await api.ListAsync(default);
        check(forgotten.Files.Length == 1 && !forgotten.DeletedNames.Contains("folder/b.dat") && rpc.Versions.SequenceEqual(new ulong[] { 0, 10, 11 }), "Forgotten differs from Deleted and next request uses acknowledged version");
        foreach (var response in new[] {
            CloudWire.Message((1, 1ul), (3, true)),
            CloudWire.Message((1, 1ul), (4, "../"), (2, File("a.dat"))),
            CloudWire.Message((1, 1ul), (2, File("a")), (2, File("a/b"))) })
        {
            var bad = new Rpc();
            bad.Responses.Enqueue(response);
            bool rejected = false;
            try { await new CloudTransfer(42, bad).ListAsync(default); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { rejected = true; }
            check(rejected, "incomplete initial delta, traversal, and file/directory collision are rejected");
        }
    }

    private sealed class Rpc : ICloudRpc
    {
        public Queue<byte[]> Responses = new();
        public List<ulong> Versions = new();
        public string RemoteRoot => "";
        public bool CloudEnabled => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
        {
            if (method != "Cloud.GetAppFileChangelist#1") throw new Exception(method);
            Versions.Add(CloudWire.Parse(request).Number(2));
            return Task.FromResult(Responses.Dequeue());
        }
    }
}
