using System.Globalization;
using System.Security;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Tweaks;

public enum TweakGroup
{
    Performance,
    Explorer,
    Privacy,
}

/// <summary>
/// One registry value a tweak sets. Turning the tweak off writes <see cref="OffValue"/>, or removes the value when it is
/// null so Windows falls back to its default; <see cref="OffDeletesKey"/> removes a whole key instead.
/// <see cref="OnWhenMissing"/> is how a missing value reads: Windows' default for that setting.
/// </summary>
public sealed record RegistryChange(
    RegistryHive Hive,
    string KeyPath,
    string ValueName,
    object OnValue,
    object? OffValue = null,
    bool OnWhenMissing = false,
    string? OffDeletesKey = null)
{
    public RegistryValueKind Kind => OnValue is string ? RegistryValueKind.String : RegistryValueKind.DWord;
    public bool RequiresAdmin => TweakEngine.NeedsAdmin(Hive, KeyPath);
}

public sealed record Tweak
{
    public required string Id { get; init; }
    public required TweakGroup Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<RegistryChange> Changes { get; init; }
    /// <summary>When a change shows, in words for the user.</summary>
    public string TakesEffect { get; init; } = "立即生效";
    public bool NeedsExplorerRestart { get; init; }
    /// <summary>Lowest Windows build the setting exists on, e.g. 22000 for Windows 11 only.</summary>
    public int MinBuild { get; init; }
    public bool RequiresAdmin => Changes.Any(c => c.RequiresAdmin);
}

/// <summary>
/// Reads and switches tweaks. Every switch records the values it replaced, so the change journal can put back exactly
/// what was there before, including "the value did not exist" and keys that were created along the way.
/// </summary>
public sealed class TweakEngine(ChangeJournal journal, FileLog? log = null)
{
    public static bool IsAvailable(Tweak tweak) => Environment.OSVersion.Version.Build >= tweak.MinBuild;

    public static bool IsOn(Tweak tweak) => tweak.Changes.All(IsOn);

