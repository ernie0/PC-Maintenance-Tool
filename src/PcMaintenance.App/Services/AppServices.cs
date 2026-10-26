using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using PcMaintenance.App.ViewModels;
using PcMaintenance.App.Views;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.App.Services;

public sealed class AppServices
{
    public required AppSettings Settings { get; init; }
    public string? SettingsSource { get; init; }
    public string? SettingsWarning { get; init; }
    public required ChangeJournal Journal { get; init; }
    public required FileLog Log { get; init; }
    public required SystemFolders Folders { get; init; }
    public required bool IsElevated { get; init; }

    /// <summary>Company policy in ProgramData wins; a settings.json beside the program is the fallback.</summary>
    public static AppServices Create()
    {
        var settings = SettingsLoader.Load(AppPaths.MachineSettingsFile, Path.Combine(AppContext.BaseDirectory, "settings.json"));
        return new AppServices
        {
            Settings = settings.Settings,
            SettingsSource = settings.SourcePath,
            SettingsWarning = settings.Warning,
            Journal = new ChangeJournal(AppPaths.JournalFile),
            Log = new FileLog(AppPaths.LogDir),
            Folders = SystemFolders.Current,
            IsElevated = Elevation.IsElevated(),
        };
    }
}

public static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a new elevated instance; false when the user declines the UAC prompt.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}

public static class Dialogs
{
    private const string AppTitle = "電腦維護工具";

    public static bool Confirm(string message, string title = "請確認") =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public static void Info(string message, string title = AppTitle) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message, string title = "無法完成操作") =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>Asks where to save an inventory CSV; the suggested name carries the computer name and date.</summary>
    public static string? AskCsvPath(string kind)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"匯出{kind}",
            Filter = "CSV 檔案（可用 Excel 開啟）|*.csv",
            DefaultExt = ".csv",
            FileName = InventoryExport.DefaultFileName(kind, Environment.MachineName, DateTime.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    /// <summary>The small window that follows a one-click scan or fix; it closes itself shortly after the run ends.</summary>
    public static void ShowMaintenanceProgress(MaintenanceViewModel viewModel) =>
        MaintenanceProgressWindow.ShowFor(viewModel, Application.Current.MainWindow);

    // A question asked while the progress window is in front belongs to that window.
    private static Window Owner() => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}

public static class Shell
{
    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    /// <summary>Opens the folder holding the item with the item selected (also for a folder, unlike <see cref="ShowInExplorer"/>).</summary>
    public static void Select(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
