using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Uninstall;

public enum LeftoverKind
{
    Folder,
    RegistryKey,
}

public sealed record Leftover
{
    public required LeftoverKind Kind { get; init; }
    public required string DisplayPath { get; init; }
    public required string Reason { get; init; }
    public string? FolderPath { get; init; }
    public RegistryHive? Hive { get; init; }
    public RegistryView? View { get; init; }
    public string? KeyPath { get; init; }
    public long? SizeBytes { get; init; }
    public bool DefaultSelected { get; init; }
}

/// <summary>
/// Finds what an uninstaller left behind. Only exact-name matches are proposed, never a system
/// folder or its ancestors, and never a folder another installed program still uses.
/// </summary>
public sealed partial class LeftoverScanner(SystemFolders? folders = null, Func<RegistryHive, RegistryView, string, bool>? keyExists = null)
{
    private readonly SystemFolders _folders = folders ?? SystemFolders.Current;
    private readonly Func<RegistryHive, RegistryView, string, bool> _keyExists = keyExists ?? RegistryNames.KeyExists;

    [GeneratedRegex(@"\s+v?\d+(\.\d+)*(\s*\((x64|x86|64-bit|32-bit)\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    public IReadOnlyList<Leftover> Scan(InstalledApp app, IReadOnlyCollection<InstalledApp> stillInstalled)
    {
        var results = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherLocations = stillInstalled
            .Where(a => !(a.Hive == app.Hive && a.View == app.View && a.KeyPath.Equals(app.KeyPath, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.InstallLocation)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !LeftoverRules.IsProtected(l, _folders))
            .ToList();

        void TryAddFolder(string path, string reason, bool selected)
        {
            if (!LeftoverRules.IsRemovableFolder(path, _folders, otherLocations)) return;
            var normalized = PathSafety.Normalize(path);
            if (!seen.Add(normalized)) return;
            results.Add(new Leftover
            {
                Kind = LeftoverKind.Folder,
                DisplayPath = normalized,
                FolderPath = normalized,
                Reason = reason,
                SizeBytes = LeftoverRules.TryGetFolderSize(normalized),
                DefaultSelected = selected,
            });
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation)) TryAddFolder(app.InstallLocation, "安裝資料夾仍存在", true);

        var names = CandidateNames(app);
        foreach (var name in names)
        {
            TryAddFolder(Path.Combine(_folders.ProgramFiles, name), "Program Files 中的同名資料夾", true);
            TryAddFolder(Path.Combine(_folders.ProgramFilesX86, name), "Program Files (x86) 中的同名資料夾", true);
            TryAddFolder(Path.Combine(_folders.ProgramData, name), "ProgramData 中的同名資料夾（可能含設定）", false);
            TryAddFolder(Path.Combine(_folders.AppData, name), "AppData 中的同名資料夾（可能含使用者資料）", false);
            TryAddFolder(Path.Combine(_folders.LocalAppData, name), "LocalAppData 中的同名資料夾（可能含使用者資料）", false);
        }

        // An MSI entry belongs to Windows Installer; only non-MSI entries whose uninstaller is gone are orphans.
        if (!app.IsWindowsInstaller && _keyExists(app.Hive, app.View, app.KeyPath) && UninstallCommand.IsUninstallerMissing(app))
        {
            results.Add(new Leftover
            {
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = app.RegistryDisplayPath,
                Hive = app.Hive,
                View = app.View,
                KeyPath = app.KeyPath,
                Reason = "解除安裝程式已不存在的登錄項目",
                DefaultSelected = true,
            });
        }

        if (app.Publisher.Length > 0 && app.Publisher.IndexOfAny(['\\', '/']) < 0)
        {
            foreach (var name in names)
            {
                foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
                {
                    var keyPath = $@"Software\{app.Publisher}\{name}";
                    var display = RegistryNames.Display(hive, view, keyPath);
                    if (!_keyExists(hive, view, keyPath) || !seen.Add(display)) continue;
                    results.Add(new Leftover
                    {
                        Kind = LeftoverKind.RegistryKey,
                        DisplayPath = display,
                        Hive = hive,
                        View = view,
                        KeyPath = keyPath,
                        Reason = "程式設定登錄機碼（重新安裝時可能會用到）",
                        DefaultSelected = false,
                    });
                }
            }
        }
        return results;
    }

    internal static IReadOnlyList<string> CandidateNames(InstalledApp app)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            var name = value?.Trim();
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        Add(app.DisplayName);
        Add(VersionSuffix().Replace(app.DisplayName, ""));
        return names;
    }
}

internal static class LeftoverRules
{
    /// <summary>A protected folder itself, or an ancestor of one (removing it would take the protected folder along).</summary>
    public static bool IsProtected(string path, SystemFolders folders)
    {
        foreach (var root in folders.ProtectedRoots())
            if (PathSafety.IsSameOrUnder(path, root)) return true;
        return false;
    }

