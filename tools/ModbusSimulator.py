# -*- coding: utf-8 -*-
# ============================================================================
# 文件：tools/ModbusSimulator.py
# 作用：TripTestBench 上位机的 Modbus TCP 从机模拟器（模拟汇川 EASY320 点位）。
#       完整模拟一次试验流程，可用于无 PLC 环境下联调上位机：
#         1) M0=ON（充电） 2 秒后 → X3=ON（充电完成）
#         2) M1=ON（开始）→ 读取 D1000/D1002 设定值，D1600 从设定时间倒数到 0
#            期间 D1520 输出带波动的电流，结束后 D1550 给出脱扣电流、M80=ON
#         3) M3=ON（停止）→ 电流/运行时间清零，回到待机
# 用法：
#   pip install pymodbus==3.6.9
#   python tools/ModbusSimulator.py
#   上位机“通讯设置”里把 IP 改为 127.0.0.1（端口 502 不变）即可连接
# 注意：字序与上位机一致——int32 低字在低地址（low word first）。
# ============================================================================

import asyncio
import random
import time

from pymodbus.datastore import ModbusSequentialDataBlock, ModbusServerContext, ModbusSlaveContext
from pymodbus.server.async_io import StartAsyncTcpServer

PORT = 502

# ---------------- D 区（保持寄存器，地址 = D 编号） ----------------
D_SET_TIME = 1000       # int32 设定时间（s ×1000 = ms）
D_SET_CURRENT = 1002    # int32 设定电流（kA ×1000）
D_CURRENT = 1520        # int32 实时电流（A）
D_TRIP = 1550           # 16 位脱扣电流（kA ×1000）
D_RUNTIME = 1600        # int32 运行时间（ms，倒数到 0 表示试验结束）

# ---------------- M 区（线圈，地址 = M 编号） ----------------
M_MAIN = 0              # M0 充电（主回路）
M_START = 1             # M1 开始测试
M_STOP = 3              # M3 停止测试
M_COMPLETE = 80         # M80 完成标志

# ---------------- X 区（离散输入，地址 = 63488 + X 编号） ----------------
XB = 63488
X_EMG1 = XB + 0         # X0 急停（常闭，正常 ON）
X_EMG2 = XB + 1         # X1 急停（常闭，正常 ON）
X_CHARGE_OK = XB + 3    # X3 充电完成
X_ALARMS = [XB + i for i in range(4, 8)]  # X4~X7 告警（常闭，正常 ON）


def words32(value: int):
    """int32 → [低字, 高字]（低字在低地址，与上位机 ToInt32 一致）。"""
    return [value & 0xFFFF, (value >> 16) & 0xFFFF]


def read32(store, address: int) -> int:
    lo, hi = store.getValues(3, address, 2)
    return lo | (hi << 16)


def write32(store, address: int, value: int):
    store.setValues(3, address, words32(value))


async def simulate(store: ModbusSlaveContext):
    """后台任务：监视 M0/M1/M3，模拟充电 → 测试 → 完成的完整流程。"""
    state = "idle"
    charge_started = 0.0
    set_current_raw = 0

    # 常闭触点初始状态：急停 X0/X1、告警 X4~X7 均为 ON（正常）
    store.setValues(2, X_EMG1, [True])
    store.setValues(2, X_EMG2, [True])
    store.setValues(2, X_CHARGE_OK, [False])
    for a in X_ALARMS:
        store.setValues(2, a, [True])

    print("模拟器运行中：M0=充电 → X3 充电完成；M1=开始 → D1600 倒数到 0；M3=停止")

    while True:
        await asyncio.sleep(0.1)

        m0 = store.getValues(1, M_MAIN, 1)[0]
        m1 = store.getValues(1, M_START, 1)[0]
        m3 = store.getValues(1, M_STOP, 1)[0]
        x3 = store.getValues(2, X_CHARGE_OK, 1)[0]

        # ---- 停止：M3 = ON，一切归零回到待机 ----
        if m3:
            if state != "idle":
                print(">> M3=ON：停止测试，电流/运行时间清零，X3/M80 复位")
            state = "idle"
            write32(store, D_CURRENT, 0)
            write32(store, D_RUNTIME, 0)
            store.setValues(2, X_CHARGE_OK, [False])
            store.setValues(1, M_COMPLETE, [False])
            continue

        # ---- 充电：M0 = ON，2 秒后 X3 = ON ----
        if m0 and state == "idle":
            state = "charging"
            charge_started = time.monotonic()
            print(">> M0=ON：开始充电，2 秒后 X3=ON")

        if state == "charging" and not x3 and time.monotonic() - charge_started >= 2.0:
            store.setValues(2, X_CHARGE_OK, [True])
            print(">> 充电完成：X3=ON（等待 M1）")

        # ---- 开始测试：M1 = ON 且已充电完成 ----
        if m1 and x3 and state not in ("testing", "done"):
            set_current_raw = read32(store, D_SET_CURRENT)
            write32(store, D_RUNTIME, read32(store, D_SET_TIME))
            store.setValues(1, M_COMPLETE, [False])
            state = "testing"
            print(f">> M1=ON：开始测试，设定电流 {set_current_raw} (kA*1000)")

        # ---- 测试中：D1600 每 100ms 递减 100，D1520 输出带波动电流 ----
        if state == "testing":
            rt = max(read32(store, D_RUNTIME) - 100, 0)
            write32(store, D_RUNTIME, rt)
            noise = random.uniform(-0.03, 0.03) * set_current_raw
            write32(store, D_CURRENT, max(int(set_current_raw + noise), 0))

            if rt == 0:
                write32(store, D_CURRENT, 0)
                store.setValues(3, D_TRIP, [int(set_current_raw * 1.02)])  # 脱扣电流 102% → 合格
                store.setValues(1, M_COMPLETE, [True])
                state = "done"
                print(">> 试验完成：D1550 脱扣电流已写入，M80=ON")


async def main():
    store = ModbusSlaveContext(
        co=ModbusSequentialDataBlock(0, [False] * 1000),    # M0 ~ M80+
        di=ModbusSequentialDataBlock(0, [False] * 65536),   # X 区（63488 起）
        hr=ModbusSequentialDataBlock(0, [0] * 65536),       # D 区（1000~1601）
        ir=ModbusSequentialDataBlock(0, [0] * 65536),
        zero_mode=True,                                     # 地址原样使用，不做 +1 偏移
    )
    context = ModbusServerContext(slaves=store, single=True)
    asyncio.create_task(simulate(store))
    print(f"Modbus TCP 从机模拟器已启动：0.0.0.0:{PORT}")
    await StartAsyncTcpServer(context, address=("0.0.0.0", PORT))


if __name__ == "__main__":
    asyncio.run(main())
