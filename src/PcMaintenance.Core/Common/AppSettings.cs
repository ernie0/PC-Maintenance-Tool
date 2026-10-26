using System.Text.Json;

namespace PcMaintenance.Core.Common;

/// <summary>
/// Company-wide policy, read from %ProgramData%\PcMaintenance\settings.json so IT can deploy one
/// file to every PC. Missing or invalid files fall back to these defaults.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Temp and report files newer than this are left alone so running installers keep their files.</summary>
    public int MinFileAgeHours { get; init; } = 24;
    public IReadOnlyList<string> DisabledJunkCategories { get; init; } = [];
    /// <summary>Folders the tool never deletes from, including duplicate removal.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public bool ShowMicrosoftItems { get; init; }
    public bool AllowServiceChanges { get; init; } = true;
    public int DuplicateMinSizeMB { get; init; } = 1;
    /// <summary>Minimum days between two "--auto-clean" runs for the same user; 0 cleans at every run.</summary>
    public int AutoCleanIntervalDays { get; init; } = 7;

    public static AppSettings Default { get; } = new();

    public bool IsExcluded(string path) =>
        ExcludedPaths.Any(p => !string.IsNullOrWhiteSpace(p) && PathSafety.IsSameOrUnder(p, path));
}

public sealed record SettingsLoadResult(AppSettings Settings, string? SourcePath, string? Warning);

public static class SettingsLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads the first candidate file that exists.</summary>
    public static SettingsLoadResult Load(params string[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? AppSettings.Default;
                return new SettingsLoadResult(Normalize(settings), path, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new SettingsLoadResult(AppSettings.Default, path, $"設定檔無法讀取，已改用預設值（{UserMessage.For(ex)}：{path}）");
            }
        }
        return new SettingsLoadResult(AppSettings.Default, null, null);
    }

    private static AppSettings Normalize(AppSettings s) => s with
    {
        MinFileAgeHours = Math.Clamp(s.MinFileAgeHours, 0, 24 * 365),
        DuplicateMinSizeMB = Math.Clamp(s.DuplicateMinSizeMB, 0, 1024 * 1024),
        AutoCleanIntervalDays = Math.Clamp(s.AutoCleanIntervalDays, 0, 365),
        // JSON null overrides the initializer, so restore empty lists.
        DisabledJunkCategories = s.DisabledJunkCategories ?? [],
        ExcludedPaths = s.ExcludedPaths ?? [],
    };
}
