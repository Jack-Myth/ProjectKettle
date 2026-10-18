using Kettle.Core;

internal static class LibraryChecks
{
    internal const string Licenses = """
    License packageID 0:
     - State : Active
     - Apps : 999, (1 in total)
    License packageID 1:
     - State : Active
     - Apps : 10, 20, 30, (3 in total)
    License packageID 2:
     - State : Active
     - Apps : 10, ... (3 in total)
    License packageID 3:
     - State : Inactive
     - Apps : 666, (1 in total)
    """;
    public static async Task Run(Action<bool, string> check, string root)
    {
        var licenses = SteamLibraryParser.Licenses(Licenses);
        check(licenses.Length == 3 && licenses.Single(p => p.PackageId == 2).Truncated, "active licenses parse and truncation detected");
        check(SteamLibraryParser.Packages("\"2\" { \"appids\" { \"0\" \"10\" \"1\" \"40\" \"2\" \"50\" } }")[2].Length == 3, "package appids recover full elided list");
        var app = SteamLibraryParser.Apps("\"10\" { \"common\" { \"name\" \"A {Game}\" \"type\" \"Game\" \"associations\" { \"0\" { \"name\" \"Wrong Publisher\" \"type\" \"publisher\" } } } }")[10];
        check(app.Name == "A {Game}" && app.Kind == AppKind.Game, "nested publisher fields cannot overwrite game name and type");
        var provider = new SteamOwnedLibraryProvider(Path.Combine(root, "owned-cache"));
        var cache = provider.CachePath("player_a");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, System.Text.Json.JsonSerializer.Serialize(new OwnedLibrarySnapshot([new(10, "Legacy", AppKind.Game, true)], true, true, DateTime.UtcNow, "legacy")));
        check(provider.ReadCache("player_a")?.Apps.Single().Name == "Legacy" && provider.ReadCache("player_b") is null, "legacy owned cache remains readable without network acquisition");
    }
}

internal sealed class FixtureMetadata(bool partial = false, string? licensesOverride = null) : ISteamMetadataCommands
{
    public bool IsAuthenticated => true;
    public List<string> Queries { get; } = [];

    public Task<string> QueryMetadataAsync(IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Queries.AddRange(commands);
        var outputs = commands.Select(command => command == "licenses_print" ? licensesOverride ?? LibraryChecks.Licenses : command.StartsWith("package_info_print") ? "\"2\" { \"appids\" { \"0\" \"10\" \"1\" \"40\" \"2\" \"50\" } }" : App(uint.Parse(command.Split(' ')[1])));
        return Task.FromResult(string.Join('\n', outputs));
    }

    private string App(uint id)
    {
        if (partial && id == 50)
            return "No app info for AppID 50";
        var type = id switch
        {
            10 => "Game",
            20 => "DLC",
            30 => "Tool",
            40 => "Demo",
            _ => "Application"
        };
        return $"\"{id}\" {{ \"common\" {{ \"name\" \"Fixture {id}\" \"type\" \"{type}\" }} }}";
    }
}
