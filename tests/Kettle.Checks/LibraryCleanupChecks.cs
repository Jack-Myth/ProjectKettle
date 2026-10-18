using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Kettle.Core;

internal static class LibraryCleanupChecks
{
    private const string Fixture = """
    // keep comment and unknown fields byte for byte
    "libraryfolders"
    {
      "unknown" { "quote" "a\"b\\c" "braces" "{ untouched }" "unicode" "中文" }
      "0" { "path" "C:\\Fixture" "apps" {
        "228980" "0"
        "42" "17"
        "7" "00"
        "8" "unknown"
        "9" "-0"
        "10" { "size" "0" }
        "11" "0" "11" "23"
        "12" "0" [$WIN32]
      } "after" "keep" }
      "1" { "path" "F:\\Library" "apps" { "228980" "0" } }
    }
    "other-root" "keep"
    """;
    public static async Task Run(Action<bool, string> check, string root)
    {
        foreach (var encoding in new Encoding[]
        {
            new UTF8Encoding(false),
            new UTF8Encoding(true),
            new UnicodeEncoding(false, true),
            new UnicodeEncoding(true, true)
        }

        )
        {
            var home = NewHome(root);
            string exe = Path.Combine(home, "steamcmd.exe");
            var paths = Paths(home);
            var original = encoding.GetPreamble().Concat(encoding.GetBytes(FixtureFor(home))).ToArray();
            foreach (var path in paths)
                File.WriteAllBytes(path, original);
            string manifest = Path.Combine(home, "steamapps", "appmanifest_42.acf");
            File.WriteAllText(manifest, "never delete");
            var result = SteamLibraryCleanup.CleanBeforeStart(exe);
            check(result.Files.Length == 2 && result.RemovedEntries == 4 && result.Files.All(x => x.AppIds.SequenceEqual(new[] { "228980", "7" })), "both configuration copies remove numeric zero only in library zero: " + encoding.WebName);
            foreach (var file in result.Files)
            {
                check(file.BackupPath.Length == 0 && Directory.GetFiles(home, "*.kettle*backup*", SearchOption.AllDirectories).Length == 0, "library cleanup creates no persistent backup");
                var bytes = File.ReadAllBytes(file.Path);
                string updated = encoding.GetString(bytes[encoding.GetPreamble().Length..]);
                string fixture = FixtureFor(home);
                string expected = fixture.Remove(fixture.IndexOf("\"228980\" \"0\"", StringComparison.Ordinal), "\"228980\" \"0\"".Length).Replace("\"7\" \"00\"", "");
                check(updated == expected && bytes.AsSpan().StartsWith(encoding.GetPreamble()), "all non-target source spans, duplicates, conditional nodes, comments, escapes and encoding survive");
                var doc = KeyValuesDocument.Parse(updated);
                var libraries = doc.Children.Single(x => x.Key == "libraryfolders");
                var one = libraries.Children!.Single(x => x.Key == "1");
                check(one.Children!.Single(x => x.Key == "apps").Children!.Single().Value == "0", "same app ID in other library remains unchanged");
            }

            int backups = Directory.GetFiles(home, "*.kettle-zero-backup-*", SearchOption.AllDirectories).Length;
            check(SteamLibraryCleanup.CleanBeforeStart(exe).RemovedEntries == 0 && Directory.GetFiles(home, "*.kettle-zero-backup-*", SearchOption.AllDirectories).Length == backups, "second cleanup is a byte-stable no-op without extra backups");
            check(File.ReadAllText(manifest) == "never delete" && Directory.Exists(Path.Combine(home, "game-sentinel")), "manifest and game directory are untouched");
        }

        foreach (string broken in new[]
        {
            "\"libraryfolders\" {",
            "\"libraryfolders\" { \"0\" { \"apps\" { \"2\" \"unterminated } } }",
            Fixture + " garbage",
            "#include \"other.vdf\"\n" + Fixture,
            "\"libraryfolders\" { \"0\" {} \"0\" {} }",
            "\"libraryfolders\" { \"0\" { \"apps\" {} \"apps\" {} } }"
        }

        )
        {
            string home = NewHome(root);
            var paths = Paths(home);
            File.WriteAllText(paths[0], FixtureFor(home));
            File.WriteAllText(paths[1], broken);
            var before = paths.Select(File.ReadAllBytes).ToArray();
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(Path.Combine(home, "steamcmd.exe"));
            }
            catch (SteamLibraryCleanupException error)
            {
                failed = error.Message.Contains("原配置") || error.Message.Contains("原文件");
            }

            check(failed && paths.Select((p, i) => File.ReadAllBytes(p).SequenceEqual(before[i])).All(x => x) && Directory.GetFiles(home, "*.kettle-zero-backup-*", SearchOption.AllDirectories).Length == 0, "parse all copies first; malformed/ambiguous input changes neither file");
        }

