// =====================================================================
//  Reader.cs  --  UHF RFID reader wrapper, polling thread
//  通信方式：USB 虚拟串口（KL9007R 系列），默认 57600 波特率(baud=5)
//
//  本版新增（对应“刷卡距离限制”需求）：
//   1. SetPower()/RefreshPower()：通过 SDK 的 SetPowerDbm/Getpower 把射频输出
//      功率从出厂最大值降到指定 dBm。UHF 读卡距离与功率近似成正比，降功率
//      就是物理意义上的“限距”，可避免把隔壁货架/整箱设备一起误读进来。
//   2. 连续命中确认：标签必须在连续若干轮盘点中都被读到（允许漏一轮）才算一次
//      有效刷卡；距离边缘的偶发读到只会被“看见一两次”，一律判为距离未确认并
//      通过 OnTagRejected 上报，界面提示“距离过远/读取不稳定”，不产生借还记录。
//   3. 边沿触发：同一标签在未离开天线范围前只上报一次，彻底避免“借出后标签
//      仍放在读卡器上导致立即自动归还”的问题。
//   4. 所有 SDK 调用加锁串行化，避免界面线程设置功率与轮询线程抢串口。
//
//  容错：轮询异常自动降速；连续失败视为 USB 断开，触发 OnDisconnected
// =====================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using ReaderB;

namespace LabBorrow
{
    public class TagEventArgs : EventArgs
    {
        public string Epc;
        public DateTime Time;
        public TagEventArgs(string e, DateTime t) { Epc = e; Time = t; }
    }

    /// <summary>距离未确认 / 读取不稳定等“非有效刷卡”事件，用于界面提示与审计</summary>
    public class TagRejectEventArgs : EventArgs
    {
        public string Epc;
        public DateTime Time;
        public string Reason;
        public int Hits;          // 实际命中轮数
        public int NeedHits;      // 需要的命中轮数
        public TagRejectEventArgs(string e, DateTime t, string reason, int hits, int need)
        { Epc = e; Time = t; Reason = reason; Hits = hits; NeedHits = need; }
    }

    /// <summary>读卡器运行参数（由 SwipeRules 映射而来，运行时可整体替换）</summary>
    public class ReaderOptions
    {
        public int PollIntervalMs = 250;   // 盘点间隔
        public int HitConfirmTimes = 2;    // 连续命中确认轮数，1=关闭确认
        public int TagCooldownMs = 600;    // 同一标签两次上报的最小间隔
        public int RfPowerDbm = 20;        // 射频功率；0=不修改读卡器功率
        public int MinTotalHits = 3;       // 滑动窗口内累计命中次数门槛，1=关闭窗口过滤
        public int MaxReadWindowMs = 2500; // 累计命中窗口总时长（毫秒）

        public ReaderOptions Clone()
        {
            return new ReaderOptions
            {
                PollIntervalMs = PollIntervalMs,
                HitConfirmTimes = HitConfirmTimes,
                TagCooldownMs = TagCooldownMs,
                RfPowerDbm = RfPowerDbm,
                MinTotalHits = MinTotalHits,
                MaxReadWindowMs = MaxReadWindowMs
            };
        }
    }

    public class Reader : IDisposable
    {
        public bool Connected { get; private set; }
        public int PortIndex { get; private set; }
        public byte ComAdr = 0xFF;
        public string LastError { get; private set; }
        /// <summary>读卡器当前射频功率(dBm)，-1 表示未知</summary>
        public int CurrentPowerDbm { get; private set; }

        public event Action<string> OnLog;
        public event EventHandler<TagEventArgs> OnTag;
        public event EventHandler<TagRejectEventArgs> OnTagRejected;  // 距离/稳定性未确认
        public event Action OnDisconnected;   // USB 断开 / 通信异常

        private Thread pollThread;
        private volatile bool pollingFlag;
        private volatile ReaderOptions options;
        private readonly object ioLock = new object();   // 串口操作串行化

        // 距离限制 / 边沿触发的软件闸门（纯逻辑，见 SwipeGuard.cs 的 TagGate）
        private readonly TagGate gate = new TagGate();
        private int lastPruneRound;

        public Reader(ReaderOptions opt)
        {
            options = opt != null ? opt.Clone() : new ReaderOptions();
            PortIndex = -1;
            CurrentPowerDbm = -1;
        }

        public ReaderOptions Options { get { return options; } }

        /// <summary>运行中热更新参数（设置页保存后调用），下一轮盘点生效</summary>
        public void UpdateOptions(ReaderOptions opt)
        {
            if (opt == null) return;
            options = opt.Clone();
        }

        // ----------------------------------------------------------------
        //  连接
        // ----------------------------------------------------------------
        public bool Open()
        {
            try
            {
                int port = 0;
                int idx = 0;
                byte baud = 5;
                int ret;
                byte adr = 0xFF;
                lock (ioLock) { ret = StaticClassReaderB.AutoOpenComPort(ref port, ref adr, baud, ref idx); }
                if (ret != 0) { SetError(ret); return false; }
                ComAdr = adr;
                PortIndex = idx;
                Connected = true;
                LastError = "";
                Log("已连接端口 COM" + idx + "，地址 0x" + ComAdr.ToString("X2"));
                ApplyConfiguredPower();
                return true;
            }
            catch (Exception ex) { LastError = "打开串口异常: " + ex.Message; Log(LastError); return false; }
        }

