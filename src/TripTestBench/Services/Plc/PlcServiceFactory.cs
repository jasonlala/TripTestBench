// ============================================================================
// 文件：PlcServiceFactory.cs
// 作用：创建 PLC 服务实例，并提供通讯设置窗口的“测试连接”能力。
//       说明：把创建逻辑集中在这里，界面层不需要知道具体实现类型。
// ============================================================================

using TripTestBench.Models; // PlcOptions / PlcPoints

namespace TripTestBench.Services.Plc;

/// <summary>PLC 服务工厂。</summary>
public static class PlcServiceFactory
{
    /// <summary>创建 PLC 服务实例（汇川 EASY320 / Modbus TCP）。</summary>
    public static IPlcService Create() => new ModbusPlcService();

    /// <summary>
    /// 通讯设置窗口中的“测试连接”：建立连接并读取 1 个寄存器验证链路。
    /// </summary>
    public static Task TestConnectionAsync(PlcOptions options, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // 建立连接后读一次 D1520（实时电流），能读通即视为连接成功
            using var client = new ModbusTcpClient(options.Ip, options.Port, options.UnitId, 2000);
            client.ReadHoldingRegisters(PlcPoints.D_RealtimeCurrent, 1);
        }, ct);
}
