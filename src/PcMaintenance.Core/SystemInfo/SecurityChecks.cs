using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using PcMaintenance.Core.Network;

namespace PcMaintenance.Core.SystemInfo;

public enum DiskEncryption
{
    /// <summary>Only administrators may ask.</summary>
    NeedsElevation,
    Encrypted,
    NotEncrypted,
    /// <summary>BitLocker answered but does not list the system drive (for example a virtual machine's disk).</summary>
    NotSupported,
    /// <summary>No BitLocker on this edition (Windows Home).</summary>
    NoBitLocker,
    Unreadable,
}

/// <summary>Registry values the checks read, so the judgement can be tested apart from the machine.</summary>
public sealed record SecuritySettings
{
    /// <summary>Firewalls other than Windows' own that Security Center lists as on; they replace Windows Firewall.</summary>
    public IReadOnlyList<string> OtherFirewalls { get; init; } = [];
    /// <summary>Windows Firewall per profile (domain, private, public); null when unreadable.</summary>
    public bool? DomainFirewall { get; init; }
    public bool? PrivateFirewall { get; init; }
    public bool? PublicFirewall { get; init; }
    public DiskEncryption SystemDrive { get; init; } = DiskEncryption.NeedsElevation;
    public bool RemoteDesktopOn { get; init; }
    public bool RemoteDesktopNla { get; init; } = true;
    public bool Smb1ClientOn { get; init; }
    public bool UacOn { get; init; } = true;
    public bool AdminsPromptedForConsent { get; init; } = true;
    public bool AutoLogon { get; init; }
    public bool AutoLogonPasswordStored { get; init; }
    public bool GuestEnabled { get; init; }
    public IReadOnlyList<string> Administrators { get; init; } = [];
}

/// <summary>
/// Settings that leave a PC open: firewall, disk encryption, remote desktop, the old SMBv1 protocol, user account
/// control, automatic sign-in, guest and administrator accounts. Only read; each finding says where to change it.
/// </summary>
public static class SecurityChecks
{
    public static IReadOnlyList<DiagnosisStep> Read(bool isElevated, bool isLaptop) => Evaluate(ReadSettings(isElevated), isElevated, isLaptop);

