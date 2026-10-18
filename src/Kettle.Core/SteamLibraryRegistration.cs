using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record LibraryRegistration(string SteamCmdPath, string Root, string ConfigPath, string BackupPath, bool Changed, bool Removed = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? OriginalBytes
    {
        get; init;
    }
}

/// <summary>Edits only this SteamCMD instance's library configuration. Direct writes do not restart its session.
/// A verified file write is distinct from runtime mounting, which is queried after the user restarts.</summary>
public static class SteamLibraryRegistration
{
    public static string NormalizeFolder(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException(L.Text(TextId.SelectALibraryDirectory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Trim()));
        if (string.Equals(Path.GetFileName(full), "steamapps", StringComparison.OrdinalIgnoreCase))
            full = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(L.Text(TextId.TheLibraryDirectoryDoesNotExistOrIsInaccessible));
        return full;
    }

    public static LibraryRegistration RegisterDirect(string executable, string folder) => RegisterStopped(executable, folder, true);
    public static LibraryRegistration RemoveDirect(string executable, string folder) => RemoveStopped(executable, folder, true);
    public static SteamVolume[] ReadConfigured(string executable)
    {
        var config = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "steamapps", "libraryfolders.vdf");
        return ReadStructure(File.ReadAllText(config)).Entries.Select(entry => new SteamVolume(entry.Index, entry.Path, false)).ToArray();
    }

    public static LibraryRegistration RegisterStopped(string executable, string folder, bool allowRunning = false)
    {
        executable = Path.GetFullPath(executable);
        if (!SteamCmdPath.Resolve(executable, Environment.CurrentDirectory).IsValid)
            throw new IOException(L.Text(TextId.InvalidSteamCMDPath));
        var root = NormalizeFolder(folder);
        using var lease = Acquire(executable, allowRunning);
        var config = Path.Combine(Path.GetDirectoryName(executable)!, "steamapps", "libraryfolders.vdf");
        // Refuse to invent/replace a configuration whose format has not been verified.
        if (!File.Exists(config))
            throw new IOException(L.Text(TextId.SteamCMDHasNotCreatedALibraryConfigurationYetLog));
        var original = File.ReadAllBytes(config);
        var text = Encoding.UTF8.GetString(original).TrimStart('\uFEFF');
        var (close, entries) = ReadStructure(text);
        if (entries.Any(e => Path.TrimEndingDirectorySeparator(e.Path).Equals(root, StringComparison.OrdinalIgnoreCase)))
            return new(executable, root, config, "", false);
        var index = entries.Max(e => e.Index) + 1;
        var escaped = root.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var addition = $"\n\t\"{index}\"\n\t{{\n\t\t\"path\"\t\t\"{escaped}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{{\n\t\t}}\n\t}}\n";
        var updated = text.Insert(close, addition);
        _ = ReadStructure(updated);
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        // Check again, then atomically replace only the designated instance's file.
        if ((!allowRunning && SteamSession.HasConflict(executable)) || !File.ReadAllBytes(config).SequenceEqual(original))
            throw new IOException(L.Text(TextId.LibraryConfigurationChangedTheDirectoryWasNotRegisteredRetry));
        var temporary = config + ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            File.Move(temporary, config, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        if (!ReadConfigured(executable).Any(volume => SamePath(volume.Root, root)))
            throw new IOException(L.Text(TextId.TheConfigurationReadBackDidNotConfirmTheWrite));
        return new(executable, root, config, "", true)
        {
            OriginalBytes = original
        };
    }

    public static bool Confirmed(LibraryRegistration registration, IEnumerable<SteamVolume> volumes) => registration.Removed ? !volumes.Any(v => SamePath(v.Root, registration.Root)) : volumes.Any(v => v.Mounted && SamePath(v.Root, registration.Root));
    public static bool SamePath(string first, string second) => Path.TrimEndingDirectorySeparator(first).Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
    public static LibraryRegistration RemoveStopped(string executable, string folder, bool allowRunning = false)
    {
        executable = Path.GetFullPath(executable);
        if (!SteamCmdPath.Resolve(executable, Environment.CurrentDirectory).IsValid)
            throw new IOException(L.Text(TextId.InvalidSteamCMDPath));
        // Offline libraries can still be unregistered; no game directory access is required.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetFileName(root).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root)!;
        using var lease = Acquire(executable, allowRunning);
        var config = Path.Combine(Path.GetDirectoryName(executable)!, "steamapps", "libraryfolders.vdf");
        var original = File.ReadAllBytes(config);
        var text = Encoding.UTF8.GetString(original).TrimStart('\uFEFF');
        var (_, entries) = ReadStructure(text);
        var entry = entries.FirstOrDefault(e => SamePath(e.Path, root));
        if (SamePath(root, Path.GetDirectoryName(executable)!) || entry.Path is not null && entry.Index == 0)
            throw new InvalidOperationException(L.Text(TextId.TheDefaultLibraryCannotBeRemoved));
        if (entry.Path is null)
            return new(executable, root, config, "", false, true);
        var updated = text.Remove(entry.Start, entry.End - entry.Start);
        _ = ReadStructure(updated);
        if ((!allowRunning && SteamSession.HasConflict(executable)) || !File.ReadAllBytes(config).SequenceEqual(original))
            throw new IOException(L.Text(TextId.LibraryConfigurationChangedTheRegistrationWasNotRemoved));
        var temporary = config + ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            File.Move(temporary, config, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        if (ReadConfigured(executable).Any(volume => SamePath(volume.Root, root)))
            throw new IOException(L.Text(TextId.TheConfigurationReadBackDidNotConfirmRemovalRefresh));
        return new(executable, root, config, "", true, true)
        {
            OriginalBytes = original
        };
    }

    public static void RestoreIfUnchanged(LibraryRegistration change)
    {
        if (!change.Changed)
            return;
        using var lease = Acquire(change.SteamCmdPath);
        var original = change.OriginalBytes ?? File.ReadAllBytes(change.BackupPath);
        var text = File.ReadAllText(change.ConfigPath);
        var (_, current) = ReadStructure(text);
        var (_, prior) = ReadStructure(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        var expected = change.Removed ? prior.Where(e => !SamePath(e.Path, change.Root)).Select(e => e.Path) : prior.Select(e => e.Path).Append(change.Root);
        if (!expected.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(current.Select(e => e.Path)))
            throw new IOException(L.Text(TextId.LibraryConfigurationChangedExternallyAndWasNotOverwrittenThe));
        File.WriteAllBytes(change.ConfigPath, original);
    }

    // Used for the reversible registration test only, never on a running client.
    public static void RestoreTestRegistration(LibraryRegistration registration)
    {
        if (!registration.Changed)
            return;
        using var lease = Acquire(registration.SteamCmdPath);
        var original = registration.OriginalBytes ?? File.ReadAllBytes(registration.BackupPath);
        var (_, prior) = ReadStructure(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        var (_, current) = ReadStructure(File.ReadAllText(registration.ConfigPath));
        var expected = prior.Select(e => e.Path).Append(registration.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(current.Select(e => e.Path)))
            throw new IOException(L.Text(TextId.TheLibraryDirectoryChangedExternallyAndWasNotRestored));
        File.WriteAllBytes(registration.ConfigPath, original);
    }

    private static IDisposable Acquire(string executable, bool allowRunning = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        var semaphore = new Semaphore(1, 1, (allowRunning ? "Local\\Kettle.LibraryConfig." : "Local\\Kettle.SteamCMD.") + hash);
        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            throw new IOException(L.Text(TextId.SteamCMDIsInUseLibraryConfigurationWasNotModified));
        }

        var lease = new RegistrationLease(semaphore);
        try
        {
            if (!allowRunning && SteamSession.HasConflict(executable))
                throw new IOException(L.Text(TextId.SteamCMDIsInUseLibraryConfigurationWasNotModified));
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class RegistrationLease(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
            semaphore.Dispose();
        }
    }

    private static (int Close, List<(int Index, string Path, int Start, int End)> Entries) ReadStructure(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]").Cast<Match>().ToArray();
        if (tokens.Length < 4 || !tokens[0].Groups[1].Value.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase) || tokens[1].Value != "{")
            throw new InvalidDataException(L.Text(TextId.UnrecognizedSteamCMDLibraryConfigurationNoChangesWereMade));
        int depth = 0, close = -1, index = -1, start = -1;
        var entries = new List<(int Index, string Path, int Start, int End)>();
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Value == "{")
            {
                depth++;
                continue;
            }

            if (token.Value == "}")
            {
                if (depth == 2 && entries.Count > 0 && entries[^1].Index == index)
                {
                    var last = entries[^1];
                    entries[^1] = (last.Index, last.Path, last.Start, token.Index + 1);
                }

                depth--;
                if (depth == 0)
                {
                    close = token.Index;
                    if (i != tokens.Length - 1)
                        throw new InvalidDataException(L.Text(TextId.LibraryConfigurationContainsUnknownTrailingContent));
                }

                if (depth < 0)
                    throw new InvalidDataException(L.Text(TextId.MismatchedBracesInLibraryConfiguration));
                continue;
            }

            if (depth == 1 && i + 1 < tokens.Length && tokens[i + 1].Value == "{")
            {
                if (!int.TryParse(token.Groups[1].Value, out index))
                    throw new InvalidDataException(L.Text(TextId.InvalidLibraryConfigurationIndex));
                start = token.Index;
            }

            if (depth == 2 && token.Groups[1].Value.Equals("path", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
            {
                var path = tokens[++i].Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
                if (index < 0 || !Path.IsPathFullyQualified(path))
                    throw new InvalidDataException(L.Text(TextId.InvalidLibraryConfigurationPath));
                entries.Add((index, path, start, 0));
            }
        }

        if (depth != 0 || close < 0 || entries.Count == 0 || entries.Any(e => e.End <= e.Start) || entries.Select(e => e.Index).Distinct().Count() != entries.Count)
            throw new InvalidDataException(L.Text(TextId.InvalidLibraryConfigurationStructureNoChangesWereMade));
        return (close, entries);
    }
}
