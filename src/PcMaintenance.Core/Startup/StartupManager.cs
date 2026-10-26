using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.CSharp.RuntimeBinder;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

public sealed record StartupLoadResult(IReadOnlyList<StartupItem> Items, IReadOnlyList<string> Errors);

public sealed class StartupManager(ChangeJournal journal, IReadOnlyList<IStartupProvider> providers, FileLog? log = null)
{
    public static StartupManager CreateDefault(AppSettings settings, ChangeJournal journal, FileLog? log) =>
        new(journal, [new RunKeyProvider(), new StartupFolderProvider(), new ScheduledTaskProvider(), new ServiceProvider(settings)], log);

    public StartupLoadResult Load()
    {
        var items = new List<StartupItem>();
        var errors = new List<string>();
        foreach (var provider in providers)
        {
            try
            {
                items.AddRange(provider.Enumerate());
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                errors.Add($"{StartupText.SourceName(provider.Source)}讀取失敗：{UserMessage.For(ex)}");
                log?.Error($"讀取{StartupText.SourceName(provider.Source)}失敗", ex);
            }
        }
        var ordered = items
            .OrderBy(i => i.Source)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new StartupLoadResult(ordered, errors);
    }

    public ChangeRecord SetEnabled(StartupItem item, bool enabled)
    {
        if (!item.CanChange) throw new InvalidOperationException("此項目不允許變更");
        var provider = ProviderFor(item.Source);
        var data = provider.SetEnabled(item, enabled);
        return Record(provider.Kind, item, enabled ? "啟用開機啟動" : "停用開機啟動", data);
    }

    public ChangeRecord SetServiceStartType(StartupItem item, ServiceStartType type)
    {
        var provider = (ServiceProvider)ProviderFor(StartupSource.Service);
        var data = provider.SetStartType(item, type);
        // Read after "已" in the status line, so it is phrased as an action.
        return Record(provider.Kind, item, $"將服務啟動類型改為「{StartupText.StartTypeName(type)}」", data);
    }

    public bool CanRestore(ChangeRecord record) => providers.Any(p => p.Kind == record.Kind);

    public void Restore(ChangeRecord record)
    {
        var provider = providers.First(p => p.Kind == record.Kind);
        provider.Restore(record.RestoreData);
        journal.MarkRestored(record.Id);
        log?.Info($"已還原：{record.Description}－{record.Target}");
    }

    /// <summary>Errors a startup change can legitimately raise (permissions, missing items, COM failures).</summary>
    public static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or COMException or
            Win32Exception or InvalidOperationException or RuntimeBinderException;

    private IStartupProvider ProviderFor(StartupSource source) =>
        providers.FirstOrDefault(p => p.Source == source) ?? throw new InvalidOperationException($"沒有{StartupText.SourceName(source)}的處理程式");

    private ChangeRecord Record(ChangeKind kind, StartupItem item, string description, Dictionary<string, string> data)
    {
        var record = new ChangeRecord
        {
            Kind = kind,
            Target = $"{item.Name}（{item.Location}）",
            Description = description,
            RestoreData = data,
        };
        journal.Add(record);
        log?.Info($"{description}：{record.Target}");
        return record;
    }
}
