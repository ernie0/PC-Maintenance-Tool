using System.IO;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Junk;
using PcMaintenance.Core.Maintenance;
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
                CliAction.ExportSoftware => Export(command.Path!, InventoryExport.SoftwareKind, log,
                    target => InventoryExport.WriteSoftware(target, new InstalledAppProvider().GetApps(), Environment.MachineName)),
                CliAction.ExportStartup => Export(command.Path!, InventoryExport.StartupKind, log,
                    target => InventoryExport.WriteStartup(target, StartupManager.CreateDefault(services.Settings, services.Journal, log).Load().Items, Environment.MachineName)),
                CliAction.ExportSystem => Export(command.Path!, InventoryExport.SystemKind, log, target =>
                {
                    InventoryExport.WriteSystem(target, SystemInfoReader.Read(), DateTime.Now);
                    return 1;
                }),
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

    private static int Export(string path, string kind, FileLog log, Func<string, int> write)
    {
        var target = InventoryExport.ResolveTarget(path, kind, Environment.MachineName, DateTime.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var count = write(target);
        log.Info($"匯出{kind}：{count} 筆，{target}");
        return Ok;
    }
}
