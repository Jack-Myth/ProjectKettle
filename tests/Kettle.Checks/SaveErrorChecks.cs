using System.Text.Json;
using Kettle.Core;

internal static class SaveErrorChecks
{
    public static void Run(Action<bool, string> check)
    {
        var path = @"C:\saves\{literal}.sav";
        var error = SaveErrors.Tag(new IOException("worker fallback"), SaveErrorCode.CannotDeleteALinkedPath, path);
        var detail = SaveErrors.Describe(error);
        var result = new CoreSessionSyncResult("request", "error", error.Message, Error: detail);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, CoreSessionJson.Default.CoreSessionSyncResult), CoreSessionJson.Default.CoreSessionSyncResult)!;
        check(restored.Error == detail, "worker error code and literal detail survive JSON");
        // LocalizationChecks validates both resource sets; only the protocol-to-key mapping is specific here.
        foreach (var code in Enum.GetValues<SaveErrorCode>())
            check(Enum.TryParse<TextId>(code.ToString(), out var key) && Enum.IsDefined(key), "save error maps to a frontend resource key: " + code);
        var originalLanguage = L.Language;
        try
        {
            foreach (var language in new[] { "en", "zh-CN" })
            {
                L.Initialize(language);
                check(CloudMessages.Format(restored.Error, restored.Message) == L.Text(TextId.CannotDeleteALinkedPath) + path, "frontend localizes worker error and preserves literal path: " + language);
                var nested = new SaveError(nameof(SaveErrorCode.SyncIsIncompleteSomeLocalTransactionsStillRequireSafe), Cause: detail,
                    PrefixCode: nameof(SaveErrorCode.SaveVersionsChangedThePreviousChoiceWasNotApplied));
                check(CloudMessages.Format(nested, "") == L.Text(TextId.SaveVersionsChangedThePreviousChoiceWasNotApplied) + L.Text(TextId.SyncIsIncompleteSomeLocalTransactionsStillRequireSafe) + L.Text(TextId.CannotDeleteALinkedPath) + path,
                    "frontend retains prefix, nested error and detail");
            }
            check(CloudMessages.Format(null, "older worker") == "older worker", "older result messages remain readable");
            check(CloudMessages.Format(SaveErrors.Describe(new IOException("native diagnostic")), "") == "native diagnostic", "unmapped native errors retain diagnostic text");
        }
        finally { L.Initialize(originalLanguage); }
    }
}
