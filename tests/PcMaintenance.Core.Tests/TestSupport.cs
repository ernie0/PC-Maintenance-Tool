using System.Diagnostics;
using Microsoft.Win32;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Tests;

/// <summary>A unique folder under %TEMP% that is removed afterwards; junctions inside are unlinked, never followed.</summary>
internal sealed class TempDir : IDisposable
{
    private readonly List<string> _junctions = [];

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PcMaintenanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Dir(params string[] parts)
    {
        var dir = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string File(string relativePath, int size = 10, DateTime? timeUtc = null, byte fill = 0x41)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());
        if (timeUtc is { } t)
        {
            System.IO.File.SetCreationTimeUtc(path, t);
            System.IO.File.SetLastWriteTimeUtc(path, t);
        }
        return path;
    }

    public string Junction(string relativeLink, string target)
    {
        var link = System.IO.Path.Combine(Path, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link)) throw new InvalidOperationException("無法建立 junction");
        _junctions.Add(link);
        return link;
    }

    public SystemFolders Folders() => new()
    {
        WindowsDir = Dir("Windows"),
        ProgramFiles = Dir("Program Files"),
        ProgramFilesX86 = Dir("Program Files (x86)"),
        CommonProgramFiles = Dir("Program Files", "Common Files"),
        CommonProgramFilesX86 = Dir("Program Files (x86)", "Common Files"),
        ProgramData = Dir("ProgramData"),
        UserProfile = Dir("Users", "tester"),
        AppData = Dir("Users", "tester", "AppData", "Roaming"),
        LocalAppData = Dir("Users", "tester", "AppData", "Local"),
        UserTemp = Dir("Users", "tester", "AppData", "Local", "Temp"),
    };

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            try
            {
                Directory.Delete(junction);
            }
            catch (IOException)
            {
            }
        }
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A throwaway key under HKCU\Software\PcMaintenanceTests, deleted afterwards.</summary>
internal sealed class TestRegistryKey : IDisposable
{
    private const string ParentPath = @"Software\PcMaintenanceTests";

    public TestRegistryKey()
    {
        KeyPath = $@"{ParentPath}\{Guid.NewGuid():N}";
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
    }

    public string KeyPath { get; }

    public RegistryKey Create(string relativePath) => Registry.CurrentUser.CreateSubKey($@"{KeyPath}\{relativePath}", writable: true);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        // Tests run in parallel, so the shared parent goes only once the last test key is gone.
        using (var parent = Registry.CurrentUser.OpenSubKey(ParentPath))
        {
            if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
        }
        try
        {
            Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
            // Another test created its key in the meantime; it will remove the parent when done.
        }
    }
}
