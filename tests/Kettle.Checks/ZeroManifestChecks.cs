using System.Security.Cryptography;
using System.Text;
using Kettle.Core;

internal static class ZeroManifestChecks
{
    private const string Empty = "\"AppState\" { \"appid\" \"228980\" \"SizeOnDisk\" \"0\" \"StateFlags\" \"2\" \"buildid\" \"0\" \"StagingSize\" \"0\" \"InstalledDepots\" {} }";
    public static void Run(Action<bool, string> check, string root)
    {
        {
            var f = new Fixture(root);
            f.Write();
            var trash = new Trash(root);
            var original = File.ReadAllBytes(f.Manifest);
            string other = Path.Combine(f.Home, "other-library", "steamapps");
            Directory.CreateDirectory(other);
            string otherAcf = Path.Combine(other, Path.GetFileName(f.Manifest));
            File.WriteAllText(otherAcf, Empty.Replace("\"SizeOnDisk\" \"0\"", "\"SizeOnDisk\" \"100\""));
            var otherBytes = File.ReadAllBytes(otherAcf);
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
            check(result.RemovedEntries == 2 && result.DeletedManifests.SequenceEqual(new[] { f.Manifest }) && !File.Exists(f.Manifest), "zero default ACF is deleted together with both VDF zero registrations");
            check(trash.Moves.Count == 1 && trash.Moves[0].SequenceEqual(original), "deleter receives original complete ACF bytes");
            check(File.ReadAllBytes(otherAcf).SequenceEqual(otherBytes) && f.Configs.All(p => File.ReadAllText(p).Contains("\"228980\" \"100\"")), "same AppID in another library and its valid ACF are preserved");
            check(File.ReadAllText(f.Game) == "game/save sentinel" && !Directory.GetFiles(f.Home, "*.tmp", SearchOption.AllDirectories).Any() && !Directory.GetFiles(f.Home, "*backup*", SearchOption.AllDirectories).Any(), "no game/save writes or persistent config backups");
            var again = SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
            check(again.RemovedEntries == 0 && again.DeletedManifests.Length == 0 && trash.Moves.Count == 1, "second cleanup is idempotent");
        }

        {
            var f = new Fixture(root);
            f.Write(apps: "");
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            check(result.RemovedEntries == 0 && result.DeletedManifests.Length == 1 && !File.Exists(f.Manifest), "orphan zero ACF left after historical VDF-only cleanup is deleted");
        }

        foreach (string valid in new[]
        {
            Empty.Replace("\"SizeOnDisk\" \"0\"", "\"SizeOnDisk\" \"123\""),
            Empty.Replace("\"StateFlags\" \"2\"", "\"StateFlags\" \"4\""),
            Empty.Replace("\"buildid\" \"0\"", "\"buildid\" \"9\""),
            Empty.Replace("\"InstalledDepots\" {}", "\"InstalledDepots\" {\"1\" {\"manifest\" \"123\"}}"),
            Empty.Replace("\"StagingSize\" \"0\"", "\"StagingSize\" \"9\""),
            Empty.Replace("\"appid\" \"228980\"", "\"appid\" \"42\""),
            Empty.Replace("\"SizeOnDisk\" \"0\"", ""),
            Empty.Replace("\"InstalledDepots\" {}", "")
        }

        )
        {
            var f = new Fixture(root);
            f.Write(manifest: valid);
            var before = f.Snapshot();
            var trash = new Trash(root);
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
            check(f.Same(before) && trash.Moves.Count == 0 && result.RemovedEntries == 0 && result.Skipped.Length == 1, "valid/nonzero/incomplete/mismatched ACF preserves both zero VDF entries and reports skip");
        }

        foreach (string bad in new[]
        {
            "\"AppState\" {",
            Empty.Replace("\"SizeOnDisk\" \"0\"", "\"SizeOnDisk\" \"0\" [$WIN32]"),
            Empty.Replace("\"appid\" \"228980\"", "\"appid\" \"228980\" \"appid\" \"228980\"")
        }

        )
        {
            var f = new Fixture(root);
            f.Write(manifest: bad);
            var before = f.Snapshot();
            var trash = new Trash(root);
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }

            check(failed && f.Same(before) && trash.Moves.Count == 0, "malformed/conditional/duplicate ACF fields fail before any mutation");
        }

        foreach (string apps in new[]
        {
            "\"228980\" \"0\" \"228980\" \"0\"",
            "\"228980\" \"0\" \"0228980\" \"0\"",
            "\"228980\" \"unknown\"",
            "\"228980\" \"20\""
        }

        )
        {
            var f = new Fixture(root);
            f.Write(apps: apps);
            var before = f.Snapshot();
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            check(f.Same(before) && result.RemovedEntries == 0 && result.DeletedManifests.Length == 0, "ambiguous/nonzero default registration prevents ACF removal");
        }

        {
            var f = new Fixture(root);
            f.Write();
            File.WriteAllText(f.Configs[1], f.Vdf("\"228980\" \"10\""));
            var before = f.Snapshot();
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            check(f.Same(before) && result.RemovedEntries == 0 && result.DeletedManifests.Length == 0, "nonzero registration in either VDF blocks cleanup in both copies");
        }

