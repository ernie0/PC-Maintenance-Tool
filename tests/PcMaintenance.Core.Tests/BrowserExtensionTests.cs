using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class BrowserExtensionTests
{
    [Fact]
    public void Chromium_extensions_are_read_from_each_profile_without_built_in_components()
    {
        using var temp = new TempDir();
        var userData = temp.Dir("User Data");
        temp.Dir("User Data", "System Profile");
        var profile = temp.Dir("User Data", "Profile 2");
        // Store extension whose name is localised, unpacked developer extension, built-in component, and a leftover record.
        var store = temp.Dir("User Data", "Profile 2", "Extensions", "aaaa", "1.2.0_0");
        File.WriteAllText(Path.Combine(store, "manifest.json"),
            """{ "name": "__MSG_appName__", "version": "1.2.0", "default_locale": "en", "update_url": "https://clients2.google.com/service/update2/crx" }""");
        Directory.CreateDirectory(Path.Combine(store, "_locales", "zh_TW"));
        File.WriteAllText(Path.Combine(store, "_locales", "zh_TW", "messages.json"), """{ "APPNAME": { "message": "翻譯小幫手" } }""");
        var unpacked = temp.Dir("dev-ext");
        File.WriteAllText(Path.Combine(unpacked, "manifest.json"), """{ "name": "測試外掛", "version": "0.1" }""");
        File.WriteAllText(Path.Combine(profile, "Secure Preferences"), $$"""
            { "extensions": { "settings": {
                "aaaa": { "location": 1, "path": "aaaa\\1.2.0_0", "disable_reasons": [] },
                "bbbb": { "location": 4, "path": {{System.Text.Json.JsonSerializer.Serialize(unpacked)}}, "state": 0 },
                "cccc": { "location": 5, "path": "C:\\Program Files\\browser\\pdf", "manifest": { "name": "PDF" } },
                "dddd": { "state": 0, "disable_reasons": 8192 },
                "eeee": { "location": 6, "path": "eeee\\1.0", "from_webstore": true, "manifest": { "name": "文件離線", "version": "1.0", "update_url": "https://clients2.google.com/service/update2/crx" } },
                "ffff": { "location": 2, "path": "ffff\\2.0", "manifest": { "name": "工具列", "version": "2.0" } }
            } } }
            """);
        File.WriteAllText(Path.Combine(profile, "Preferences"), "{ not json");

        var list = BrowserExtensions.ReadChromium("Chrome", userData).OrderBy(e => e.Id).ToList();
        Assert.Equal(4, list.Count);
        Assert.Equal(new BrowserExtension("Chrome", "設定檔 2", "eeee", "文件離線", "1.0", null, "Chrome 線上應用程式商店（由其他程式加入）", true), list[2]);
        Assert.Equal(new BrowserExtension("Chrome", "設定檔 2", "ffff", "工具列", "2.0", null, "其他程式安裝（非商店）", false), list[3]);
        Assert.Equal(new BrowserExtension("Chrome", "設定檔 2", "aaaa", "翻譯小幫手", "1.2.0", true, "Chrome 線上應用程式商店", true), list[0]);
        Assert.Equal(new BrowserExtension("Chrome", "設定檔 2", "bbbb", "測試外掛", "0.1", false, "開發人員模式載入（非商店）", false), list[1]);
    }

    [Fact]
    public void Firefox_extensions_come_from_extensions_json()
    {
        using var temp = new TempDir();
        var profile = temp.Dir("Profiles", "x1y2.default-release");
        File.WriteAllText(Path.Combine(profile, "extensions.json"), """
            { "addons": [
              { "id": "ublock@example", "type": "extension", "location": "app-profile", "version": "1.60", "active": true, "signedState": 2, "defaultLocale": { "name": "uBlock Origin" } },
              { "id": "builtin@mozilla.org", "type": "extension", "location": "app-builtin", "version": "1.0", "active": true },
              { "id": "zh-TW@dictionaries", "type": "dictionary", "location": "app-profile", "version": "1.0" },
              { "id": "local@x", "type": "extension", "location": "app-profile", "version": "0.1", "active": false, "signedState": 0, "defaultLocale": { "name": "自製" } }
            ] }
            """);
        var list = BrowserExtensions.ReadFirefox(temp.Path + @"\Profiles");
        Assert.Equal(2, list.Count);
        Assert.Equal(new BrowserExtension("Firefox", "default-release", "ublock@example", "uBlock Origin", "1.60", true, "Firefox 附加元件（Mozilla 已簽署）", true), list[0]);
        Assert.Equal("未經 Mozilla 簽署", list[1].Source);
    }

    [Fact]
    public void This_users_browsers_read_without_errors() =>
        Assert.All(BrowserExtensions.Read(), e => Assert.False(string.IsNullOrEmpty(e.Name)));
}