        {
            string home = NewHome(root);
            var paths = Paths(home);
            foreach (var p in paths)
                File.WriteAllText(p, FixtureFor(home));
            var bytes = paths.Select(File.ReadAllBytes).ToArray();
            bool failed = false;
            using (var held = new FileStream(paths[1], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    SteamLibraryCleanup.CleanBeforeStart(Path.Combine(home, "steamcmd.exe"));
                }
                catch (SteamLibraryCleanupException)
                {
                    failed = true;
                }
            }

            check(failed && paths.Select((p, i) => File.ReadAllBytes(p).SequenceEqual(bytes[i])).All(x => x) && Directory.GetFiles(home, "*.kettle-zero-backup-*", SearchOption.AllDirectories).Length == 0, "second replacement failure rolls back first copy and uses only in-memory rollback");
        }

        {
            string home = NewHome(root);
            check(SteamLibraryCleanup.CleanBeforeStart(Path.Combine(home, "steamcmd.exe")).RemovedEntries == 0 && Paths(home).All(p => !File.Exists(p)), "first startup with no configuration does not invent or recreate files");
            var paths = Paths(home);
            File.WriteAllText(paths[0], FixtureFor(home));
            byte[] invalid = [0xFF, 0x00, 0xEE];
            File.WriteAllBytes(paths[1], invalid);
            bool failed = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(Path.Combine(home, "steamcmd.exe"));
            }
            catch (SteamLibraryCleanupException)
            {
                failed = true;
            }