        {
            var f = new Fixture(root);
            f.Write();
            File.WriteAllText(f.Configs[1], f.Vdf().Replace(f.Home.Replace("\\", "\\\\"), "C:\\Wrong"));
            var before = f.Snapshot();
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }

            check(failed && f.Same(before), "mismatched library-zero root prevents mutation of all copies and ACF");
        }

        foreach (int failOn in new[]
        {
            1,
            2
        }

        )
        {
            var f = new Fixture(root);
            f.Write(apps: "\"228980\" \"0\" \"7\" \"0\"");
            string extra = Path.Combine(f.Home, "steamapps", "appmanifest_7.acf");
            File.WriteAllText(extra, Empty.Replace("228980", "7"));
            var before = f.Snapshot();
            byte[] extraBefore = File.ReadAllBytes(extra);
            var trash = new Trash(root, failOn);
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }

            check(failed && f.Same(before) && File.ReadAllBytes(extra).SequenceEqual(extraBefore), "deletion failure restores both VDFs and all already deleted ACFs from memory: " + failOn);
            check(!Directory.GetFiles(f.Home, "*.tmp", SearchOption.AllDirectories).Any(), "deletion rollback leaves no temporary backup files");
        }

        {
            var f = new Fixture(root);
            f.Write();
            var before = f.Snapshot();
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(f.Exe, new DeleteThenFail(root));
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }

            check(failed && f.Same(before), "deleter that deletes then reports failure restores absent ACF and both original VDFs");
        }

        {
            var f = new Fixture(root);
            f.Write();
            var before = f.Snapshot();
            var trash = new Trash(root);
            bool failed = false;
            using (var held = new FileStream(f.Configs[1], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    SteamLibraryCleanup.CleanBeforeStart(f.Exe, trash);
                }
                catch (SteamLibraryCleanupException)
                {
                    failed = true;
                }
            }

            check(failed && f.Same(before) && trash.Moves.Count == 0, "VDF replacement failure restores earlier VDF and never begins ACF deletion");
        }

        {
            var f = new Fixture(root);
            f.Write();
            var before = f.Snapshot();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Exe.ToUpperInvariant())));
            using var lease = new Semaphore(1, 1, "Local\\Kettle.SteamCMD." + hash);
            lease.WaitOne();
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }
            finally
            {
                lease.Release();
            }

            check(failed && f.Same(before), "occupied SteamCMD ownership prevents ACF and VDF writes");
        }

        {
            var f = new Fixture(root);
            File.WriteAllText(f.Manifest, Empty);
            var result = SteamLibraryCleanup.CleanBeforeStart(f.Exe, new Trash(root));
            check(File.Exists(f.Manifest) && result.DeletedManifests.Length == 0, "missing library-zero identity preserves even an empty orphan ACF");
        }
    }

    private sealed class Fixture
    {
        public string Home, Exe, Manifest, Game;
        public string[] Configs;
        public Fixture(string root)
        {
            Home = Path.Combine(root, "acf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "steamapps"));
            Directory.CreateDirectory(Path.Combine(Home, "config"));
            Exe = Path.Combine(Home, "steamcmd.exe");
            File.WriteAllText(Exe, "synthetic");
            Manifest = Path.Combine(Home, "steamapps", "appmanifest_228980.acf");
            Game = Path.Combine(Home, "game-save.txt");
            File.WriteAllText(Game, "game/save sentinel");
            Configs = [Path.Combine(Home, "steamapps", "libraryfolders.vdf"), Path.Combine(Home, "config", "libraryfolders.vdf")];
        }

        public string Vdf(string apps = "\"228980\" \"0\"") => "\"libraryfolders\" { \"0\" { \"path\" \"" + Home.Replace("\\", "\\\\") + "\" \"apps\" {" + apps + "} } \"1\" {\"path\" \"F:\\\\Library\" \"apps\" { \"228980\" \"100\" }} }";
        public void Write(string apps = "\"228980\" \"0\"", string manifest = Empty)
        {
            foreach (var p in Configs)
                File.WriteAllText(p, Vdf(apps));
            File.WriteAllText(Manifest, manifest);
        }

        public byte[][] Snapshot() => Configs.Append(Manifest).Select(File.ReadAllBytes).ToArray();
        public bool Same(byte[][] before) => Configs.Append(Manifest).Select((p, i) => File.Exists(p) && File.ReadAllBytes(p).SequenceEqual(before[i])).All(x => x);
    }

    private sealed class Trash(string root, int failOn = 0) : IBackupDelete
    {
        public List<byte[]> Moves = [];
        private int calls;
        public void Delete(string path)
        {
            if (++calls == failOn)
                throw new IOException("synthetic deletion failure");
            Moves.Add(File.ReadAllBytes(path));
            new TestBackupDelete(root).Delete(path);
        }
    }

    private sealed class DeleteThenFail(string root) : IBackupDelete
    {
        public void Delete(string path)
        {
            new TestBackupDelete(root).Delete(path);
            throw new IOException("synthetic failure after deletion");
        }
    }
}
