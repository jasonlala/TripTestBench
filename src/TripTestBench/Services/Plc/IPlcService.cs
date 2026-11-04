// ============================================================================
// 文件：IPlcService.cs
// 作用：定义 PLC 数据采集与控制能力的统一接口。
//       真实设备（ModbusPlcService）实现该接口，上层 ViewModel 只依赖接口，
//       不关心具体的通讯协议细节。
// ============================================================================

using TripTestBench.Models; // PlcOptions / PlcSnapshot

namespace TripTestBench.Services.Plc;

/// <summary>PLC 数据采集与控制抽象。</summary>
public interface IPlcService : IDisposable
{
    /// <summary>当前是否已连接（指示灯颜色依据）。</summary>
    bool IsConnected { get; }

    /// <summary>当前通讯参数（IP / 端口 / 轮询周期）。</summary>
    PlcOptions Options { get; }

    /// <summary>每读到一帧实时数据触发一次（后台线程，界面需切回 UI 线程）。</summary>
    event EventHandler<PlcSnapshot>? SnapshotReceived;

    /// <summary>连接状态变化时触发（用于刷新 PLC 指示灯）。</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>启动采集服务。</summary>
    Task StartAsync(PlcOptions options);

    /// <summary>停止采集服务并释放连接。</summary>
    Task StopAsync();

    /// <summary>写入当前工位的设定电流与设定时间（界面值 × 1000）。</summary>
    Task WriteTestSettingsAsync(int station, double currentKa, double timeSeconds);

    /// <summary>主回路启动 / 停止（M0：ON 启动、OFF 停止）。</summary>
    Task SetMainCircuitAsync(bool on);

    /// <summary>开始测试电平（M1：ON 启动 / OFF 撤销，电平模式不自复位）。</summary>
    Task SetStartTestAsync(bool on);

    /// <summary>停止测试电平（M3：ON 停止 / OFF 撤销，电平模式不自复位）。</summary>
    Task SetStopTestAsync(bool on);

    /// <summary>清零峰值电流统计。</summary>
    void ResetPeak();
}
