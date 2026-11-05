// ============================================================================
// 文件：tools/ModbusSimulator/Program.cs
// 作用：TripTestBench 上位机的 Modbus TCP 从机模拟器（模拟汇川 EASY320 点位）。
//       自研 MBAP 组帧从机（功能码 01/02/03/05/06/0F/10），无第三方依赖。
//       地址约定与上位机一致：
//         · 保持寄存器（FC03/06/10）：地址 = D 编号
//         · 线圈（FC01/05/0F）：地址 = M 编号
//         · 离散输入（FC02）：地址 = 63488 + X 编号
//         · int32 低字在低地址（与 ModbusPlcService.ToInt32 一致）
// 模拟流程：
//         M0=ON（充电）2 秒后 X3=ON（充电完成）
//         M1=ON（开始）→ 读 D1000/D1002 设定值，D1600 从设定时间倒数到 0，
//                        期间 D1520 输出带波动的电流，结束后 D1550 写脱扣电流、M80=ON
//         M3=ON（停止）→ 电流/运行时间清零，回到待机
// 用法：dotnet run --project tools/ModbusSimulator -c Release
//       上位机“通讯设置”里把 IP 改为 127.0.0.1（端口 502 不变）即可连接。
// ============================================================================

using System.Net;
using System.Net.Sockets;

namespace ModbusSimulator;

internal static class Program
{
    // ---------------- 点位定义 ----------------
    private const int D_SetTime = 1000;      // int32 设定时间（s ×1000 = ms）
    private const int D_SetCurrent = 1002;   // int32 设定电流（kA ×1000）
    private const int D_Current = 1520;      // int32 实时电流（A）
    private const int D_Trip = 1550;         // 16 位脱扣电流（kA ×1000）
    private const int D_RunTime = 1600;      // int32 运行时间（ms，倒数到 0 = 试验结束）

    private const int M_Main = 0;            // M0 充电（主回路）
    private const int M_Start = 1;           // M1 开始测试
    private const int M_Stop = 3;            // M3 停止测试
    private const int M_Complete = 80;       // M80 完成标志

    private const int XBase = 63488;              // X 区 Modbus 基址
    private const int X_Emergency1 = XBase + 0;   // X0 急停（常闭，正常 ON）
    private const int X_Emergency2 = XBase + 1;   // X1 急停（常闭，正常 ON）
    private const int X_ChargeOk = XBase + 3;     // X3 充电完成
    private const int X_AlarmStart = XBase + 4;   // X4 ~ X7 告警（常闭，正常 ON）

    // ---------------- 数据区 ----------------
    private static readonly bool[] Co = new bool[1000];      // M 区线圈
    private static readonly bool[] Di = new bool[65536];     // X 区离散输入
    private static readonly ushort[] Hr = new ushort[65536]; // D 区保持寄存器

    private static string _simState = "idle";
    private static long _chargeStarted;
    private static int _setCurrentRaw;
    private static readonly Random Rand = new();

    private static int Read32(int address) => Hr[address] | (Hr[address + 1] << 16);

