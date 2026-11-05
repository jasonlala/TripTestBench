// ============================================================================
// 文件：tools/RegMonitor/Program.cs
// 作用：Modbus TCP 寄存器实时监视器（Modbus 主站，只读）。
//       每 500ms 轮询一次 D 区 / M 区 / X 区关键点位，在控制台原地刷新显示，
//       可用于实时查看 ModbusSimulator 或真实 PLC 的当前数据。
// 用法：dotnet run --project tools/RegMonitor -c Release [IP]
//       可选参数 IP，默认 127.0.0.1（端口固定 502，从站号 1）。
// ============================================================================

using System.Net.Sockets;
using System.Text;

namespace RegMonitor;

internal static class Program
{
    private const int Port = 502;
    private const int XBase = 63488;

    // D 区点位（与上位机 PlcPoints 一致）
    private const int D_SetTime = 1000;      // int32 设定时间（ms）
    private const int D_SetCurrent = 1002;   // int32 设定电流（kA×1000）
    private const int D_Current = 1520;      // int32 实时电流（A）
    private const int D_Trip = 1550;         // 16 位脱扣电流（kA×1000）
    private const int D_RunTime = 1600;      // int32 运行时间（ms）

    private static async Task<int> Main(string[] args)
    {
        var ip = args.Length > 0 ? args[0] : "127.0.0.1";
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = $"寄存器监视 - {ip}:{Port}";
        Console.CursorVisible = false;

        var tx = new Tx();

        while (true)
        {
            TcpClient? client = null;
            try
            {
                Console.Clear();
                DrawLine($"寄存器实时监视 → {ip}:{Port}（每 500ms 刷新，Ctrl+C 退出）");
                client = new TcpClient();
                await client.ConnectAsync(ip, Port);
                var stream = client.GetStream();

                while (true)
                {
                    // ---- D 区 ----
                    var settings = await ReadRegsAsync(stream, tx, D_SetTime, 16); // D1000~D1015
                    var current = await ReadRegsAsync(stream, tx, D_Current, 2);   // D1520~D1521
                    var trip = await ReadRegsAsync(stream, tx, D_Trip, 1);         // D1550
                    var runtime = await ReadRegsAsync(stream, tx, D_RunTime, 2);   // D1600~D1601

                    // ---- M 区（M0~M80）----
                    var coils = await ReadBitsAsync(stream, tx, 0x01, 0, 81);

                    // ---- X 区（X0~X16，基址 63488）----
                    var inputs = await ReadBitsAsync(stream, tx, 0x02, XBase, 17);

                    Render(settings, current, trip, runtime, coils, inputs);
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                DrawLine($"连接失败：{ex.Message}，2 秒后重试…");
                await Task.Delay(2000);
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private static void Render(ushort[] settings, ushort[] current, ushort[] trip, ushort[] runtime, bool[] coils, bool[] inputs)
    {
        static string OnOff(bool v) => v ? "ON " : "OFF";

        DrawLine("╔═══════════════════════ D 区（保持寄存器） ═══════════════════════╗");
        DrawLine($"  设定时间 D1000 (ms)     : {Read32(settings, 0),12}");
        DrawLine($"  设定电流 D1002 (kA×1000): {Read32(settings, 2),12}   ({Read32(settings, 2) / 1000.0:F3} kA)");
        DrawLine($"  工位2 设定 D1006/D1008  : {Read32(settings, 6),12} / {Read32(settings, 8)}");
        DrawLine($"  工位3 设定 D1012/D1014  : {Read32(settings, 12),12} / {Read32(settings, 14)}");
        DrawLine($"  实时电流 D1520 (A)      : {Read32(current, 0),12}   ({Read32(current, 0) / 1000.0:F3} kA)");
        DrawLine($"  脱扣电流 D1550 (kA×1000): {trip[0],12}   ({trip[0] / 1000.0:F3} kA)");
        DrawLine($"  运行时间 D1600 (ms)     : {Read32(runtime, 0),12}   ({Read32(runtime, 0) / 1000.0:F2} s)");
        DrawLine("╠═══════════════════════ M 区（线圈） ═════════════════════════════╣");
        DrawLine($"  M0 充电: {OnOff(coils[0])}    M1 启动: {OnOff(coils[1])}    " +
                 $"M3 停止: {OnOff(coils[3])}    M80 完成: {OnOff(coils[80])}");
        DrawLine("╠═══════════════════════ X 区（离散输入，63488 起） ════════════════╣");
        DrawLine($"  X0 急停: {OnOff(inputs[0])}    X1 急停: {OnOff(inputs[1])}    " +
                 $"X3 充电完成: {OnOff(inputs[3])}");
        DrawLine($"  X4: {OnOff(inputs[4])}  X5: {OnOff(inputs[5])}  " +
                 $"X6: {OnOff(inputs[6])}  X7: {OnOff(inputs[7])}   （任一 OFF = 告警）");
        DrawLine($"  刷新时间: {DateTime.Now:HH:mm:ss.fff}");
    }

    private static int Read32(ushort[] words, int offset) => words[offset] | (words[offset + 1] << 16);

    private static readonly object ConsoleLock = new();

    private static void DrawLine(string line)
    {
        lock (ConsoleLock)
        {
            var width = Math.Max(Console.WindowWidth - 1, line.Length);
            Console.WriteLine(line.PadRight(width));
        }
    }

    // ---------------- Modbus 主站（只读 FC01/02/03） ----------------
    private sealed class Tx
    {
        public ushort Id;   // Modbus 事务号（逐次递增）
    }

    private static async Task<ushort[]> ReadRegsAsync(NetworkStream s, Tx tx, ushort address, ushort count)
    {
        var pdu = await TransactAsync(s, tx, 0x03, address, count);
        var data = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        }
        return data;
    }

    private static async Task<bool[]> ReadBitsAsync(NetworkStream s, Tx tx, byte fc, ushort address, ushort count)
    {
        var pdu = await TransactAsync(s, tx, fc, address, count);
        var data = new bool[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = ((pdu[2 + i / 8] >> (i % 8)) & 1) == 1;
        }
        return data;
    }

    private static async Task<byte[]> TransactAsync(NetworkStream s, Tx tx, byte fc, ushort address, ushort count)
    {
        var req = new byte[]
        {
            (byte)(tx.Id >> 8), (byte)tx.Id,        // 事务号
            0x00, 0x00,                             // 协议号
            0x00, 0x06,                             // 长度
            0x01,                                   // 单元号
            fc,
            (byte)(address >> 8), (byte)address,
            (byte)(count >> 8), (byte)count,
        };
        tx.Id++;

        await s.WriteAsync(req);

        var header = new byte[7];
        await ReadExactlyAsync(s, header);
        var length = (header[4] << 8) | header[5];
        var pdu = new byte[length - 1];
        await ReadExactlyAsync(s, pdu);

        if (pdu.Length >= 2 && (pdu[0] & 0x80) != 0)
        {
            throw new IOException($"Modbus 异常码 {pdu[1]}（FC{fc} 地址 {address}）");
        }
        return pdu;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset));
            if (n == 0) throw new IOException("连接已关闭");
            offset += n;
        }
    }
}
