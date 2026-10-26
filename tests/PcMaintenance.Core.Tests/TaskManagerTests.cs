using System.Diagnostics;
using System.Net.NetworkInformation;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Performance;
using PcMaintenance.Core.Processes;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Tests;

public class TaskManagerTests
{
    [Fact]
    public void Snapshot_fields_match_what_windows_reports_for_this_process()
    {
        using var self = Process.GetCurrentProcess();
        var cpuBefore = self.TotalProcessorTime.Ticks;
        var processes = ProcessSnapshot.ReadAll();
        self.Refresh();
        var cpuAfter = self.TotalProcessorTime.Ticks;

        var me = Assert.Single(processes, p => p.Id == Environment.ProcessId);
        Assert.Equal(self.ProcessName + ".exe", me.ImageName, ignoreCase: true);
        Assert.Equal(self.SessionId, me.SessionId);
        Assert.Equal(self.StartTime.ToFileTimeUtc(), me.CreateTime);
        Assert.InRange(me.CpuTime, cpuBefore, cpuAfter);
        Assert.InRange(me.PrivateWorkingSet, 1, me.WorkingSet);
        Assert.True(me.Threads > 0 && me.Handles > 0);
        // Ids taken a moment apart still overlap almost completely.
        var dotnetIds = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        Assert.True(processes.Count(p => dotnetIds.Contains(p.Id)) > processes.Count * 9 / 10);
    }

    [Fact]
    public void Process_cpu_is_its_share_of_every_core_and_a_reused_id_starts_over()
    {
        ProcessSample Sample(int id, long created, long cpu) => new(id, "a.exe", 1, 0, 0, created, cpu, 1, 1);
        var tracker = new ProcessCpuTracker(processorCount: 4);
        var second = Stopwatch.Frequency;

        Assert.Equal(0, tracker.Update([Sample(10, 1, 0), Sample(11, 1, 0)], 1 * second)[(10, 1)]);
        // Half a second of processor time in one second on four cores is 12.5%.
        var usage = tracker.Update([Sample(10, 1, 5_000_000), Sample(11, 2, 9_000_000)], 2 * second);

        Assert.Equal(12.5, usage[(10, 1)], precision: 6);
        Assert.Equal(0, usage[(11, 2)]);
    }

    [Fact]
    public void System_cpu_counts_kernel_and_user_time_minus_idle() =>
        Assert.Equal(25, SystemCpuTracker.Usage((0, 0, 0), (Idle: 600, Kernel: 700, User: 100)), precision: 6);

    [Fact]
    public void Performance_sample_covers_cpu_memory_and_the_disk_holding_windows()
    {
        using var sampler = new PerformanceSampler(baseMHz: 2000);
        Thread.Sleep(300);
        var snapshot = sampler.Sample(Stopwatch.GetTimestamp());

        Assert.InRange(snapshot.Cpu.Usage, 0, 100);
        Assert.True(snapshot.Cpu.SpeedMHz > 0);
        Assert.True(snapshot.Memory.Status.TotalPhysical > 0);
        Assert.True(snapshot.Memory.CachedBytes > 0);
        var systemLetter = Path.GetPathRoot(Environment.SystemDirectory)![..2];
        Assert.Single(snapshot.Disks, d => d.Letters.Contains(systemLetter, StringComparison.OrdinalIgnoreCase));
        Assert.All(snapshot.Disks, d => Assert.InRange(d.ActivePercent, 0, 100));
    }

    [Theory]
    [InlineData("1 C:", 1, "C:")]
    [InlineData("0 D: F:", 0, "D: F:")]
    [InlineData("2", 2, "")]
    public void Disk_instances_name_the_disk_number_and_letters(string instance, int number, string letters) =>
        Assert.Equal((number, letters), PerformanceSampler.ParseDiskInstance(instance));

    [Fact]
    public void The_total_instance_is_not_a_disk() => Assert.Null(PerformanceSampler.ParseDiskInstance("_Total"));

    [Fact]
    public void Gpu_usage_is_the_busiest_engine_type_summed_over_processes()
    {
        var usage = PerformanceSampler.GpuUtilization(new Dictionary<string, double>
        {
            ["pid_10_luid_0x00000000_0x0000D1A3_phys_0_eng_0_engtype_3D"] = 20,
            ["pid_11_luid_0x00000000_0x0000D1A3_phys_0_eng_0_engtype_3D"] = 15,
            ["pid_11_luid_0x00000000_0x0000D1A3_phys_0_eng_5_engtype_VideoDecode"] = 30,
            ["pid_12_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D"] = 5,
        });

        Assert.Equal(35, usage["0x00000000_0x0000D1A3"]);
        Assert.Equal(5, usage["0x00000000_0x0000ABCD"]);
    }

