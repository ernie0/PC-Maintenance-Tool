using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

public sealed record StartupFolderLocation(string FolderPath, RegistryHive Hive, string DisplayName, bool RequiresAdmin)
{
    public RegistryView ApprovedView => Hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default;
    public string ApprovedKeyPath => StartupApproved.BaseKey + "StartupFolder";
}

public sealed class StartupFolderProvider(IReadOnlyList<StartupFolderLocation>? locations = null) : IStartupProvider
{
    public static IReadOnlyList<StartupFolderLocation> DefaultLocations { get; } =
    [
        new(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "啟動資料夾（目前使用者）", false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "啟動資料夾（所有使用者）", true),
    ];

    private readonly IReadOnlyList<StartupFolderLocation> _locations = locations ?? DefaultLocations;

    public StartupSource Source => StartupSource.StartupFolder;
    public ChangeKind Kind => ChangeKind.StartupFolder;

    public IReadOnlyList<StartupItem> Enumerate()
    {
        var items = new List<StartupItem>();
        for (var i = 0; i < _locations.Count; i++)
        {
            var location = _locations[i];
            if (!Directory.Exists(location.FolderPath)) continue;
            using var approvedBase = RegistryKey.OpenBaseKey(location.Hive, location.ApprovedView);
            using var approved = approvedBase.OpenSubKey(location.ApprovedKeyPath);
            foreach (var file in Directory.GetFiles(location.FolderPath))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var (target, arguments) = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShortcutReader.Read(file)
                    : (file, "");
                var exe = string.IsNullOrEmpty(target) ? file : target;
                var publisher = FilePublisher.Get(exe);
                items.Add(new StartupItem
                {
                    Key = $"folder|{i}|{fileName}",
                    Source = StartupSource.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = arguments.Length > 0 ? $"\"{exe}\" {arguments}" : exe,
                    ExecutablePath = exe,
                    Publisher = publisher,
                    Location = location.DisplayName,
                    Enabled = StartupApproved.IsEnabled(approved?.GetValue(fileName) as byte[]),
                    RequiresAdmin = location.RequiresAdmin,
                    IsMicrosoft = FilePublisher.IsMicrosoft(publisher),
                    CanChange = true,
                    Data = new Dictionary<string, string>
                    {
                        ["location"] = i.ToString(CultureInfo.InvariantCulture),
                        ["value"] = fileName,
                    },
                });
            }
        }
        return items;
    }

    public Dictionary<string, string> SetEnabled(StartupItem item, bool enabled)
    {
        var location = _locations[int.Parse(item.Data["location"], CultureInfo.InvariantCulture)];
        return StartupApproved.Apply(location.Hive, location.ApprovedView, location.ApprovedKeyPath, item.Data["value"], enabled);
    }

    public void Restore(IReadOnlyDictionary<string, string> data) => StartupApproved.Restore(data);
}

internal static class ShortcutReader
{
    /// <summary>Reads a .lnk target for display; returns empty strings when the shortcut cannot be read.</summary>
    public static (string Target, string Arguments) Read(string lnkPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return ("", "");
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            dynamic shortcut = ((dynamic)shell!).CreateShortcut(lnkPath);
            string target = shortcut.TargetPath ?? "";
            string arguments = shortcut.Arguments ?? "";
            return (target, arguments);
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException)
        {
            return ("", "");
        }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
