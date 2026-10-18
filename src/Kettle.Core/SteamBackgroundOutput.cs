using System.Text.RegularExpressions;

namespace Kettle.Core;

// Only complete, known informational lines. Unknown text and error variants stay
// visible to response validation; the original stream remains in debug output.
internal static class SteamBackgroundOutput
{
    private const string Client = @"IPC client is in my process, could/should be using an in-process pipe\.";
    private const string Server = @"IPC server is in my process - could/should be using an in process pipe";
    private static readonly Regex Information = new(
        @"(?m)^(?<prompt>[ \t]*(?:Steam>[ \t]*)*)(?:roaming config store loaded successfully - [0-9]+ bytes\.|(?:" + Client + "|" + Server + @")[ \t]*(?:(?:" + Client + "|" + Server + @")[ \t]*)*)[ \t]*(?:\r?\n|\z)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string Remove(string text) => Information.Replace(text, "${prompt}\n");
}
