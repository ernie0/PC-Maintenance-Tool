using System.Windows;
using System.Windows.Threading;
using PcMaintenance.App.Services;
using PcMaintenance.App.ViewModels;
using PcMaintenance.Core.Common;

namespace PcMaintenance.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = AppServices.Create();
        var command = CliCommand.Parse(e.Args);
        if (command.Action != CliAction.None || command.Error is not null)
        {
            _services.Log.Info($"命令列執行：{string.Join(' ', e.Args)}（系統管理員權限：{(_services.IsElevated ? "是" : "否")}）");
            Shutdown(CommandRunner.Run(command, _services));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        _services.Log.Info($"啟動（系統管理員權限：{(_services.IsElevated ? "是" : "否")}，設定檔：{_services.SettingsSource ?? "預設值"}）");
        var window = new MainWindow { DataContext = new MainViewModel(_services) };
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error("未預期的錯誤", e.Exception);
        Dialogs.Error($"發生未預期的錯誤，操作已中止。\n\n{UserMessage.For(e.Exception)}");
        e.Handled = true;
    }
}
