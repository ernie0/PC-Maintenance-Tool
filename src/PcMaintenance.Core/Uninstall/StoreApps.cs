using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Uninstall;

public sealed record StoreApp
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public required string PackageFullName { get; init; }
    public required string PackageFamilyName { get; init; }
    /// <summary>Preinstalled consumer apps a company PC rarely needs; only a suggestion, nothing is selected for the user.</summary>
    public bool IsCommonlyUnneeded { get; init; }
}

/// <summary>
/// Store and preinstalled apps of the current user, through Windows PowerShell's Appx commands (present on every
/// Windows 10/11). Frameworks, system apps and apps Windows marks as non-removable are never listed. Removal only
/// affects the current user, and the app can be installed again from the Microsoft Store.
/// </summary>
public static class StoreApps
{
    // Removing these breaks Windows itself (the Store, winget, the display language, the Start menu, Windows Security),
    // so they are not offered at all.
    private static readonly string[] ProtectedPrefixes =
    [
        "Microsoft.LanguageExperiencePack", "Microsoft.WindowsAppRuntime", "Microsoft.WinAppRuntime", "Microsoft.VCLibs",
        "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.Services.Store",
    ];

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.StartExperiencesApp",
        "Microsoft.SecHealthUI", "Microsoft.ApplicationCompatibilityEnhancements", "Microsoft.Winget.Source",
        "Microsoft.WidgetsPlatformRuntime", "MicrosoftWindows.CrossDevice",
    };

    private static readonly HashSet<string> CommonlyUnneeded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.Getstarted",
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.WindowsFeedbackHub", "Microsoft.GamingApp",
        "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.Xbox.TCUI", "Microsoft.ZuneVideo", "Microsoft.MicrosoftOfficeHub", "Clipchamp.Clipchamp",
        "Microsoft.549981C3F5F10", "Microsoft.Todos", "Microsoft.PowerAutomateDesktop", "Microsoft.WindowsMaps",
        "Microsoft.MixedReality.Portal", "Microsoft.SkypeApp", "MicrosoftTeams", "Microsoft.Copilot", "Microsoft.Windows.DevHome",
        "MicrosoftCorporationII.MicrosoftFamily",
    };

    // -InputObject keeps a single app as a one-item array; piping it would print a bare object.
    private const string ListScript =
        "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
        "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | " +
        "Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | " +
        "Select-Object Name, Publisher, @{n='Version';e={[string]$_.Version}}, PackageFullName, PackageFamilyName, InstallLocation)";

    public static async Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken ct = default)
    {
        var (exitCode, output, error) = await RunPowerShellAsync(ListScript, ct);
        if (exitCode != 0) throw new InvalidOperationException($"無法讀取 App 清單：{error.Trim()}");
        return Parse(output)
            .Where(p => !IsProtected(p.Name))
            .Select(p =>
            {
                var (name, publisher) = ManifestNames(p);
                return new StoreApp
                {
                    Name = p.Name,
                    DisplayName = name ?? p.Name,
                    Publisher = publisher ?? PublisherName(p.Publisher),
                    Version = p.Version ?? "",
                    PackageFullName = p.PackageFullName,
                    PackageFamilyName = p.PackageFamilyName,
                    IsCommonlyUnneeded = CommonlyUnneeded.Contains(p.Name),
                };
            })
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static bool IsProtected(string name) =>
        Protected.Contains(name) || ProtectedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static async Task<(bool Success, string Message)> RemoveAsync(StoreApp app, CancellationToken ct = default)
    {
        // The full name only holds letters, digits, dots, underscores and dashes; it is still quoted for PowerShell.
        var script = $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; Remove-AppxPackage -Package '{app.PackageFullName.Replace("'", "''")}' -ErrorAction Stop";
        var (exitCode, _, error) = await RunPowerShellAsync(script, ct);
        return exitCode == 0 ? (true, "已移除") : (false, FirstLine(error) is { Length: > 0 } line ? line : "無法移除");
    }

    internal sealed record PackageEntry(
        string Name, string? Publisher, string? Version, string PackageFullName, string PackageFamilyName, string? InstallLocation);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static IReadOnlyList<PackageEntry> Parse(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        return JsonSerializer.Deserialize<List<PackageEntry>>(json, JsonOptions) ?? [];
    }

    /// <summary>The app and publisher names Windows shows, from the package manifest; ms-resource names are looked up in the package.</summary>
    private static (string? Name, string? Publisher) ManifestNames(PackageEntry package)
    {
        if (string.IsNullOrEmpty(package.InstallLocation)) return (null, null);
        try
        {
            var properties = XDocument.Load(Path.Combine(package.InstallLocation, "AppxManifest.xml"))
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
            string? Read(string element)
            {
                var value = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == element)?.Value.Trim();
                if (string.IsNullOrEmpty(value)) return null;
                return value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? ResourceString(package, value) : value;
            }
            return (Read("DisplayName"), Read("PublisherDisplayName"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (null, null);
        }
    }

    private static string? ResourceString(PackageEntry package, string reference)
    {
        var key = reference["ms-resource:".Length..];
        var uri = key.StartsWith("//", StringComparison.Ordinal) ? $"ms-resource:{key}"
            : key.StartsWith('/') ? $"ms-resource://{package.Name}{key}"
            : key.Contains('/') ? $"ms-resource://{package.Name}/{key}"
            : $"ms-resource://{package.Name}/Resources/{key}";
        var buffer = new StringBuilder(512);
        var hr = NativeMethods.SHLoadIndirectString($"@{{{package.PackageFullName}? {uri}}}", buffer, buffer.Capacity, IntPtr.Zero);
        return hr == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    /// <summary>"CN=Microsoft Corporation, O=…" becomes "Microsoft Corporation".</summary>
    internal static string PublisherName(string? publisher)
    {
        if (string.IsNullOrEmpty(publisher)) return "";
        var first = publisher.Split(',')[0].Trim();
        return first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? first[3..].Trim('"') : first;
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("無法啟動 PowerShell");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}

public sealed class StoreAppRemover(ChangeJournal journal, FileLog? log = null)
{
    public async Task<(bool Success, string Message)> RemoveAsync(StoreApp app, CancellationToken ct = default)
    {
        var (success, message) = await StoreApps.RemoveAsync(app, ct);
        if (!success)
        {
            log?.Warn($"無法移除 App「{app.DisplayName}」：{message}");
            return (false, message);
        }
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.StoreAppRemoved,
            Target = app.DisplayName,
            Description = $"移除 App「{app.DisplayName}」（目前使用者）",
            RestoreData = new Dictionary<string, string> { ["family"] = app.PackageFamilyName, ["name"] = app.Name },
        });
        log?.Info($"移除 App：{app.DisplayName}（{app.PackageFullName}）");
        return (true, message);
    }

    /// <summary>Removed apps come back through the Store; this is its page for the app.</summary>
    public static string StoreLink(ChangeRecord record) => $"ms-windows-store://pdp/?PFN={record.RestoreData["family"]}";
}
