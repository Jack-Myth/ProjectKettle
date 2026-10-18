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
    public LibraryResult Read(string _) => new([new("Hollow Knight: Silksong", "1030300", "演示数据 · 未读取磁盘", "演示 · 已安装", "7.64 GB", ""), new("Little Busters! English Edition", "635940", "演示数据 · 未读取磁盘", "演示 · 已安装", "12.78 GB", ""), new("你的下一段冒险", "000000", "演示数据 · 未读取磁盘", "演示 · 未安装", "—", "")], [], 0);
}