    public static IReadOnlyList<DiagnosisStep> Evaluate(SecuritySettings s, bool isElevated, bool isLaptop)
    {
        var steps = new List<DiagnosisStep>();

        if (s.OtherFirewalls.Count > 0)
        {
            steps.Add(new("防火牆", DiagnosisResult.Pass, $"{string.Join("、", s.OtherFirewalls)} 正在保護"));
        }
        else
        {
            var off = new[] { ("網域", s.DomainFirewall), ("私人", s.PrivateFirewall), ("公用", s.PublicFirewall) }
                .Where(p => p.Item2 == false).Select(p => p.Item1).ToList();
            steps.Add(off.Count == 0
                ? new("防火牆", DiagnosisResult.Pass, "Windows 防火牆在網域、私人、公用網路都已開啟")
                : new("防火牆", DiagnosisResult.Fail, $"Windows 防火牆在{string.Join("、", off)}網路是關閉的",
                    "到「系統工具」的「Windows 防火牆」把防火牆開啟；公司有其他防火牆政策時請先問資訊人員"));
        }

        steps.Add(s.SystemDrive switch
        {
            DiskEncryption.Encrypted => new("磁碟加密", DiagnosisResult.Pass, "系統磁碟已用 BitLocker 加密"),
            DiskEncryption.NotEncrypted => isLaptop
                ? new("磁碟加密", DiagnosisResult.Warning, "系統磁碟沒有加密，筆電遺失時裡面的資料可能被讀走",
                    "在「設定」→「隱私權與安全性」→「裝置加密」或控制台的「BitLocker 磁碟機加密」開啟，並把修復金鑰交給資訊人員保管")
                : new("磁碟加密", DiagnosisResult.Info, "系統磁碟沒有加密"),
            DiskEncryption.NotSupported => new("磁碟加密", DiagnosisResult.Info, "系統磁碟不支援 BitLocker（例如虛擬機器的磁碟）"),
            DiskEncryption.NoBitLocker => new("磁碟加密", DiagnosisResult.Info, "這個 Windows 版本沒有 BitLocker"),
            DiskEncryption.Unreadable => new("磁碟加密", DiagnosisResult.Info, "無法讀取 BitLocker 狀態"),
            _ => new("磁碟加密", DiagnosisResult.Info, "以系統管理員重新啟動後才能讀取"),
        });

        steps.Add(!s.RemoteDesktopOn
            ? new("遠端桌面", DiagnosisResult.Pass, "已關閉")
            : s.RemoteDesktopNla
                ? new("遠端桌面", DiagnosisResult.Info, "已開啟，連線前要先驗證身分（網路層級驗證）", "不需要從別台電腦連進來時，可以在「設定」→「系統」→「遠端桌面」關閉")
                : new("遠端桌面", DiagnosisResult.Warning, "已開啟，而且沒有要求網路層級驗證，連線畫面會直接暴露在網路上",
                    "不需要時關閉；需要時在「遠端桌面」設定開啟「要求電腦使用網路層級驗證」"));

        steps.Add(s.Smb1ClientOn
            ? new("舊版檔案分享（SMBv1）", DiagnosisResult.Warning, "已開啟；WannaCry 等勒索軟體就是透過它散播",
                "在「開啟或關閉 Windows 功能」取消「SMB 1.0/CIFS 檔案共用支援」；有舊 NAS 或影印機只支援 SMBv1 時先和資訊人員確認")
            : new("舊版檔案分享（SMBv1）", DiagnosisResult.Pass, "已關閉（Windows 的預設）"));

        steps.Add(!s.UacOn
            ? new("使用者帳戶控制", DiagnosisResult.Fail, "已關閉，所有程式都直接以系統管理員權限執行",
                "在「使用者帳戶控制設定」調回預設值，重新開機後生效")
            : !s.AdminsPromptedForConsent
                ? new("使用者帳戶控制", DiagnosisResult.Warning, "系統管理員帳號的程式不經詢問就取得最高權限",
                    "在「使用者帳戶控制設定」調回預設值（變更時通知我）")
                : new("使用者帳戶控制", DiagnosisResult.Pass, "已開啟"));

        steps.Add(s.AutoLogonPasswordStored
            ? new("自動登入", DiagnosisResult.Fail, "開機會自動登入，而且密碼以明碼存在登錄中，能讀登錄的人都看得到",
                "請資訊人員改用 Windows 內建的自動登入設定，或取消自動登入，並刪除登錄中的 DefaultPassword")
            : s.AutoLogon
                ? new("自動登入", DiagnosisResult.Warning, "開機會自動登入，任何人開機就能使用這台電腦", "不需要時在「netplwiz」勾選「必須輸入使用者名稱和密碼」")
                : new("自動登入", DiagnosisResult.Pass, "沒有設定自動登入"));

        steps.Add(s.GuestEnabled
            ? new("Guest 帳號", DiagnosisResult.Warning, "Guest 帳號已啟用，不用密碼就能登入", "在「電腦管理」→「本機使用者和群組」停用 Guest")
            : new("Guest 帳號", DiagnosisResult.Pass, "已停用"));

        steps.Add(s.Administrators.Count == 0
            ? new("系統管理員帳號", DiagnosisResult.Info, "無法讀取")
            : new("系統管理員帳號", DiagnosisResult.Info, $"{s.Administrators.Count} 個：{string.Join("、", s.Administrators)}",
                "不認識的帳號請資訊人員確認"));
        return steps;
    }

