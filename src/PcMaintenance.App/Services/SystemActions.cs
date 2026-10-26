using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Maintenance;

namespace PcMaintenance.App.Services;

/// <summary>Starts Windows' own tools and the repair commands offered on the tools page.</summary>
public static class SystemActions
{
    private const int ErrorCancelled = 1223;

    /// <summary>Opens a program, .msc or .cpl file, or a settings link the way Explorer would; false when elevation is declined.</summary>
    public static bool Open(string target, string arguments = "", bool elevated = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true, Verb = elevated ? "runas" : "" })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>Runs commands in an elevated console that stays open, so their progress and result can be read there.</summary>
    public static bool RunInElevatedConsole(string title, string commands) =>
        Open(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/k title {title}&{commands}", elevated: true);

    /// <summary>
    /// Clears the DNS cache. Newer Windows builds let only administrators do that, so without elevation a failed try is
    /// repeated through Windows' permission prompt, in a hidden window.
    /// </summary>
    public static async Task<string?> FlushDnsAsync()
    {
        var (exitCode, output) = await ConsoleTool.RunAsync("ipconfig.exe", "/flushdns");
        if (exitCode == 0) return "已清除 DNS 快取。";
        if (Elevation.IsElevated()) return $"無法清除 DNS 快取：{Core.DiskTools.DiskMaintenance.Tail(output, 2)}";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return "無法清除 DNS 快取。";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? "已清除 DNS 快取。" : "無法清除 DNS 快取（以系統管理員執行也失敗）。";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return "已取消：這台電腦的 Windows 需要系統管理員權限才能清除 DNS 快取。";
        }
    }

    /// <summary>Ends the desktop shell of this session and starts it again; open File Explorer windows close.</summary>
    public static async Task RestartExplorerAsync(Action? whileStopped = null)
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId != session) continue;
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
                {
                }
            }
        }
        whileStopped?.Invoke();
        // Windows may bring the shell back by itself; a second copy would only open a File Explorer window.
        await Task.Delay(1500);
        if (!ShellRunning(session))
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))?.Dispose();
    }

    private static bool ShellRunning(int session)
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process) running |= process.SessionId == session;
        }
        return running;
    }

    /// <summary>Deletes the icon and thumbnail caches while the shell is stopped (it keeps them open otherwise).</summary>
    public static async Task<int> RebuildIconCacheAsync()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var explorerCache = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        var deleted = 0;
        await RestartExplorerAsync(() =>
        {
            var files = new List<string> { Path.Combine(local, "IconCache.db") };
            if (Directory.Exists(explorerCache))
            {
                files.AddRange(Directory.EnumerateFiles(explorerCache, "iconcache_*.db"));
                files.AddRange(Directory.EnumerateFiles(explorerCache, "thumbcache_*.db"));
            }
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
        return deleted;
    }

    /// <summary>
    /// Presses Win+Ctrl+Shift+B, Windows' own shortcut that restarts the graphics driver: the screen goes black for a
    /// moment and open programs keep running.
    /// </summary>
    public static bool RestartGraphicsDriver()
    {
        ushort[] keys = [0x5B, 0x11, 0x10, 0x42]; // Left Windows, Ctrl, Shift, B
        var inputs = keys.Select(k => Key(k, up: false)).Concat(keys.Reverse().Select(k => Key(k, up: true))).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 0x0002u : 0u }, // KEYEVENTF_KEYUP
    };

    // INPUT on x64: a 4-byte type, padding, then the 32-byte union (sized by MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}

/// <summary>Creates a restore point: directly when this window is elevated, otherwise through an elevated copy of the program.</summary>
public static class RestorePointLauncher
{
    private const int ErrorCancelled = 1223;

    /// <param name="started">Called once Windows has been given permission and the elevated copy is running.</param>
    public static async Task<RestorePointResult> CreateAsync(bool isElevated, Action? started = null)
    {
        if (isElevated) return await Task.Run(RestorePoint.Create);
        try
        {
            // Starting returns only after the permission prompt is answered; meanwhile the window keeps drawing.
            using var process = await Task.Run(() =>
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restore-point") { UseShellExecute = true, Verb = "runas" }));
            if (process is null) return new RestorePointResult(RestorePointOutcome.Failed, "無法啟動建立還原點的程式。");
            started?.Invoke();
            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                CommandRunner.Ok => new RestorePointResult(RestorePointOutcome.Created, RestorePoint.CreatedMessage),
                CommandRunner.RestorePointSkipped => new RestorePointResult(RestorePointOutcome.SkippedRecent, RestorePoint.SkippedMessage),
                CommandRunner.RestorePointProtectionOff => new RestorePointResult(RestorePointOutcome.ProtectionOff, RestorePoint.ProtectionOffMessage),
                _ => new RestorePointResult(RestorePointOutcome.Failed, "無法建立還原點，原因記在系統管理員帳號的每日記錄檔。"),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, "已取消（沒有同意系統管理員權限），沒有建立還原點。");
        }
    }
}