    [Fact]
    public void Gpu_counters_use_the_same_adapter_ids_as_directx()
    {
        var gpus = SystemInfoReader.ReadGpus();
        Assert.All(gpus, g => Assert.Matches("^0x[0-9A-F]{8}_0x[0-9A-F]{8}$", g.Luid));
        if (gpus.Count == 0) return;
        using var sampler = new PerformanceSampler(baseMHz: null);
        Thread.Sleep(300);
        Assert.Contains(sampler.Sample(Stopwatch.GetTimestamp()).Gpus, s => gpus.Any(g => g.Luid == s.Luid));
    }

    [Fact]
    public void System_info_names_this_computer_and_its_windows_build()
    {
        var info = SystemInfoReader.Read();
        Assert.Equal(Environment.MachineName, info.Machine.ComputerName);
        Assert.Contains("Windows", info.Os.Name);
        Assert.StartsWith(Environment.OSVersion.Version.Build.ToString(), info.Os.Build);
        Assert.Equal(Environment.ProcessorCount, info.Processor.LogicalProcessors);
        Assert.NotEqual("", info.Processor.Name);
        Assert.NotEmpty(info.MemoryModules.Modules);
    }

    [Theory]
    [InlineData("Default string", "")]
    [InlineData(" To be filled by O.E.M. ", "")]
    [InlineData("B760M GAMING DDR4", "B760M GAMING DDR4")]
    public void Firmware_placeholders_are_not_shown_as_real_values(string raw, string shown) =>
        Assert.Equal(shown, SystemInfoReader.Text(raw));

    [Fact]
    public void Wmi_dates_are_read_as_calendar_dates()
    {
        Assert.Equal(new DateTime(2025, 6, 12), SystemInfoReader.CimDate("20250612000000.000000-000"));
        Assert.Null(SystemInfoReader.CimDate("unknown"));
    }

    [Fact]
    public void Spec_export_is_one_row_named_after_the_computer()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "spec.csv");
        InventoryExport.WriteSystem(file, SystemInfoReader.Read(), new DateTime(2026, 10, 25, 9, 0, 0));

        var lines = File.ReadAllText(file).TrimEnd().Split("\r\n");
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("電腦名稱,網域／工作群組,製造商,型號,序號", lines[0]);
        Assert.StartsWith(Environment.MachineName + ",", lines[1]);
        Assert.EndsWith("2026-10-25 09:00", lines[1]);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller", OperationalStatus.Up, true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6 AX201 160MHz", OperationalStatus.Up, true)]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", OperationalStatus.Up, false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller", OperationalStatus.Down, false)]
    [InlineData(NetworkInterfaceType.Loopback, "Software Loopback Interface 1", OperationalStatus.Up, false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek Gaming 2.5GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", OperationalStatus.Up, false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek Gaming 2.5GbE Family Controller-QoS Packet Scheduler-0000", OperationalStatus.Up, false)]
    public void Only_connected_physical_adapters_are_shown(NetworkInterfaceType type, string description, OperationalStatus status, bool shown) =>
        Assert.Equal(shown, NetworkActivitySampler.IsShown(type, description, status));

    [Fact]
    public void Windows_essentials_other_sessions_and_this_app_cannot_be_ended()
    {
        ProcessSample Sample(int id, string name, int session) => new(id, name, session, 0, 0, 1, 0, 1, 1);
        Assert.NotNull(ProcessControl.WhyNotEnd(Sample(700, "csrss.exe", 1), currentSession: 1, currentProcessId: 99));
        Assert.NotNull(ProcessControl.WhyNotEnd(Sample(700, "EXPLORER.EXE", 1), 1, 99));
        Assert.NotNull(ProcessControl.WhyNotEnd(Sample(4, "System", 0), 1, 99));
        Assert.NotNull(ProcessControl.WhyNotEnd(Sample(700, "vendorsvc.exe", 0), 1, 99));
        Assert.NotNull(ProcessControl.WhyNotEnd(Sample(99, "PcMaintenance.exe", 1), 1, 99));
        Assert.Null(ProcessControl.WhyNotEnd(Sample(700, "notepad.exe", 1), 1, 99));
    }

    [Fact]
    public void Ending_a_process_checks_it_is_still_the_same_one()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" }) startInfo.ArgumentList.Add(arg);
        using var child = Process.Start(startInfo)!;
        try
        {
            var sample = ProcessSnapshot.ReadAll().Single(p => p.Id == child.Id);
            Assert.Throws<InvalidOperationException>(() => ProcessControl.End(sample with { CreateTime = sample.CreateTime - 1 }));
            Assert.False(child.HasExited);

            ProcessControl.End(sample);
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public void Services_that_may_not_be_changed_are_never_started_or_stopped()
    {
        var item = new StartupItem
        {
            Key = "service|Spooler",
            Source = StartupSource.Service,
            Name = "Print Spooler",
            CanChange = false,
            Data = new Dictionary<string, string> { ["service"] = "Spooler" },
        };
        Assert.Throws<InvalidOperationException>(() => ServiceRunner.Run(item, ServiceAction.Stop));
    }
}
