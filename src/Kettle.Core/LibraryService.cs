namespace Kettle.Core;

public sealed record Game(string Name, string AppId, string InstallPath, string State, string Size, string Source)
{
    public string Monogram => Name.Length == 0 ? "K" : Name[..1].ToUpperInvariant();
    public string AppLabel => $"APP {AppId}";
    public string MetadataStatus { get; init; } = "";
}

public sealed record LibraryResult(IReadOnlyList<Game> Games, IReadOnlyList<string> Warnings, int Roots)
{
    public IReadOnlyList<string> RootPaths { get; init; } = [];
}

public sealed class PreviewLibraryService
{
    public LibraryResult Read(string _) => new([new("Hollow Knight: Silksong", "1030300", L.Text(TextId.DemoDataDiskNotAccessed), L.Text(TextId.DemoInstalled), "7.64 GB", ""), new("Little Busters! English Edition", "635940", L.Text(TextId.DemoDataDiskNotAccessed), L.Text(TextId.DemoInstalled), "12.78 GB", ""), new(L.Text(TextId.YourNextAdventure), "000000", L.Text(TextId.DemoDataDiskNotAccessed), L.Text(TextId.DemoNotInstalled), "—", "")], [], 0);
}
