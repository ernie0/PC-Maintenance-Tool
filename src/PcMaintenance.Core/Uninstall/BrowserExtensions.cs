using System.Text.Json;

namespace PcMaintenance.Core.Uninstall;

/// <param name="Profile">"預設" or "設定檔 3": the profile folder, never the person's name or account the browser shows.</param>
/// <param name="Enabled">Null when the browser's settings do not say.</param>
/// <param name="FromStore">Installed from the browser's own store, or pushed by company policy.</param>
public sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, string Version, bool? Enabled, string Source, bool FromStore)
{
    public string StateText => Enabled switch { true => "啟用", false => "已停用", null => "—" };
}

/// <summary>
/// Extensions the current user installed in Chrome, Edge and Firefox, read from each profile's own settings files. Built-in
/// components are left out. Read only: removing one is done in the browser, which keeps its settings consistent.
/// </summary>
public static class BrowserExtensions
{
    public const string ChromeName = "Chrome";
    public const string EdgeName = "Edge";
    public const string FirefoxName = "Firefox";

    public static IReadOnlyList<BrowserExtension> Read()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<BrowserExtension>();
        list.AddRange(ReadChromium(ChromeName, Path.Combine(local, @"Google\Chrome\User Data")));
        list.AddRange(ReadChromium(EdgeName, Path.Combine(local, @"Microsoft\Edge\User Data")));
        list.AddRange(ReadFirefox(Path.Combine(roaming, @"Mozilla\Firefox\Profiles")));
        return list
            .OrderBy(e => e.Browser, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ThenBy(e => e.Profile, StringComparer.CurrentCulture)
            .ToList();
    }

    // Chromium's install locations: 1 internal (store or manual), 2/3/6 another program, 4 unpacked by a developer,
    // 5/10 built-in components, 7/9 company policy, 8 command line.
    private const int Internal = 1, Unpacked = 4, Component = 5, ExternalComponent = 10, PolicyDownload = 7, Policy = 9;

    public static IReadOnlyList<BrowserExtension> ReadChromium(string browser, string userData)
    {
        var list = new List<BrowserExtension>();
        if (!Directory.Exists(userData)) return list;
        foreach (var profileDir in SafeDirectories(userData).Where(IsChromiumProfile))
        {
            var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            // Newer versions keep extension settings in "Secure Preferences"; "Preferences" may hold the rest.
            foreach (var file in new[] { "Secure Preferences", "Preferences" })
            {
                if (ReadJson(Path.Combine(profileDir, file)) is not { } json) continue;
                if (json.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("settings", out var all) && all.ValueKind == JsonValueKind.Object)
                {
                    foreach (var item in all.EnumerateObject())
                        if (!settings.ContainsKey(item.Name)) settings[item.Name] = item.Value.Clone();
                }
            }
            var profile = ProfileLabel(Path.GetFileName(profileDir));
            foreach (var (id, s) in settings)
            {
                if (Extension(browser, profile, profileDir, id, s) is { } extension) list.Add(extension);
            }
        }
        return list;
    }

