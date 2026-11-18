using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DualNicRouteTool.Models;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.ViewModels;

/// <summary>主窗口的全部状态与操作。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 2000;

    private static readonly string[] ApplyStepLabels = ["备份", "外网路由", "内网路由", "清理冲突", "守护", "校验"];
    private static readonly string[] RestoreStepLabels = ["读取备份", "恢复路由", "校验"];

    private readonly IRouteTable _table;
    private readonly IBackupStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly RouteExecutor _executor;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _routeTimer;
    private readonly DispatcherTimer _networkChangeTimer;

    private IReadOnlyList<RouteEntry> _routes = [];
    private RouteSnapshot? _activeSnapshot;
    private CancellationTokenSource? _guardCts;
    private bool _suppressSelection;
    private bool _readingRoutes;

    public MainViewModel(IRouteTable table, IBackupStore store, SettingsStore settingsStore, IDialogService dialogs)
    {
        _table = table;
        _store = store;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _executor = new RouteExecutor(table, store, Log);
        _settings = settingsStore.Load();

        IsAdmin = AppEnvironment.IsAdministrator();
        _guardRounds = Math.Clamp(_settings.GuardRounds, 0, 20);
        _guardIntervalSeconds = Math.Clamp(_settings.GuardIntervalSeconds, 0.5, 10);
        _persistentGuard = _settings.PersistentGuard;
        _persistentGuardIntervalSeconds = Math.Clamp(_settings.PersistentGuardIntervalSeconds, 2, 60);

        foreach (var text in _settings.Networks)
        {
            var parsed = Ipv4Network.Parse(text);
            if (parsed.Success && Networks.All(n => n.Network != parsed.Network))
                Networks.Add(new NetworkChipViewModel(parsed.Network));
        }

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy && !IsRefreshing);
        SwapCommand = new RelayCommand(Swap, () => !IsBusy && (SelectedWan is not null || SelectedLan is not null));
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => CanApply);
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(null), () => IsAdmin && !IsBusy && HasRestorable);
        RestoreSnapshotCommand = new RelayCommand(
            p => _ = RestoreAsync((p as SnapshotViewModel)?.Snapshot),
            p => IsAdmin && !IsBusy && p is SnapshotViewModel { CanRestore: true });
        DeleteSnapshotCommand = new RelayCommand(p => DeleteSnapshot(p as SnapshotViewModel), _ => !IsBusy);
        AddNetworkCommand = new RelayCommand(AddNetwork, () => !IsBusy);
        RemoveNetworkCommand = new RelayCommand(p => RemoveNetwork(p as NetworkChipViewModel), _ => !IsBusy);
        ResetNetworksCommand = new RelayCommand(ResetNetworks, () => !IsBusy);
        CopyLogCommand = new RelayCommand(CopyLog, () => Logs.Count > 0);
        ClearLogCommand = new RelayCommand(Logs.Clear, () => Logs.Count > 0);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        HelpCommand = new RelayCommand(_dialogs.ShowHelp);
        DiagnosticsCommand = new RelayCommand(() => _ = ShowDiagnosticsAsync());

        // 路由表每 3 秒读一次（只读、开销很小），拓扑图和列表因此始终反映真实状态
        _routeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _routeTimer.Tick += (_, _) => _ = ReadRoutesAsync();

        // 网卡插拔、地址变化会连发多个事件，合并为 1 秒后刷新一次
        _networkChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _networkChangeTimer.Tick += (_, _) =>
        {
            _networkChangeTimer.Stop();
            if (!IsBusy) _ = RefreshAsync(silent: true);
        };
    }

    // ========================================================================
    // 事件
    // ========================================================================

    /// <summary>一次配置或恢复结束，参数为是否成功。视图用它播放结果动效。</summary>
    public event Action<bool>? OperationCompleted;

    /// <summary>内外网已对调。视图用它播放交换动效。</summary>
    public event Action? Swapped;

    /// <summary>追加了日志。视图用它滚动到末尾。</summary>
    public event Action? LogAppended;

    // ========================================================================
    // 命令
    // ========================================================================

    public ICommand RefreshCommand { get; }
    public ICommand SwapCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestoreSnapshotCommand { get; }
    public ICommand DeleteSnapshotCommand { get; }
    public ICommand AddNetworkCommand { get; }
    public ICommand RemoveNetworkCommand { get; }
    public ICommand ResetNetworksCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand DiagnosticsCommand { get; }

    // ========================================================================
    // 集合
    // ========================================================================

    public ObservableCollection<NicInfo> Interfaces { get; } = [];
    public ObservableCollection<NetworkChipViewModel> Networks { get; } = [];
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<RouteRowViewModel> Routes { get; } = [];
    public ObservableCollection<SnapshotViewModel> Snapshots { get; } = [];

    // ========================================================================
    // 基本状态
    // ========================================================================

    public string VersionText => $"v{AppEnvironment.Version}";

    public bool IsAdmin { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanApply));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsBusy;

    private string _busyText = string.Empty;
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    private bool _showSteps;
    /// <summary>是否显示分步进度（操作进行中，以及结束后保留到下一次操作）。</summary>
    public bool ShowSteps
    {
        get => _showSteps;
        private set
        {
            if (SetProperty(ref _showSteps, value)) OnPropertyChanged(nameof(ShowBlockReason));
        }
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    // ========================================================================
    // 网卡与网关
    // ========================================================================

    private NicInfo? _selectedWan;
    public NicInfo? SelectedWan
    {
        get => _selectedWan;
        set
        {
            // 列表重建期间下拉框会把选中项清空再写回来，这些中间值一律忽略
            if (_suppressSelection || Equals(_selectedWan, value)) return;
            _selectedWan = value;
            OnPropertyChanged();
            WanGateway = ResolveGateway(value);
            UpdateStatus();
        }
    }

    private NicInfo? _selectedLan;
    public NicInfo? SelectedLan
    {
        get => _selectedLan;
        set
        {
            if (_suppressSelection || Equals(_selectedLan, value)) return;
            _selectedLan = value;
            OnPropertyChanged();
            LanGateway = ResolveGateway(value);
            UpdateStatus();
        }
    }

    private string _wanGateway = string.Empty;
    public string WanGateway
    {
        get => _wanGateway;
        set
        {
            if (SetProperty(ref _wanGateway, value ?? string.Empty)) UpdateStatus();
        }
    }

    private string _lanGateway = string.Empty;
    public string LanGateway
    {
        get => _lanGateway;
        set
        {
            if (SetProperty(ref _lanGateway, value ?? string.Empty)) UpdateStatus();
        }
    }

    private string? _wanGatewayError;
    public string? WanGatewayError
    {
        get => _wanGatewayError;
        private set => SetProperty(ref _wanGatewayError, value);
    }

    private string? _lanGatewayError;
    public string? LanGatewayError
    {
        get => _lanGatewayError;
        private set => SetProperty(ref _lanGatewayError, value);
    }

    // ========================================================================
    // 目标网段
    // ========================================================================

    private string _newNetworkText = string.Empty;
    public string NewNetworkText
    {
        get => _newNetworkText;
        set
        {
            if (SetProperty(ref _newNetworkText, value ?? string.Empty)) NewNetworkError = null;
        }
    }

    private string? _newNetworkError;
    public string? NewNetworkError
    {
        get => _newNetworkError;
        private set => SetProperty(ref _newNetworkError, value);
    }

    // ========================================================================
    // 生效情况（来自实际路由表）
    // ========================================================================

    private PathState _wanState;
    public PathState WanState
    {
        get => _wanState;
        private set => SetProperty(ref _wanState, value);
    }

    private PathState _lanState;
    public PathState LanState
    {
        get => _lanState;
        private set => SetProperty(ref _lanState, value);
    }

    private string _wanDetail = string.Empty;
    public string WanDetail
    {
        get => _wanDetail;
        private set => SetProperty(ref _wanDetail, value);
    }

    private string _lanDetail = string.Empty;
    public string LanDetail
    {
        get => _lanDetail;
        private set => SetProperty(ref _lanDetail, value);
    }

    private string _wanCaption = string.Empty;
    public string WanCaption
    {
        get => _wanCaption;
        private set => SetProperty(ref _wanCaption, value);
    }

    private string _lanCaption = string.Empty;
    public string LanCaption
    {
        get => _lanCaption;
        private set => SetProperty(ref _lanCaption, value);
    }

    private string? _blockReason;
    /// <summary>当前不能执行“一键配置”的原因；可以执行时为 null。</summary>
    public string? BlockReason
    {
        get => _blockReason;
        private set
        {
            if (!SetProperty(ref _blockReason, value)) return;
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(ShowBlockReason));
        }
    }

    public bool ShowBlockReason => !ShowSteps && !string.IsNullOrEmpty(BlockReason);

    public bool CanApply => !IsBusy && BlockReason is null;

    private bool _showAllRoutes;
    public bool ShowAllRoutes
    {
        get => _showAllRoutes;
        set
        {
            if (SetProperty(ref _showAllRoutes, value)) RebuildRouteRows();
        }
    }

    private string _routeSummary = string.Empty;
    public string RouteSummary
    {
        get => _routeSummary;
        private set => SetProperty(ref _routeSummary, value);
    }

    private bool _hasRestorable;
    public bool HasRestorable
    {
        get => _hasRestorable;
        private set => SetProperty(ref _hasRestorable, value);
    }

    public bool HasSnapshots => Snapshots.Count > 0;

    public string BackupDirectory => _store.Directory;

    // ========================================================================
    // 守护设置
    // ========================================================================

    private int _guardRounds;
    public int GuardRounds
    {
        get => _guardRounds;
        set
        {
            if (SetProperty(ref _guardRounds, Math.Clamp(value, 0, 20))) SaveSettings();
        }
    }

    private double _guardIntervalSeconds;
    public double GuardIntervalSeconds
    {
        get => _guardIntervalSeconds;
        set
        {
            if (SetProperty(ref _guardIntervalSeconds, Math.Clamp(value, 0.5, 10))) SaveSettings();
        }
    }

    private bool _persistentGuard;
    public bool PersistentGuard
    {
        get => _persistentGuard;
        set
        {
            if (!SetProperty(ref _persistentGuard, value)) return;
            SaveSettings();
            if (value) StartPersistentGuard();
            else StopPersistentGuard(log: true);
            OnPropertyChanged(nameof(GuardStatusText));
        }
    }

    private int _persistentGuardIntervalSeconds;
    public int PersistentGuardIntervalSeconds
    {
        get => _persistentGuardIntervalSeconds;
        set
        {
            if (SetProperty(ref _persistentGuardIntervalSeconds, Math.Clamp(value, 2, 60))) SaveSettings();
        }
    }

    private bool _isGuardRunning;
    public bool IsGuardRunning
    {
        get => _isGuardRunning;
        private set
        {
            if (SetProperty(ref _isGuardRunning, value)) OnPropertyChanged(nameof(GuardStatusText));
        }
    }

    private int _guardCleanedCount;

    public string GuardStatusText =>
        IsGuardRunning ? $"持续守护中 · 已清理 {_guardCleanedCount} 条"
        : PersistentGuard ? "持续守护待命"
        : "持续守护未开启";

    // ========================================================================
    // 启动与刷新
    // ========================================================================

    public async Task InitializeAsync()
    {
        Log(LogLevel.Info, $"{AppEnvironment.AppName} v{AppEnvironment.Version} 已启动");
        if (!IsAdmin) Log(LogLevel.Warning, "当前没有管理员权限：可以查看网卡与路由，但无法配置或恢复。");

        await RefreshAsync(initial: true);

        _routeTimer.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        if (PersistentGuard) StartPersistentGuard();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            _networkChangeTimer.Stop();
            _networkChangeTimer.Start();
        });

    /// <summary>重新读取网卡、路由表和备份。</summary>
    private async Task RefreshAsync(bool initial = false, bool silent = false)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;

        // 读取本身只要几十毫秒；让刷新图标至少转半秒，用户才看得出确实刷新过
        var minimumSpin = Task.Delay(silent ? 0 : 500);
        try
        {
            var (nics, routes, snapshots) = await Task.Run(() =>
                (NetworkInterfaceService.GetInterfaces(), _table.GetRoutes(), _store.LoadAll()));

            ApplySnapshots(snapshots);
            _routes = routes;
            ApplyInterfaces(nics, initial);
            UpdateStatus();

            if (!silent) Log(LogLevel.Info, $"发现 {nics.Count} 块已连接的网卡，路由表共 {routes.Count} 条 IPv4 路由");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"读取网卡或路由表失败：{ex.Message}");
        }

        await minimumSpin;
        IsRefreshing = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ReadRoutesAsync()
    {
        if (_readingRoutes) return;
        _readingRoutes = true;
        try
        {
            var routes = await Task.Run(_table.GetRoutes);
            if (!routes.SequenceEqual(_routes))
            {
                _routes = routes;
                UpdateStatus();
            }
        }
        catch (Exception)
        {
            // 定时读取失败不打扰用户，下一轮再试
        }
        finally
        {
            _readingRoutes = false;
        }
    }

    private void ApplyInterfaces(IReadOnlyList<NicInfo> nics, bool initial)
    {
        var previousWan = _selectedWan;
        var previousLan = _selectedLan;
        var wanId = previousWan?.Id ?? (initial ? _settings.WanInterfaceId : null);
        var lanId = previousLan?.Id ?? (initial ? _settings.LanInterfaceId : null);

        // 网卡没有任何变化时不重建列表，避免下拉框无谓地闪动
        if (!nics.SequenceEqual(Interfaces))
        {
            _suppressSelection = true;
            try
            {
                Interfaces.Clear();
                foreach (var nic in nics) Interfaces.Add(nic);
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        // 按网卡 GUID 找回之前的选择；首次启动且没有保存过选择时自动预选
        var wan = nics.FirstOrDefault(n => n.Id == wanId);
        var lan = nics.FirstOrDefault(n => n.Id == lanId);
        if (initial)
        {
            wan ??= GuessWan(nics, lan);
            lan ??= GuessLan(nics, wan);
        }

        _selectedWan = wan;
        _selectedLan = lan;
        OnPropertyChanged(nameof(SelectedWan));
        OnPropertyChanged(nameof(SelectedLan));

        // 网卡没变且已填过网关：保留用户填写的内容；否则重新取网关
        if (wan?.Id != previousWan?.Id || string.IsNullOrWhiteSpace(_wanGateway)) SetGatewayQuietly(ref _wanGateway, ResolveGateway(wan), nameof(WanGateway));
        if (lan?.Id != previousLan?.Id || string.IsNullOrWhiteSpace(_lanGateway)) SetGatewayQuietly(ref _lanGateway, ResolveGateway(lan), nameof(LanGateway));
    }

    private void SetGatewayQuietly(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    /// <summary>外网默认选当前跃点最低的默认路由所在的网卡。</summary>
    private NicInfo? GuessWan(IReadOnlyList<NicInfo> nics, NicInfo? exclude)
    {
        var candidates = nics.Where(n => n.Id != exclude?.Id).ToList();
        var best = _routes
            .Where(r => r.IsDefault)
            .Select(r => (Route: r, Nic: candidates.FirstOrDefault(n => n.Index == r.InterfaceIndex)))
            .Where(x => x.Nic is not null)
            .OrderBy(x => x.Route.Metric + (x.Nic!.InterfaceMetric ?? 0))
            .Select(x => x.Nic)
            .FirstOrDefault();
        return best ?? candidates.FirstOrDefault(n => n.Gateway is not null);
    }

    /// <summary>内网默认选另一块实体网卡；虚拟网卡、VPN、蓝牙不自动选中。</summary>
    private static NicInfo? GuessLan(IReadOnlyList<NicInfo> nics, NicInfo? wan) =>
        nics.FirstOrDefault(n => n.Id != wan?.Id && n.Kind is NicKind.Ethernet or NicKind.WiFi or NicKind.Cellular);

    /// <summary>
    /// 取网卡的网关：优先用系统当前上报的；没有则用上次保存的，再没有就从备份里找这块网卡原来的默认路由。
    /// （配置后内网默认路由被删除，系统不再上报内网网关，必须靠后两者找回。）
    /// </summary>
    private string ResolveGateway(NicInfo? nic)
    {
        if (nic is null) return string.Empty;
        if (!string.IsNullOrEmpty(nic.Gateway)) return nic.Gateway;
        if (_settings.Gateways.TryGetValue(nic.Id, out var saved) && Ipv4.TryParseGateway(saved, out _)) return saved;

        var original = _activeSnapshot?.Original.FirstOrDefault(r => r.IsDefault && !r.IsOnLink && r.IsOn(nic.Index, nic.Luid));
        return original?.NextHop ?? string.Empty;
    }

    // ========================================================================
    // 状态计算
    // ========================================================================

    private RouteRequest? BuildRequest()
    {
        if (SelectedWan is null || SelectedLan is null) return null;
        return new RouteRequest(
            SelectedWan.ToEndpoint(WanGateway),
            SelectedLan.ToEndpoint(LanGateway),
            Networks.Select(n => n.Network).ToList());
    }

    /// <summary>根据当前选择与实际路由表，更新拓扑图、网段标签、路由列表和校验提示。</summary>
    private void UpdateStatus()
    {
        var wan = SelectedWan?.ToEndpoint(WanGateway);
        var lan = SelectedLan?.ToEndpoint(LanGateway);
        var networks = Networks.Select(n => n.Network).ToList();
        var status = RouteStatusEvaluator.Evaluate(wan, lan, networks, _routes);

        WanState = status.Wan;
        WanDetail = status.WanDetail;
        LanState = status.Lan;
        LanDetail = status.LanDetail;
        WanCaption = SelectedWan is null ? "未选择" : string.IsNullOrWhiteSpace(WanGateway) ? SelectedWan.Name : $"{SelectedWan.Name} · {WanGateway.Trim()}";
        LanCaption = SelectedLan is null ? "未选择" : string.IsNullOrWhiteSpace(LanGateway) ? SelectedLan.Name : $"{SelectedLan.Name} · {LanGateway.Trim()}";

        foreach (var chip in Networks)
        {
            chip.IsActive = status.NetworkActive.GetValueOrDefault(chip.Network);
            chip.ToolTip = chip.IsActive
                ? $"已生效：{chip.Text} 经 {SelectedLan?.Name} 走内网"
                : $"尚未生效：路由表里没有 {chip.Text} 指向内网网卡的路由";
        }

        Validate();
        RebuildRouteRows();
    }

    private void Validate()
    {
        // 外网网关可以留空，前提是该网卡上已有在链路上的默认路由（拨号连接）
        string? wanError = null;
        if (SelectedWan is not null)
        {
            if (string.IsNullOrWhiteSpace(WanGateway))
            {
                var hasOnLinkDefault = _routes.Any(r => r.IsDefault && r.IsOnLink && r.IsOn(SelectedWan.Index, SelectedWan.Luid));
                if (!hasOnLinkDefault) wanError = "未检测到网关，请手动填写";
            }
            else if (!Ipv4.TryParseGateway(WanGateway, out _))
            {
                wanError = "不是有效的 IPv4 地址";
            }
        }

        string? lanError = null;
        if (SelectedLan is not null)
        {
            if (string.IsNullOrWhiteSpace(LanGateway)) lanError = "未检测到网关，请手动填写";
            else if (!Ipv4.TryParseGateway(LanGateway, out _)) lanError = "不是有效的 IPv4 地址";
        }

        WanGatewayError = wanError;
        LanGatewayError = lanError;

        BlockReason =
            !IsAdmin ? "需要管理员权限才能修改路由"
            : SelectedWan is null ? "请选择外网网卡"
            : SelectedLan is null ? "请选择内网网卡"
            : SelectedWan.Id == SelectedLan.Id ? "外网和内网不能是同一块网卡"
            : wanError is not null ? $"外网网关：{wanError}"
            : lanError is not null ? $"内网网关：{lanError}"
            : Networks.Count == 0 ? "请至少添加一个内网目标网段"
            : null;

        CommandManager.InvalidateRequerySuggested();
    }

    private void RebuildRouteRows()
    {
        var networks = Networks.Select(n => n.Network).ToList();
        var added = _activeSnapshot?.Added ?? [];

        var rows = new List<RouteRowViewModel>();
        foreach (var route in _routes)
        {
            var isTarget = !route.IsOnLink && networks.Any(route.HasPrefix);
            var addedByTool = added.Any(a => a.SameRoute(route));
            if (!ShowAllRoutes && !route.IsDefault && !isTarget && !addedByTool) continue;

            var role = SelectedWan is not null && route.IsOn(SelectedWan.Index, SelectedWan.Luid) ? "Wan"
                : SelectedLan is not null && route.IsOn(SelectedLan.Index, SelectedLan.Luid) ? "Lan"
                : string.Empty;
            var kind = route.IsDefault ? "默认路由" : isTarget ? "内网网段" : route.IsOnLink ? "本地" : string.Empty;

            rows.Add(new RouteRowViewModel(
                route.Prefix, route.NextHopText, route.InterfaceText, route.Metric.ToString(),
                DiagnosticsReport.OriginText(route.Origin), role, kind, addedByTool));
        }

        var ordered = rows
            .OrderByDescending(r => r.Kind == "默认路由")
            .ThenByDescending(r => r.Kind == "内网网段")
            .ThenBy(r => Ipv4.TryParse(r.Prefix.Split('/')[0], out var ip) ? ip : 0)
            .ThenBy(r => r.Prefix.Length)
            .ToList();

        // 内容没变就不动列表，避免每 3 秒闪一下
        if (!ordered.SequenceEqual(Routes))
        {
            Routes.Clear();
            foreach (var row in ordered) Routes.Add(row);
        }

        RouteSummary = ShowAllRoutes
            ? $"全部 IPv4 路由，共 {_routes.Count} 条"
            : $"默认路由与目标网段相关路由 {ordered.Count} 条（路由表共 {_routes.Count} 条）";
    }

    private void ApplySnapshots(IReadOnlyList<RouteSnapshot> snapshots)
    {
        var boot = AppEnvironment.BootTimeUtc;
        _activeSnapshot = snapshots.FirstOrDefault(s => s.IsRestorable && !s.IsStale(boot));
        HasRestorable = _activeSnapshot is not null;

        Snapshots.Clear();
        foreach (var snapshot in snapshots) Snapshots.Add(new SnapshotViewModel(snapshot, boot));
        OnPropertyChanged(nameof(HasSnapshots));
        CommandManager.InvalidateRequerySuggested();
    }

    // ========================================================================
    // 一键配置
    // ========================================================================

    private async Task ApplyAsync()
    {
        var request = BuildRequest();
        if (!CanApply || request is null) return;

        ApplyContext context;
        try
        {
            context = await Task.Run(() => _executor.Prepare(request));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"读取路由表失败：{ex.Message}");
            _dialogs.Show("无法读取路由表", ex.Message, DialogKind.Error);
            return;
        }

        if (!context.Plan.IsValid)
        {
            _dialogs.Show("无法配置", string.Join("\n", context.Plan.Errors), DialogKind.Warning);
            return;
        }

        if (!context.Plan.HasChanges)
        {
            Log(LogLevel.Success, "当前路由表已符合所选配置，无需修改。");
            _dialogs.Show("无需修改", "当前路由表已经符合所选配置，没有需要新增或删除的路由。");
            RememberSelection();
            return;
        }

        var preview = new PlanPreviewModel(request, context.Plan, context.Active is not null, SystemParameters.IsRemoteSession);
        if (!_dialogs.ConfirmPlan(preview)) return;

        StopPersistentGuard();
        BeginBusy("正在配置路由 ...", ApplyStepLabels);
        var progress = new Progress<StageProgress>(p =>
        {
            if ((int)p.Stage >= Steps.Count) return;
            Steps[(int)p.Stage].State = p.State;
            if (p.State == StageState.Active) BusyText = $"正在配置：{ApplyStepLabels[(int)p.Stage]} ...";
        });

        ApplyResult result;
        try
        {
            var guard = new GuardOptions(GuardRounds, TimeSpan.FromSeconds(GuardIntervalSeconds));
            result = await Task.Run(() => _executor.ApplyAsync(context, guard, progress));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"配置过程中出现意外错误：{ex.Message}");
            result = new ApplyResult(false, false, null, null);
        }

        await RefreshAsync(silent: true);
        EndBusy(result.Success);

        if (result.Success)
        {
            RememberSelection();
            if (PersistentGuard) StartPersistentGuard();
        }
        else
        {
            _dialogs.Show("配置失败",
                result.RolledBack
                    ? "配置过程中出错，已自动撤销本次改动。详细原因见操作日志。"
                    : "配置未能执行，路由表没有被修改。详细原因见操作日志。",
                DialogKind.Error);
        }
    }

    // ========================================================================
    // 恢复
    // ========================================================================

    private async Task RestoreAsync(RouteSnapshot? snapshot)
    {
        snapshot ??= _activeSnapshot;
        if (snapshot is null || IsBusy || !IsAdmin) return;

        var stale = snapshot.IsStale(AppEnvironment.BootTimeUtc);
        var message =
            $"将按 {snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的备份恢复路由：\n\n" +
            $"• 补回 {snapshot.Original.Count} 条原始路由中缺失的部分\n" +
            $"• 移除本工具添加的 {snapshot.Added.Count} 条路由\n\n" +
            "其他路由不会被改动。恢复期间网络可能短暂中断。";
        if (stale)
        {
            message += "\n\n注意：这份备份创建于系统上次重启之前。本工具添加的是临时路由，重启时已自动清除，" +
                       "通常不需要再恢复；如果网络环境已经变化，恢复可能写入过时的路由。";
        }

        if (!_dialogs.Confirm("恢复原路由", message, "恢复", stale ? DialogKind.Warning : DialogKind.Question)) return;

        StopPersistentGuard();
        BeginBusy("正在恢复路由 ...", RestoreStepLabels);
        Steps[0].State = StageState.Done;
        Steps[1].State = StageState.Active;

        RestoreResult result;
        try
        {
            result = await Task.Run(() => _executor.Restore(snapshot));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"恢复过程中出现意外错误：{ex.Message}");
            result = new RestoreResult(false, 0, 0, 0, 1);
        }

        Steps[1].State = result.Success ? StageState.Done : StageState.Failed;
        Steps[2].State = StageState.Active;
        await RefreshAsync(silent: true);
        Steps[2].State = result.Success ? StageState.Done : StageState.Failed;
        EndBusy(result.Success);

        if (!result.Success)
            _dialogs.Show("恢复未完成", $"有 {result.Failed} 条路由未能还原，备份已保留，可稍后重试。详细原因见操作日志。", DialogKind.Warning);
    }

    private void DeleteSnapshot(SnapshotViewModel? item)
    {
        if (item is null) return;
        var active = item.Snapshot.IsRestorable && !item.IsStale;
        var message = active
            ? "这份备份对应的配置仍在生效。删除后将无法再通过本工具恢复到配置前的状态。\n\n确定删除吗？"
            : "确定删除这份备份吗？";
        if (!_dialogs.Confirm("删除备份", message, "删除", active ? DialogKind.Warning : DialogKind.Question)) return;

        try
        {
            if (ReferenceEquals(item.Snapshot, _activeSnapshot) || item.Snapshot.Id == _activeSnapshot?.Id) StopPersistentGuard(log: true);
            _store.Delete(item.Snapshot);
            ApplySnapshots(_store.LoadAll());
            RebuildRouteRows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Show("删除失败", ex.Message, DialogKind.Error);
        }
    }

    // ========================================================================
    // 持续守护
    // ========================================================================

    private void StartPersistentGuard()
    {
        StopPersistentGuard();
        if (!IsAdmin || !PersistentGuard) return;

        // 只守护“已生效”的配置；守护对象是备份里记录的外网网卡与网关
        var snapshot = _activeSnapshot;
        if (snapshot is not { Status: SnapshotStatus.Applied, Wan: not null }) return;

        var cts = _guardCts = new CancellationTokenSource();
        _guardCleanedCount = 0;
        IsGuardRunning = true;
        Log(LogLevel.Info, $"持续守护已启动：每 {PersistentGuardIntervalSeconds} 秒检查一次，只保留 {snapshot.Wan.InterfaceName} 上的默认路由");
        _ = GuardLoopAsync(snapshot, cts);
    }

    private void StopPersistentGuard(bool log = false)
    {
        var cts = _guardCts;
        if (cts is null) return;
        _guardCts = null;
        cts.Cancel();
        IsGuardRunning = false;
        if (log) Log(LogLevel.Info, "持续守护已停止");
    }

    private async Task GuardLoopAsync(RouteSnapshot snapshot, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var wan = snapshot.Wan!;
        var paused = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(PersistentGuardIntervalSeconds), token);
                if (IsBusy) continue;

                var result = await Task.Run(() => _executor.GuardOnce(wan, snapshot, "[持续守护] "), token);
                if (token.IsCancellationRequested) break;

                if (result.Cleaned > 0)
                {
                    _guardCleanedCount += result.Cleaned;
                    OnPropertyChanged(nameof(GuardStatusText));
                    await ReadRoutesAsync();
                }

                // 外网掉线期间不清理；只在状态变化时各提示一次
                if (result.Skipped && !paused) Log(LogLevel.Warning, "[持续守护] 外网默认路由不存在，暂停清理，恢复后自动继续");
                else if (!result.Skipped && paused) Log(LogLevel.Info, "[持续守护] 外网默认路由已恢复，继续守护");
                paused = result.Skipped;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"[持续守护] 意外停止：{ex.Message}");
            if (ReferenceEquals(_guardCts, cts))
            {
                _guardCts = null;
                IsGuardRunning = false;
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    // ========================================================================
    // 目标网段
    // ========================================================================

    private void AddNetwork()
    {
        var parts = Ipv4Network.SplitList(NewNetworkText);
        if (parts.Count == 0) return;

        // 先全部校验，有一个不合法就整体不添加，让用户改完再提交
        var parsed = parts.Select(Ipv4Network.Parse).ToList();
        var invalid = parsed.FirstOrDefault(p => !p.Success);
        if (invalid is not null)
        {
            NewNetworkError = $"{invalid.Input}：{invalid.Error}";
            return;
        }

        foreach (var result in parsed)
        {
            if (Networks.Any(n => n.Network == result.Network)) continue;
            Networks.Add(new NetworkChipViewModel(result.Network));
            if (result.Normalized) Log(LogLevel.Info, $"网段 {result.Input} 含主机位，已规范化为 {result.Network}");
        }

        NewNetworkText = string.Empty;
        UpdateStatus();
    }

    private void RemoveNetwork(NetworkChipViewModel? chip)
    {
        if (chip is null || !Networks.Remove(chip)) return;
        UpdateStatus();
    }

    private void ResetNetworks()
    {
        Networks.Clear();
        foreach (var text in AppSettings.DefaultNetworks.Split(';'))
            Networks.Add(new NetworkChipViewModel(Ipv4Network.Parse(text).Network));
        NewNetworkError = null;
        UpdateStatus();
    }

    // ========================================================================
    // 其他操作
    // ========================================================================

    private void Swap()
    {
        (_selectedWan, _selectedLan) = (_selectedLan, _selectedWan);
        (_wanGateway, _lanGateway) = (_lanGateway, _wanGateway);
        OnPropertyChanged(nameof(SelectedWan));
        OnPropertyChanged(nameof(SelectedLan));
        OnPropertyChanged(nameof(WanGateway));
        OnPropertyChanged(nameof(LanGateway));
        UpdateStatus();
        Swapped?.Invoke();
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Logs.Select(l => l.ToLine())));
        }
        catch (Exception)
        {
            // 剪贴板被其他程序占用时会失败，重试即可
            _dialogs.Show("复制失败", "剪贴板正被其他程序占用，请稍后再试。", DialogKind.Warning);
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Show("无法打开备份目录", $"{_store.Directory}\n\n{ex.Message}", DialogKind.Warning);
        }
    }

    private async Task ShowDiagnosticsAsync()
    {
        var report = await Task.Run(() => DiagnosticsReport.Build(_table, _store));
        _dialogs.ShowDiagnostics(report);
    }

    private void BeginBusy(string text, string[] stepLabels)
    {
        Steps.Clear();
        foreach (var label in stepLabels) Steps.Add(new StepViewModel(label));
        BusyText = text;
        ShowSteps = true;
        IsBusy = true;
        SelectedTab = 0;
    }

    private void EndBusy(bool success)
    {
        BusyText = success ? "已完成" : "未完成";
        IsBusy = false;
        OperationCompleted?.Invoke(success);
    }

    /// <summary>记住这次用的网卡、网关和网段，下次启动时还原。</summary>
    private void RememberSelection()
    {
        _settings.WanInterfaceId = SelectedWan?.Id;
        _settings.LanInterfaceId = SelectedLan?.Id;
        if (SelectedWan is not null && Ipv4.Normalize(WanGateway) is { } wanGateway) _settings.Gateways[SelectedWan.Id] = wanGateway;
        if (SelectedLan is not null && Ipv4.Normalize(LanGateway) is { } lanGateway) _settings.Gateways[SelectedLan.Id] = lanGateway;
        _settings.Networks = Networks.Select(n => n.Text).ToList();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.GuardRounds = GuardRounds;
        _settings.GuardIntervalSeconds = GuardIntervalSeconds;
        _settings.PersistentGuard = PersistentGuard;
        _settings.PersistentGuardIntervalSeconds = PersistentGuardIntervalSeconds;
        _settingsStore.Save(_settings);
    }

    // ========================================================================
    // 日志
    // ========================================================================

    /// <summary>追加日志；可从任意线程调用。</summary>
    private void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        if (_dispatcher.CheckAccess()) Append(entry);
        else _dispatcher.BeginInvoke(() => Append(entry));
    }

    private void Append(LogEntry entry)
    {
        if (Logs.Count >= MaxLogEntries) Logs.RemoveAt(0);
        Logs.Add(entry);
        LogAppended?.Invoke();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _routeTimer.Stop();
        _networkChangeTimer.Stop();
        StopPersistentGuard();
    }
}
