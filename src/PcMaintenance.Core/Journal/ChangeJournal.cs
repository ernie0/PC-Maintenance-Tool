using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMaintenance.Core.Journal;

public enum ChangeKind
{
    StartupRegistry,
    StartupFolder,
    ScheduledTask,
    ServiceStartType,
    RegistryKeyDeleted,
    FolderRecycled,
    ShortcutRecycled,
    SettingChanged,
    StoreAppRemoved,
}

/// <summary>One change made to the system, with everything needed to undo it.</summary>
public sealed record ChangeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public ChangeKind Kind { get; init; }
    public string Target { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, string> RestoreData { get; init; } = [];
    public bool Restored { get; init; }
    public DateTimeOffset? RestoredTime { get; init; }
}

public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly List<ChangeRecord> _records = [];
    private readonly Lock _gate = new();

    public ChangeJournal(string file)
    {
        _file = file;
        if (!File.Exists(file)) return;
        try
        {
            _records.AddRange(JsonSerializer.Deserialize<List<ChangeRecord>>(File.ReadAllText(file), Options) ?? []);
        }
        catch (JsonException)
        {
            // Keep the unreadable journal for inspection instead of overwriting it with a fresh one.
            File.Move(file, $"{file}.broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
    }

    public IReadOnlyList<ChangeRecord> Records
    {
        get
        {
            lock (_gate) return _records.ToList();
        }
    }

    public void Add(ChangeRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            Save();
        }
    }

    public ChangeRecord MarkRestored(Guid id)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == id);
            if (index < 0) throw new KeyNotFoundException($"找不到變更紀錄 {id}");
            _records[index] = _records[index] with { Restored = true, RestoredTime = DateTimeOffset.Now };
            Save();
            return _records[index];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_records, Options));
        File.Move(temp, _file, overwrite: true);
    }
}
