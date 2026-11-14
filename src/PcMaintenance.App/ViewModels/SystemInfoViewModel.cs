using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.App.ViewModels;

public sealed record InfoSection(string Title, IReadOnlyList<LabeledValue> Rows)
{
    public string Glyph => Title switch
    {
        "Windows" => "",
        "電腦" => "",
        "處理器" => "",
        "記憶體" => "",
        "顯示卡" => "",
        "磁碟" => "",
        "網路介面卡" => "",
        "裝置狀態" => "\uE772",
        "安全性" => "\uE72E",
        "穩定度（最近 30 天）" => "\uE9D9",
        "電池" => "\uE83F",
        "印表機" => "\uE749",
        "Windows 11 升級" => "\uE777",
        "檔案備份" => "\uE8F7",
        _ => "",
    };
}

public sealed partial class SystemInfoViewModel(AppServices services) : PageViewModel
{
    private ComputerInfo? _info;
    private ComputerHealth? _health;
    private IReadOnlyList<InfoSection> _sections = [];

    public override string Title => "電腦資訊";

    public override string Subtitle => "這台電腦的 Windows 版本與硬體配備。可以複製後貼給維修人員，或匯出成 CSV 彙整各台電腦的資料。";

    public ObservableCollection<InfoSection> LeftSections { get; } = [];
    public ObservableCollection<InfoSection> RightSections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CopyCommand), nameof(CopyHelpCommand), nameof(ExportCommand), nameof(ExportReportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    public override void OnNavigatedTo()
    {
        if (_info is null && !IsBusy) RefreshCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "正在讀取電腦資訊…";
        try
        {
            (_info, _health) = await Task.Run(() =>
            {
                var info = SystemInfoReader.Read();
                return (info, ComputerHealth.Read(services.IsElevated, info));
            });
            _sections = Sections(_info, _health, services.IsElevated, DateTime.Now);
            // Each section goes to the shorter column so the two columns end up about the same height.
            LeftSections.Clear();
            RightSections.Clear();
            int left = 0, right = 0;
            foreach (var section in _sections)
            {
                var size = section.Rows.Count + 2;
                if (left <= right)
                {
                    LeftSections.Add(section);
                    left += size;
                }
                else
                {
                    RightSections.Add(section);
                    right += size;
                }
            }
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool NotBusy() => !IsBusy;
    private bool HasInfo() => !IsBusy && _info is not null;

    [RelayCommand(CanExecute = nameof(HasInfo))]
    private void Copy()
    {
        var text = new StringBuilder();
        foreach (var section in _sections)
        {
            text.AppendLine($"【{section.Title}】");
            foreach (var row in section.Rows) text.AppendLine($"{row.Label}：{row.Value}");
            text.AppendLine();
        }
        CopyText(text.ToString().TrimEnd(), "已複製，可以貼到訊息或文件中。");
    }

    /// <summary>The few lines a help desk asks for first, ready to paste into a message or read out on the phone.</summary>
    [RelayCommand(CanExecute = nameof(HasInfo))]
    private void CopyHelp()
    {
        if (_info is null) return;
        var adapter = Core.Network.NetworkDiagnosis.ReadActiveAdapter();
        CopyText(HelpText(_info, adapter, $"{Environment.UserDomainName}\\{Environment.UserName}", DateTime.Now, MainViewModel.AppVersion),
            "已複製求助資訊（電腦名稱、使用者、IP 位址、型號與序號、Windows 版本、開機時間與剩餘空間），可以貼給資訊人員。");
    }

    internal static string HelpText(ComputerInfo info, Core.Network.ActiveAdapter? adapter, string user, DateTime now, string appVersion)
    {
        var uptime = now - info.Os.LastBoot;
        var system = info.Disks.Volumes.FirstOrDefault(v => v.IsSystem) ?? info.Disks.Volumes.FirstOrDefault();
        var ip = adapter?.IPv4 is { } address
            ? $"{address}（{(adapter.IsWireless ? "Wi-Fi" : "有線網路")}）"
            : info.Network.SelectMany(n => n.IPv4).FirstOrDefault() ?? "沒有網路連線";
        var model = $"{info.Machine.Manufacturer} {info.Machine.Model}".Trim();
        return string.Join(Environment.NewLine, new[]
        {
            $"電腦名稱：{info.Machine.ComputerName}",
            $"使用者：{user}",
            $"IP 位址：{ip}",
            $"型號：{(model.Length > 0 ? model : "—")}" + (info.Machine.SerialNumber.Length > 0 ? $"（序號 {info.Machine.SerialNumber}）" : ""),
            $"Windows：{info.Os.Name} {info.Os.DisplayVersion}（組建 {info.Os.Build}）".Replace("  ", " "),
            $"已開機：{(int)uptime.TotalDays} 天 {uptime.Hours} 小時（上次開機 {info.Os.LastBoot:yyyy/MM/dd HH:mm}）",
            system is null ? "" : $"{system.Letter}: 剩 {ByteSize.Format(system.FreeBytes)}／{ByteSize.Format(system.TotalBytes)}（{system.FreePercent:0}%）",
            $"電腦維護工具 {appVersion}",
        }.Where(line => line.Length > 0));
    }

    private void CopyText(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText = done;
        }
        catch (COMException)
        {
            StatusText = "剪貼簿正被其他程式使用，請再試一次。";
        }
    }

    [RelayCommand(CanExecute = nameof(HasInfo))]
    private void Export()
    {
        if (_info is null || _health is null || Dialogs.AskCsvPath(InventoryExport.SystemKind) is not { } path) return;
        try
        {
            InventoryExport.WriteSystem(path, _info, _health, DateTime.Now);
            services.Log.Info($"匯出電腦配備：{path}");
            StatusText = $"已匯出到 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
    }

    /// <summary>Writes the printable health report and opens it in the browser, from where it can be printed or saved as PDF.</summary>
    [RelayCommand(CanExecute = nameof(HasInfo))]
    private async Task ExportReportAsync()
    {
        if (_info is null || _health is null || Dialogs.AskReportPath() is not { } path) return;
        IsBusy = true;
        StatusText = "正在產生電腦健檢報告…";
        try
        {
            var info = _info;
            var health = _health;
            await Task.Run(() =>
            {
                var data = HealthReportData.Collect(services.IsElevated) with { Info = info, Health = health };
                HealthReport.Write(path, data);
            });
            services.Log.Info($"匯出電腦健檢報告：{path}");
            Shell.OpenFile(path);
            StatusText = $"已匯出到 {path}，並用瀏覽器開啟（可以從瀏覽器列印或存成 PDF）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static IReadOnlyList<InfoSection> Sections(ComputerInfo info, ComputerHealth health, bool isElevated, DateTime now)
    {
        static string Or(string value) => value.Length > 0 ? value : "—";
        static string Date(DateTime? date) => date?.ToString("yyyy/MM/dd") ?? "—";
        var os = info.Os;
        var machine = info.Machine;
        var cpu = info.Processor;
        var modules = info.MemoryModules;

        var sections = new List<InfoSection>
        {
            new("Windows",
            [
                new("版本", os.Name),
                new("功能更新", Or(os.DisplayVersion)),
                new("組建", os.Build),
                new("系統類型", $"{os.Architecture}作業系統"),
                new("安裝日期", Date(os.InstallDate)),
                new("上次開機", os.LastBoot.ToString("yyyy/MM/dd HH:mm")),
                new("上次更新", HealthText.LastUpdate(health.Updates, now)),
                .. health.Updates.Failures.Count > 0
                    ? [new LabeledValue("安裝失敗", HealthText.Failures(health.Updates))]
                    : Array.Empty<LabeledValue>(),
            ]),
            new("電腦",
            [
                new("電腦名稱", machine.ComputerName),
                new(machine.IsDomainJoined ? "網域" : "工作群組", Or(machine.DomainOrWorkgroup)),
                new("製造商", Or(machine.Manufacturer)),
                new("型號", Or(machine.Model)),
                new("序號", Or(machine.SerialNumber)),
                new("主機板", Or(machine.BaseBoard)),
                new("BIOS", machine.BiosDate is { } biosDate ? $"{machine.BiosVersion}（{biosDate:yyyy/MM/dd}）" : Or(machine.BiosVersion)),
                new("安全開機", machine.SecureBoot switch { true => "已啟用", false => "未啟用", null => "—" }),
            ]),
            new("處理器",
            [
                new("型號", Or(cpu.Name)),
                new("核心", cpu.Cores is { } cores ? $"{cores} 核心、{cpu.LogicalProcessors} 個邏輯處理器" : $"{cpu.LogicalProcessors} 個邏輯處理器"),
                new("基本速度", cpu.BaseMHz is { } mhz ? $"{mhz / 1000.0:0.00} GHz" : "—"),
                new("快取", string.Join("、", new[] { cpu.L2KB is { } l2 ? $"L2 {l2 / 1024.0:0.#} MB" : "", cpu.L3KB is { } l3 ? $"L3 {l3 / 1024.0:0.#} MB" : "" }.Where(s => s.Length > 0)) is { Length: > 0 } cache ? cache : "—"),
                new("虛擬化", cpu.Virtualization switch { true => "已啟用", false => "已停用", null => "—" }),
            ]),
        };

        var memoryRows = new List<LabeledValue>
        {
            new("已安裝", $"{ByteSize.Format((long)info.Memory.InstalledPhysical)}（Windows 可用 {ByteSize.Format((long)info.Memory.TotalPhysical)}）"),
            new("插槽", string.Join("，", new[]
            {
                modules.SlotCount is { } slots ? $"已使用 {modules.Modules.Count}／{slots}" : $"已使用 {modules.Modules.Count}",
                modules.MaxCapacityBytes is { } max ? $"主機板最多 {ByteSize.Format((long)max)}" : "",
            }.Where(s => s.Length > 0))),
        };
        memoryRows.AddRange(modules.Modules.Select(m => new LabeledValue(m.Slot,
            string.Join(" ", new[] { ByteSize.Format((long)m.CapacityBytes), m.Type, m.SpeedMHz > 0 ? $"{m.SpeedMHz} MHz" : "", m.Manufacturer, m.PartNumber }.Where(s => s.Length > 0)))));
        sections.Add(new("記憶體", memoryRows));

        if (info.Gpus.Count > 0)
            sections.Add(new("顯示卡", info.Gpus.Select((g, i) => new LabeledValue($"GPU {i}", string.Join("・", new[]
            {
                g.Name,
                g.DedicatedBytes > 0 ? $"專用記憶體 {ByteSize.Format((long)g.DedicatedBytes)}" : "",
                g.DriverVersion.Length > 0 ? $"驅動程式 {g.DriverVersion}" + (g.DriverDate is { } d ? $"（{d:yyyy/MM/dd}）" : "") : "",
            }.Where(s => s.Length > 0)))).ToList()));

        if (info.Disks.Disks.Count > 0)
            sections.Add(new("磁碟", info.Disks.Disks.Select(d => new LabeledValue($"磁碟 {d.Number}",
                $"{d.Model}・{(d.MediaType == "未知" ? d.BusType : $"{d.MediaType}（{d.BusType}）")}・{ByteSize.Format(d.SizeBytes)}・健康狀態{d.Health}")).ToList()));

        if (info.Network.Count > 0)
            sections.Add(new("網路介面卡", info.Network.Select(n => new LabeledValue(n.Name, string.Join("・", new[]
            {
                n.Description,
                n.SpeedBitsPerSecond > 0 ? (n.SpeedBitsPerSecond >= 1_000_000_000 ? $"{n.SpeedBitsPerSecond / 1e9:0.#} Gbps" : $"{n.SpeedBitsPerSecond / 1e6:0} Mbps") : "",
                n.IPv4.Count > 0 ? $"IPv4 {string.Join("、", n.IPv4)}" : "",
                n.Mac.Length > 0 ? $"實體位址 {n.Mac}" : "",
            }.Where(s => s.Length > 0)))).ToList()));

        var faulty = health.Devices.Where(d => !d.IsDisabled).ToList();
        var disabled = health.Devices.Where(d => d.IsDisabled).ToList();
        sections.Add(new("裝置狀態",
        [
            .. faulty.Count == 0
                ? [new LabeledValue("狀態", "裝置管理員中沒有發生問題的裝置")]
                : faulty.Select(d => new LabeledValue("有問題", $"{d.Name}：{d.Reason}。{d.NextStep}。")),
            .. disabled.Select(d => new LabeledValue("已停用", d.Name)),
        ]));

        if (health.Windows11 is { IsWindows11: false } upgrade)
        {
            sections.Add(new("Windows 11 升級",
            [
                new LabeledValue("結論", upgrade.Concern ?? upgrade.Summary),
                .. upgrade.Checks.Select(c => new LabeledValue(c.Item, $"{c.Pass switch { true => "✔", false => "✘", null => "？" }} {c.Detail}")),
            ]));
        }

        sections.Add(new("安全性", HealthText.Security(health.Security)));
        sections.Add(new("穩定度（最近 30 天）", HealthText.Stability(health.Stability)));

        if (health.Backup.Read)
        {
            var backup = health.Backup;
            sections.Add(new("檔案備份", !backup.AnyFound
                ? [new LabeledValue("狀態", "沒有偵測到 OneDrive 資料夾備份、檔案歷程記錄或備份軟體。電腦故障或中勒索病毒時，桌面與文件裡的檔案可能救不回來，請和資訊人員確認備份方式。")]
                :
                [
                    .. backup.OneDriveFolders.Count > 0 ? [new LabeledValue("OneDrive", $"{string.Join("、", backup.OneDriveFolders)}在 OneDrive 資料夾中，會自動備份到雲端")] : Array.Empty<LabeledValue>(),
                    .. backup.FileHistory ? [new LabeledValue("檔案歷程記錄", "已設定")] : Array.Empty<LabeledValue>(),
                    .. backup.BackupSoftware.Count > 0 ? [new LabeledValue("備份軟體", string.Join("、", backup.BackupSoftware))] : Array.Empty<LabeledValue>(),
                    new LabeledValue("說明", "只能確認有設定備份，無法確認最近一次備份是否成功"),
                ]));
        }

        foreach (var battery in health.Batteries)
            sections.Add(new("電池", HealthText.Battery(battery, isElevated)));

        sections.Add(new("印表機", health.Printers.Count == 0
            ? [new LabeledValue("狀態", "沒有安裝印表機")]
            : health.Printers.Select(p => new LabeledValue(p.IsDefault ? "預設" : "印表機", string.Join("・", new[]
            {
                p.Name,
                p.IsNetwork ? "網路印表機" : "",
                p.IsOffline ? "離線" : "",
                p.Jobs > 0 ? $"{p.Jobs} 個列印工作等待中" : "",
            }.Where(s => s.Length > 0)))).ToList()));
        return sections;
    }
}
