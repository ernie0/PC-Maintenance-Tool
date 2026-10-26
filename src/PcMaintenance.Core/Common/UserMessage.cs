using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace PcMaintenance.Core.Common;

/// <summary>
/// Turns exceptions into text for people. .NET runtime messages are English-only, so messages that
/// are already Chinese (ours, or Windows' localized Win32 text) are kept and the rest are replaced
/// by a description of the exception type.
/// </summary>
public static class UserMessage
{
    public static string For(Exception ex)
    {
        if (ex is JsonException json)
            return json.LineNumber is { } line ? $"設定檔格式錯誤（第 {line + 1} 行）" : "設定檔格式錯誤";
        if (ContainsChinese(ex.Message)) return ex.Message;
        return ex switch
        {
            UnauthorizedAccessException or SecurityException => "權限不足",
            FileNotFoundException or DirectoryNotFoundException => "找不到檔案或資料夾",
            PathTooLongException => "路徑太長",
            IOException => "檔案或資料夾正在使用中，或無法存取",
            COMException com => $"系統元件回應錯誤（0x{com.HResult:X8}）",
            Win32Exception win32 => $"系統錯誤（代碼 {win32.NativeErrorCode}）",
            _ => "發生未預期的錯誤",
        };
    }

    private static bool ContainsChinese(string text) => text.Any(c => c is >= '一' and <= '鿿');
}