        // 显式打开指定 COM 端口（用于手动选择）
        public bool Open(int comPort, byte baud)
        {
            try
            {
                byte adr = 0xFF;
                int handle = -1;
                int ret;
                lock (ioLock) { ret = StaticClassReaderB.OpenComPort(comPort, ref adr, baud, ref handle); }
                if (ret != 0) { SetError(ret, "COM" + comPort); return false; }
                PortIndex = handle;
                ComAdr = adr;
                Connected = true;
                LastError = "";
                Log("已连接端口 COM" + comPort + "，地址 0x" + ComAdr.ToString("X2"));
                ApplyConfiguredPower();
                return true;
            }
            catch (Exception ex) { LastError = "打开串口异常: " + ex.Message; Log(LastError); return false; }
        }

        private void SetError(int ret, string portHint = "")
        {
            string code = "0x" + ret.ToString("X2");
            string msg;
            switch (ret)
            {
                case 0x30: msg = "读卡器无响应（请检查电源/重新插拔 USB 后重试）"; break;
                case 0x31: msg = "串口通信超时"; break;
                case 0x35: msg = "串口打开失败（端口不存在或被占用）"; break;
                case 0x37: msg = "波特率设置失败"; break;
                case 0xFF: msg = "未找到可用读卡器"; break;
                default:   msg = "读卡器返回错误码 " + code; break;
            }
            LastError = (string.IsNullOrEmpty(portHint) ? "" : portHint + " ") + msg + " [" + code + "]";
            Log("连接失败: " + LastError);
        }

        public void Close()
        {
            StopPoll();
            if (Connected && PortIndex >= 0)
            {
                try { lock (ioLock) { StaticClassReaderB.CloseSpecComPort(PortIndex); } } catch { }
                Log("已关闭端口");
            }
            Connected = false;
            PortIndex = -1;
            ResetTagState();
        }

        // ----------------------------------------------------------------
        //  射频功率（刷卡距离限制的硬件手段）
        // ----------------------------------------------------------------
        private void ApplyConfiguredPower()
        {
            ReaderOptions o = options;
            if (o.RfPowerDbm > 0) SetPower(o.RfPowerDbm);
            else
            {
                RefreshPower();
                Log("射频功率：保持读卡器出厂设置（当前 " + PowerText() + "）");
            }
        }

