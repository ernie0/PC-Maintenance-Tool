using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

/// <summary>Task Scheduler 2.0 through its scripting COM API (taskschd.dll); collections are 1-based.</summary>
public sealed class ScheduledTaskProvider : IStartupProvider
{
    private const int TaskEnumHidden = 1;
    private const int TaskActionExec = 0;

    public StartupSource Source => StartupSource.ScheduledTask;
    public ChangeKind Kind => ChangeKind.ScheduledTask;

    public IReadOnlyList<StartupItem> Enumerate()
    {
        var items = new List<StartupItem>();
        var service = Connect();
        Walk(service.GetFolder(@"\"), items);
        return items;
    }

    public Dictionary<string, string> SetEnabled(StartupItem item, bool enabled)
    {
        var path = item.Data["path"];
        var previous = SetTaskEnabled(path, enabled);
        return new Dictionary<string, string> { ["path"] = path, ["previous"] = previous.ToString() };
    }

    public void Restore(IReadOnlyDictionary<string, string> data) => SetTaskEnabled(data["path"], bool.Parse(data["previous"]));

    internal static string FolderOf(string taskPath)
    {
        var index = taskPath.LastIndexOf('\\');
        return index <= 0 ? @"\" : taskPath[..index];
    }

    internal static string NameOf(string taskPath) => taskPath[(taskPath.LastIndexOf('\\') + 1)..];

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("找不到工作排程器");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static bool SetTaskEnabled(string taskPath, bool enabled)
    {
        var service = Connect();
        var task = service.GetFolder(FolderOf(taskPath)).GetTask(NameOf(taskPath));
        bool previous = task.Enabled;
        task.Enabled = enabled;
        return previous;
    }

    private static void Walk(dynamic folder, List<StartupItem> items)
    {
        var tasks = folder.GetTasks(TaskEnumHidden);
        int taskCount = tasks.Count;
        for (var i = 1; i <= taskCount; i++)
        {
            // One unreadable task must not hide the rest.
            try
            {
                items.Add((StartupItem)ToItem(tasks[i]));
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException)
            {
            }
        }

        var folders = folder.GetFolders(0);
        int folderCount = folders.Count;
        for (var i = 1; i <= folderCount; i++)
        {
            try
            {
                Walk(folders[i], items);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static StartupItem ToItem(dynamic task)
    {
        string path = task.Path;
        string name = task.Name;
        bool enabled = task.Enabled;
        var definition = task.Definition;
        // Arguments typed dynamic make the call dynamically bound; convert results back to static types.
        (string Command, string? Exe) action = ReadAction(definition.Actions);
        string triggers = DescribeTriggers(definition.Triggers);
        string author = definition.RegistrationInfo.Author ?? "";
        var publisher = FilePublisher.Get(action.Exe);
        if (publisher.Length == 0) publisher = author;
        return new StartupItem
        {
            Key = "task|" + path,
            Source = StartupSource.ScheduledTask,
            Name = name,
            Command = action.Command,
            ExecutablePath = action.Exe,
            Publisher = publisher,
            Location = "工作排程器 " + FolderOf(path),
            Enabled = enabled,
            RequiresAdmin = true,
            IsMicrosoft = path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || FilePublisher.IsMicrosoft(publisher),
            CanChange = true,
            Note = triggers,
            Data = new Dictionary<string, string> { ["path"] = path },
        };
    }

    private static (string Command, string? Exe) ReadAction(dynamic actions)
    {
        int count = actions.Count;
        for (var i = 1; i <= count; i++)
        {
            var action = actions[i];
            if ((int)action.Type != TaskActionExec) continue;
            string program = action.Path ?? "";
            string arguments = action.Arguments ?? "";
            var quoted = program.StartsWith('"') ? program : $"\"{program}\"";
            return (arguments.Length > 0 ? $"{program} {arguments}" : program, CommandLine.ExtractExecutable(quoted));
        }
        return (count > 0 ? "（非執行程式的動作）" : "", null);
    }

    private static string DescribeTriggers(dynamic triggers)
    {
        var names = new List<string>();
        int count = triggers.Count;
        for (var i = 1; i <= count; i++)
        {
            int type = triggers[i].Type;
            var name = type switch
            {
                0 => "事件觸發",
                1 => "指定時間",
                2 => "每天",
                3 => "每週",
                4 or 5 => "每月",
                6 => "閒置時",
                7 => "建立或修改時",
                8 => "開機時",
                9 => "登入時",
                11 => "工作階段變更時",
                _ => "其他",
            };
            if (!names.Contains(name)) names.Add(name);
        }
        return string.Join("、", names);
    }
}