    private static void Write32(int address, int value)
    {
        Hr[address] = (ushort)(value & 0xFFFF);
        Hr[address + 1] = (ushort)((value >> 16) & 0xFFFF);
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private static async Task Main()
    {
        // 常闭触点初始状态：急停 X0/X1、告警 X4~X7 均 ON（正常）
        Di[X_Emergency1] = true;
        Di[X_Emergency2] = true;
        for (var i = 0; i < 4; i++) Di[X_AlarmStart + i] = true;

        _ = Task.Run(SimulateLoopAsync);

        var listener = new TcpListener(IPAddress.Any, 502);
        listener.Start();
        Log("Modbus TCP 从机模拟器已启动：0.0.0.0:502（等待上位机连接）");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    // ---------------- 试验流程模拟 ----------------
    private static async Task SimulateLoopAsync()
    {
        while (true)
        {
            await Task.Delay(100);

            var m0 = Co[M_Main];
            var m1 = Co[M_Start];
            var m3 = Co[M_Stop];
            var x3 = Di[X_ChargeOk];

            // ---- 停止：M3 = ON，一切归零 ----
            if (m3)
            {
                if (_simState != "idle") Log(">> M3=ON：停止测试，电流/运行时间清零，X3/M80 复位");
                _simState = "idle";
                Write32(D_Current, 0);
                Write32(D_RunTime, 0);
                Di[X_ChargeOk] = false;
                Co[M_Complete] = false;
                continue;
            }

            // ---- 充电：M0 = ON，2 秒后 X3 = ON ----
            if (m0 && _simState == "idle")
            {
                _simState = "charging";
                _chargeStarted = Environment.TickCount64;
                Log(">> M0=ON：开始充电，2 秒后 X3=ON");
            }

            if (_simState == "charging" && !x3 && Environment.TickCount64 - _chargeStarted >= 2000)
            {
                Di[X_ChargeOk] = true;
                Log(">> 充电完成：X3=ON（等待 M1）");
            }

            // ---- 开始测试：M1 = ON 且已充电完成 ----
            if (m1 && x3 && _simState is not ("testing" or "done"))
            {
                _setCurrentRaw = Read32(D_SetCurrent);
                Write32(D_RunTime, Read32(D_SetTime));
                Co[M_Complete] = false;
                _simState = "testing";
                Log($">> M1=ON：开始测试，设定电流 {_setCurrentRaw}（kA×1000）");
            }

            // ---- 测试中：D1600 每 100ms 递减 100，D1520 输出带波动电流 ----
            if (_simState != "testing") continue;

            var runTime = Math.Max(Read32(D_RunTime) - 100, 0);
            Write32(D_RunTime, runTime);
            var noise = (int)(_setCurrentRaw * (Rand.NextDouble() * 0.06 - 0.03));
            Write32(D_Current, Math.Max(_setCurrentRaw + noise, 0));

            if (runTime != 0) continue;

            Write32(D_Current, 0);
            Hr[D_Trip] = (ushort)Math.Min((int)(_setCurrentRaw * 1.02), ushort.MaxValue);
            Co[M_Complete] = true;
            _simState = "done";
            Log(">> 试验完成：D1550 脱扣电流已写入（102%，合格），M80=ON");
        }
    }

    // ---------------- Modbus TCP 从机 ----------------
    private static async Task HandleClientAsync(TcpClient client)
    {
        Log($"客户端已连接：{client.Client.RemoteEndPoint}");
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var header = new byte[7];

            while (true)
            {
                // MBAP 头：事务号(2) 协议号(2) 长度(2) 单元号(1)
                if (!await ReadExactlyAsync(stream, header)) return;
                if (header[2] != 0 || header[3] != 0) return;              // 协议号必须为 0
                var length = (header[4] << 8) | header[5];
                if (length < 2) return;
                var pdu = new byte[length - 1];
                if (!await ReadExactlyAsync(stream, pdu)) return;

                var response = Process(pdu);
                var resp = new byte[7 + response.Length];
                resp[0] = header[0];                                       // 事务号原样返回
                resp[1] = header[1];
                resp[2] = 0;
                resp[3] = 0;
                resp[4] = (byte)((response.Length + 1) >> 8);
                resp[5] = (byte)((response.Length + 1) & 0xFF);
                resp[6] = header[6];                                       // 单元号原样返回
                Array.Copy(response, 0, resp, 7, response.Length);
                await stream.WriteAsync(resp);
            }
        }
        catch (Exception ex)
        {
            Log($"连接断开：{ex.Message}");
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset));
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    private static byte[] Process(byte[] pdu) => pdu[0] switch
    {
        0x01 => ReadBits(pdu, false),
        0x02 => ReadBits(pdu, true),
        0x03 => ReadRegisters(pdu),
        0x05 => WriteSingleCoil(pdu),
        0x06 => WriteSingleRegister(pdu),
        0x0F => WriteMultipleCoils(pdu),
        0x10 => WriteMultipleRegisters(pdu),
        _ => Exception(pdu[0], 1),
    };

    // ---- FC01 / FC02：读线圈 / 读离散输入 ----
    private static byte[] ReadBits(byte[] pdu, bool discrete)
    {
        var address = (pdu[1] << 8) | pdu[2];
        var count = (pdu[3] << 8) | pdu[4];
        if (count is < 1 or > 2000) return Exception(pdu[0], 3);
        if (address + count > 65536) return Exception(pdu[0], 2);

        var bytes = (count + 7) / 8;
        var data = new byte[2 + bytes];
        data[0] = pdu[0];
        data[1] = (byte)bytes;

        for (var i = 0; i < count; i++)
        {
            var addr = address + i;
            var value = discrete ? Di[addr] : (addr < Co.Length && Co[addr]);
            if (value) data[2 + i / 8] |= (byte)(1 << (i % 8));
        }
        return data;
    }

    // ---- FC03：读保持寄存器 ----
    private static byte[] ReadRegisters(byte[] pdu)
    {
        var address = (pdu[1] << 8) | pdu[2];
        var count = (pdu[3] << 8) | pdu[4];
        if (count is < 1 or > 125) return Exception(pdu[0], 3);
        if (address + count > Hr.Length) return Exception(pdu[0], 2);

        var data = new byte[2 + count * 2];
        data[0] = pdu[0];
        data[1] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            data[2 + i * 2] = (byte)(Hr[address + i] >> 8);
            data[3 + i * 2] = (byte)(Hr[address + i] & 0xFF);
        }
        return data;
    }