        /// <summary>设置射频功率(dBm)。返回 false 时读卡器仍可正常使用，只是距离未受控。</summary>
        public bool SetPower(int dbm)
        {
            if (!Connected || PortIndex < 0) { LastError = "读卡器未连接，无法设置射频功率"; return false; }
            int p = dbm; if (p < 0) p = 0; if (p > 30) p = 30;
            try
            {
                int ret;
                lock (ioLock) { ret = StaticClassReaderB.SetPowerDbm(ref ComAdr, (byte)p, PortIndex); }
                if (ret != 0)
                {
                    Log("设置射频功率失败，返回 0x" + ret.ToString("X2") + "（沿用读卡器当前功率 " + PowerText() + "）");
                    RefreshPower();
                    return false;
                }
                RefreshPower();
                Log("射频功率已设为 " + p + " dBm（读回 " + PowerText() + "），读卡距离随之缩短");
                return true;
            }
            catch (Exception ex)
            {
                Log("设置射频功率异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>读回读卡器当前功率，-1 表示读取失败</summary>
        public int RefreshPower()
        {
            if (!Connected || PortIndex < 0) { CurrentPowerDbm = -1; return -1; }
            try
            {
                byte p = 0;
                int ret;
                lock (ioLock) { ret = StaticClassReaderB.Getpower(ref ComAdr, ref p, PortIndex); }
                if (ret == 0) { CurrentPowerDbm = p; return p; }
            }
            catch { }
            return CurrentPowerDbm;
        }

        public string PowerText()
        {
            return CurrentPowerDbm >= 0 ? (CurrentPowerDbm + " dBm") : "未知";
        }

        /// <summary>读卡器信息（版本/类型/功率/盘点时间），失败返回空串；仅供设置页展示</summary>
        public string ReadInfo()
        {
            if (!Connected || PortIndex < 0) return "";
            try
            {
                byte[] ver = new byte[8];
                byte readerType = 0;
                byte[] trType = new byte[8];
                byte dmax = 0, dmin = 0, power = 0, scan = 0;
                int ret;
                lock (ioLock)
                {
                    ret = StaticClassReaderB.GetReaderInformation(ref ComAdr, ver, ref readerType,
                        trType, ref dmax, ref dmin, ref power, ref scan, PortIndex);
                }
                if (ret != 0) return "";
                CurrentPowerDbm = power;
                string verText = "";
                for (int i = 0; i < ver.Length; i++)
                    if (ver[i] != 0) verText += ((char)ver[i]).ToString();
                return string.Format("版本 {0} / 类型 0x{1:X2} / 功率 {2} dBm / 盘点时间 {3}",
                    string.IsNullOrEmpty(verText) ? "?" : verText, readerType, power, scan);
            }
            catch { return ""; }
        }

        // ----------------------------------------------------------------
        //  轮询
        // ----------------------------------------------------------------
        public void StartPoll()
        {
            if (!Connected) return;
            if (pollThread != null && pollThread.IsAlive) return;
            pollingFlag = true;
            pollThread = new Thread(PollLoop) { IsBackground = true };
            pollThread.Start();
        }

        public void StopPoll()
        {
            pollingFlag = false;
            if (pollThread != null) { pollThread.Join(500); pollThread = null; }
            ResetTagState();
        }

        private void ResetTagState()
        {
            gate.Reset();
        }

        private void PollLoop()
        {
            byte[] epcBuf = new byte[8192];
            int failCount = 0;                 // 连续通信失败计数
            const int FailThreshold = 5;       // 连续 5 次失败 => 判定 USB 断开
            var seen = new List<string>();

            while (pollingFlag)
            {
                ReaderOptions o = options;
                try
                {
                    int totalLen = 0, cardNum = 0;
                    int ret;
                    lock (ioLock)
                    {
                        ret = StaticClassReaderB.Inventory_G2(ref ComAdr, 0, 0, 0, epcBuf, ref totalLen, ref cardNum, PortIndex);
                    }
                    if (ret == 1 || ret == 2 || ret == 3 || ret == 4 || ret == 0xFB)
                    {
                        failCount = 0;         // 通信正常（含“无标签”情形）
                        seen.Clear();

                        if (cardNum > 0 && totalLen > 0)
                        {
                            int pos = 0;
                            for (int i = 0; i < cardNum && pos < totalLen; i++)
                            {
                                int epcLen = epcBuf[pos++];
                                if (epcLen <= 0 || pos + epcLen > totalLen) break; // 脏数据保护
                                string hex = BitConverter.ToString(epcBuf, pos, epcLen).Replace("-", "");
                                pos += epcLen;
                                if (!seen.Contains(hex)) seen.Add(hex);
                            }
                        }

                        // ---- 交给距离闸门判定：命中确认 + 边沿触发 + 上报冷却 ----
                        var now = DateTime.Now;
                        lock (gate)
                        {
                            gate.HitConfirmTimes = o.HitConfirmTimes;
                            gate.TagCooldownMs = o.TagCooldownMs;
                            gate.MinTotalHits = o.MinTotalHits;
                            gate.MaxReadWindowMs = o.MaxReadWindowMs;
                            gate.BeginRound();
                            for (int i = 0; i < seen.Count; i++) gate.Seen(seen[i]);
                            gate.FinishRound(now);

                            for (int i = 0; i < gate.Confirmed.Count; i++)
                            {
                                string hex = gate.Confirmed[i];
                                var h = OnTag; if (h != null) h(this, new TagEventArgs(hex, now));
                            }
                            for (int i = 0; i < gate.Unconfirmed.Count; i++)
                            {
                                string hex = gate.Unconfirmed[i];
                                var rh = OnTagRejected;
                                if (rh != null)
                                    rh(this, new TagRejectEventArgs(hex, now,
                                        "读取不稳定，可能是距离过远/标签未对准天线", 1, o.HitConfirmTimes));
                            }

                            // 定期清理长期不出现的标签，避免字典无限增长
                            if (gate.Round - lastPruneRound >= 400)
                            {
                                lastPruneRound = gate.Round;
                                gate.PruneStale(500);
                            }
                        }
                    }
                    else if (ret != 0)
                    {
                        failCount++;
                        Log("Inventory 返回 0x" + ret.ToString("X2") + " (连续失败 " + failCount + ")");
                        if (failCount >= FailThreshold)
                        {
                            // USB 断开 / 通信异常：关闭句柄、停止轮询并通知 UI，程序不崩溃
                            pollingFlag = false;
                            Connected = false;
                            try { lock (ioLock) { StaticClassReaderB.CloseSpecComPort(PortIndex); } } catch { }
                            PortIndex = -1;
                            var d = OnDisconnected; if (d != null) d();
                            break;
                        }
                        Thread.Sleep(800);
                    }
                }
                catch (Exception ex)
                {
                    // 读卡器被拔出时底层可能抛异常：同样按断连处理，绝不让异常穿透到 UI
                    failCount++;
                    Log("Poll 异常: " + ex.Message + " (连续失败 " + failCount + ")");
                    if (failCount >= FailThreshold)
                    {
                        pollingFlag = false;
                        Connected = false;
                        try { lock (ioLock) { StaticClassReaderB.CloseSpecComPort(PortIndex); } } catch { }
                        PortIndex = -1;
                        var d = OnDisconnected; if (d != null) d();
                        break;
                    }
                    Thread.Sleep(500);
                }
                int sleep = options.PollIntervalMs;
                if (sleep < 50) sleep = 50;
                Thread.Sleep(sleep);
            }
        }

        private void Log(string m)
        {
            var h = OnLog;
            if (h != null) h(m);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
