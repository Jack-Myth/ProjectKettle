using System.Globalization;
using Kettle.Core;

internal static class LocalizationChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        foreach (var culture in new[] { "zh-CN", "zh-TW", "zh-HK", "zh-Hans", "zh-Hant" })
            check(L.Resolve("system", CultureInfo.GetCultureInfo(culture)) == "zh-CN", culture + " uses Simplified Chinese");
        foreach (var culture in new[] { "en-US", "ja-JP", "de-DE" })
            check(L.Resolve("system", CultureInfo.GetCultureInfo(culture)) == "en", culture + " uses English");
        check(L.Resolve("en", CultureInfo.GetCultureInfo("zh-TW")) == "en" && L.Resolve("zh-CN", CultureInfo.GetCultureInfo("en-US")) == "zh-CN", "explicit language overrides system");
        var store = new PortableSettings(Path.Combine(root, "language"));
        check(store.Load().Language == "system", "missing settings follow system");
        store.Save(new(Username: "fixture", Language: "en"));
        check(store.Load().Language == "en" && store.Load().Username == "fixture", "language persists with account settings");
        store.Save(store.Load() with { Language = "invalid" });
        check(store.Load().Language == "system", "invalid saved language falls back to system");
        try
        {
            L.Initialize("en");
            check(L.Text(TextId.UpdateRequired) == "Update required", "English update status");
            check(L.Format(TextId.InstalledApps, 2, 3) == "2 / 3 installed apps", "localized format preserves arguments");
            foreach (var key in Enum.GetValues<TextId>())
            {
                var english = L.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.GetString(key.ToString());
                var chinese = L.Resources.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, false)!.GetString(key.ToString());
                check(english is not null && chinese is not null, "both languages contain " + key);
                var enFormat = System.Text.CompositeFormat.Parse(english!);
                var zhFormat = System.Text.CompositeFormat.Parse(chinese!);
                check(enFormat.MinimumArgumentCount == zhFormat.MinimumArgumentCount, "format arguments match for " + key);
            }
        }
        finally { L.Initialize("zh-CN"); }
    }
}
