using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.App.ViewModels;

/// <summary>A choice in the network tab's segment lists; the label is what screen readers read.</summary>
public sealed record NetworkFilter(string Label, Func<ConnectionRow, bool> Matches)
{
    public override string ToString() => Label;
}

public sealed record NetworkSection(string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The network tab of the task manager: which program talks to which address, how much each program sends and
/// receives (elevated only), and the devices on the local network this PC talked to. Everything comes from Windows'
/// own tables and events; nothing is sent unless the user asks for host names.
/// </summary>
public sealed partial class NetworkViewModel : ObservableObject
{
    public static readonly NetworkSection ConnectionsSection = new("連線");
    public static readonly NetworkSection TrafficSection = new("程式流量");
    public static readonly NetworkSection DevicesSection = new("區域網路裝置");
    public static readonly NetworkSection DiagnosisSection = new("網路診斷");

    private readonly AppServices _services;
    private readonly ProcessNames _names;
    private readonly HostNameResolver _hostNames = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<(TransportProtocol, IPEndPoint, IPEndPoint?, int), ConnectionRow> _rows = [];
    private readonly Dictionary<IPAddress, DeviceRow> _devices = [];
    private readonly NetworkFilter _suspiciousFilter = new("可疑連線", r => r.IsSuspicious);
    private SuspiciousListLoad? _suspicious;
    private bool _active;
    private bool _sampling;
    private string? _trafficProblem;

    public NetworkViewModel(AppServices services, ProcessNames names)
    {
        _services = services;
        _names = names;
        Filters =
        [
            new NetworkFilter("全部", _ => true),
            new NetworkFilter("網際網路", r => !r.Info.Connection.IsListening && r.Info.Scope == AddressScope.Internet),
            new NetworkFilter("區域網路", r => !r.Info.Connection.IsListening && r.Info.Scope == AddressScope.LocalNetwork),
            new NetworkFilter("接聽中", r => r.Info.Connection.IsListening),
        ];
        _selectedFilter = Filters[0];
        ConnectionView = new ListCollectionView(Connections) { Filter = item => item is ConnectionRow row && IsShown(row) };
        ConnectionView.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.ProgramName), ListSortDirection.Ascending));
        ConnectionView.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.AddressText), ListSortDirection.Ascending));
        DeviceView = new ListCollectionView(Devices) { Filter = item => item is DeviceRow row && row.Matches(SearchText.Trim()) };
        DeviceView.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.SortKey), ListSortDirection.Ascending));
        Traffic = new ProgramTrafficViewModel(services);
        Diagnosis = new NetworkDiagnosisViewModel(services);
        Diagnosis.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NetworkDiagnosisViewModel.SummaryText) && IsDiagnosis) UpdateTexts();
        };
        Traffic.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProgramTrafficViewModel.SelectedProgram)) OpenLocationCommand.NotifyCanExecuteChanged();
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public IReadOnlyList<NetworkSection> Sections { get; } = [ConnectionsSection, TrafficSection, DevicesSection, DiagnosisSection];
    public ProgramTrafficViewModel Traffic { get; }
    public NetworkDiagnosisViewModel Diagnosis { get; }
    public ObservableCollection<NetworkFilter> Filters { get; }
    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ListCollectionView ConnectionView { get; }
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ListCollectionView DeviceView { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevices), nameof(IsConnections), nameof(IsTraffic), nameof(IsDiagnosis), nameof(HasAddresses),
        nameof(CanShowLocation), nameof(HasSearch), nameof(SearchHint))]
    [NotifyCanExecuteChangedFor(nameof(OpenLocationCommand))]
    private NetworkSection _selectedSection = ConnectionsSection;

    [ObservableProperty]
    private NetworkFilter _selectedFilter;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Also lists traffic inside this PC and connections whose program already closed them; both are hidden by default as noise.</summary>
    [ObservableProperty]
    private bool _showInternalAndClosed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText), nameof(DetailTooltip), nameof(WarningText))]
    [NotifyCanExecuteChangedFor(nameof(OpenLocationCommand))]
    private ConnectionRow? _selectedConnection;

    [ObservableProperty]
    private string _countText = "正在讀取…";

    [ObservableProperty]
    private string _hiddenToggleText = "顯示本機內部與已結束的連線";

    [ObservableProperty]
    private string _noteText = "";

    [ObservableProperty]
    private string _statusText = "";

    public bool IsDevices => SelectedSection == DevicesSection;
    public bool IsTraffic => SelectedSection == TrafficSection;
    public bool IsConnections => SelectedSection == ConnectionsSection;
    public bool IsDiagnosis => SelectedSection == DiagnosisSection;
    /// <summary>Host names and the export apply to lists of addresses: connections and devices.</summary>
    public bool HasAddresses => IsConnections || IsDevices;
    public bool CanShowLocation => IsConnections || IsTraffic;
    public bool HasSearch => !IsDiagnosis;
    public string SearchHint => IsDevices ? "搜尋 IP、實體位址或名稱" : IsTraffic ? "搜尋程式" : "搜尋程式、位址、服務或名稱";

    public string DetailText => SelectedConnection is not { } row ? "" :
        (row.Info.IsClosed ? "程式已關閉的連線（Windows 會在幾分鐘內自動清除）" : $"{row.ProgramName}（處理程序 {row.ProcessId}）") +
        $"　{row.ProtocolText}　本機 {row.LocalText}　" +
        (row.Info.Connection.Remote is null ? "等待連入" : $"遠端 {row.AddressText}") +
        (row.HostName.Length > 0 ? $"（{row.HostName}）" : "") + $"　{ConnectionText.Service(row.Info.Service)}　{row.ScopeText}";

    /// <summary>The detail line in full, with the program's path; the line itself is cut to fit small windows.</summary>
    public string DetailTooltip => SelectedConnection?.Info.ProgramPath is { } path ? $"{DetailText}\n{path}" : DetailText;

    public string WarningText => SelectedConnection is { IsSuspicious: true }
        ? "這個遠端位址列在公司的可疑 IP 清單中，請確認這個程式是否應該連到這裡；需要時到「處理程序」結束它。"
        : "";

    partial void OnSelectedSectionChanged(NetworkSection value)
    {
        // A lookup or export result is about the view it was made in.
        StatusText = "";
        UpdateTraffic();
        UpdateTexts();
        _ = RefreshAsync();
    }

    partial void OnSelectedFilterChanged(NetworkFilter value) => ConnectionView.Refresh();
    partial void OnShowInternalAndClosedChanged(bool value)
    {
        ConnectionView.Refresh();
        UpdateTexts();
    }

    partial void OnSearchTextChanged(string value)
    {
        ConnectionView.Refresh();
        DeviceView.Refresh();
        Traffic.ApplySearch(value);
    }

    /// <summary>Samples every 2 seconds while the tab is on screen; the company's suspicious list is read again each time it opens.</summary>
    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        UpdateTraffic();
        if (!active)
        {
            _timer.Stop();
            return;
        }
        _suspicious = null;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Traffic is counted only while its view is on screen, so the ETW session never outlives it.</summary>
    private void UpdateTraffic()
    {
        if (_active && IsTraffic) _trafficProblem = Traffic.Start();
        else Traffic.Stop();
    }

    [RelayCommand]
    private async Task LookupHostNamesAsync()
    {
        IReadOnlyList<IPAddress> addresses = IsDevices
            ? DeviceView.Cast<DeviceRow>().Select(d => d.Address).ToList()
            : ConnectionView.Cast<ConnectionRow>().Select(c => c.Info.RemoteAddress).OfType<IPAddress>().ToList();
        var total = _hostNames.CountPending(addresses);
        StatusText = $"正在向 DNS 伺服器查詢主機名稱（0／{total:N0}）…";
        var progress = new Progress<int>(done =>
        {
            StatusText = $"正在向 DNS 伺服器查詢主機名稱（{done:N0}／{total:N0}）…";
            ShowHostNames();
        });
        var asked = await _hostNames.ResolveAsync(addresses, progress);
        ShowHostNames();
        var found = addresses.Select(_hostNames.Get).Count(n => !string.IsNullOrEmpty(n));
        StatusText = asked == 0 && found == 0
            ? "畫面上沒有需要查詢的網際網路或區域網路位址。"
            : $"已向 DNS 伺服器查詢 {asked:N0} 個位址，畫面上有 {found:N0} 個找到主機名稱。";
    }

    [RelayCommand]
    private void Export()
    {
        var kind = IsDevices ? InventoryExport.DevicesKind : InventoryExport.ConnectionsKind;
        if (Dialogs.AskCsvPath(kind) is not { } path) return;
        try
        {
            var count = IsDevices
                ? InventoryExport.WriteDevices(path, Devices.Select(d => d.Device), Environment.MachineName, _hostNames.Get)
                : InventoryExport.WriteConnections(path, Connections.Select(c => c.Info), Environment.MachineName, _hostNames.Get);
            StatusText = $"已匯出 {count:N0} 筆{kind}：{path}";
            _services.Log.Info($"匯出{kind}：{count} 筆，{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出{kind}：{UserMessage.For(ex)}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation()
    {
        if (SelectedProgramPath is { } path) Shell.ShowInExplorer(path);
    }

    private bool CanOpenLocation() => SelectedProgramPath is not null;

    private string? SelectedProgramPath => IsConnections ? SelectedConnection?.Info.ProgramPath : IsTraffic ? Traffic.SelectedProgram?.Path : null;

    private bool IsShown(ConnectionRow row) =>
        (ShowInternalAndClosed || !IsBackground(row)) && SelectedFilter.Matches(row) && row.Matches(SearchText.Trim());

    private static bool IsBackground(ConnectionRow row) => row.Info.IsLoopbackOnly || row.Info.IsClosed;

    private async Task RefreshAsync()
    {
        if (_sampling || !_active || IsDiagnosis) return;
        _sampling = true;
        try
        {
            var devices = IsDevices;
            var traffic = IsTraffic;
            var settings = _services.Settings;
            var snapshot = await Task.Run(() =>
            {
                var suspicious = _suspicious ?? SuspiciousAddressList.Load(settings);
                var processes = ProcessSnapshot.ReadAll();
                var connections = ConnectionInventory.Describe(ConnectionTable.Read(), processes, _names, suspicious.List);
                var counts = traffic ? Traffic.Snapshot() : null;
                var identities = counts is null ? null : processes.Where(p => counts.ContainsKey(p.Id)).ToDictionary(p => p.Id, _names.Get);
                return (suspicious, connections, devices ? NeighborTable.Read() : null, counts, identities);
            });
            _suspicious = snapshot.suspicious;
            ApplyConnections(snapshot.connections);
            if (snapshot.Item3 is { } neighbors) ApplyDevices(neighbors);
            if (IsTraffic && snapshot.counts is { } counts && snapshot.identities is { } identities) Traffic.Apply(counts, identities);
            UpdateTexts();
        }
        catch (Win32Exception ex)
        {
            StatusText = $"無法讀取網路連線：{UserMessage.For(ex)}";
        }
        finally
        {
            _sampling = false;
        }
    }

    private void ApplyConnections(IReadOnlyList<ConnectionInfo> infos)
    {
        var hasList = _suspicious?.List is not null;
        if (hasList != Filters.Contains(_suspiciousFilter))
        {
            if (hasList) Filters.Add(_suspiciousFilter);
            else
            {
                if (SelectedFilter == _suspiciousFilter) SelectedFilter = Filters[0];
                Filters.Remove(_suspiciousFilter);
            }
        }

        var seen = new HashSet<(TransportProtocol, IPEndPoint, IPEndPoint?, int)>();
        foreach (var info in infos)
        {
            var c = info.Connection;
            var key = (c.Protocol, c.Local, c.Remote, c.ProcessId);
            if (!seen.Add(key)) continue;
            if (_rows.TryGetValue(key, out var row)) row.Info = info;
            else
            {
                row = new ConnectionRow(info) { HostName = HostNameText(info.RemoteAddress) };
                _rows[key] = row;
                Connections.Add(row);
            }
        }
        foreach (var (key, row) in _rows.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _rows.Remove(key);
            Connections.Remove(row);
        }
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(DetailTooltip));
        OnPropertyChanged(nameof(WarningText));
    }

    private void ApplyDevices(IReadOnlyList<NeighborDevice> devices)
    {
        var seen = new HashSet<IPAddress>();
        foreach (var device in devices)
        {
            if (!seen.Add(device.Address)) continue;
            if (_devices.TryGetValue(device.Address, out var row)) row.Device = device;
            else
            {
                row = new DeviceRow(device) { HostName = HostNameText(device.Address) };
                _devices[device.Address] = row;
                Devices.Add(row);
            }
        }
        foreach (var (address, row) in _devices.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _devices.Remove(address);
            Devices.Remove(row);
        }
    }

    private void ShowHostNames()
    {
        foreach (var row in Connections) row.HostName = HostNameText(row.Info.RemoteAddress);
        foreach (var row in Devices) row.HostName = HostNameText(row.Address);
        OnPropertyChanged(nameof(DetailText));
    }

    private string HostNameText(IPAddress? address) => address is null ? "" : _hostNames.Get(address) switch
    {
        null => "",
        "" => "（查不到名稱）",
        var name => name,
    };

    private void UpdateTexts()
    {
        if (IsDiagnosis)
        {
            CountText = Diagnosis.SummaryText;
            NoteText = NetworkDiagnosisViewModel.Explanation;
            return;
        }
        if (IsTraffic)
        {
            CountText = Traffic.SummaryText;
            NoteText = _trafficProblem ?? Traffic.StatusText;
            return;
        }
        if (IsDevices)
        {
            CountText = $"裝置 {Devices.Count:N0} 台";
            NoteText = "只列出這台電腦最近通訊過的裝置（Windows 的 ARP 表），不會主動掃描網路。";
            return;
        }
        var listed = Connections.Where(r => ShowInternalAndClosed || !IsBackground(r)).ToList();
        var internet = listed.Count(r => !r.Info.Connection.IsListening && r.Info.Scope == AddressScope.Internet);
        var local = listed.Count(r => !r.Info.Connection.IsListening && r.Info.Scope == AddressScope.LocalNetwork);
        var listening = listed.Count(r => r.Info.Connection.IsListening);
        var suspicious = listed.Count(r => r.IsSuspicious);
        CountText = $"連線 {listed.Count:N0} 條：網際網路 {internet:N0}、區域網路 {local:N0}、接聽中 {listening:N0}" +
                    (_suspicious?.List is null ? "" : $"、可疑 {suspicious:N0}");
        var hidden = Connections.Count(IsBackground);
        HiddenToggleText = hidden == 0 ? "顯示本機內部與已結束的連線" : $"顯示本機內部與已結束的連線（{hidden:N0} 條）";
        NoteText = _suspicious switch
        {
            { Error: { } error } => error,
            { List: { } list, Path: var path } => $"可疑 IP 清單：{list.Count:N0} 筆（{path}）" +
                                                 (list.RejectedLines > 0 ? $"，{list.RejectedLines:N0} 行無法辨識" : ""),
            _ => "",
        };
    }
}

public sealed partial class ConnectionRow(ConnectionInfo info) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgramName), nameof(StateText), nameof(ScopeText), nameof(ServiceText), nameof(IsSuspicious), nameof(Tooltip))]
    private ConnectionInfo _info = info;

    [ObservableProperty]
    private string _hostName = "";

    public int ProcessId => Info.Connection.ProcessId;
    public string ProgramName => Info.ProgramName;
    public string ProtocolText => ConnectionText.Protocol(Info.Connection.Protocol);
    public string LocalText => ConnectionText.Endpoint(Info.Connection.Local);
    public string AddressText => ConnectionText.Address(Info.Connection);
    public string StateText => ConnectionText.State(Info.Connection);
    public string ScopeText => ConnectionText.Scope(Info);
    /// <summary>Just what the service is for ("加密網頁"); the protocol name is in the detail line and the export.</summary>
    public string ServiceText => Info.Service?.Description ?? "—";
    public bool IsSuspicious => Info.IsSuspicious;

    public string Tooltip => Info.ProgramPath ?? (ProcessId == 0 ? "程式已關閉這條連線，Windows 會在一段時間後自動清除。" : ProgramName);

    public bool Matches(string text) =>
        text.Length == 0 ||
        ProgramName.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        AddressText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        LocalText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        HostName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        ServiceText.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        (Info.Service?.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
        ProcessId.ToString().Contains(text, StringComparison.Ordinal);
}

public sealed partial class DeviceRow(NeighborDevice device) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mac), nameof(AdapterName), nameof(RoleText))]
    private NeighborDevice _device = device;

    [ObservableProperty]
    private string _hostName = "";

    public IPAddress Address => Device.Address;
    public string AddressText => Device.Address.ToString();
    /// <summary>Sorts 192.168.1.9 before 192.168.1.10.</summary>
    public uint SortKey => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Device.Address.GetAddressBytes());
    public string Mac => Device.Mac;
    public string AdapterName => Device.AdapterName;
    public string RoleText => Device.IsGateway ? "預設閘道（路由器）" : Device.IsStatic ? "手動設定的位址" : "";

    public bool Matches(string text) =>
        text.Length == 0 ||
        AddressText.Contains(text, StringComparison.Ordinal) ||
        Mac.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        HostName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        AdapterName.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}
