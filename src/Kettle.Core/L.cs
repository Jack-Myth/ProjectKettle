using System.Globalization;
using System.Resources;

namespace Kettle.Core;

// Select once at startup so an active session never mixes languages.
public static class L
{
    internal static readonly ResourceManager Resources = new("Kettle.Core.Localization.Strings", typeof(L).Assembly);
    private static CultureInfo resourceCulture = CultureInfo.GetCultureInfo("zh-CN");
    public static string Language { get; private set; } = "zh-CN";

    public static string NormalizePreference(string? preference) => preference switch
    {
        "zh-CN" or "en" => preference,
        _ => "system"
    };

    public static string Resolve(string? preference, CultureInfo systemCulture) => NormalizePreference(preference) switch
    {
        "zh-CN" => "zh-CN",
        "en" => "en",
        _ => systemCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en"
    };

    public static void Initialize(string? preference, CultureInfo? systemCulture = null)
    {
        Language = Resolve(preference, systemCulture ?? CultureInfo.CurrentUICulture);
        resourceCulture = CultureInfo.GetCultureInfo(Language);
    }

    public static string Text(TextId key) => Resources.GetString(key.ToString(), resourceCulture)
        ?? throw new MissingManifestResourceException($"Missing localization resource: {key}");

    public static string Format(TextId key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
}
