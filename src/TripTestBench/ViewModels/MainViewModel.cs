// ============================================================================
// 文件：MainViewModel.cs
// 作用：主界面（过流脱扣试验台）的全部业务逻辑与状态机。
//       1) 与 PLC 通讯服务对接，负责“电容充电 / 开始测试 / 停止测试 / 保存记录”等操作；
//       2) 维护试验状态机：待机 → 充电中 → 就绪 → 测试中 → 完成 / 停止 / 急停；
//       3) 测试过程中每 1 秒从 PLC 寄存器取一个采样点绘制电流曲线；
//       4) 运行时间（D1600）归零时，自动向 PLC 下发“停止测试”命令（M3=ON + M0 断开），
//          并生成本次试验记录，供操作员保存到历史数据库。
// 说明：本类不直接操作任何控件，界面刷新全部依靠属性通知与数据绑定完成。
// ============================================================================

using System.Collections.ObjectModel;          // 观测集合：曲线采样点集合
using System.Diagnostics;                      // 秒表：用于倒计时与运行时间统计
using System.Globalization;                    // 数字解析：兼容中英文小数点格式
using System.Windows;                          // WPF 基础类型（Application / Dispatcher）
using System.Windows.Input;                    // ICommand 命令接口
using System.Windows.Media;                    // Brush 画刷：状态颜色
using System.Windows.Threading;                // DispatcherTimer：系统时钟与倒计时刷新
using TripTestBench.Converters;                // 颜色转换器：从主题资源取画刷
using TripTestBench.Localization;              // 多语言：中英文文案
using TripTestBench.Models;                    // 数据模型：试验记录 / PLC 快照 / 状态枚举
using TripTestBench.Mvvm;                      // MVVM 基类与命令实现
using TripTestBench.Services;                  // 本地服务：用户 / 记录 / 配置 / 日志 / 对话框
using TripTestBench.Services.Plc;              // PLC 通讯抽象与工厂

namespace TripTestBench.ViewModels;

