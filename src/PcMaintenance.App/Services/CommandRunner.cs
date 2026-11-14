using System.IO;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Junk;
using PcMaintenance.Core.Maintenance;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.App.Services;

/// <summary>
/// Runs a command-line action without a window. Results and errors go to the daily log only, because an
/// unattended run (logon entry, scheduled task) has nobody to answer a dialog.
/// </summary>
public static class CommandRunner
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int InvalidArguments = 2;
    public const int RestorePointSkipped = 3;
    public const int RestorePointProtectionOff = 4;

    public static int Run(CliCommand command, AppServices services)
    {
        var log = services.Log;
        if (services.SettingsWarning is { } warning) log.Warn(warning);
        if (command.Error is { } error)
        {
            log.Warn($"命令列參數錯誤：{error}{Environment.NewLine}{CliCommand.Usage}");
            return InvalidArguments;
        }
        try
        {
            return command.Action switch
            {
                CliAction.Clean => Clean(services, command.DryRun, onlyIfDue: false),
                CliAction.AutoClean => Clean(services, command.DryRun, onlyIfDue: true),
                CliAction.AutoExport => AutoExportToFolder(services),
                CliAction.ExportSoftware => Export(command.Path!, InventoryExport.SoftwareKind, log,
                    target => InventoryExport.WriteSoftware(target, new InstalledAppProvider().GetApps(), Environment.MachineName)),
                CliAction.ExportStartup => Export(command.Path!, InventoryExport.StartupKind, log,
                    target =>
                    {
                        var risky = StartupRisk.RiskyFolders();
                        return InventoryExport.WriteStartup(target, StartupManager.CreateDefault(services.Settings, services.Journal, log).Load().Items, Environment.MachineName,
                            item => item.ExecutablePath is { Length: > 0 } path ? StartupRisk.Describe(item, FileSignatures.Check(path), risky) : null);
                    }),
                CliAction.ExportSystem => Export(command.Path!, InventoryExport.SystemKind, log, target =>
                {
                    var info = SystemInfoReader.Read();
                    InventoryExport.WriteSystem(target, info, ComputerHealth.Read(services.IsElevated, info), DateTime.Now);
                    return 1;
                }),
                CliAction.ExportExtensions => Export(command.Path!, InventoryExport.ExtensionsKind, log,
                    target => InventoryExport.WriteExtensions(target, BrowserExtensions.Read(), Environment.MachineName)),
                CliAction.ExportConnections => Export(command.Path!, InventoryExport.ConnectionsKind, log, target =>
                {
                    var suspicious = SuspiciousAddressList.Load(services.Settings);
                    if (suspicious.Error is { } listError) log.Warn(listError);
                    return InventoryExport.WriteConnections(target, ConnectionInventory.ReadCurrent(suspicious.List), Environment.MachineName);
                }),
                CliAction.ExportReport => Export(command.Path!, HealthReport.Kind, log, target =>
                {
                    HealthReport.Write(target, HealthReportData.Collect(services.IsElevated));
                    return 1;
                }, ".html"),
                CliAction.RestorePoint => CreateRestorePoint(services),
                _ => InvalidArguments,
            };
        }
        catch (Exception ex)
        {
            // Top of an unattended run: anything unexpected is logged instead of raising a crash dialog.
            log.Error($"命令列動作失敗（{command.Action}）", ex);
            return Failed;
        }
    }

    private static int Clean(AppServices services, bool dryRun, bool onlyIfDue)
    {
        var now = DateTimeOffset.Now;
        var interval = services.Settings.AutoCleanIntervalDays;
        if (onlyIfDue && AutoCleanSchedule.Load(AppPaths.AutoCleanStateFile) is { } last && !AutoCleanSchedule.IsDue(last, now, interval))
        {
            services.Log.Info($"自動清理：上次在 {last.LastRun.LocalDateTime:yyyy/MM/dd HH:mm}，未滿 {interval} 天，這次略過");
            return Ok;
        }
        BackgroundMode.Enter();
        var categories = JunkCatalog.Create(services.Settings, services.Folders);
        var report = new AutoCleaner(services.Settings, services.Log).Run(categories, services.IsElevated, dryRun);
        if (!dryRun) AutoCleanSchedule.Save(AppPaths.AutoCleanStateFile, new AutoCleanState(now, report.Bytes, report.Files));
        return Ok;
    }

    /// <summary>The logon export to the company's inventory folder; an unreachable folder is tried again at the next logon.</summary>
    private static int AutoExportToFolder(AppServices services)
    {
        var log = services.Log;
        var folder = services.Settings.InventoryFolder.Trim();
        if (folder.Length == 0)
        {
            log.Info("自動匯出：公司設定檔沒有指定 inventoryFolder，這次略過");
            return Ok;
        }
        var now = DateTimeOffset.Now;
        var interval = services.Settings.InventoryIntervalDays;
        if (AutoExport.Load(AppPaths.AutoExportStateFile) is { } last && !AutoExport.IsDue(last, now, interval, folder))
        {
            log.Info($"自動匯出：上次在 {last.LastRun.LocalDateTime:yyyy/MM/dd HH:mm}，未滿 {interval} 天，這次略過");
            return Ok;
        }
        BackgroundMode.Enter();
        if (!AutoExport.WaitForFolder(folder, TimeSpan.FromMinutes(2)))
        {
            log.Warn($"自動匯出：連不到 {folder}，下次登入再試");
            return Failed;
        }
        var files = AutoExport.Write(folder, services.IsElevated, now.LocalDateTime);
        AutoExport.Save(AppPaths.AutoExportStateFile, new AutoExportState(now, folder));
        log.Info($"自動匯出：已寫入 {string.Join("、", files)}");
        return Ok;
    }

    /// <summary>Started elevated by the window (see <see cref="RestorePointLauncher"/>), which reads the result from the exit code.</summary>
    private static int CreateRestorePoint(AppServices services)
    {
        if (!services.IsElevated)
        {
            services.Log.Warn("建立還原點需要系統管理員權限");
            return Failed;
        }
        var result = RestorePoint.Create();
        services.Log.Info($"建立還原點：{result.Message}");
        return result.Outcome switch
        {
            RestorePointOutcome.Created => Ok,
            RestorePointOutcome.SkippedRecent => RestorePointSkipped,
            RestorePointOutcome.ProtectionOff => RestorePointProtectionOff,
            _ => Failed,
        };
    }

    private static int Export(string path, string kind, FileLog log, Func<string, int> write, string extension = ".csv")
    {
        var target = InventoryExport.ResolveTarget(path, kind, Environment.MachineName, DateTime.Now, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var count = write(target);
        log.Info($"匯出{kind}：{count} 筆，{target}");
        return Ok;
    }
}
