using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Memory;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.App.ViewModels;

public sealed partial class MemoryViewModel(AppServices services) : PageViewModel
{
    private const int ProgramCount = 15;
    private const int ErrorCancelled = 1223;
    private bool _hardwareLoaded;

    public override string Title => "記憶體";

    public override string Subtitle =>
        "查看記憶體用量、最占記憶體的程式與已安裝的記憶體模組。「釋放記憶體」會請程式交還暫時用不到的記憶體，不會關閉程式；效果是暫時的，切回程式時可能稍慢。";

    public ObservableCollection<ProgramRow> Programs { get; } = [];
    public ObservableCollection<ModuleRow> Modules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(FreeMemoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _usedPercent;

    [ObservableProperty]
    private string _usedPercentText = "";

    [ObservableProperty]
    private string _usageText = "";

    [ObservableProperty]
    private string _commitText = "";

    [ObservableProperty]
    private string _commitDetail = "";

    [ObservableProperty]
    private string _installedText = "";

    [ObservableProperty]
    private string _installedDetail = "";

    public override void OnNavigatedTo()
    {
        if (!IsBusy) RefreshCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task FreeMemoryAsync()
    {
        IsBusy = true;
        try
        {
            using var self = Process.GetCurrentProcess();
            var session = self.SessionId;
            var result = await Task.Run(() => MemoryTrimmer.TrimSession(session, services.Log));
            await LoadAsync();
            StatusText = result.Gained > 0
                ? $"已整理 {result.Trimmed} 個程式，可用記憶體由 {ByteSize.Format((long)result.AvailableBefore)} 增加到 {ByteSize.Format((long)result.AvailableAfter)}。"
                : $"已整理 {result.Trimmed} 個程式，可用記憶體沒有明顯增加（程式正在使用的記憶體不會被拿走）。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ScheduleMemoryTest()
    {
        if (!Dialogs.Confirm("Windows 記憶體診斷會在重新開機時檢查記憶體是否故障，約需 15 到 30 分鐘，期間無法使用電腦。\n\n" +
                             "接著會開啟 Windows 的視窗（需要系統管理員同意），請在視窗中選擇立即重新開機，或下次開機時檢查。", "記憶體檢測"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mdsched.exe")) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
        }
        catch (Win32Exception ex)
        {
            Dialogs.Error($"無法開啟記憶體診斷：{UserMessage.For(ex)}");
        }
    }

    private async Task LoadAsync()
    {
        var loadHardware = !_hardwareLoaded;
        var (status, programs, hardware) = await Task.Run(() => (
            MemoryInfo.Read(),
            MemoryUsage.TopPrograms(ProcessSnapshot.ReadAll(), ProgramCount),
            loadHardware ? MemoryHardwareReader.Read() : null));

        UsedPercent = status.UsedPercent;
        UsedPercentText = $"{status.UsedPercent:0}%";
        UsageText = $"已用 {ByteSize.Format((long)status.UsedPhysical)}／{ByteSize.Format((long)status.TotalPhysical)}，可用 {ByteSize.Format((long)status.AvailablePhysical)}";
        CommitText = Pair(status.CommitUsed, status.CommitLimit);
        CommitDetail = "已配置／上限（實體記憶體加上分頁檔）";
        InstalledText = ByteSize.Format((long)status.InstalledPhysical);

        Programs.Clear();
        foreach (var program in programs) Programs.Add(new ProgramRow(program, status.TotalPhysical));

        if (hardware is null) return;
        _hardwareLoaded = hardware.Error is null;
        Modules.Clear();
        foreach (var module in hardware.Modules) Modules.Add(new ModuleRow(module));
        InstalledDetail = hardware.Error ?? HardwareSummary(hardware);
    }

    /// <summary>"38.4／57.8 GB": one unit when both match, so the large number fits a narrow card.</summary>
    private static string Pair(ulong used, ulong total)
    {
        string usedText = ByteSize.Format((long)used), totalText = ByteSize.Format((long)total);
        var unit = totalText[(totalText.LastIndexOf(' ') + 1)..];
        return usedText.EndsWith(" " + unit, StringComparison.Ordinal) ? $"{usedText[..^(unit.Length + 1)]}／{totalText}" : $"{usedText}／{totalText}";
    }

    private static string HardwareSummary(MemoryHardware hardware)
    {
        var text = $"{hardware.Modules.Count} 條記憶體";
        if (hardware.SlotCount is { } slots) text += $"，共 {slots} 個插槽";
        if (hardware.MaxCapacityBytes is { } max) text += $"，主機板最多 {ByteSize.Format((long)max)}";
        return text;
    }
}

public sealed class ProgramRow(ProgramMemory program, ulong totalPhysical)
{
    public string Name => program.DisplayName;
    public string ImageName => program.ImageName;
    public string ProcessesText => $"{program.Processes:N0}";
    public string MemoryText => ByteSize.Format(program.PrivateWorkingSet);
    public long MemorySort => program.PrivateWorkingSet;
    public double Percent => totalPhysical == 0 ? 0 : 100.0 * program.PrivateWorkingSet / totalPhysical;
    public string PercentText => $"{Percent:0.#}%";
}

public sealed class ModuleRow(MemoryModule module)
{
    public string Slot => module.Slot;
    public string CapacityText => ByteSize.Format((long)module.CapacityBytes);
    public string SpecText => string.Join(" ", new[] { module.Type, module.SpeedMHz > 0 ? $"{module.SpeedMHz} MHz" : "" }.Where(s => s.Length > 0));
    public string Maker => string.Join(" ", new[] { module.Manufacturer, module.PartNumber }.Where(s => s.Length > 0));
}
