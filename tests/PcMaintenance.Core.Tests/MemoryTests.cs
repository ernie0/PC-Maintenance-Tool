using System.Diagnostics;
using PcMaintenance.Core.Memory;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Tests;

public class MemoryTests
{
    [Fact]
    public void Memory_status_is_consistent()
    {
        var status = MemoryInfo.Read();
        Assert.True(status.TotalPhysical > 0);
        Assert.InRange(status.AvailablePhysical, 1UL, status.TotalPhysical);
        Assert.True(status.CommitLimit >= status.CommitAvailable);
        Assert.True(status.InstalledPhysical >= status.TotalPhysical);
    }

    [Fact]
    public void Programs_add_up_their_processes_and_fall_back_to_the_file_name()
    {
        ProcessSample Sample(int id, string name, long privateBytes) => new(id, name, 1, privateBytes, privateBytes, 0, 0, 1, 1);
        ProcessSample[] processes =
        [
            Sample(10, "chrome.exe", 300),
            Sample(11, "chrome.exe", 200),
            Sample(12, "notepad.exe", 400),
            Sample(4, "System", 900),
        ];

        var top = MemoryUsage.TopPrograms(processes, 5, id => id == 12 ? "記事本" : null);

        Assert.Equal([("chrome", 2, 500L), ("記事本", 1, 400L)], top.Select(p => (p.DisplayName, p.Processes, p.PrivateWorkingSet)));
    }

    [Fact]
    public void Trimming_a_process_returns_its_unused_memory()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-Command", "$b = [byte[]]::new(200MB); for ($i = 0; $i -lt $b.Length; $i += 4096) { $b[$i] = 1 }; Start-Sleep -Seconds 60" })
            startInfo.ArgumentList.Add(arg);
        using var child = Process.Start(startInfo)!;
        try
        {
            long WorkingSet() => ProcessSnapshot.ReadAll().Single(p => p.Id == child.Id).WorkingSet;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (WorkingSet() < 200L << 20 && DateTime.UtcNow < deadline) Thread.Sleep(200);
            var before = WorkingSet();
            Assert.True(before >= 200L << 20, $"child only reached {before} bytes");

            Assert.True(MemoryTrimmer.Trim(child.Id));
            Assert.True(WorkingSet() < before / 2);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Memory_modules_are_listed()
    {
        var hardware = MemoryHardwareReader.Read();
        Assert.Null(hardware.Error);
        Assert.NotEmpty(hardware.Modules);
        Assert.All(hardware.Modules, m => Assert.True(m.CapacityBytes > 0));
    }
}