    public static SecuritySettings ReadSettings(bool isElevated)
    {
        var lm = Registry.LocalMachine;
        var winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        return new SecuritySettings
        {
            OtherFirewalls = ReadOtherFirewalls(),
            DomainFirewall = FirewallOn("DomainProfile", "DomainProfile"),
            PrivateFirewall = FirewallOn("StandardProfile", "PrivateProfile"),
            PublicFirewall = FirewallOn("PublicProfile", "PublicProfile"),
            SystemDrive = isElevated ? ReadBitLocker() : DiskEncryption.NeedsElevation,
            RemoteDesktopOn = Value(lm, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") is 0,
            RemoteDesktopNla = Value(lm, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication") is not 0,
            // The SMB 1.0 client driver exists only while the optional feature is installed; start type 4 means disabled.
            Smb1ClientOn = KeyExists(lm, @"SYSTEM\CurrentControlSet\Services\mrxsmb10") && Value(lm, @"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start") is not 4,
            UacOn = Value(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA") is not 0,
            AdminsPromptedForConsent = Value(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin") is not 0,
            AutoLogon = Value(lm, winlogon, "AutoAdminLogon") is "1",
            // Only whether a password value exists; its content is never read.
            AutoLogonPasswordStored = Value(lm, winlogon, "AutoAdminLogon") is "1" && ValueNames(lm, winlogon).Contains("DefaultPassword", StringComparer.OrdinalIgnoreCase),
            GuestEnabled = ReadGuestEnabled(),
            Administrators = ReadAdministrators(),
        };
    }

    /// <summary>A group policy setting, when there is one, wins over the local one.</summary>
    private static bool? FirewallOn(string localProfile, string policyProfile)
    {
        var policy = Value(Registry.LocalMachine, $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{policyProfile}", "EnableFirewall");
        var local = Value(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{localProfile}", "EnableFirewall");
        return (policy ?? local) switch { int value => value != 0, _ => null };
    }

    private static IReadOnlyList<string> ReadOtherFirewalls()
    {
        var names = new List<string>();
        Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct", item =>
        {
            // Same reading of productState as for antivirus products: bits 12-15 are 1 while it protects.
            if (((Convert.ToUInt32(item["productState"] ?? 0u) >> 12) & 0xF) == 1 && item["displayName"] is string name) names.Add(name.Trim());
        });
        return names.Distinct().ToList();
    }

    private static DiskEncryption ReadBitLocker()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var state = DiskEncryption.NotSupported;
        try
        {
            // This namespace refuses connections that are not encrypted (packet privacy), even from an administrator.
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
                new ConnectionOptions { Authentication = AuthenticationLevel.PacketPrivacy });
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'"));
            using var results = searcher.Get();
            foreach (var item in results.Cast<ManagementObject>())
            {
                // 0 off, 1 on, 2 unknown (for example locked).
                using (item)
                    state = Convert.ToUInt32(item["ProtectionStatus"] ?? 2u) switch
                    {
                        0 => DiskEncryption.NotEncrypted,
                        1 => DiskEncryption.Encrypted,
                        _ => DiskEncryption.Unreadable,
                    };
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        {
            state = DiskEncryption.NoBitLocker;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            state = DiskEncryption.Unreadable;
        }
        return state;
    }

    private static bool ReadGuestEnabled()
    {
        var enabled = false;
        // The guest account is the local account whose SID ends in -501, whatever it is called.
        Wmi.Query(@"root\cimv2", "SELECT SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = TRUE", item =>
        {
            if ((item["SID"] as string)?.EndsWith("-501", StringComparison.Ordinal) == true && item["Disabled"] is false) enabled = true;
        });
        return enabled;
    }

    /// <summary>Members of the built-in Administrators group (S-1-5-32-544), as DOMAIN\name.</summary>
    internal static IReadOnlyList<string> ReadAdministrators()
    {
        string group;
        try
        {
            group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value.Split('\\').Last();
        }
        catch (IdentityNotMappedException)
        {
            return [];
        }
        var members = new List<string>();
        if (NetLocalGroupGetMembers(null, group, 2, out var buffer, -1, out var read, out _, IntPtr.Zero) != 0) return members;
        try
        {
            var size = Marshal.SizeOf<LocalGroupMember>();
            for (var i = 0; i < read; i++)
            {
                var member = Marshal.PtrToStructure<LocalGroupMember>(buffer + i * size);
                if (!string.IsNullOrEmpty(member.DomainAndName)) members.Add(member.DomainAndName);
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
        return members;
    }

    private static object? Value(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] ValueNames(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool KeyExists(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMember
    {
        public IntPtr Sid;
        public int SidUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