/// <summary>
/// 主界面视图模型：负责试验流程控制、PLC 数据接入与曲线采样。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // ---------------------------------------------------------------------
    // 曲线采样周期：每 1000 毫秒（1 秒）从 PLC 寄存器取一个点绘制到图表上。
    // 说明：PLC 轮询本身更快（默认 200ms），用于“实时电流”数字的流畅刷新；
    //       绘制曲线时按 1 秒降频，既满足工艺要求，也让曲线点距均匀、便于判读。
    // ---------------------------------------------------------------------
    private const double ChartSampleIntervalSeconds = 1.0;

    // 曲线最多保留的秒数（比绘图窗口 30 秒多留 30 秒余量，避免边界丢点）
    private const double ChartHistorySeconds = 60.0;

    // ---------------------------------------------------------------------
    // 依赖注入的本地服务与外部回调
    // ---------------------------------------------------------------------
    private readonly UserService _userService;     // 用户服务：当前登录账户信息
    private readonly RecordRepository _records;    // 试验记录仓库：保存历史数据
    private readonly Action _openHistory;          // 打开“数据查询”界面的回调
    private readonly Action _logout;               // 退出登录（返回登录界面）的回调
    private readonly Action _openSettings;         // 打开“通讯设置”窗口的回调
    private readonly Dispatcher _dispatcher;       // 主线程调度器：把后台数据切回 UI 线程
    private readonly DispatcherTimer _clockTimer;  // 1 秒时钟：刷新系统时间与倒计时
    private readonly Stopwatch _uptime = Stopwatch.StartNew(); // 程序运行计时（待机曲线时间轴）
    private readonly Stopwatch _testWatch = new();             // 单次试验计时（倒计时依据）

    // ---------------------------------------------------------------------
    // PLC 通讯与运行标志
    // ---------------------------------------------------------------------
    private IPlcService? _plc;                     // 当前 PLC 服务实例
    private bool _started;                         // 是否已完成首次激活（防止重复初始化）
    private bool _disposed;                        // 是否已释放（防止窗口关闭后回调报错）
    private bool _busy;                            // 是否正在执行耗时操作（按钮防重入）
    private int _toastVersion;                     // 提示消息版本号：用于覆盖旧提示的自动隐藏

    // ---------------------------------------------------------------------
    // 界面状态字段
    // ---------------------------------------------------------------------
    private TestState _state = TestState.Idle;     // 当前试验状态
    private bool _plcConnected;                    // PLC 是否已连接（指示灯颜色依据）

    private string _productName = "测试样品";      // 产品名称（默认值，操作员可修改）
    private string _productCode = "SP-0001";       // 产品编号（默认值，操作员可修改）
    private string _tester = string.Empty;         // 试验员（默认取登录账号）
    private string _setCurrentText = "10.000";     // 设定电流文本（kA）
    private string _setTimeText = "3.00";          // 设定时间文本（s）
    private int _station = 1;                      // 当前选择的试验工位（1/2/3）

    private double _currentKa;                     // 实时电流（kA）
    private double _peakKa;                        // 本次试验峰值电流（kA）
    private double _tripKa;                        // 脱扣电流（kA）
    private double _runTimeSeconds;                // 运行时间（s）

    private double _activeSetCurrent = 10;         // 本次试验实际下发的设定电流（kA）
    private double _activeSetTime = 3;             // 本次试验实际下发的设定时间（s）
    private double _chartTimeOffset;               // 曲线时间轴零点偏移（让测试从 0 秒开始）

    // ---------------------------------------------------------------------
    // 曲线采样与试验结束控制标志
    // ---------------------------------------------------------------------
    private double _lastChartSampleSeconds = double.NegativeInfinity; // 上一次采样时间（秒）
    private bool _finishRequested;                 // 本次试验是否已判定结束（防重复生成记录）
    private bool _sawRunTimeNonZero;               // 本次试验是否出现过非零运行时间（防止 D1600 初值为 0 时误判结束）
    private PlcSnapshot? _latestSnapshot;          // 最近一帧 PLC 数据（生成记录时取值）
    private bool _chartRecording;                  // 是否正在向图表记录数据（仅测试期间为 true）
    private readonly List<ChartSample> _lastRecordSamples = new(); // 最近一次试验的曲线快照（保存文件用）

    // ---------------------------------------------------------------------
    // 边沿检测：用于识别 PLC 信号的“上升沿”
    // ---------------------------------------------------------------------
    private bool _prevCompleted;                   // 上一帧完成信号（M80）
    private bool _prevChargeComplete;              // 上一帧充电完成信号（X3）

    /// <summary>电容充电超时时间（秒）：超时未收到 X3 则断开主回路并提示。</summary>
    private const double ChargeTimeoutSeconds = 40;

    /// <summary>本次充电的截止时刻（充电开始时设置；就绪 / 停止 / 超时后清空）。</summary>
    private DateTime? _chargeDeadline;

    private TestRecord? _lastRecord;               // 最近一次试验记录（等待保存）
    private bool _lastRecordSaved;                 // 该记录是否已保存（控制保存按钮可用性）

    private string _clockText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss"); // 顶部系统时间
    private string _toastMessage = string.Empty;   // 底部提示消息文本
    private bool _toastVisible;                    // 提示是否可见
    private bool _toastIsError;                    // 提示是否为错误级别（决定颜色与停留时长）

    /// <summary>
    /// 曲线截图提供者：由 MainView 创建时注入，返回当前图表区域的 PNG 字节。
    /// 保存记录时调用一次，结果写入数据库最后一列 ChartImage。
    /// </summary>
    public Func<byte[]?>? ChartCaptureProvider { get; set; }

    /// <summary>
    /// 构造主界面视图模型：初始化命令、时钟，并预填试验员（登录账号）。
    /// </summary>
    public MainViewModel(
        UserService userService,
        RecordRepository records,
        Action openHistory,
        Action logout,
        Action openSettings)
    {
        // 保存外部注入的服务与回调
        _userService = userService;
        _records = records;
        _openHistory = openHistory;
        _logout = logout;
        _openSettings = openSettings;
        _dispatcher = Application.Current.Dispatcher;

        // 1 秒时钟：刷新系统时间；测试中同时刷新倒计时（平滑递减）
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += OnClockTick;

        // -----------------------------------------------------------------
        // 命令定义：CanExecute 决定按钮是否可用，界面自动变灰
        // -----------------------------------------------------------------

        // 说明：每个命令都带名称，点击时自动写入 USER 日志，便于还原操作轨迹

        // 电容充电：必填项齐全 + 待机 / 就绪 / 完成 / 已停止状态可用（测试过程中禁止重复充电）
        ChargeCommand = new AsyncRelayCommand(
            ChargeAsync,
            () => CanControl && HasRequiredInputs
                  && State is TestState.Idle or TestState.Ready or TestState.Completed or TestState.Stopped,
            "电容充电");

        // 开始测试：必填项齐全 + 充电完成（X3 = ON，进入就绪状态）后才可用
        StartCommand = new AsyncRelayCommand(
            StartTestAsync,
            () => CanControl && HasRequiredInputs && State == TestState.Ready,
            "开始测试");

        // 停止测试：PLC 已连接的任意时刻都可用（停止永远优先，保证安全）
        StopCommand = new AsyncRelayCommand(
            StopTestAsync,
            () => PlcConnected,
            "停止测试");

        // 保存记录：产品名称 / 产品编号不为空即可保存（无试验数据时给出提示）
        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => !string.IsNullOrWhiteSpace(ProductName)
                  && !string.IsNullOrWhiteSpace(ProductCode)
                  && !_lastRecordSaved,
            "保存数据");

        // 导航类命令：历史记录 / 通讯设置 / 退出登录
        HistoryCommand = new RelayCommand(() => _openHistory(), null, "打开数据查询");
        SettingsCommand = new RelayCommand(() => _openSettings(), null, "打开通讯设置");
        LogoutCommand = new RelayCommand(Logout, null, "退出登录");
        OpenLogCommand = new RelayCommand(OpenLogFolder, null, "打开日志文件夹");

        // 语言切换时刷新与语言相关的只读属性（状态文字、PLC 状态文字）
        LanguageManager.LanguageChanged += OnLanguageChanged;

        // 试验员默认填当前登录账号，操作员可再修改
        if (_userService.CurrentUser is not null)
        {
            Tester = _userService.CurrentUser.UserName;
        }
    }

    // =====================================================================
    // 一、曲线数据集合（供 CurrentChart 控件绑定）
    // =====================================================================
    public ObservableCollection<ChartSample> Samples { get; } = new();

    // =====================================================================
    // 二、可编辑的工艺参数（绑定到界面输入框）
    // =====================================================================
    public string ProductName
    {
        get => _productName;
        set
        {
            if (SetProperty(ref _productName, value))
            {
                UpdateCommandStates(); // 必填项变化 → 充电 / 开始 / 保存按钮可用性联动
            }
        }
    }

    public string ProductCode
    {
        get => _productCode;
        set
        {
            if (SetProperty(ref _productCode, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string Tester
    {
        get => _tester;
        set => SetProperty(ref _tester, value);
    }

    public string SetCurrentText
    {
        get => _setCurrentText;
        set
        {
            if (SetProperty(ref _setCurrentText, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string SetTimeText
    {
        get => _setTimeText;
        set
        {
            if (SetProperty(ref _setTimeText, value))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// 必填项是否齐全：产品名称 / 产品编号 / 设定电流 / 设定时间全部非空。
    /// 任一为空时充电、开始测试、保存数据按钮均不可用。
    /// </summary>
    private bool HasRequiredInputs
        => !string.IsNullOrWhiteSpace(ProductName)
           && !string.IsNullOrWhiteSpace(ProductCode)
           && !string.IsNullOrWhiteSpace(SetCurrentText)
           && !string.IsNullOrWhiteSpace(SetTimeText);

    /// <summary>试验工位：1 / 2 / 3，切换时同步刷新三个单选按钮的选中状态。</summary>
    public int Station
    {
        get => _station;
        set
        {
            if (SetProperty(ref _station, value))
            {
                OnPropertyChanged(nameof(IsStation1));
                OnPropertyChanged(nameof(IsStation2));
                OnPropertyChanged(nameof(IsStation3));
            }
        }
    }

    // 三个工位按钮的双向绑定属性（RadioButton.IsChecked）
    public bool IsStation1
    {
        get => Station == 1;
        set { if (value) Station = 1; }
    }

    public bool IsStation2
    {
        get => Station == 2;
        set { if (value) Station = 2; }
    }

    public bool IsStation3
    {
        get => Station == 3;
        set { if (value) Station = 3; }
    }

    // =====================================================================
    // 三、运行状态与 PLC 连接状态（指示灯 / 文案 / 按钮可用性）
    // =====================================================================
    public TestState State
    {
        get => _state;
        private set
        {
            var previous = _state;
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            // 状态机跃迁是排障的关键线索：待机 → 充电中 → 就绪 → 测试中 → 完成 / 停止 / 急停
            Logger.Info($"状态切换：{previous} → {value}");

            // 状态变化会联动多个派生属性，逐一通知界面刷新
            OnPropertyChanged(nameof(StatusText));           // 状态文字
            OnPropertyChanged(nameof(StatusBrush));          // 状态颜色
            OnPropertyChanged(nameof(IsParameterEditable));  // 参数是否可编辑
            OnPropertyChanged(nameof(IsTesting));            // 是否测试中
            UpdateCommandStates();                           // 按钮可用性
        }
    }

    /// <summary>
    /// PLC 连接标志：指示灯颜色的直接依据。
    /// true = 通讯正常（绿色常亮）；false = 通讯中断（红色闪烁）。
    /// </summary>
    public bool PlcConnected
    {
        get => _plcConnected;
        private set
        {
            if (SetProperty(ref _plcConnected, value))
            {
                OnPropertyChanged(nameof(PlcStatusText));  // 状态文字：已连接 / 未连接
                OnPropertyChanged(nameof(PlcStatusBrush)); // 指示灯颜色
                UpdateCommandStates();                     // 断开时禁用操作按钮
            }
        }
    }

    /// <summary>实时电流（kA）：界面大号数字，200ms 刷新一次。</summary>
    public double CurrentKa
    {
        get => _currentKa;
        private set => SetProperty(ref _currentKa, value);
    }

    /// <summary>本次试验峰值电流（kA）。</summary>
    public double PeakKa
    {
        get => _peakKa;
        private set => SetProperty(ref _peakKa, value);
    }

    /// <summary>脱扣电流（kA）：PLC 在脱扣瞬间记录的最大电流。</summary>
    public double TripKa
    {
        get => _tripKa;
        private set => SetProperty(ref _tripKa, value);
    }

    /// <summary>运行时间（s）：读取 D1600（ms）÷1000，随轮询实时刷新。</summary>
    public double RunTimeSeconds
    {
        get => _runTimeSeconds;
        private set => SetProperty(ref _runTimeSeconds, value);
    }

    /// <summary>顶部系统时间文本（秒级刷新）。</summary>
    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    /// <summary>状态文字：按当前状态从语言资源取文案（中英文自动切换）。</summary>
    public string StatusText => LanguageManager.Get($"L.Status.{State}");

    /// <summary>状态颜色：不同阶段使用不同颜色，便于操作员远距离识别。</summary>
    public Brush StatusBrush => State switch
    {
        TestState.Idle => PassedToBrushConverter.FindBrush("B.TextSecondary"), // 待机：灰
        TestState.Charging => PassedToBrushConverter.FindBrush("B.Warning"),   // 充电中：橙
        TestState.Ready => PassedToBrushConverter.FindBrush("B.Success"),      // 就绪：绿
        TestState.Testing => PassedToBrushConverter.FindBrush("B.Accent"),     // 测试中：青
        TestState.Completed => PassedToBrushConverter.FindBrush("B.Success"),  // 完成：绿
        TestState.Stopped => PassedToBrushConverter.FindBrush("B.TextMuted"),  // 已停止：暗灰
        TestState.Alarm => PassedToBrushConverter.FindBrush("B.Warning"),      // 告警：橙
        TestState.Emergency => PassedToBrushConverter.FindBrush("B.Danger"),   // 急停：红
        _ => PassedToBrushConverter.FindBrush("B.TextSecondary")
    };

    /// <summary>PLC 状态文字：已连接 / 未连接。</summary>
    public string PlcStatusText => PlcConnected
        ? LanguageManager.Get("L.Header.PlcConnected")
        : LanguageManager.Get("L.Header.PlcDisconnected");

    /// <summary>PLC 指示灯颜色：连接正常 = 绿色；通讯中断 = 红色（界面配合闪烁动画）。</summary>
    public Brush PlcStatusBrush => PlcConnected
        ? PassedToBrushConverter.FindBrush("B.Success")
        : PassedToBrushConverter.FindBrush("B.Danger");

    /// <summary>测试中禁止修改工艺参数，避免下发值中途变化。</summary>
    public bool IsParameterEditable => State is not (TestState.Testing or TestState.Charging);

    /// <summary>是否正在测试（供窗口关闭前做二次确认）。</summary>
    public bool IsTesting => State == TestState.Testing;

    /// <summary>当前登录用户名。</summary>
    public string CurrentUserName => _userService.CurrentUser?.UserName ?? string.Empty;

    /// <summary>“操作员”标签文字（随语言切换）。</summary>
    public string OperatorLabel => LanguageManager.Get("L.Header.Operator");

    // =====================================================================
    // 四、底部提示消息（Toast）
    // =====================================================================
    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public bool ToastIsError
    {
        get => _toastIsError;
        private set => SetProperty(ref _toastIsError, value);
    }

    // =====================================================================
    // 五、命令属性（由 XAML 绑定到各按钮）
    // =====================================================================
    public ICommand ChargeCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand HistoryCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand LogoutCommand { get; }

    /// <summary>打开日志文件夹（现场排障时直接定位到 log 目录）。</summary>
    public ICommand OpenLogCommand { get; }

    /// <summary>操作类按钮的总开关：PLC 已连接、无急停、无告警、且没有正在执行的操作。</summary>
    private bool CanControl
        => PlcConnected
           && State is not TestState.Emergency
           && !IsAlarming
           && !_busy;

    // -----------------------------------------------------------------
    // 三个状态指示灯（对应 PLC 实时信号，随轮询数据帧刷新）
    // -----------------------------------------------------------------
    private bool _isWorking;        // 工作状态：M0（主回路输出）
    private bool _isAlarming;       // 告警状态：X4 ~ X7 任一断开
    private bool _isEmergencyStop;  // 急停状态：X0 / X1 任一断开

    /// <summary>工作指示灯：M0 = ON 时亮绿色。</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
            {
                OnPropertyChanged(nameof(WorkLampBrush));
            }
        }
    }

    /// <summary>告警指示灯：X4 ~ X7 任一断开时亮橙色。</summary>
    public bool IsAlarming
    {
        get => _isAlarming;
        private set
        {
            if (SetProperty(ref _isAlarming, value))
            {
                OnPropertyChanged(nameof(AlarmLampBrush));
                UpdateCommandStates(); // 告警出现/解除 → 充电 / 开始等按钮可用性联动
            }
        }
    }

    /// <summary>急停指示灯：X0 / X1 任一急停时亮红色。</summary>
    public bool IsEmergencyStop
    {
        get => _isEmergencyStop;
        private set
        {
            if (SetProperty(ref _isEmergencyStop, value))
            {
                OnPropertyChanged(nameof(EmergencyLampBrush));
            }
        }
    }

    /// <summary>工作灯颜色：M0 输出中 = 绿；空闲 = 灰。</summary>
    public Brush WorkLampBrush => IsWorking
        ? PassedToBrushConverter.FindBrush("B.Success")
        : PassedToBrushConverter.FindBrush("B.TextMuted");

    /// <summary>告警灯颜色：X4 ~ X7 任一断开 = 橙；正常 = 灰。</summary>
    public Brush AlarmLampBrush => IsAlarming
        ? PassedToBrushConverter.FindBrush("B.Warning")
        : PassedToBrushConverter.FindBrush("B.TextMuted");

    /// <summary>急停灯颜色：X0 / X1 急停 = 红；正常 = 灰。</summary>
    public Brush EmergencyLampBrush => IsEmergencyStop
        ? PassedToBrushConverter.FindBrush("B.Danger")
        : PassedToBrushConverter.FindBrush("B.TextMuted");

    // =====================================================================
    // 六、生命周期：激活、重连、释放
    // =====================================================================

    /// <summary>
    /// 界面首次显示时调用：启动时钟并连接 PLC（只执行一次，从历史界面返回时不重复连接）。
    /// </summary>
    public void OnActivated()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _clockTimer.Start();      // 开始秒级时钟
        _ = ConnectPlcAsync();    // 异步连接 PLC，不阻塞界面显示
    }

    /// <summary>
    /// 修改通讯参数后重新连接 PLC，并给出提示。
    /// </summary>
    public async Task ReconnectAsync()
    {
        await ConnectPlcAsync().ConfigureAwait(true);
        ShowToast(LanguageManager.Get("L.Settings.Saved"));
    }

    /// <summary>
    /// 按当前配置创建并启动 PLC 服务（Modbus TCP，汇川 EASY320）。
    /// </summary>
    private async Task ConnectPlcAsync()
    {
        try
        {
            // 先断开旧连接，避免重复订阅事件
            DisconnectPlc();

            // 读取配置副本，避免运行过程中被外部修改
            var options = AppConfig.Instance.Settings.Plc.Clone();

            var service = PlcServiceFactory.Create();
            service.SnapshotReceived += OnPlcSnapshot;          // 数据帧回调
            service.ConnectionChanged += OnPlcConnectionChanged; // 连接状态回调
            _plc = service;

            await service.StartAsync(options).ConfigureAwait(true);
            Logger.Info($"PLC 服务已启动（{options.Ip}:{options.Port}，轮询 {options.PollIntervalMs} ms）");
        }
        catch (Exception ex)
        {
            Logger.Error("启动 PLC 服务失败", ex);
            ShowToast(string.Format(LanguageManager.Get("L.Msg.OperationFailed"), ex.Message), true);
        }
    }

    /// <summary>断开并释放当前 PLC 服务（取消事件订阅，防止内存泄漏）。</summary>
    private void DisconnectPlc()
    {
        var service = _plc;
        _plc = null;
        if (service is null)
        {
            return;
        }

        service.SnapshotReceived -= OnPlcSnapshot;
        service.ConnectionChanged -= OnPlcConnectionChanged;

        // 停止服务属于后台动作，失败只记录日志，不打断界面
        _ = service.StopAsync().ContinueWith(
            t => Logger.Error("停止 PLC 服务失败", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);

        service.Dispose();
        PlcConnected = false;
    }

    // =====================================================================
    // 七、PLC 数据回调：连接状态 + 实时数据帧
    // =====================================================================

    /// <summary>PLC 连接状态变化（后台线程回调 → 切回 UI 线程更新指示灯）。</summary>
    private void OnPlcConnectionChanged(object? sender, bool connected)
    {
        _dispatcher.BeginInvoke(() =>
        {
            PlcConnected = connected;

            if (!connected)
            {
                Logger.Warn("PLC 连接断开，等待自动重连");
            }
        });
    }

    /// <summary>收到一帧 PLC 数据：切到 UI 线程后统一处理。</summary>
    private void OnPlcSnapshot(object? sender, PlcSnapshot snapshot)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                // 数据帧处理绝不允许把界面搞崩：记录日志后丢弃本帧，等待下一帧
                Logger.Error("处理 PLC 数据帧失败", ex);
            }
        });
    }

    /// <summary>
    /// 处理一帧 PLC 数据：刷新实时值、按 1 秒节奏采样曲线、处理各类信号（急停 / 告警 / 完成）。
    /// </summary>
    private void ApplySnapshot(PlcSnapshot snapshot)
    {
        // 窗口已关闭则丢弃回调，避免访问已释放资源
        if (_disposed)
        {
            return;
        }

        // 记录最新一帧，供生成试验记录使用
        _latestSnapshot = snapshot;

        // 1) 同步连接状态与实时数值（这几项每次轮询都刷新，保证数字跟手）
        PlcConnected = snapshot.Connected;
        CurrentKa = snapshot.CurrentKa;                 // D1520 原始值 A，÷1000 = kA
        RunTimeSeconds = snapshot.RunTimeSeconds;       // D1600 原始值 ms，÷1000 = s，直接刷新显示

        // 1.1) 状态指示灯：工作 M0 / 告警 X4~X7 / 急停 X0·X1
        IsWorking = snapshot.Working;
        IsAlarming = snapshot.Alarm;
        IsEmergencyStop = snapshot.Emergency;

        if (snapshot.PeakCurrentKa > PeakKa)
        {
            PeakKa = snapshot.PeakCurrentKa;
        }

        // 2) 急停优先级最高：立即下发 M3=ON + M0=OFF，并终止一切动作
        if (snapshot.Emergency)
        {
            if (State != TestState.Emergency)
            {
                _testWatch.Stop();
                _chartRecording = false;   // ★ 急停：立即停止记录曲线
                CurrentKa = 0;
                State = TestState.Emergency;
                ShowToast(LanguageManager.Get("L.Msg.Emergency"), true);
                Logger.Warn("检测到急停信号，试验中止");
                _ = FireStopOutputAsync("急停信号触发");   // M3 = true、M0 = OFF
            }

            _prevCompleted = snapshot.Completed;
            _prevChargeComplete = snapshot.ChargeComplete;
            return;
        }

        // 急停解除后回到待机
        if (State == TestState.Emergency)
        {
            State = TestState.Idle;
        }

        // 2.5) 告警：立即下发 M3=ON + M0=OFF 并锁定操作（只在进入告警时执行一次）
        if (snapshot.Alarm && State is TestState.Testing or TestState.Charging or TestState.Ready)
        {
            _testWatch.Stop();
            _chartRecording = false;
            _chargeDeadline = null;
            State = TestState.Alarm;
            ShowToast(LanguageManager.Get("L.Msg.Alarm"), true);
            Logger.Warn("检测到告警信号，停止输出并锁定操作");
            _ = FireStopOutputAsync("告警信号触发");       // M3 = true、M0 = OFF
        }
        else if (!snapshot.Alarm && State == TestState.Alarm)
        {
            State = TestState.Idle;                        // 告警解除，回到待机
            Logger.Info("告警信号解除，回到待机状态");
        }

        // 3) 充电超时保护：充电开始后 20 秒内未收到 X3 → 断开主回路并转入已停止
        if (State == TestState.Charging
            && _chargeDeadline is { } deadline
            && DateTime.UtcNow >= deadline)
        {
            HandleChargeTimeout();
        }

        // 4) 充电完成 X3 上升沿：充电中 → 就绪（M0 保持 ON，无需写 false）
        if (snapshot.ChargeComplete && !_prevChargeComplete && State == TestState.Charging)
        {
            _chargeDeadline = null;        // 充电完成，取消超时监视
            State = TestState.Ready;
            ShowToast(LanguageManager.Get("L.Status.Ready"));
            Logger.Info("电容充电完成，进入就绪状态");
        }

        _prevChargeComplete = snapshot.ChargeComplete;

        // 5) 测试中：每 1 秒采样曲线；试验结束由 PLC 信号驱动（不倒计时）
        if (State == TestState.Testing)
        {
            var elapsed = _testWatch.Elapsed.TotalSeconds;          // 本次试验已运行秒数

            // ★ 1 秒采样一次：仅当“正在记录”时才把实时电流写入图表。
            //   停止测试后 _chartRecording 会被置为 false，图表随即停止记录数据。
            if (_chartRecording)
            {
                TryAppendChartSample(snapshot.CurrentKa, elapsed);
            }

            // 记录本次试验是否出现过非零运行时间（防止 PLC 尚未装载设定值时误判结束）
            if (snapshot.RunTimeSeconds > 0)
            {
                _sawRunTimeNonZero = true;
            }

            // ★ 运行时间（D1600）归零：PLC 判定试验结束，软件只生成记录，
            //   不操作 M3（停止输出仅由停止按钮 / 告警 / 急停触发）
            if (_sawRunTimeNonZero && snapshot.RunTimeSeconds <= 0 && !_finishRequested)
            {
                _finishRequested = true;
                _chartRecording = false;   // ★ 图表立即停止记录
                Logger.Info("运行时间归零（D1600 = 0），PLC 判定试验结束");
                CompleteTest(snapshot);
            }
        }
        else if (_chartRecording)
        {
            // 兜底：状态已离开“测试中”，但记录标志仍为真
            //（例如 PLC 完成信号 M80 抢先结束试验），立即停止记录，
            // 保证“停止测试后图表不再新增任何数据点”。
            _chartRecording = false;
        }

        // 说明：停止 / 完成 / 待机 / 就绪状态下不再向图表追加采样点，
        //       曲线会保持停止瞬间的形态，便于操作员回看本次试验波形。

        // 6) PLC 完成信号 M80 上升沿：结束试验并生成记录（软件不操作 M3）
        if (snapshot.Completed && !_prevCompleted && State == TestState.Testing)
        {
            CompleteTest(snapshot);
        }

        _prevCompleted = snapshot.Completed;
    }

    /// <summary>
    /// 充电超时处理：充电开始后 20 秒仍未收到 X3 充电完成信号。
    /// 断开主回路（M0=OFF）防止电容持续带电，状态转入“已停止”并提示操作员检查。
    /// </summary>
    private void HandleChargeTimeout()
    {
        _chargeDeadline = null;
        _testWatch.Stop();
        _chartRecording = false;
        State = TestState.Stopped;
        ShowToast(LanguageManager.Get("L.Msg.ChargeTimeout"), true);
        Logger.Warn($"电容充电超时（{ChargeTimeoutSeconds:F0} 秒未收到 X3 充电完成信号），已断开主回路");
        _ = TurnOffMainCircuitAsync(); // M0 = OFF（内部已捕获异常，只记日志）
    }

    /// <summary>
    /// 停止输出：向 PLC 写 M3 = true 并断开主回路（M0 = OFF）。
    /// 仅在三种场景调用：用户点击停止按钮、检测到告警、检测到急停。
    /// 其余任何流程不得操作 M3。
    /// </summary>
    private async Task FireStopOutputAsync(string reason)
    {
        try
        {
            if (_plc is not null)
            {
                await _plc.SetStopTestAsync(true).ConfigureAwait(false);      // M3 = true
                await _plc.SetMainCircuitAsync(false).ConfigureAwait(false);  // M0 = OFF
            }

            Logger.Warn($"下发停止输出（M3=ON、M0=OFF）：{reason}");
        }
        catch (Exception ex)
        {
            // 停止命令失败必须记录（此时设备可能仍带电，需要现场确认）
            Logger.Error($"下发停止输出失败（{reason}）", ex);
        }
    }

    /// <summary>充电完成后断开主回路（M0=OFF），避免长时间带电。</summary>
    private async Task TurnOffMainCircuitAsync()    {
        try
        {
            if (_plc is not null)
            {
                await _plc.SetMainCircuitAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"充电完成后断开主回路失败：{ex.Message}");
        }
    }

    // =====================================================================
    // 八、曲线采样（1 Hz）
    // =====================================================================

    /// <summary>
    /// 按 1 秒间隔把电流值加入曲线集合。
    /// 说明：PLC 轮询频率高于 1 秒，这里做降频，保证图表横轴点距为 1 秒。
    /// </summary>
    /// <param name="value">实时电流（kA）</param>
    /// <param name="seconds">当前时间轴秒数（测试中为相对测试开始的秒数）</param>
    private void TryAppendChartSample(double value, double seconds)
    {
        // 新一次试验时间轴归零时，重置采样节拍
        if (seconds < _lastChartSampleSeconds)
        {
            _lastChartSampleSeconds = double.NegativeInfinity;
        }

        // 不足 1 秒不采样（减去 1ms 容差，避免浮点误差导致漏点）
        if (seconds - _lastChartSampleSeconds < ChartSampleIntervalSeconds - 0.001)
        {
            return;
        }

        _lastChartSampleSeconds = seconds;
        Samples.Add(new ChartSample(seconds, value));

        // 只保留最近 60 秒数据，避免长时间运行导致集合无限增长
        var limit = seconds - ChartHistorySeconds;
        while (Samples.Count > 0 && Samples[0].Time < limit)
        {
            Samples.RemoveAt(0);
        }
    }

    /// <summary>1 秒时钟：仅刷新顶部系统时间（倒计时已删除，试验结束由 PLC 信号驱动）。</summary>
    private void OnClockTick(object? sender, EventArgs e)
    {
        // 顶部系统时间（精确到秒）
        ClockText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
    }

    // =====================================================================
    // 九、业务操作：充电 / 开始 / 停止 / 保存
    // =====================================================================

    /// <summary>电容充电：置位主回路（M0=ON），等待 PLC 返回充电完成信号 X3。</summary>
    private async Task ChargeAsync()
    {
        if (!EnsureControlReady())
        {
            return;
        }

        if (State == TestState.Testing)
        {
            ShowToast(LanguageManager.Get("L.Msg.Testing"), true);
            return;
        }

        if (!DialogService.Confirm("L.Msg.ConfirmCharge"))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _prevChargeComplete = false;                      // 清空上升沿记忆
            State = TestState.Charging;                       // 进入充电中
            await _plc!.SetMainCircuitAsync(true).ConfigureAwait(true); // M0 = ON
            _chargeDeadline = DateTime.UtcNow.AddSeconds(ChargeTimeoutSeconds); // 40 秒充电超时
            ShowToast(LanguageManager.Get("L.Status.Charging"));
            Logger.User($"工艺操作：电容充电（工位 {Station}，设定 {SetCurrentText} kA / {SetTimeText} s，超时 {ChargeTimeoutSeconds:F0} 秒）");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 开始测试：
    /// 1) 校验输入参数；2) 把设定值写入 PLC 对应工位寄存器；
    /// 3) 清零曲线与计时；4) 向 PLC 发送启动命令（M1=true、M3=false）；
    /// 5) 之后由数据回调驱动“1 秒采样”，试验结束由 PLC 信号（运行时间归零 / M80）判定。
    /// </summary>
    private async Task StartTestAsync()
    {
        if (!EnsureControlReady())
        {
            return;
        }

        if (!TryValidateInputs(out var current, out var time))
        {
            return;
        }

        if (State is TestState.Emergency)
        {
            ShowToast(LanguageManager.Get("L.Msg.AlarmActive"), true);
            return;
        }

        if (State != TestState.Ready)
        {
            ShowToast(LanguageManager.Get("L.Msg.ChargeFirst"), true);
            return;
        }

        if (!DialogService.Confirm("L.Msg.ConfirmStart"))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            // 记录本次试验的设定值（后续记录、判定都以它为准）
            _activeSetCurrent = current;
            _activeSetTime = time;

            // 1) 设定电流 / 时间写入当前工位寄存器（D 区，值 ×1000）
            await _plc!.WriteTestSettingsAsync(Station, current, time).ConfigureAwait(true);
            _plc.ResetPeak(); // 清空峰值统计

            // 2) 复位界面与曲线，准备新一轮采集
            _prevCompleted = false;
            _prevChargeComplete = false;
            _chartTimeOffset = _uptime.Elapsed.TotalSeconds; // 曲线时间轴从 0 开始
            _lastChartSampleSeconds = double.NegativeInfinity; // 复位采样节拍
            _finishRequested = false;                          // 复位试验结束标志
            _sawRunTimeNonZero = false;                        // 复位运行时间非零标志
            _chartRecording = true;                            // ★ 开始向图表记录数据
            Samples.Clear();                                   // 清空上一轮波形
            PeakKa = 0;
            TripKa = 0;
            CurrentKa = 0;
            RunTimeSeconds = 0;

            // 3) 启动计时并切换到“测试中”
            _testWatch.Restart();
            State = TestState.Testing;

            // 4) 发送启动电平：只写 M1 = true，无需写 M3
            await _plc!.SetStartTestAsync(true).ConfigureAwait(true);
            Logger.User($"工艺操作：开始测试 工位 {Station}，{current:F3} kA / {time:F2} s（M1=ON）");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 手动停止测试：PLC 已连接的任意时刻均可点击。
    /// 向 PLC 撤销 M1、置位 M3 停止电平并断开主回路（M0=OFF），状态转为“已停止”。
    /// 不走 RunBusyAsync：停止命令必须能打断其它忙碌操作，只依赖命令自身的防重入。
    /// </summary>
    private async Task StopTestAsync()
    {
        if (!PlcConnected)
        {
            ShowToast(LanguageManager.Get("L.Msg.NoPlc"), true);
            return;
        }

        if (!DialogService.Confirm("L.Msg.ConfirmStop"))
        {
            return;
        }

        var wasActive = State is TestState.Charging or TestState.Testing or TestState.Ready;

        try
        {
            // 立即冻结界面采集，再下发停止命令
            _testWatch.Stop();
            _chartRecording = false;          // ★ 停止记录：图表不再新增数据点，曲线保持冻结
            _prevChargeComplete = false;
            _chargeDeadline = null;           // 取消充电超时监视

            // 停止输出：M3 = true + M0 = OFF（唯一允许操作 M3 的场景之一）
            await FireStopOutputAsync("手动停止测试").ConfigureAwait(true);

            if (wasActive)
            {
                State = TestState.Stopped;
                ShowToast(LanguageManager.Get("L.Msg.Stopped"));
            }

            Logger.User($"工艺操作：手动停止测试（M3=ON 并断开主回路，停止前状态：{State}）");
        }
        catch (Exception ex)
        {
            Logger.Error("停止测试失败", ex);
            ShowToast(string.Format(LanguageManager.Get("L.Msg.OperationFailed"), ex.Message), true);
        }
    }

    /// <summary>保存试验记录到本地历史库。</summary>
    private async Task SaveAsync()
    {
        if (_lastRecord is null)
        {
            ShowToast(LanguageManager.Get("L.Msg.NoDataToSave"), true);
            return;
        }

        if (_lastRecordSaved)
        {
            return;
        }

        var record = _lastRecord;

        // 1) 先在 UI 线程把当前曲线区域渲染成 PNG
        //    （RenderTargetBitmap 必须在 UI 线程调用，所以放在 RunBusyAsync 之外）
        var chartImage = ChartCaptureProvider?.Invoke();

        await RunBusyAsync(() =>
        {
            // 2) 写入 SQLite：记录字段 + 最后一列曲线截图（BLOB）
            _records.Add(record, chartImage);

            // 3) 附带生成一份 CSV 归档副本，便于现场直接双击查看或邮件发送；
            //    归档失败不影响主数据（数据库已写入成功），只记录日志
            string? archivePath = null;
            try
            {
                archivePath = _records.SaveTestDataCsv(
                    record,
                    _lastRecordSamples,
                    BuildRecordInfoItems(record),
                    LanguageManager.Get("L.Data.ColTime"),
                    LanguageManager.Get("L.Data.ColCurrent"),
                    LanguageManager.Get("L.Data.FilePrefix"));
            }
            catch (Exception ex)
            {
                Logger.Error("生成 CSV 归档副本失败（数据库记录已保存）", ex);
            }

            _lastRecordSaved = true;
            UpdateCommandStates();

            // 4) 提示：数据已进数据库，曲线截图随记录一起保存
            ShowToast(LanguageManager.Get(chartImage is { Length: > 0 } ? "L.Msg.SavedDb" : "L.Msg.SavedDbNoChart"));

            Logger.Info($"当前试验数据已保存到 SQLite：{record.ProductName} / {record.ProductCode}"
                        + $"（数据库 {_records.DatabasePath}，截图 {(chartImage?.Length ?? 0) / 1024} KB，"
                        + $"曲线快照 {_lastRecordSamples.Count} 点，当前图表 {Samples.Count} 点）"
                        + (archivePath is null ? "，CSV 归档失败" : $"，CSV 归档 {archivePath}"));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 组装写入本地文件用的“试验信息”键值对（标签跟随当前语言）。
    /// </summary>
    /// <param name="record">本次试验记录</param>
    private static List<(string Label, string Value)> BuildRecordInfoItems(TestRecord record)
    {
        return new List<(string, string)>
        {
            (LanguageManager.Get("L.History.Col.Time"), record.TestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.ProductName"), record.ProductName),
            (LanguageManager.Get("L.History.Col.ProductCode"), record.ProductCode),
            (LanguageManager.Get("L.History.Col.Tester"), record.Tester),
            (LanguageManager.Get("L.History.Col.Station"), record.Station.ToString(CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.SetCurrent"), record.SetCurrentKa.ToString("F3", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.SetTime"), record.SetTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.TripCurrent"), record.TripCurrentKa.ToString("F3", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.Readout.PeakCurrent") + " (kA)", record.PeakCurrentKa.ToString("F3", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.RunTime"), record.RunTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)),
            (LanguageManager.Get("L.History.Col.Result"), record.Passed
                ? LanguageManager.Get("L.Msg.Pass")
                : LanguageManager.Get("L.Msg.Fail"))
        };
    }

    /// <summary>
    /// 结束本次试验：计算脱扣电流、运行时间、合格判定，并生成待保存的记录。
    /// </summary>
    private void CompleteTest(PlcSnapshot snapshot)
    {
        // 防重入：状态已经不是“测试中”说明已被处理过
        if (State != TestState.Testing)
        {
            return;
        }

        _testWatch.Stop();
        _chartRecording = false;   // ★ 试验结束：停止向图表记录数据

        // 脱扣电流优先取 PLC 记录值；若 PLC 未给出，则用峰值近似
        var trip = snapshot.TripCurrentKa > 0
            ? snapshot.TripCurrentKa
            : Math.Max(PeakKa, snapshot.CurrentKa);

        TripKa = Math.Round(trip, 3);
        RunTimeSeconds = Math.Round(_testWatch.Elapsed.TotalSeconds, 2);
        State = TestState.Completed;

        // 合格判定：脱扣电流与设定值偏差 ≤ 10%
        var setCurrent = _activeSetCurrent > 0 ? _activeSetCurrent : 10;
        var passed = trip > 0 && Math.Abs(trip - setCurrent) / setCurrent <= 0.10;

        // 组装试验记录（保存按钮可用，等待操作员确认入库）
        _lastRecord = new TestRecord
        {
            TestTime = DateTime.Now,
            ProductName = ProductName.Trim(),
            ProductCode = ProductCode.Trim(),
            Tester = Tester.Trim(),
            Station = Station,
            SetCurrentKa = setCurrent,
            SetTimeSeconds = _activeSetTime,
            TripCurrentKa = Math.Round(trip, 3),
            PeakCurrentKa = Math.Round(Math.Max(PeakKa, trip), 3),
            RunTimeSeconds = RunTimeSeconds,
            Passed = passed
        };
        _lastRecordSaved = false;

        // ★ 立刻快照本次试验的曲线数据：
        //   操作员之后可能重新充电 / 开始新试验（Samples 会被清空），
        //   因此“保存数据”必须使用这份快照，才能写入真正属于本次试验的波形。
        _lastRecordSamples.Clear();
        _lastRecordSamples.AddRange(Samples);

        UpdateCommandStates();

        Logger.Info($"测试完成：脱扣电流 {trip:F3} kA，设定 {setCurrent:F3} kA，结果 {(passed ? "合格" : "不合格")}");
        ShowToast(LanguageManager.Format("L.Msg.Completed", trip), !passed);
    }

    // =====================================================================
    // 十、输入校验与通用辅助
    // =====================================================================

    /// <summary>通用前置校验：PLC 必须连接，且不处于急停状态。</summary>
    private bool EnsureControlReady()
    {
        if (!PlcConnected)
        {
            ShowToast(LanguageManager.Get("L.Msg.NoPlc"), true);
            return false;
        }

        if (State is TestState.Emergency)
        {
            ShowToast(LanguageManager.Get("L.Msg.AlarmActive"), true);
            return false;
        }

        return true;
    }

    /// <summary>校验工艺参数输入（产品名称 / 编号 / 试验员必填，电流 0.1~100 kA，时间 0.1~600 s）。</summary>
    private bool TryValidateInputs(out double current, out double time)
    {
        current = 0;
        time = 0;

        if (string.IsNullOrWhiteSpace(ProductName))
        {
            ShowToast(LanguageManager.Get("L.Msg.ValidationProductName"), true);
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProductCode))
        {
            ShowToast(LanguageManager.Get("L.Msg.ValidationProductCode"), true);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Tester))
        {
            ShowToast(LanguageManager.Get("L.Msg.ValidationTester"), true);
            return false;
        }

        if (!TryParseNumber(SetCurrentText, out current) || current < 0.1 || current > 100)
        {
            ShowToast(LanguageManager.Get("L.Msg.ValidationCurrent"), true);
            return false;
        }

        if (!TryParseNumber(SetTimeText, out time) || time < 0.1 || time > 600)
        {
            ShowToast(LanguageManager.Get("L.Msg.ValidationTime"), true);
            return false;
        }

        return true;
    }

    /// <summary>数字解析：先按当前区域格式，再按不变区域格式（兼容逗号/点号小数）。</summary>
    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
               || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>执行一次“忙碌”操作：期间禁用按钮，统一捕获异常并提示。</summary>
    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateCommandStates();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Error("操作执行失败", ex);
            ShowToast(string.Format(LanguageManager.Get("L.Msg.OperationFailed"), ex.Message), true);
        }
        finally
        {
            _busy = false;
            UpdateCommandStates();
        }
    }

    /// <summary>刷新所有命令的可用状态（按钮变灰 / 恢复）。</summary>
    private void UpdateCommandStates()
    {
        (ChargeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (HistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>退出登录：确认后释放资源并返回登录界面。</summary>
    private void Logout()
    {
        if (!DialogService.Confirm("L.Msg.ConfirmLogout"))
        {
            return;
        }

        Logger.User("用户确认退出登录");
        _userService.Logout();
        Dispose();
        _logout();
    }

    /// <summary>打开日志文件夹：现场排障时可直接把日志发给技术支持。</summary>
    private static void OpenLogFolder()
    {
        try
        {
            var directory = Logger.LogDirectory;
            Logger.User($"打开日志文件夹：{directory}");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开日志文件夹失败", ex);
        }
    }

    /// <summary>显示底部提示（带版本号，新提示会顶掉旧提示的自动隐藏计时）。</summary>
    private async void ShowToast(string message, bool isError = false)
    {
        var version = ++_toastVersion;
        ToastMessage = message;
        ToastIsError = isError;
        ToastVisible = true;

        // 错误提示停留更久，便于操作员看清
        await Task.Delay(isError ? 3800 : 2400).ConfigureAwait(true);
        if (version == _toastVersion)
        {
            ToastVisible = false;
        }
    }

    /// <summary>语言切换：刷新所有“运行时拼接”的文字属性。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Logger.User($"切换界面语言：{LanguageManager.Current}");
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlcStatusText));
        OnPropertyChanged(nameof(OperatorLabel));
    }

    /// <summary>释放资源：停止时钟、断开 PLC、取消事件订阅。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        LanguageManager.LanguageChanged -= OnLanguageChanged;
        DisconnectPlc();
    }
}
