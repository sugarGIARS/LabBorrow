// =====================================================================
//  LabBorrow  --  校园卡 / 13.56MHz NFC 读卡支持
//
//  【为什么现在刷不了校园卡】
//    本机现在这套读写器是「超高频 UHF RFID」（国标/ISO 18000-6C，860~960MHz，
//    读的是贴在设备上的 EPC 标签）。
//    而江南大学的校园卡是「高频 HF RFID」（13.56MHz，Mifare / CPU 卡，ISO 14443）。
//    两者的工作频率、天线、通信协议完全不同 —— 就像 FM 收音机收不到 Wi-Fi 一样，
//    把校园卡放到现在的天线上一张也读不到，这不是程序 bug，是物理规格不同。
//
//  【怎么解决】
//    Windows 自带「智能卡」体系 PC/SC，几乎所有 13.56MHz 读卡器（几十元钱的
//    USB 免驱读卡器，比如 ACR122U、各品牌校园卡读卡器）插上就能被系统识别。
//    本模块通过 PC/SC 读取校园卡的卡号(UID)——这张全球唯一的序列号，
//    正好可以用来识别"这是谁的卡"，存进 users.card_epc 字段即可，
//    UHF 设备标签与 NFC 校园卡互不冲突，各走各的路。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LabBorrow
{
    public class NfcEventArgs : EventArgs
    {
        public string CardId;          // 卡号（十六进制，如 "A1B2C3D4"）
        public DateTime Time;
        public NfcEventArgs(string id, DateTime t) { CardId = id; Time = t; }
    }

    public class NfcReader : IDisposable
    {
        // ---------------- PC/SC API ----------------
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardEstablishContext(int scope, IntPtr r1, IntPtr r2, out IntPtr ctx);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardReleaseContext(IntPtr ctx);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardListReaders(IntPtr ctx, string groups, IntPtr buf, ref int len);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardConnect(IntPtr ctx, string reader, int share, int proto,
                                               out IntPtr card, out int activeProto);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardDisconnect(IntPtr card, int disposition);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardBeginTransaction(IntPtr card);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardEndTransaction(IntPtr card, int disposition);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        private static extern int SCardTransmit(IntPtr card, IntPtr pioSend, byte[] sendBuf, int sendLen,
                                                IntPtr pioRecv, byte[] recvBuf, ref int recvLen);

        private const int SCARD_SCOPE_USER = 0;
        private const int SCARD_SHARE_SHARED = 2;
        private const int SCARD_PROTOCOL_T0 = 1;
        private const int SCARD_PROTOCOL_T1 = 2;
        private const int SCARD_LEAVE_CARD = 0;
        private const int SCARD_RESET_CARD = 1;

        // 通用地讀取 UID 的伪 APDU（PC/SC v2 Part 3：读 UID / ATR）
        private static readonly byte[] CmdGetUid = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };
        // 兼容有些 Windows 内置驱动：先取回 ATR，再 fallback 到 GET DATA
        private static readonly byte[] CmdGetData = { 0xFF, 0xCA, 0x00, 0x00, 0x04 };

        public event EventHandler<NfcEventArgs> OnCard;          // 读到一张卡（含重复上报）
        public event Action<string> OnLog;
        public string LastError { get; private set; }
        public bool Polling { get { return polling; } }

        private Thread th;
        private volatile bool polling;
        private IntPtr ctx = IntPtr.Zero;
        private string readerName = "";
        private string lastSeen = "";       // 同一个卡持续放在上面，只上报一次
        private DateTime lastCardTime = DateTime.MinValue;

        /// <summary>列出本机可用的 PC/SC 读卡器名称</summary>
        public static List<string> ListReaderNames()
        {
            var list = new List<string>();
            IntPtr ctx = IntPtr.Zero;
            try
            {
                if (SCardEstablishContext(SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx) != 0) return list;
                int len = 0;
                SCardListReaders(ctx, null, IntPtr.Zero, ref len);
                if (len <= 0) return list;
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (SCardListReaders(ctx, null, buf, ref len) == 0)
                    {
                        string multi = Marshal.PtrToStringUni(buf, len / 2);
                        foreach (var s in multi.Split('\0'))
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            finally { try { if (ctx != IntPtr.Zero) SCardReleaseContext(ctx); } catch { } }
            return list;
        }

        /// <summary>本机有没有可用的 NFC 读卡器</summary>
        public static bool HasReader { get { return ListReaderNames().Count > 0; } }

        public bool Open(string preferredReader)
        {
            try
            {
                LastError = "";
                var names = ListReaderNames();
                if (names.Count == 0) { LastError = "没有检测到 NFC 读卡器（需要 13.56MHz 的 PC/SC 读卡器，USB 免驱那种）"; return false; }

                readerName = "";
                if (!string.IsNullOrEmpty(preferredReader))
                    foreach (var n in names)
                        if (string.Equals(n, preferredReader, StringComparison.OrdinalIgnoreCase)) { readerName = n; break; }
                if (string.IsNullOrEmpty(readerName)) readerName = names[0];

                if (SCardEstablishContext(SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx) != 0)
                {
                    LastError = "无法连接智能卡服务（PC/SC），请确认 Windows 的 Smart Card 服务已启动";
                    return false;
                }
                Log("NFC 读卡器已就绪：" + readerName);
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        public void StartPoll()
        {
            if (polling) return;
            polling = true;
            th = new Thread(PollLoop) { IsBackground = true };
            th.Start();
        }

        public void StopPoll()
        {
            polling = false;
            try { if (th != null && th.IsAlive) th.Join(900); } catch { }
        }

        private void PollLoop()
        {
            while (polling)
            {
                try
                {
                    string id = TryReadOnce();
                    if (!string.IsNullOrEmpty(id))
                    {
                        // 同一张卡拿着不动，隔 2.5 秒才允许再报一次（防止重复触发借还）
                        bool fresh = id != lastSeen || (DateTime.Now - lastCardTime).TotalSeconds > 2.5;
                        if (fresh)
                        {
                            lastSeen = id;
                            lastCardTime = DateTime.Now;
                            var h = OnCard;
                            if (h != null) h(this, new NfcEventArgs(id, DateTime.Now));
                        }
                    }
                    else
                    {
                        lastSeen = "";      // 卡拿走了
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
        }

        /// <summary>尝试读一次卡号；没卡返回空串</summary>
        public string TryReadOnce()
        {
            if (ctx == IntPtr.Zero) return "";
            IntPtr card = IntPtr.Zero;
            try
            {
                int proto;
                int rc = SCardConnect(ctx, readerName, SCARD_SHARE_SHARED,
                                      SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out card, out proto);
                if (rc != 0) return "";

                string uid = GetUid(card);
                if (string.IsNullOrEmpty(uid)) return "";
                // 统一大写十六进制，不留空格（数据库里就是这个格式）
                uid = uid.Replace(" ", "").Replace("-", "").ToUpperInvariant();
                return uid;
            }
            catch { return ""; }
            finally
            {
                try { if (card != IntPtr.Zero) SCardDisconnect(card, SCARD_LEAVE_CARD); } catch { }
            }
        }

        private static string GetUid(IntPtr card)
        {
            foreach (var cmd in new[] { CmdGetUid, CmdGetData })
            {
                try
                {
                    if (SCardBeginTransaction(card) != 0) continue;
                    var recv = new byte[64];
                    int recvLen = recv.Length;
                    int rc = SCardTransmit(card, IntPtr.Zero, cmd, cmd.Length, IntPtr.Zero, recv, ref recvLen);
                    SCardEndTransaction(card, SCARD_LEAVE_CARD);
                    if (rc != 0 || recvLen < 2) continue;

                    // 成功：前 recvLen-2 字节是 UID，最后 2 字节是状态字 SW（90 00 表示成功）
                    byte[] data = new byte[recvLen - 2];
                    Array.Copy(recv, 0, data, 0, data.Length);
                    if (data.Length == 0) continue;

                    var sb = new StringBuilder();
                    foreach (var b in data) sb.Append(b.ToString("X2"));
                    // 7 字节/10 字节 UID 里混有非法的 0x8x 也正常，直接返回
                    return sb.ToString();
                }
                catch { }
            }
            return "";
        }

        private void Log(string m) { try { if (OnLog != null) OnLog(m); } catch { } }

        public void Dispose()
        {
            StopPoll();
            try { if (ctx != IntPtr.Zero) SCardReleaseContext(ctx); } catch { }
            ctx = IntPtr.Zero;
        }
    }
}
