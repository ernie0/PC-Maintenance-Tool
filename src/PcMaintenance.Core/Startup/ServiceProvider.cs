using System.ComponentModel;
using System.Security;
using System.ServiceProcess;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

/// <summary>
/// Win32 services. The start type is changed here (effective next boot, journaled); starting and stopping on
/// request is <see cref="ServiceRunner"/>'s job. Microsoft services are read-only for both.
/// </summary>
public sealed class ServiceProvider(AppSettings settings, SystemFolders? folders = null) : IStartupProvider
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const int Win32ServiceTypes = 0x30;
    private const int UserServiceFlag = 0x40;

    private readonly SystemFolders _folders = folders ?? SystemFolders.Current;

    public StartupSource Source => StartupSource.Service;
    public ChangeKind Kind => ChangeKind.ServiceStartType;

    public IReadOnlyList<StartupItem> Enumerate()
    {
        var status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in ServiceController.GetServices())
        {
            using (controller)
            {
                try
                {
                    status[controller.ServiceName] = DescribeStatus(controller.Status);
                }
                catch (InvalidOperationException)
                {
                    status[controller.ServiceName] = "未知";
                }
            }
        }

        var items = new List<StartupItem>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = hklm.OpenSubKey(ServicesKey);
        if (services is null) return items;
        foreach (var name in services.GetSubKeyNames())
        {
            if (status.TryGetValue(name, out var state) && TryRead(services, name, state) is { } item) items.Add(item);
        }
        return items;
    }

    /// <summary>Some service keys deny standard users; those are skipped instead of failing the whole list.</summary>
    private StartupItem? TryRead(RegistryKey services, string name, string state)
    {
        try
        {
            using var key = services.OpenSubKey(name);
            if (key is null) return null;
            var type = key.GetValue("Type") is int t ? t : 0;
            if ((type & Win32ServiceTypes) == 0 || (type & UserServiceFlag) != 0) return null;

            var startType = ReadStartType(key);
            var imagePath = key.GetValue("ImagePath")?.ToString() ?? "";
            string? serviceDll;
            using (var parameters = key.OpenSubKey("Parameters"))
                serviceDll = parameters?.GetValue("ServiceDll")?.ToString();
            var binary = string.IsNullOrEmpty(serviceDll)
                ? CommandLine.ExtractExecutable(imagePath, _folders)
                : Environment.ExpandEnvironmentVariables(serviceDll);
            var publisher = FilePublisher.Get(binary);
            var isMicrosoft = FilePublisher.IsMicrosoft(publisher);
            return new StartupItem
            {
                Key = "service|" + name,
                Source = StartupSource.Service,
                Name = IndirectString.Resolve(key.GetValue("DisplayName")?.ToString()) ?? name,
                Command = imagePath,
                ExecutablePath = binary,
                Publisher = publisher,
                Location = "服務 " + name,
                Enabled = startType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed,
                RequiresAdmin = true,
                IsMicrosoft = isMicrosoft,
                CanChange = settings.AllowServiceChanges && !isMicrosoft &&
                            startType is not (ServiceStartType.Boot or ServiceStartType.System),
                StartType = startType,
                ServiceStatus = state,
                Note = IndirectString.Resolve(key.GetValue("Description")?.ToString()) ?? "",
                Data = new Dictionary<string, string> { ["service"] = name },
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Turning a service "off" sets it to manual, so other programs can still start it when needed.</summary>
    public Dictionary<string, string> SetEnabled(StartupItem item, bool enabled) =>
        SetStartType(item, enabled ? ServiceStartType.Automatic : ServiceStartType.Manual);

    public Dictionary<string, string> SetStartType(StartupItem item, ServiceStartType type)
    {
        if (!item.CanChange) throw new InvalidOperationException("此服務不允許變更");
        var name = item.Data["service"];
        var previous = ReadStartType(name);
        ServiceConfig.Apply(name, type);
        return new Dictionary<string, string> { ["service"] = name, ["previous"] = previous.ToString() };
    }

    public void Restore(IReadOnlyDictionary<string, string> data) =>
        ServiceConfig.Apply(data["service"], Enum.Parse<ServiceStartType>(data["previous"]));

    internal static ServiceStartType ToStartType(int start, bool delayed) => start switch
    {
        0 => ServiceStartType.Boot,
        1 => ServiceStartType.System,
        2 => delayed ? ServiceStartType.AutomaticDelayed : ServiceStartType.Automatic,
        4 => ServiceStartType.Disabled,
        _ => ServiceStartType.Manual,
    };

    private static ServiceStartType ReadStartType(RegistryKey key) =>
        ToStartType(key.GetValue("Start") is int s ? s : 3, key.GetValue("DelayedAutostart") is 1);

    private static ServiceStartType ReadStartType(string serviceName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"{ServicesKey}\{serviceName}") ?? throw new InvalidOperationException($"找不到服務 {serviceName}");
        return ReadStartType(key);
    }

    private static string DescribeStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "執行中",
        ServiceControllerStatus.Stopped => "已停止",
        ServiceControllerStatus.Paused => "已暫停",
        ServiceControllerStatus.StartPending => "啟動中",
        ServiceControllerStatus.StopPending => "停止中",
        _ => "處理中",
    };
}

public enum ServiceAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// Starts or stops a service now, as Task Manager's service list does. This lasts until the next restart, so it
/// is logged but not journaled; the start type decides what happens after that.
/// </summary>
public static class ServiceRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void Run(StartupItem item, ServiceAction action, FileLog? log = null)
    {
        if (item.Source != StartupSource.Service || !item.CanChange) throw new InvalidOperationException("此服務不允許變更");
        using var controller = new ServiceController(item.Data["service"]);
        try
        {
            if (action is ServiceAction.Stop or ServiceAction.Restart && controller.Status != ServiceControllerStatus.Stopped)
            {
                if (!controller.CanStop) throw new InvalidOperationException("這個服務目前不接受停止");
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
            }
            if (action is ServiceAction.Start or ServiceAction.Restart)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
            }
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new InvalidOperationException("服務沒有在 30 秒內完成，請稍後按「重新整理」查看狀態");
        }
        log?.Info($"服務{ActionName(action)}：{item.Name}（{item.Data["service"]}）");
    }

    public static string ActionName(ServiceAction action) => action switch
    {
        ServiceAction.Start => "啟動",
        ServiceAction.Stop => "停止",
        _ => "重新啟動",
    };
}

internal static class ServiceConfig
{
    public static void Apply(string serviceName, ServiceStartType type)
    {
        var (start, delayed) = type switch
        {
            ServiceStartType.Automatic => (2u, false),
            ServiceStartType.AutomaticDelayed => (2u, true),
            ServiceStartType.Manual => (3u, false),
            ServiceStartType.Disabled => (4u, false),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "不支援的啟動類型"),
        };

        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var service = NativeMethods.OpenServiceW(scm, serviceName, NativeMethods.SERVICE_CHANGE_CONFIG | NativeMethods.SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!NativeMethods.ChangeServiceConfigW(service, NativeMethods.SERVICE_NO_CHANGE, start, NativeMethods.SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception();
                if (start == 2)
                {
                    var info = new NativeMethods.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
                    if (!NativeMethods.ChangeServiceConfig2W(service, NativeMethods.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        throw new Win32Exception();
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }
}