            check(failed && File.ReadAllText(paths[0]) == FixtureFor(home) && File.ReadAllBytes(paths[1]).SequenceEqual(invalid), "invalid encoding in either copy keeps all original bytes");
        }

        {
            string home = NewHome(root);
            var paths = Paths(home);
            foreach (var p in paths)
                File.WriteAllText(p, FixtureFor(home));
            string exe = Path.Combine(home, "steamcmd.exe");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToUpperInvariant())));
            using var lease = new Semaphore(1, 1, "Local\\Kettle.SteamCMD." + hash);
            lease.WaitOne();
            bool blocked = false;
            try
            {
                SteamLibraryCleanup.CleanBeforeStart(exe);
            }
            catch (SteamLibraryCleanupException)
            {
                blocked = true;
            }
            finally
            {
                lease.Release();
            }

            check(blocked && paths.All(p => File.ReadAllText(p) == FixtureFor(home)), "existing session ownership prevents configuration writes");
        }

        await Startup(check, root);
    }

    private static string FixtureFor(string home) => Fixture.Replace(@"C:\\Fixture", home.Replace("\\", "\\\\"));
    private static string NewHome(string root)
    {
        string home = Path.Combine(root, "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "steamapps"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        Directory.CreateDirectory(Path.Combine(home, "game-sentinel"));
        File.WriteAllText(Path.Combine(home, "steamcmd.exe"), "fixture only");
        return home;
    }

    private static string[] Paths(string home) => [Path.Combine(home, "steamapps", "libraryfolders.vdf"), Path.Combine(home, "config", "libraryfolders.vdf")];
    private static async Task Startup(Action<bool, string> check, string root)
    {
        string home = NewHome(root), exe = Path.Combine(home, "steamcmd.exe");
        var paths = Paths(home);
        foreach (var p in paths)
            File.WriteAllText(p, FixtureFor(home));
        bool cleaned() => paths.All(p => !File.ReadAllText(p).Contains("\"7\" \"00\""));
        var runner = new Runner(() =>
        {
            check(cleaned(), "+quit updater launch sees cleaned configuration");
            foreach (var p in paths)
                File.WriteAllText(p, FixtureFor(home));
        });
        await new SteamStartupUpdater(process: runner).RunAsync(exe, _ =>
        {
        });
        check(runner.Runs == 1 && runner.IdleChecked && paths.All(p => File.ReadAllText(p) == FixtureFor(home)), "mock updater may register zero entries again after launch");
        var terminal = new Terminal(() => check(cleaned(), "-globaluser login cleans again after updater and before terminal StartAsync"));
        await using (var session = new SteamSession(() => terminal, true))
        {
            await session.StartAsync(exe, "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            check(session.StartupCleanup?.RemovedEntries == 4, "production login path reports actual removed entries");
        }

        File.WriteAllText(paths[1], "\"libraryfolders\" {");
        File.WriteAllText(paths[0], FixtureFor(home));
        runner = new Runner(() => throw new Exception("must not launch"));
        bool rejected = false;
        try
        {
            await new SteamStartupUpdater(process: runner).RunAsync(exe, _ =>
            {
            });
        }
        catch (SteamLibraryCleanupException)
        {
            rejected = true;
        }

        check(rejected && runner.Runs == 0 && File.ReadAllText(paths[0]) == FixtureFor(home), "malformed config blocks updater startup without rewriting other copy");
        terminal = new Terminal(() => throw new Exception("must not start"));
        await using (var session = new SteamSession(() => terminal, true))
        {
            await session.StartAsync(exe, "fixture");
            check(session.Current.State == LoginState.Failed && session.Current.Message.Contains("清理 0 号库配置失败") && !terminal.Started, "login failure exposes cleanup error and never starts terminal");
        }

        File.WriteAllText(paths[1], FixtureFor(home));
        runner = new Runner(() => throw new Exception("must not launch"))
        {
            Occupied = true
        };
        rejected = false;
        try
        {
            await new SteamStartupUpdater(process: runner).RunAsync(exe, _ =>
            {
            });
        }
        catch (IOException)
        {
            rejected = true;
        }

        check(rejected && paths.All(p => File.ReadAllText(p) == FixtureFor(home)), "updater checks running-process guard before any cleanup write");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        terminal = new Terminal(() => throw new Exception("must not start"));
        await using (var session = new SteamSession(() => terminal, true))
        {
            await session.StartAsync(exe, "fixture", cancelled.Token);
            check(!terminal.Started && paths.All(p => File.ReadAllText(p) == FixtureFor(home)), "cancelled startup does not clean configuration or launch a process");
        }
    }

    private sealed class Runner(Action start) : ISteamStartupProcess
    {
        public int Runs;
        public bool IdleChecked, Occupied;
        public void EnsureIdle()
        {
            IdleChecked = true;
            if (Occupied)
                throw new IOException("fixture process occupied");
        }

        public Task<UpdateProcessResult> RunAsync(string exe, IReadOnlyList<string> args, Action<int, string, bool> output, CancellationToken token)
        {
            if (!args.SequenceEqual(new[] { "+quit" }))
                throw new Exception("unexpected updater arguments");
            Runs++;
            start();
            return Task.FromResult(new UpdateProcessResult(true, 1, [0]));
        }
    }

    private sealed class Terminal(Action start) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool Started;
        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Started = true;
            start();
            output.Writer.TryWrite("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> text, CancellationToken token)
        {
            output.Writer.TryWrite("Logged in OK\r\nSteam>");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            output.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StopAsync();
            return ValueTask.CompletedTask;
        }
    }
}
