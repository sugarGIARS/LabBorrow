// =====================================================================
//  LabBorrow  --  归还到期提醒
//
//  老师提的核心问题是「怎么提醒借用者记得还」。
//  真人不一定站在机器前，所以这里同时给出三种提醒方式：
//    1) 借出的那一刻：屏幕大字 + 语音直接说出归还截止时间；
//    2) 平时：定时扫描，主控台出现醒目的到期横幅；
//    3) 需要时：一键打开「到期名单」窗口（可按下面联系），还能导出成表格。
//  语音走系统自带的 SAPI 朗读引擎（不用联网、不用额外依赖，
//  机器上没装中文语音时会自动跳过，不影响其它功能）。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace LabBorrow
{
    /// <summary>一条需要处理到期 / 逾期的借用</summary>
    public class RemindItem
    {
        public string Borrower = "";      // 借用人姓名
        public string UserNo = "";        // 学号 / 工号（便于联系）
        public string ClassName = "";     // 班级 / 部门
        public string Phone = "";         // 手机号（可空，催还时用于联系本人）
        public string Email = "";         // 邮箱（可空，自动邮件提醒用）
        public string Epc = "";           // 设备标签（用于「同一台设备不重复发邮件」）
        public string MailNotifiedAt = "";// 上次对该设备发催还邮件的时间
        public string DeviceName = "";    // 设备名
        public string Category = "";      // 分类
        public string DueTime = "";       // 应还时间
        public bool Overdue;              // 是否已逾期
        public double Hours;              // 距到期还有多少小时（逾期时为已超期小时数）
    }

    public static class Reminder
    {
        // ------------------------------------------------------------
        //  扫描：找出「临期」和「逾期」的借用
        // ------------------------------------------------------------
        /// <summary>
        /// 扫描需要处理提醒的记录。
        /// 若 leadHours=24 且含逾期 → 距应还不足 24 小时的，以及所有逾期的都会进名单。
        /// </summary>
        public static List<RemindItem> Scan(int leadHours, bool includeOverdue)
        {
            var res = new List<RemindItem>();
            try
            {
                // 学号/班级按姓名补进去（借还记录里只存了姓名）
                var users = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in Db.ListUsers(null))
                    users[u.Name] = u;

                var now = DateTime.Now;
                // ListLent 内部会按 warnHours 判定「临期」，正好用它做筛选
                foreach (var it in Db.ListLent(Math.Max(1, leadHours)))
                {
                    bool overdue = it.State == DueUtil.StateOverdue;
                    if (overdue && !includeOverdue) continue;
                    if (!overdue && it.State != DueUtil.StateDueSoon) continue;   // 还早，不用提醒

                    var r = new RemindItem
                    {
                        Borrower = it.Borrower ?? "",
                        DeviceName = it.Name ?? "",
                        DueTime = it.DueTime ?? "",
                        Overdue = overdue,
                        Hours = overdue ? it.OverdueHours : 0,
                        Epc = it.Epc ?? "",
                        MailNotifiedAt = it.MailNotifiedAt ?? ""
                    };

                    DateTime due;
                    if (!overdue && DateTime.TryParse(r.DueTime, out due))
                        r.Hours = Math.Max(0, (due - now).TotalHours);

                    User u;
                    if (users.TryGetValue(r.Borrower, out u))
                    {
                        r.UserNo = u.UserNo ?? "";
                        r.ClassName = u.ClassName ?? "";
                        r.Phone = u.Phone ?? "";
                        r.Email = u.Email ?? "";
                    }
                    res.Add(r);
                }

                // 逾期在前、临期在后；同类里按剩余时间排
                res.Sort((a, b) =>
                {
                    if (a.Overdue != b.Overdue) return a.Overdue ? -1 : 1;
                    return a.Overdue ? b.Hours.CompareTo(a.Hours) : a.Hours.CompareTo(b.Hours);
                });
            }
            catch { }
            return res;
        }

        /// <summary>一句话概括当前有几件临期/逾期（给横幅和语音用）</summary>
        public static string Summary(List<RemindItem> items)
        {
            int due = 0, over = 0;
            foreach (var it in items) { if (it.Overdue) over++; else due++; }
            var sb = new System.Text.StringBuilder();
            if (over > 0) sb.Append(over).Append(" 件已逾期");
            if (due > 0)
            {
                if (sb.Length > 0) sb.Append("，");
                sb.Append(due).Append(" 件即将到期");
            }
            if (sb.Length == 0) return "";
            return sb.Append("　→　点「到期名单」生成催还消息发给同学本人").ToString();
        }

        /// <summary>某位借用人名下需要处理的项目（用于他本人刷卡时，当面对着他提醒）</summary>
        public static List<RemindItem> ItemsOf(List<RemindItem> all, string borrower)
        {
            var res = new List<RemindItem>();
            if (all == null || string.IsNullOrEmpty(borrower)) return res;
            foreach (var it in all)
                if (string.Equals(it.Borrower, borrower, StringComparison.OrdinalIgnoreCase)) res.Add(it);
            return res;
        }

        /// <summary>
        /// 本人在场时的提醒语（借出成功 / 归还成功之后，念给他本人听）。
        /// 这是最有效的一次提醒：人就在机器前，而且说的正是他借的东西。
        /// </summary>
        public static string OnSpotPhrase(string borrower, List<RemindItem> mine)
        {
            if (mine == null || mine.Count == 0) return "";
            int over = 0;
            var overNames = new List<string>();
            var soonNames = new List<string>();
            string soonTime = "";
            foreach (var it in mine)
            {
                if (it.Overdue) { over++; overNames.Add(it.DeviceName); }
                else { soonNames.Add(it.DeviceName); if (soonTime == "") soonTime = SpeakDue(it.DueTime); }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(string.IsNullOrEmpty(borrower) ? "提醒" : borrower + "同学，提醒一下");
            sb.Append("：");
            if (over > 0)
            {
                sb.Append("你借的");
                for (int i = 0; i < overNames.Count && i < 3; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(overNames[i]);
                }
                sb.Append("已经逾期，请尽快归还。");
            }
            if (soonNames.Count > 0)
            {
                sb.Append("你借的");
                for (int i = 0; i < soonNames.Count && i < 3; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(soonNames[i]);
                }
                sb.Append("请在").Append(soonTime).Append("前归还。");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成「催还消息」：按借用人分组，每人一段，可直接复制发到班级群/私聊/短信。
        /// 提醒的对象是借东西的同学本人，所以措辞是"你借的…请于…前归还"。
        /// </summary>
        public static string BuildUrgeText(List<RemindItem> items, string labName)
        {
            if (items == null || items.Count == 0) return "";

            // 按人分组，保持名单里「逾期在前」的顺序
            var order = new List<string>();
            var byPerson = new Dictionary<string, List<RemindItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                List<RemindItem> bucket;
                if (!byPerson.TryGetValue(it.Borrower, out bucket))
                {
                    bucket = new List<RemindItem>();
                    byPerson[it.Borrower] = bucket;
                    order.Add(it.Borrower);
                }
                bucket.Add(it);
            }

            var sb = new System.Text.StringBuilder();
            string lab = string.IsNullOrEmpty(labName) ? "实验室" : labName;
            sb.AppendLine("【" + lab + "设备归还提醒 " + DateTime.Now.ToString("MM-dd HH:mm") + "】");
            sb.AppendLine();

            foreach (var name in order)
            {
                var list = byPerson[name];
                sb.AppendLine(name + "同学" + (string.IsNullOrEmpty(list[0].UserNo) ? "" : "（" + list[0].UserNo + "）") + "：");
                foreach (var it in list)
                {
                    if (it.Overdue)
                        sb.AppendLine("　· 你借的「" + it.DeviceName + "」已逾期 "
                            + DueUtil.CompactSpan(TimeSpan.FromHours(it.Hours)) + "，请尽快归还！");
                    else
                        sb.AppendLine("　· 你借的「" + it.DeviceName + "」请于 " + ShortDue(it.DueTime) + " 前归还。");
                }
                sb.AppendLine();
            }
            sb.AppendLine("请按时归还，逾期将影响后续借用。谢谢配合！");
            return sb.ToString();
        }

        /// <summary>把「2026-10-30 18:00:00」说成人话：「10月30日晚上6点」</summary>
        public static string SpeakDue(string dueTime)
        {
            DateTime d;
            if (!DateTime.TryParse(dueTime, out d)) return "";
            string daypart = d.Hour < 6 ? "凌晨" : d.Hour < 12 ? "上午" : d.Hour < 18 ? "下午" : "晚上";
            int h12 = d.Hour % 12; if (h12 == 0) h12 = 12;
            return d.Month + "月" + d.Day + "日" + daypart + h12 + "点";
        }

        /// <summary>界面显示的短句：「10-30 18:00」</summary>
        public static string ShortDue(string dueTime)
        {
            DateTime d;
            if (!DateTime.TryParse(dueTime, out d)) return "";
            return d.ToString("MM-dd HH:mm");
        }

        // ------------------------------------------------------------
        //  语音播报（SAPI，系统自带朗读引擎）
        // ------------------------------------------------------------
        private static int speechState = -1;   // -1=未探测 0=不可用 1=可用且有中文 2=可用但可能没中文
        private static readonly object speakLock = new object();
        private const BindingFlags BFGet = BindingFlags.GetProperty | BindingFlags.InvokeMethod;
        private const BindingFlags BFSet = BindingFlags.SetProperty;

        /// <summary>这台机器上能不能说中文（探测一次后缓存）</summary>
        public static bool ChineseVoiceAvailable
        {
            get
            {
                if (speechState >= 0) return speechState == 1 || speechState == 2;
                try
                {
                    Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (t == null) { speechState = 0; return false; }
                    object v = Activator.CreateInstance(t);
                    try
                    {
                        object voices = t.InvokeMember("GetVoices", BFGet, null, v, null);
                        int n = voices == null ? 0 : Convert.ToInt32(voices.GetType().InvokeMember("Count", BFGet, null, voices, null));
                        if (n <= 0) { speechState = 0; return false; }
                        bool cn = false;
                        for (int i = 0; i < n; i++)
                        {
                            object voice = voices.GetType().InvokeMember("Item", BFGet, null, voices, new object[] { i });
                            object d = voice.GetType().InvokeMember("GetDescription", BFGet, null, voice, null);
                            string desc = d == null ? "" : d.ToString();
                            if (desc.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                                || desc.IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0
                                || desc.IndexOf("Simplified", StringComparison.OrdinalIgnoreCase) >= 0) { cn = true; break; }
                        }
                        speechState = cn ? 1 : 2;
                    }
                    finally
                    {
                        try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(v); } catch { }
                    }
                }
                catch { speechState = 0; }
                return speechState == 1 || speechState == 2;
            }
        }

        /// <summary>异步朗读一句话（中文会优先选中文音色）</summary>
        public static void Speak(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (speakLock)
            {
                try
                {
                    var th = new Thread(() =>
                    {
                        object v = null;
                        try
                        {
                            Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                            if (t == null) return;
                            v = Activator.CreateInstance(t);

                            // 尽量挑一个中文音色
                            try
                            {
                                object voices = t.InvokeMember("GetVoices", BFGet, null, v, null);
                                int n = Convert.ToInt32(voices.GetType().InvokeMember("Count", BFGet, null, voices, null));
                                if (n > 0)
                                {
                                    object voice = null;
                                    for (int i = 0; i < n; i++)
                                    {
                                        object cand = voices.GetType().InvokeMember("Item", BFGet, null, voices, new object[] { i });
                                        object d = cand.GetType().InvokeMember("GetDescription", BFGet, null, cand, null);
                                        string desc = d == null ? "" : d.ToString();
                                        if (desc.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                                            || desc.IndexOf("Simplified", StringComparison.OrdinalIgnoreCase) >= 0) { voice = cand; break; }
                                    }
                                    if (voice != null) t.InvokeMember("Voice", BFSet, null, v, new object[] { voice });
                                }
                            }
                            catch { }

                            try { t.InvokeMember("Volume", BFSet, null, v, new object[] { 100 }); } catch { }
                            try { t.InvokeMember("Rate", BFSet, null, v, new object[] { 0 }); } catch { }

                            // 2 = PurgeBeforeSpeak：有新的要说就清掉上一条，避免排队越积越长
                            t.InvokeMember("Speak", BFGet, null, v, new object[] { text, 2 });
                            try { t.InvokeMember("WaitUntilDone", BFGet, null, v, new object[] { 20000 }); } catch { }
                        }
                        catch { }
                        finally
                        {
                            try { if (v != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(v); } catch { }
                        }
                    });
                    th.SetApartmentState(ApartmentState.STA);
                    th.IsBackground = true;
                    th.Start();
                }
                catch { }
            }
        }

    }
}
