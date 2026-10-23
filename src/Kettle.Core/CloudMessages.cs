namespace Kettle.Core;

// The worker sends structured identifiers; only the frontend knows translations.
public static class CloudMessages
{
    public static string Format(SaveError? error, string fallback)
    {
        if (error is null) return fallback;
        string Text(string? code) => Enum.TryParse<TextId>(code, out var id) && Enum.IsDefined(id) ? L.Text(id) : code ?? "";
        return Text(error.PrefixCode) + Text(error.Code) + (error.Detail ?? "")
            + (error.Cause is null ? "" : Format(error.Cause, ""));
    }
    public static string Describe(Exception error) => Format(SaveErrors.Describe(error), error.Message);
}