    /// <summary>Machine settings, and policy keys: users may read Software\Policies but not write it.</summary>
    public static bool NeedsAdmin(RegistryHive hive, string keyPath) =>
        hive == RegistryHive.LocalMachine || keyPath.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase);

    public ChangeRecord Apply(Tweak tweak, bool on)
    {
        var before = Serialize(tweak.Changes.Select(Capture).ToList());
        try
        {
            foreach (var change in tweak.Changes) Write(change, on);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // A later value failed (usually permissions): put back the ones already written instead of leaving half a change.
            PutBack(before);
            throw;
        }
        var record = new ChangeRecord
        {
            Kind = ChangeKind.SettingChanged,
            Target = tweak.Name,
            Description = on ? "開啟設定" : "關閉設定",
            RestoreData = before,
        };
        journal.Add(record);
        log?.Info($"{record.Description}：{tweak.Name}（{string.Join("、", tweak.Changes.Select(c => $@"{Short(c.Hive)}\{c.KeyPath}\{c.ValueName}"))}）");
        return record;
    }

    public static bool CanRestore(ChangeRecord record) => record.Kind == ChangeKind.SettingChanged && record.RestoreData.ContainsKey("count");

    public static bool RestoreRequiresAdmin(ChangeRecord record) =>
        Entries(record.RestoreData).Any(i => NeedsAdmin(Enum.Parse<RegistryHive>(record.RestoreData[$"{i}.hive"]), record.RestoreData[$"{i}.key"]));

    public void Restore(ChangeRecord record)
    {
        PutBack(record.RestoreData);
        journal.MarkRestored(record.Id);
        log?.Info($"已還原：{record.Description}－{record.Target}");
    }

    private static void PutBack(IReadOnlyDictionary<string, string> data)
    {
        foreach (var i in Entries(data))
        {
            var keyPath = data[$"{i}.key"];
            var valueName = data[$"{i}.name"];
            using var root = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(data[$"{i}.hive"]), RegistryView.Registry64);
            if (data[$"{i}.exists"] == "1")
            {
                using var key = root.CreateSubKey(keyPath, writable: true);
                var kind = Enum.Parse<RegistryValueKind>(data[$"{i}.kind"]);
                key.SetValue(valueName, Decode(data[$"{i}.value"], kind), kind);
                continue;
            }
            using (var key = root.OpenSubKey(keyPath, writable: true)) key?.DeleteValue(valueName, throwOnMissingValue: false);
            // Keys the tweak created go too, so the registry ends up exactly as it was.
            if (data[$"{i}.created"] is { Length: > 0 } created) root.DeleteSubKeyTree(created, throwOnMissingSubKey: false);
        }
    }

    private static IEnumerable<int> Entries(IReadOnlyDictionary<string, string> data) =>
        Enumerable.Range(0, int.Parse(data["count"], CultureInfo.InvariantCulture));

    private static bool IsOn(RegistryChange change)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(change.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(change.KeyPath);
            var value = key?.GetValue(change.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? change.OnWhenMissing : Equals(value, change.OnValue);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void Write(RegistryChange change, bool on)
    {
        using var root = RegistryKey.OpenBaseKey(change.Hive, RegistryView.Registry64);
        if (!on && change.OffDeletesKey is { } doomed)
        {
            root.DeleteSubKeyTree(doomed, throwOnMissingSubKey: false);
            return;
        }
        if (!on && change.OffValue is null)
        {
            using var existing = root.OpenSubKey(change.KeyPath, writable: true);
            existing?.DeleteValue(change.ValueName, throwOnMissingValue: false);
            return;
        }
        using var key = root.CreateSubKey(change.KeyPath, writable: true);
        key.SetValue(change.ValueName, on ? change.OnValue : change.OffValue!, change.Kind);
    }

    private sealed record Snapshot(RegistryChange Change, bool Exists, RegistryValueKind Kind, string Value, string? CreatedKey);

    private static Snapshot Capture(RegistryChange change)
    {
        using var root = RegistryKey.OpenBaseKey(change.Hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(change.KeyPath);
        var value = key?.GetValue(change.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = value is null ? change.Kind : key!.GetValueKind(change.ValueName);
        return new Snapshot(change, value is not null, kind, value is null ? "" : Encode(value, kind), FirstMissingKey(root, change.KeyPath));
    }

    /// <summary>The outermost key on the path that does not exist yet, i.e. the one writing the value would create.</summary>
    private static string? FirstMissingKey(RegistryKey root, string keyPath)
    {
        var parts = keyPath.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts[..i]);
            using var key = root.OpenSubKey(path);
            if (key is null) return path;
        }
        return null;
    }

    private static Dictionary<string, string> Serialize(IReadOnlyList<Snapshot> before)
    {
        var data = new Dictionary<string, string> { ["count"] = before.Count.ToString(CultureInfo.InvariantCulture) };
        for (var i = 0; i < before.Count; i++)
        {
            var s = before[i];
            data[$"{i}.hive"] = s.Change.Hive.ToString();
            data[$"{i}.key"] = s.Change.KeyPath;
            data[$"{i}.name"] = s.Change.ValueName;
            data[$"{i}.exists"] = s.Exists ? "1" : "0";
            data[$"{i}.kind"] = s.Kind.ToString();
            data[$"{i}.value"] = s.Value;
            data[$"{i}.created"] = s.CreatedKey ?? "";
        }
        return data;
    }

    private static string Encode(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ((int)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        RegistryValueKind.MultiString => string.Join('\0', (string[])value),
        _ => value.ToString() ?? "",
    };

    private static object Decode(string text, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(text, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(text, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(text),
        RegistryValueKind.MultiString => text.Split('\0'),
        _ => text,
    };

    private static string Short(RegistryHive hive) => hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
}