    public static bool IsRemovableFolder(string path, SystemFolders folders, IReadOnlyList<string> otherInstallLocations)
    {
        string normalized;
        try
        {
            normalized = PathSafety.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!Directory.Exists(normalized) || PathSafety.IsReparsePoint(normalized)) return false;
        if (IsProtected(normalized, folders)) return false;
        foreach (var other in otherInstallLocations)
            if (PathSafety.IsSameOrUnder(normalized, other) || PathSafety.IsSameOrUnder(other, normalized)) return false;
        return true;
    }

    public static long? TryGetFolderSize(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            return new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed record RemovalOutcome(Leftover Item, bool Success, string Message);

public sealed class LeftoverRemover(ChangeJournal journal, string backupDir, FileLog? log = null, SystemFolders? folders = null)
{
    private readonly SystemFolders _folders = folders ?? SystemFolders.Current;

    public IReadOnlyList<RemovalOutcome> Remove(InstalledApp app, IReadOnlyList<Leftover> items)
    {
        var outcomes = new List<RemovalOutcome>();
        foreach (var item in items)
        {
            try
            {
                outcomes.Add(item.Kind == LeftoverKind.Folder ? RemoveFolder(app, item) : RemoveKey(app, item));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
            {
                outcomes.Add(new RemovalOutcome(item, false, UserMessage.For(ex)));
                log?.Error($"移除殘留失敗：{item.DisplayPath}", ex);
            }
        }
        return outcomes;
    }

    public static bool CanRestore(ChangeRecord record) => record.Kind == ChangeKind.RegistryKeyDeleted;

    public void Restore(ChangeRecord record)
    {
        switch (record.Kind)
        {
            case ChangeKind.RegistryKeyDeleted:
                if (!RegistryBackup.Import(record.RestoreData["backup"], Enum.Parse<RegistryView>(record.RestoreData["view"])))
                    throw new IOException("匯入登錄備份失敗");
                journal.MarkRestored(record.Id);
                log?.Info($"已還原登錄機碼：{record.Target}");
                break;
            case ChangeKind.FolderRecycled:
                throw new InvalidOperationException($"資料夾請從資源回收筒還原：{record.Target}");
            default:
                throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "不支援的變更類型");
        }
    }

    private RemovalOutcome RemoveFolder(InstalledApp app, Leftover item)
    {
        var path = item.FolderPath ?? throw new ArgumentException("缺少資料夾路徑", nameof(item));
        // The scan may be stale; re-check right before acting.
        if (!Directory.Exists(path)) return new RemovalOutcome(item, true, "資料夾已不存在");
        if (PathSafety.IsReparsePoint(path) || LeftoverRules.IsProtected(path, _folders))
            return new RemovalOutcome(item, false, "受保護的路徑，未移除");
        if (!RecycleBin.Send(path)) return new RemovalOutcome(item, false, "無法移到資源回收筒（可能有檔案正在使用）");
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.FolderRecycled,
            Target = path,
            Description = $"移除「{app.DisplayName}」殘留資料夾（已移到資源回收筒）",
            RestoreData = new Dictionary<string, string> { ["path"] = path },
        });
        log?.Info($"殘留資料夾移到資源回收筒：{path}");
        return new RemovalOutcome(item, true, "已移到資源回收筒");
    }

    private RemovalOutcome RemoveKey(InstalledApp app, Leftover item)
    {
        if (item.Hive is not { } hive || item.View is not { } view || item.KeyPath is not { } keyPath)
            throw new ArgumentException("缺少登錄機碼資訊", nameof(item));
        Directory.CreateDirectory(backupDir);
        var safeName = string.Concat(app.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var backup = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}-{Guid.NewGuid().ToString("N")[..8]}.reg");
        if (!RegistryBackup.Export(hive, view, keyPath, backup)) return new RemovalOutcome(item, false, "無法備份登錄機碼，未刪除");
        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
            baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.RegistryKeyDeleted,
            Target = item.DisplayPath,
            Description = $"刪除「{app.DisplayName}」殘留登錄機碼（已備份）",
            RestoreData = new Dictionary<string, string> { ["backup"] = backup, ["view"] = view.ToString() },
        });
        log?.Info($"刪除殘留登錄機碼：{item.DisplayPath}，備份 {backup}");
        return new RemovalOutcome(item, true, "已刪除（已備份）");
    }
}

internal static class RegistryBackup
{
    private static string RegExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

    public static bool Export(RegistryHive hive, RegistryView view, string keyPath, string file) =>
        Run(["export", $@"{RegistryNames.ShortHive(hive)}\{keyPath}", file, "/y", ViewSwitch(view)]) && File.Exists(file);

    public static bool Import(string file, RegistryView view) => File.Exists(file) && Run(["import", file, ViewSwitch(view)]);

    private static string ViewSwitch(RegistryView view) => view == RegistryView.Registry32 ? "/reg:32" : "/reg:64";

    private static bool Run(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(RegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return false;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            return false;
        }
        Task.WaitAll(stdout, stderr);
        return process.ExitCode == 0;
    }
}
