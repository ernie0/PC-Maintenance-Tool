using System.Globalization;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

public sealed record RunKeyLocation(
    RegistryHive Hive,
    RegistryView RunView,
    string RunKeyPath,
    RegistryView ApprovedView,
    string ApprovedKeyPath,
    string DisplayName,
    bool RequiresAdmin);

public sealed class RunKeyProvider(IReadOnlyList<RunKeyLocation>? locations = null) : IStartupProvider
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // 32-bit HKLM Run entries live under WOW6432Node, but their approval values sit in the 64-bit view's Run32 key.
    public static IReadOnlyList<RunKeyLocation> DefaultLocations { get; } =
    [
        new(RegistryHive.CurrentUser, RegistryView.Default, RunPath, RegistryView.Default, StartupApproved.BaseKey + "Run", "登錄檔（目前使用者）", false),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, RegistryView.Registry64, StartupApproved.BaseKey + "Run", "登錄檔（所有使用者）", true),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, RunPath, RegistryView.Registry64, StartupApproved.BaseKey + "Run32", "登錄檔（所有使用者，32 位元）", true),
    ];

    private readonly IReadOnlyList<RunKeyLocation> _locations = locations ?? DefaultLocations;

    public StartupSource Source => StartupSource.RegistryRun;
    public ChangeKind Kind => ChangeKind.StartupRegistry;

    public IReadOnlyList<StartupItem> Enumerate()
    {
        var items = new List<StartupItem>();
        for (var i = 0; i < _locations.Count; i++)
        {
            var location = _locations[i];
            using var runBase = RegistryKey.OpenBaseKey(location.Hive, location.RunView);
            using var run = runBase.OpenSubKey(location.RunKeyPath);
            if (run is null) continue;
            using var approvedBase = RegistryKey.OpenBaseKey(location.Hive, location.ApprovedView);
            using var approved = approvedBase.OpenSubKey(location.ApprovedKeyPath);
            foreach (var valueName in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(valueName)) continue;
                var command = run.GetValue(valueName)?.ToString() ?? "";
                var exe = CommandLine.ExtractExecutable(command);
                var publisher = FilePublisher.Get(exe);
                items.Add(new StartupItem
                {
                    Key = $"run|{i}|{valueName}",
                    Source = StartupSource.RegistryRun,
                    Name = valueName,
                    Command = command,
                    ExecutablePath = exe,
                    Publisher = publisher,
                    Location = location.DisplayName,
                    Enabled = StartupApproved.IsEnabled(approved?.GetValue(valueName) as byte[]),
                    RequiresAdmin = location.RequiresAdmin,
                    IsMicrosoft = FilePublisher.IsMicrosoft(publisher),
                    CanChange = true,
                    Data = new Dictionary<string, string>
                    {
                        ["location"] = i.ToString(CultureInfo.InvariantCulture),
                        ["value"] = valueName,
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
