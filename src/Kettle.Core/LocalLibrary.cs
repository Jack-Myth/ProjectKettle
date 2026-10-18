namespace Kettle.Core;

public sealed record LibraryEntry(string Root, string Source, string Status)
{
    public bool CanRemove
    {
        get; init;
    }
}
