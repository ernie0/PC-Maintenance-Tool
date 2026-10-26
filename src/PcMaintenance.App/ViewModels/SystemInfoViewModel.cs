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
        _ => "",
    };
}

public sealed partial class SystemInfoViewModel(AppServices services) : PageViewModel
{
    private ComputerInfo? _info;
    private IReadOnlyList<InfoSection> _sections = [];

    public override string Title => "電腦資訊";

    public override string Subtitle => "這台電腦的 Windows 版本與硬體配備。可以複製後貼給維修人員，或匯出成 CSV 彙整各台電腦的資料。";

    public ObservableCollection<InfoSection> LeftSections { get; } = [];
    public ObservableCollection<InfoSection> RightSections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CopyCommand), nameof(ExportCommand))]
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
            _info = await Task.Run(SystemInfoReader.Read);
            _sections = Sections(_info);
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
        try
        {
            Clipboard.SetText(text.ToString().TrimEnd());
            StatusText = "已複製，可以貼到訊息或文件中。";
        }
        catch (COMException)
        {
            StatusText = "剪貼簿正被其他程式使用，請再試一次。";
        }
    }

    [RelayCommand(CanExecute = nameof(HasInfo))]
    private void Export()
    {
        if (_info is null || Dialogs.AskCsvPath(InventoryExport.SystemKind) is not { } path) return;
        try
        {
            InventoryExport.WriteSystem(path, _info, DateTime.Now);
            services.Log.Info($"匯出電腦配備：{path}");
            StatusText = $"已匯出到 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
    }

    internal static IReadOnlyList<InfoSection> Sections(ComputerInfo info)
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
        return sections;
    }
}
