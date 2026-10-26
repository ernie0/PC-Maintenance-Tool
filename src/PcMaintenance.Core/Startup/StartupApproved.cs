using Microsoft.Win32;

namespace PcMaintenance.Core.Startup;

/// <summary>
/// The Explorer\StartupApproved values that Task Manager uses to switch startup entries on and off
/// without deleting them. Each value is 12 bytes: byte 0 is 0x02 (enabled) or 0x03 (disabled),
/// bytes 4-11 the FILETIME it was disabled. An odd first byte means disabled; no value means enabled.
/// </summary>
public static class StartupApproved
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    public static byte[] Encode(bool enabled, DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.TryWriteBytes(bytes.AsSpan(4), utcNow.ToFileTimeUtc());
        return bytes;
    }

    internal static Dictionary<string, string> Apply(RegistryHive hive, RegistryView view, string keyPath, string valueName, bool enabled)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.CreateSubKey(keyPath, writable: true);
        var previous = key.GetValue(valueName) as byte[];
        key.SetValue(valueName, Encode(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
        return new Dictionary<string, string>
        {
            ["hive"] = hive.ToString(),
            ["view"] = view.ToString(),
            ["key"] = keyPath,
            ["value"] = valueName,
            ["hadValue"] = (previous is not null).ToString(),
            ["previous"] = previous is null ? "" : Convert.ToHexString(previous),
        };
    }

    internal static void Restore(IReadOnlyDictionary<string, string> data)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(data["hive"]), Enum.Parse<RegistryView>(data["view"]));
        using var key = baseKey.CreateSubKey(data["key"], writable: true);
        if (bool.Parse(data["hadValue"]))
            key.SetValue(data["value"], Convert.FromHexString(data["previous"]), RegistryValueKind.Binary);
        else
            key.DeleteValue(data["value"], throwOnMissingValue: false);
    }
}