    // ---- FC05：写单个线圈 ----
    private static byte[] WriteSingleCoil(byte[] pdu)
    {
        var address = (pdu[1] << 8) | pdu[2];
        if (address >= Co.Length) return Exception(pdu[0], 2);
        Co[address] = pdu[3] == 0xFF;
        Log($"写入线圈 M{address} = {(pdu[3] == 0xFF ? "ON" : "OFF")}");
        return new byte[] { pdu[0], pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    // ---- FC06：写单个寄存器 ----
    private static byte[] WriteSingleRegister(byte[] pdu)
    {
        var address = (pdu[1] << 8) | pdu[2];
        if (address >= Hr.Length) return Exception(pdu[0], 2);
        Hr[address] = (ushort)((pdu[3] << 8) | pdu[4]);
        Log($"写入寄存器 D{address} = {Hr[address]}");
        return new byte[] { pdu[0], pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    // ---- FC0F：写多个线圈 ----
    private static byte[] WriteMultipleCoils(byte[] pdu)
    {
        var address = (pdu[1] << 8) | pdu[2];
        var count = (pdu[3] << 8) | pdu[4];
        var byteCount = pdu[5];
        if (count is < 1 or > 1968 || byteCount != (count + 7) / 8) return Exception(pdu[0], 3);
        if (address + count > Co.Length) return Exception(pdu[0], 2);

        for (var i = 0; i < count; i++)
        {
            var bit = (pdu[6 + i / 8] >> (i % 8)) & 1;
            Co[address + i] = bit == 1;
        }
        Log($"写入多线圈 M{address} 起 {count} 点");
        return new byte[] { pdu[0], pdu[1], pdu[2], (byte)(count >> 8), (byte)count };
    }

    // ---- FC10：写多个寄存器 ----
    private static byte[] WriteMultipleRegisters(byte[] pdu)
    {
        var address = (pdu[1] << 8) | pdu[2];
        var count = (pdu[3] << 8) | pdu[4];
        var byteCount = pdu[5];
        if (count is < 1 or > 123 || byteCount != count * 2) return Exception(pdu[0], 3);
        if (address + count > Hr.Length) return Exception(pdu[0], 2);

        for (var i = 0; i < count; i++)
        {
            Hr[address + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
        }
        Log($"写入多寄存器 D{address} 起 {count} 点（首值 {Hr[address]}）");
        return new byte[] { pdu[0], pdu[1], pdu[2], (byte)(count >> 8), (byte)count };
    }

    // ---- 异常响应：功能码 | 0x80 + 异常码 ----
    private static byte[] Exception(byte functionCode, byte code) => new[] { (byte)(functionCode | 0x80), code };
}
