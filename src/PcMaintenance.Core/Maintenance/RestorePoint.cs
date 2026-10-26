using System.Management;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Maintenance;

public enum RestorePointOutcome
{
    Created,
    /// <summary>Windows creates at most one restore point per 24 hours by default and quietly skips the rest.</summary>
    SkippedRecent,
    ProtectionOff,
    Failed,
}

public sealed record RestorePointResult(RestorePointOutcome Outcome, string Message);

/// <summary>Creates a Windows restore point through WMI; needs elevation.</summary>
public static class RestorePoint
{
    public const string Description = "電腦維護工具";
    public const string CreatedMessage = "已建立還原點「" + Description + "」。";
    public const string SkippedMessage = "Windows 在 24 小時內已建立過還原點，這次沒有新增；需要時可以使用最近的那一個。";
    public const string ProtectionOffMessage = "系統磁碟沒有開啟「系統保護」，無法建立還原點；可在「系統工具」開啟「系統保護設定」後再試。";
    public const string ServiceDisabledMessage = "系統還原需要的 Windows 服務已停用（0x80070422），無法建立還原點；請資訊人員檢查是否有服務被停用。";

    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;
    // The same Windows error as an exception: a service the restore point needs is disabled (seen in Windows Sandbox).
    private const int ServiceDisabledHResult = unchecked((int)0x80070422);

    public static RestorePointResult Create()
    {
        try
        {
            var before = LatestSequence();
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = Description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output["ReturnValue"] ?? 1u);
            if (code == ErrorServiceDisabled) return new RestorePointResult(RestorePointOutcome.ProtectionOff, ProtectionOffMessage);
            if (code != 0) return new RestorePointResult(RestorePointOutcome.Failed, $"無法建立還原點（錯誤碼 {code}）。");
            return LatestSequence() is { } after && after != before
                ? new RestorePointResult(RestorePointOutcome.Created, CreatedMessage)
                : new RestorePointResult(RestorePointOutcome.SkippedRecent, SkippedMessage);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, "需要系統管理員權限才能建立還原點。");
        }
        catch (COMException ex) when (ex.HResult == ServiceDisabledHResult)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, ServiceDisabledMessage);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, $"無法建立還原點：{UserMessage.For(ex)}");
        }
    }

    private static uint? LatestSequence()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
        using var results = searcher.Get();
        uint? latest = null;
        foreach (var item in results.Cast<ManagementObject>())
        {
            using (item)
            {
                var sequence = Convert.ToUInt32(item["SequenceNumber"] ?? 0u);
                if (latest is null || sequence > latest) latest = sequence;
            }
        }
        return latest;
    }
}