    internal static BrowserExtension? Extension(string browser, string profile, string profileDir, string id, JsonElement s)
    {
        var location = s.TryGetProperty("location", out var l) && l.TryGetInt32(out var value) ? value : -1;
        if (location is Component or ExternalComponent or < 0) return null;
        if (!s.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path) return null;
        var folder = Path.IsPathRooted(path) ? path : Path.Combine(profileDir, "Extensions", path);
        JsonElement? manifest = s.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.Object ? m : ReadJson(Path.Combine(folder, "manifest.json"));
        var rawName = manifest is { } mf && mf.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var name = Localize(rawName, folder, manifest);
        if (name.Length == 0) name = id;
        var version = manifest is { } mv && mv.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

        bool? enabled = null;
        if (s.TryGetProperty("disable_reasons", out var reasons))
            enabled = reasons.ValueKind switch
            {
                JsonValueKind.Array => reasons.GetArrayLength() == 0,
                JsonValueKind.Number => reasons.GetInt64() == 0,
                _ => null,
            };
        else if (s.TryGetProperty("state", out var state) && state.TryGetInt32(out var stateValue))
            enabled = stateValue == 1;

        var updateUrl = manifest is { } mu && mu.TryGetProperty("update_url", out var u) ? u.GetString() ?? "" : "";
        // Another program may add a store extension (location 6, e.g. Google Docs Offline); it still comes from the store.
        var added = location == Internal ? "" : "（由其他程式加入）";
        var (source, fromStore) = location switch
        {
            PolicyDownload or Policy => ("公司原則安裝", true),
            Unpacked => ("開發人員模式載入（非商店）", false),
            _ when updateUrl.Contains("edge.microsoft.com", StringComparison.OrdinalIgnoreCase) => ($"Edge 附加元件商店{added}", true),
            _ when updateUrl.Contains("google.com", StringComparison.OrdinalIgnoreCase) ||
                   (s.TryGetProperty("from_webstore", out var w) && w.ValueKind == JsonValueKind.True) => ($"Chrome 線上應用程式商店{added}", true),
            Internal => ("非商店來源", false),
            _ => ("其他程式安裝（非商店）", false),
        };
        return new BrowserExtension(browser, profile, id, name, version, enabled, source, fromStore);
    }

    /// <summary>"__MSG_appName__" names come from the extension's _locales, preferring Traditional Chinese.</summary>
    private static string Localize(string name, string folder, JsonElement? manifest)
    {
        if (!name.StartsWith("__MSG_", StringComparison.Ordinal) || !name.EndsWith("__", StringComparison.Ordinal)) return name;
        var key = name[6..^2];
        var fallback = manifest is { } m && m.TryGetProperty("default_locale", out var d) ? d.GetString() : null;
        foreach (var locale in new[] { "zh_TW", "zh_HK", fallback, "en", "en_US" }.Where(x => x is not null).Distinct())
        {
            if (ReadJson(Path.Combine(folder, "_locales", locale!, "messages.json")) is not { } messages) continue;
            foreach (var entry in messages.EnumerateObject())
            {
                if (entry.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Value.TryGetProperty("message", out var text) && text.GetString() is { Length: > 0 } result)
                    return result;
            }
        }
        return "";
    }

    public static IReadOnlyList<BrowserExtension> ReadFirefox(string profilesRoot)
    {
        var list = new List<BrowserExtension>();
        if (!Directory.Exists(profilesRoot)) return list;
        foreach (var profileDir in SafeDirectories(profilesRoot))
        {
            if (ReadJson(Path.Combine(profileDir, "extensions.json")) is not { } json || !json.TryGetProperty("addons", out var addons)) continue;
            // Profile folders are "random.default-release"; the part after the dot is the profile's name.
            var folderName = Path.GetFileName(profileDir);
            var profile = folderName.Contains('.') ? folderName[(folderName.IndexOf('.') + 1)..] : folderName;
            foreach (var addon in addons.EnumerateArray())
            {
                if (Text(addon, "type") != "extension" || Text(addon, "location") != "app-profile") continue;
                var name = addon.TryGetProperty("defaultLocale", out var locale) ? Text(locale, "name") : "";
                var signed = addon.TryGetProperty("signedState", out var s) && s.TryGetInt32(out var state) && state >= 2;
                bool? active = addon.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;
                list.Add(new BrowserExtension(FirefoxName, profile, Text(addon, "id"), name.Length > 0 ? name : Text(addon, "id"), Text(addon, "version"),
                    active, signed ? "Firefox 附加元件（Mozilla 已簽署）" : "未經 Mozilla 簽署", signed));
            }
        }
        return list;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool IsChromiumProfile(string dir)
    {
        var name = Path.GetFileName(dir);
        return name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal);
    }

    private static string ProfileLabel(string folder) => folder == "Default" ? "預設" : folder.Replace("Profile ", "設定檔 ", StringComparison.Ordinal);

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static JsonElement? ReadJson(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            // The browser may be writing the file; share it fully and give up quietly on a partial read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
