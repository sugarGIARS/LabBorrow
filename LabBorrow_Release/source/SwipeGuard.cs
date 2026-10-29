// =====================================================================
//  SwipeGuard.cs  --  刷卡业务规则：距离限制 / 间隔限制 / 防恶意刷数据
//  ---------------------------------------------------------------------
//  设计要点：
//   1. 所有阈值都保存在 settings 表，界面可现场修改并立即生效，
//      因此演示现场老师临时改要求时，改数字点保存即可，无需改代码。
//   2. 本文件是纯逻辑（不引用 WinForms），可通过 SelfTest.cs 离线自测。
//
//  「刷卡距离限制」由两部分组成：
//   · 硬件侧：Reader.SetPower() 降低射频输出功率(dBm)，物理上缩短读卡距离；
//   · 软件侧：Reader 内的“连续命中确认”，要求标签在连续 N 轮盘点中都被读到
//             才认为是一次有效刷卡，把距离边缘的偶发读到（正是无意义误刷的
//             主要来源）过滤掉。
//
//  「刷卡间隔时限」由四层组成：
//   ① 同标签方向间隔：借出→归还、归还→借出 各自的最短间隔；
//   ② 全局最小间隔：任意两次成功登记之间至少间隔多少毫秒；
//   ③ 异常刷卡锁定：窗口内同一标签/全局刷卡次数超限，临时锁定；
//   ④ 审计留痕：每一次被拦截的刷卡都写入 audit_log 表作为证据。
// =====================================================================
using System;
using System.Collections.Generic;

namespace LabBorrow
{
    // -----------------------------------------------------------------
    //  规则集合（一一对应 settings 表的键）
    // -----------------------------------------------------------------
    public class SwipeRules
    {
        // ============ 一、读卡距离 ============
        /// <summary>射频功率 dBm；0 表示不修改读卡器功率（使用出厂默认）</summary>
        public int RfPowerDbm = 20;
        /// <summary>连续命中确认：标签需在连续 N 轮盘点中都被读到才上报（1=关闭）</summary>
        public int HitConfirmTimes = 2;
        /// <summary>滑动窗口内累计命中次数：标签在窗口内被读到这么多次才算稳定
        /// （与 HitConfirmTimes 互补，HitConfirmTimes 过滤抖动，MinTotalHits 过滤远距离偶发读到）</summary>
        public int MinTotalHits = 3;
        /// <summary>累计命中窗口的时长（毫秒）：从标签第一次被读到开始计时，
        /// 在此窗口内累计 MinTotalHits 次命中才算一次有效刷卡；超时则重置计数。</summary>
        public int MaxReadWindowMs = 2500;
        /// <summary>盘点轮询间隔（毫秒），越小反应越快、串口压力越大</summary>
        public int PollIntervalMs = 250;
        /// <summary>同一标签原始上报的最小冷却（毫秒），防止单轮抖动重复触发</summary>
        public int TagCooldownMs = 600;

        // ============ 二、刷卡间隔 / 防刷 ============
        /// <summary>任意两次成功登记之间的全局最小间隔（毫秒）</summary>
        public int GlobalGapMs = 800;
        /// <summary>同一标签 借出 → 归还 的最短间隔（秒），0=不限</summary>
        public int BorrowToReturnSec = 10;
        /// <summary>同一标签 归还 → 借出 的最短间隔（秒），0=不限</summary>
        public int ReturnToBorrowSec = 30;
        /// <summary>异常刷卡统计窗口（秒）</summary>
        public int AbuseWindowSec = 60;
        /// <summary>统计窗口内同一标签允许的最大刷卡次数</summary>
        public int AbuseMaxHits = 8;
        /// <summary>统计窗口内全局允许的最大刷卡次数</summary>
        public int AbuseGlobalMax = 40;
        /// <summary>触发防刷后的锁定时长（秒）</summary>
        public int AbuseLockSec = 120;

        // ============ 三、借用期限 ============
        /// <summary>默认借用期限（天），0=不限（不计算应还时间）</summary>
        public int BorrowDays = 7;
        /// <summary>临期提醒提前量（小时）</summary>
        public int DueWarnHours = 24;

        // ============ 四、借用人卡（学生卡） ============
        /// <summary>开启后：先刷借用人卡再刷设备卡，自动完成借出、不弹输入框</summary>
        public bool UserCardMode = false;
        /// <summary>刷借用人卡后的有效期（秒），超时后需重新刷学生卡</summary>
        public int UserCardWaitSec = 8;

        // ============ 五、数据备份 ============
        /// <summary>程序启动时自动备份数据库</summary>
        public bool AutoBackupOnStart = true;
        /// <summary>自动备份保留份数（超出后删除最旧的）</summary>
        public int AutoBackupKeep = 10;

        // ============ 六、刷卡提示音效 ============
        /// <summary>是否播放刷卡提示音效（借出/归还/拦截/异常锁定）</summary>
        public bool SoundEnabled = true;
        /// <summary>音量百分比 0~100</summary>
        public int SoundVolume = 90;
        /// <summary>自定义音频最长播放秒数，0=整首播完</summary>
        public int SoundMaxSec = 6;
        /// <summary>借出成功音效文件（留空=用内置合成音）</summary>
        public string SfxBorrowFile = "";
        /// <summary>归还成功音效文件（留空=用内置合成音）</summary>
        public string SfxReturnFile = "";
        /// <summary>被规则拦截音效文件（留空=用内置合成音）</summary>
        public string SfxBlockedFile = "";
        /// <summary>异常刷卡（判定恶意刷数据并锁定）音效文件（留空=用内置合成音）</summary>
        public string SfxAbuseFile = @"D:\CloudMusic\电台节目\华语原声带 - Man！曼巴out.mp3";

        // ============ 七、界面字号（照顾不同年龄 / 不同视力人群） ============
        /// <summary>界面字号档位：0=紧凑 0.9x，1=标准 1.0x，2=大 1.18x，3=超大 1.4x</summary>
        public int UiFontLevel = 1;
        /// <summary>自助机模式下的触控友好放大（按钮高度、列表行高）</summary>
        public bool TouchFriendly = false;

        // ============ 八、归还到期自动提醒 ============
        /// <summary>0=关闭提醒，1=仅界面提示，2=界面提示 + 语音播报</summary>
        public int RemindMode = 2;
        /// <summary>提前多少小时开始提醒（到期前 → 临期提醒）</summary>
        public int RemindLeadHours = 24;
        /// <summary>是否对已逾期的设备持续提醒</summary>
        public bool RemindOverdue = true;
        /// <summary>借出后自动打印借条（需要接打印机；没打印机时归还日期仍显示在屏幕上）</summary>
        public bool PrintSlipOnBorrow = false;
        /// <summary>每隔多少分钟提醒一次（0=只在借出/归还与开机时提醒，不反复打扰）</summary>
        public int RemindIntervalMin = 60;

        // ============ 十二、自动邮件提醒（把归还日期送进借用同学的邮箱）============
        /// <summary>邮件总开关</summary>
        public bool MailEnabled = false;
        /// <summary>借出成功即发「借用确认信」（信里写着归还日期，等于一张电子借条）</summary>
        public bool MailOnBorrow = true;
        /// <summary>到期 / 逾期自动发「催还信」</summary>
        public bool MailOnDue = true;
        public string MailHost = "";        // SMTP 服务器，如 smtp.qq.com
        public int MailPort = 587;          // 端口（QQ/163 常用 465 或 587）
        public bool MailSsl = true;         // 是否 SSL（现在基本都开）
        public string MailUser = "";        // 发件邮箱账号
        public string MailPass = "";        // 授权码（存库时已用 DPAPI 加密，不是明文）
        public string MailFrom = "";        // 发件地址
        public string MailFromName = "";    // 发件人显示名，如「物联网实验室」

        // ============ 九、自助终端（Kiosk：刷卡机 / 闸机式一体机） ============
        /// <summary>自助模式：全屏无边框、超大按钮、空闲自动回主屏、防随意退出</summary>
        public bool KioskEnabled = false;
        /// <summary>空闲多少秒自动回到主控台（0=不自动返回）</summary>
        public int KioskIdleSec = 60;
        /// <summary>是否随 Windows 开机自动启动</summary>
        public bool KioskAutoStart = false;
        /// <summary>是否阻止屏幕休眠/关闭（自助机要常亮）</summary>
        public bool KeepScreenOn = false;

        // ============ 十、校园卡 / 多种识别卡适配（13.56MHz NFC） ============
        /// <summary>启用 PC/SC NFC 读卡器（用于刷校园卡、公交卡等 13.56MHz 卡）</summary>
        public bool NfcEnabled = true;
        /// <summary>指定的 NFC 读卡器名称（留空=自动使用检测到的第一个）</summary>
        public string NfcReaderName = "";
        /// <summary>校园卡号前缀过滤（留空=接受所有卡号），可防止刷到无关卡片</summary>
        public string NfcIdPrefix = "";

        // ---------------- 读写 ----------------
        // 注意：这里刻意不使用“启动时写入默认值”，而是每次都用默认值兜底，
        // 这样老版本数据库升级上来无需改表就能直接工作。
        public static SwipeRules Load()
        {
            var r = new SwipeRules();

            r.RfPowerDbm       = Db.GetIntSetting("rf_power_dbm", r.RfPowerDbm);
            r.HitConfirmTimes  = Db.GetIntSetting("hit_confirm_times", r.HitConfirmTimes);
            r.MinTotalHits     = Db.GetIntSetting("min_total_hits", r.MinTotalHits);
            r.MaxReadWindowMs  = Db.GetIntSetting("max_read_window_ms", r.MaxReadWindowMs);
            r.PollIntervalMs   = Db.GetIntSetting("poll_interval_ms", r.PollIntervalMs);
            r.TagCooldownMs    = Db.GetIntSetting("tag_cooldown_ms", r.TagCooldownMs);

            r.GlobalGapMs      = Db.GetIntSetting("global_gap_ms", r.GlobalGapMs);
            r.BorrowToReturnSec = Db.GetIntSetting("borrow_to_return_sec", r.BorrowToReturnSec);
            // 老版本只有 min_interval_min（分钟）：迁移为“归还→借出”冷却，保持旧配置含义
            string legacy = Db.GetSetting("min_interval_min", "");
            int legacyMin;
            if (int.TryParse(legacy, out legacyMin) && legacyMin > 0)
            {
                if (Db.GetSetting("return_to_borrow_sec", "") == "")
                    r.ReturnToBorrowSec = Math.Max(r.ReturnToBorrowSec, legacyMin * 60);
                else
                    r.ReturnToBorrowSec = Db.GetIntSetting("return_to_borrow_sec", r.ReturnToBorrowSec);
            }
            else
            {
                r.ReturnToBorrowSec = Db.GetIntSetting("return_to_borrow_sec", r.ReturnToBorrowSec);
            }
            r.AbuseWindowSec   = Db.GetIntSetting("abuse_window_sec", r.AbuseWindowSec);
            r.AbuseMaxHits     = Db.GetIntSetting("abuse_max_hits", r.AbuseMaxHits);
            r.AbuseGlobalMax   = Db.GetIntSetting("abuse_global_max", r.AbuseGlobalMax);
            r.AbuseLockSec     = Db.GetIntSetting("abuse_lock_sec", r.AbuseLockSec);

            r.BorrowDays       = Db.GetIntSetting("borrow_days", r.BorrowDays);
            r.DueWarnHours     = Db.GetIntSetting("due_warn_hours", r.DueWarnHours);

            r.UserCardMode     = Db.GetIntSetting("user_card_mode", 0) == 1;
            r.UserCardWaitSec  = Db.GetIntSetting("user_card_wait_sec", r.UserCardWaitSec);

            r.AutoBackupOnStart = Db.GetIntSetting("auto_backup_on_start", 1) == 1;
            r.AutoBackupKeep    = Db.GetIntSetting("auto_backup_keep", r.AutoBackupKeep);

            r.SoundEnabled = Db.GetIntSetting("sound_enabled", 1) == 1;
            r.SoundVolume  = Db.GetIntSetting("sound_volume", r.SoundVolume);
            r.SoundMaxSec  = Db.GetIntSetting("sound_max_sec", r.SoundMaxSec);
            r.SfxBorrowFile  = Db.GetSetting("sfx_borrow_file", r.SfxBorrowFile);
            r.SfxReturnFile  = Db.GetSetting("sfx_return_file", r.SfxReturnFile);
            r.SfxBlockedFile = Db.GetSetting("sfx_blocked_file", r.SfxBlockedFile);
            r.SfxAbuseFile   = Db.GetSetting("sfx_abuse_file", r.SfxAbuseFile);

            r.UiFontLevel   = Db.GetIntSetting("ui_font_level", r.UiFontLevel);
            r.TouchFriendly = Db.GetIntSetting("touch_friendly", 0) == 1;

            r.RemindMode        = Db.GetIntSetting("remind_mode", r.RemindMode);
            r.RemindLeadHours   = Db.GetIntSetting("remind_lead_hours", r.RemindLeadHours);
            r.RemindOverdue     = Db.GetIntSetting("remind_overdue", 1) == 1;
            r.PrintSlipOnBorrow = Db.GetIntSetting("print_slip_on_borrow", 0) == 1;
            r.RemindIntervalMin = Db.GetIntSetting("remind_interval_min", r.RemindIntervalMin);

            // ---- 十二、邮件提醒 ----
            r.MailEnabled    = Db.GetIntSetting("mail_enabled", 0) == 1;
            r.MailOnBorrow   = Db.GetIntSetting("mail_on_borrow", 1) == 1;
            r.MailOnDue      = Db.GetIntSetting("mail_on_due", 1) == 1;
            r.MailHost       = Db.GetSetting("mail_host", "");
            r.MailPort       = Db.GetIntSetting("mail_port", 587);
            r.MailSsl        = Db.GetIntSetting("mail_ssl", 1) == 1;
            r.MailUser       = Db.GetSetting("mail_user", "");
            // 库里存的是 DPAPI 密文，这里解成明文给界面用（界面上看到的和自己填的一样）
            r.MailPass       = Mailer.Unprotect(Db.GetSetting("mail_pass", ""));
            r.MailFrom       = Db.GetSetting("mail_from", "");
            r.MailFromName   = Db.GetSetting("mail_from_name", "");

            r.KioskEnabled   = Db.GetIntSetting("kiosk_enabled", 0) == 1;
            r.KioskIdleSec   = Db.GetIntSetting("kiosk_idle_sec", r.KioskIdleSec);
            r.KioskAutoStart = Db.GetIntSetting("kiosk_autostart", 0) == 1;
            r.KeepScreenOn   = Db.GetIntSetting("keep_screen_on", 0) == 1;

            r.NfcEnabled    = Db.GetIntSetting("nfc_enabled", 1) == 1;
            r.NfcReaderName = Db.GetSetting("nfc_reader_name", r.NfcReaderName);
            r.NfcIdPrefix   = Db.GetSetting("nfc_id_prefix", r.NfcIdPrefix);

            r.Normalize();
            return r;
        }

        public void Save()
        {
            Normalize();
            Db.SetIntSetting("rf_power_dbm", RfPowerDbm);
            Db.SetIntSetting("hit_confirm_times", HitConfirmTimes);
            Db.SetIntSetting("min_total_hits", MinTotalHits);
            Db.SetIntSetting("max_read_window_ms", MaxReadWindowMs);
            Db.SetIntSetting("poll_interval_ms", PollIntervalMs);
            Db.SetIntSetting("tag_cooldown_ms", TagCooldownMs);

            Db.SetIntSetting("global_gap_ms", GlobalGapMs);
            Db.SetIntSetting("borrow_to_return_sec", BorrowToReturnSec);
            Db.SetIntSetting("return_to_borrow_sec", ReturnToBorrowSec);
            Db.SetIntSetting("abuse_window_sec", AbuseWindowSec);
            Db.SetIntSetting("abuse_max_hits", AbuseMaxHits);
            Db.SetIntSetting("abuse_global_max", AbuseGlobalMax);
            Db.SetIntSetting("abuse_lock_sec", AbuseLockSec);

            Db.SetIntSetting("borrow_days", BorrowDays);
            Db.SetIntSetting("due_warn_hours", DueWarnHours);

            Db.SetIntSetting("user_card_mode", UserCardMode ? 1 : 0);
            Db.SetIntSetting("user_card_wait_sec", UserCardWaitSec);

            Db.SetIntSetting("auto_backup_on_start", AutoBackupOnStart ? 1 : 0);
            Db.SetIntSetting("auto_backup_keep", AutoBackupKeep);

            Db.SetIntSetting("sound_enabled", SoundEnabled ? 1 : 0);
            Db.SetIntSetting("sound_volume", SoundVolume);
            Db.SetIntSetting("sound_max_sec", SoundMaxSec);
            Db.SetSetting("sfx_borrow_file", SfxBorrowFile ?? "");
            Db.SetSetting("sfx_return_file", SfxReturnFile ?? "");
            Db.SetSetting("sfx_blocked_file", SfxBlockedFile ?? "");
            Db.SetSetting("sfx_abuse_file", SfxAbuseFile ?? "");

            Db.SetIntSetting("ui_font_level", UiFontLevel);
            Db.SetIntSetting("touch_friendly", TouchFriendly ? 1 : 0);

            Db.SetIntSetting("remind_mode", RemindMode);
            Db.SetIntSetting("remind_lead_hours", RemindLeadHours);
            Db.SetIntSetting("remind_overdue", RemindOverdue ? 1 : 0);
            Db.SetIntSetting("print_slip_on_borrow", PrintSlipOnBorrow ? 1 : 0);
            Db.SetIntSetting("remind_interval_min", RemindIntervalMin);

            // ---- 十二、邮件提醒 ----
            Db.SetIntSetting("mail_enabled", MailEnabled ? 1 : 0);
            Db.SetIntSetting("mail_on_borrow", MailOnBorrow ? 1 : 0);
            Db.SetIntSetting("mail_on_due", MailOnDue ? 1 : 0);
            Db.SetSetting("mail_host", MailHost ?? "");
            Db.SetIntSetting("mail_port", MailPort);
            Db.SetIntSetting("mail_ssl", MailSsl ? 1 : 0);
            Db.SetSetting("mail_user", MailUser ?? "");
            // 入库前用 DPAPI 加密：数据库文件被拷走也拿不到邮箱授权码
            Db.SetSetting("mail_pass", Mailer.Protect(MailPass));
            Db.SetSetting("mail_from", MailFrom ?? "");
            Db.SetSetting("mail_from_name", MailFromName ?? "");

            Db.SetIntSetting("kiosk_enabled", KioskEnabled ? 1 : 0);
            Db.SetIntSetting("kiosk_idle_sec", KioskIdleSec);
            Db.SetIntSetting("kiosk_autostart", KioskAutoStart ? 1 : 0);
            Db.SetIntSetting("keep_screen_on", KeepScreenOn ? 1 : 0);

            Db.SetIntSetting("nfc_enabled", NfcEnabled ? 1 : 0);
            Db.SetSetting("nfc_reader_name", NfcReaderName ?? "");
            Db.SetSetting("nfc_id_prefix", NfcIdPrefix ?? "");
        }

        /// <summary>参数兜底，避免现场输入越界把读卡器搞挂</summary>
        public void Normalize()
        {
            if (RfPowerDbm < 0) RfPowerDbm = 0;
            if (RfPowerDbm > 30) RfPowerDbm = 30;
            if (HitConfirmTimes < 1) HitConfirmTimes = 1;
            if (HitConfirmTimes > 20) HitConfirmTimes = 20;
            if (MinTotalHits < 1) MinTotalHits = 1;
            if (MinTotalHits > 50) MinTotalHits = 50;
            if (MaxReadWindowMs < 200) MaxReadWindowMs = 200;
            if (MaxReadWindowMs > 30000) MaxReadWindowMs = 30000;
            if (PollIntervalMs < 50) PollIntervalMs = 50;
            if (PollIntervalMs > 2000) PollIntervalMs = 2000;
            if (TagCooldownMs < 0) TagCooldownMs = 0;
            if (TagCooldownMs > 60000) TagCooldownMs = 60000;

            if (GlobalGapMs < 0) GlobalGapMs = 0;
            if (GlobalGapMs > 60000) GlobalGapMs = 60000;
            if (BorrowToReturnSec < 0) BorrowToReturnSec = 0;
            if (ReturnToBorrowSec < 0) ReturnToBorrowSec = 0;
            if (AbuseWindowSec < 1) AbuseWindowSec = 1;
            if (AbuseMaxHits < 1) AbuseMaxHits = 1;
            if (AbuseGlobalMax < 1) AbuseGlobalMax = 1;
            if (AbuseLockSec < 1) AbuseLockSec = 1;

            if (BorrowDays < 0) BorrowDays = 0;
            if (BorrowDays > 365) BorrowDays = 365;
            if (DueWarnHours < 0) DueWarnHours = 0;
            if (DueWarnHours > 720) DueWarnHours = 720;

            if (UserCardWaitSec < 1) UserCardWaitSec = 1;
            if (UserCardWaitSec > 300) UserCardWaitSec = 300;
            if (AutoBackupKeep < 1) AutoBackupKeep = 1;
            if (AutoBackupKeep > 500) AutoBackupKeep = 500;

            if (SoundVolume < 0) SoundVolume = 0;
            if (SoundVolume > 100) SoundVolume = 100;
            if (SoundMaxSec < 0) SoundMaxSec = 0;
            if (SoundMaxSec > 3600) SoundMaxSec = 3600;

            if (UiFontLevel < 0) UiFontLevel = 0;
            if (UiFontLevel > 3) UiFontLevel = 3;

            if (RemindMode < 0) RemindMode = 0;
            if (RemindMode > 2) RemindMode = 2;
            if (RemindLeadHours < 0) RemindLeadHours = 0;
            if (RemindLeadHours > 720) RemindLeadHours = 720;
            if (RemindIntervalMin < 0) RemindIntervalMin = 0;
            if (RemindIntervalMin > 1440) RemindIntervalMin = 1440;

            if (KioskIdleSec < 0) KioskIdleSec = 0;
            if (KioskIdleSec > 3600) KioskIdleSec = 3600;

            if (MailPort < 1) MailPort = 1;
            if (MailPort > 65535) MailPort = 65535;
        }
    }

    // -----------------------------------------------------------------
    //  刷卡判定结果
    // -----------------------------------------------------------------
    public enum SwipeVerdict
    {
        Allow = 0,          // 放行
        TooSoon = 1,        // 同卡方向间隔不足
        GlobalTooFast = 2,  // 全局间隔不足
        AbuseLocked = 3     // 触发防刷锁定
    }

    // -----------------------------------------------------------------
    //  刷卡守卫：判定 + 记忆状态（内存态，程序重启即复位）
    // -----------------------------------------------------------------
    public class SwipeGuard
    {
        private class EpcState
        {
            public string LastDirection = "";
            public DateTime LastTime = DateTime.MinValue;
            public readonly List<DateTime> Hits = new List<DateTime>();
            public DateTime LockUntil = DateTime.MinValue;
        }

        private readonly Dictionary<string, EpcState> map =
            new Dictionary<string, EpcState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DateTime> globalHits = new List<DateTime>();

        private DateTime lastBusiness = DateTime.MinValue;
        private bool hasBusiness;
        private DateTime globalLockUntil = DateTime.MinValue;

        /// <summary>最近一次判定说明（用于界面提示）</summary>
        public string LastReason { get; private set; }

        private EpcState Get(string epc)
        {
            EpcState st;
            if (!map.TryGetValue(epc, out st))
            {
                st = new EpcState();
                map[epc] = st;
            }
            return st;
        }

        /// <summary>
        /// 判定一次刷卡是否放行。nextDirection 由设备当前状态决定：borrowed → "return"，否则 "borrow"。
        /// 注意：无论放行还是拒绝，都会计入防刷窗口（否则恶意刷屏抓不住）。
        /// </summary>
        public SwipeVerdict Check(string epc, string nextDirection, SwipeRules r, DateTime now, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(epc))
            {
                reason = "标签为空";
                return SwipeVerdict.TooSoon;
            }
            if (r == null) r = new SwipeRules();
            EpcState st = Get(epc);

            // ---- ① 全局锁定中 ----
            if (globalLockUntil > now)
            {
                int left = (int)Math.Ceiling((globalLockUntil - now).TotalSeconds);
                reason = string.Format("刷卡过于频繁，系统已全局锁定，剩余 {0} 秒", left);
                CountHit(st, r, now);
                LastReason = reason;
                return SwipeVerdict.AbuseLocked;
            }

            // ---- ② 该标签锁定中 ----
            if (st.LockUntil > now)
            {
                int left = (int)Math.Ceiling((st.LockUntil - now).TotalSeconds);
                reason = string.Format("该标签短时间内被反复刷卡，已临时锁定，剩余 {0} 秒（可在系统设置中解除）", left);
                CountHit(st, r, now);
                LastReason = reason;
                return SwipeVerdict.AbuseLocked;
            }

            // ---- ③ 防刷计数（放行/拒绝都要计） ----
            CountHit(st, r, now);
            if (st.Hits.Count > r.AbuseMaxHits)
            {
                st.LockUntil = now.AddSeconds(r.AbuseLockSec);
                int n = st.Hits.Count;
                st.Hits.Clear();
                reason = string.Format("异常刷卡：{0} 秒内同一标签刷卡 {1} 次（上限 {2} 次），已锁定 {3} 秒",
                    r.AbuseWindowSec, n, r.AbuseMaxHits, r.AbuseLockSec);
                LastReason = reason;
                return SwipeVerdict.AbuseLocked;
            }
            if (globalHits.Count > r.AbuseGlobalMax)
            {
                globalLockUntil = now.AddSeconds(r.AbuseLockSec);
                int n = globalHits.Count;
                globalHits.Clear();
                reason = string.Format("异常刷卡：{0} 秒内总刷卡 {1} 次（上限 {2} 次），已全局锁定 {3} 秒",
                    r.AbuseWindowSec, n, r.AbuseGlobalMax, r.AbuseLockSec);
                LastReason = reason;
                return SwipeVerdict.AbuseLocked;
            }

            // ---- ④ 全局最小间隔 ----
            if (hasBusiness && r.GlobalGapMs > 0)
            {
                double gap = (now - lastBusiness).TotalMilliseconds;
                if (gap < r.GlobalGapMs)
                {
                    reason = string.Format("与上一次登记仅间隔 {0} 毫秒，小于全局最小间隔 {1} 毫秒", (int)gap, r.GlobalGapMs);
                    LastReason = reason;
                    return SwipeVerdict.GlobalTooFast;
                }
            }

            // ---- ⑤ 同标签方向间隔 ----
            if (!string.IsNullOrEmpty(st.LastDirection) && st.LastTime > DateTime.MinValue)
            {
                int needSec = 0;
                string what = null;
                if (st.LastDirection == "borrow" && nextDirection == "return")
                {
                    needSec = r.BorrowToReturnSec; what = "借出后再次归还";
                }
                else if (st.LastDirection == "return" && nextDirection == "borrow")
                {
                    needSec = r.ReturnToBorrowSec; what = "归还后再次借出";
                }
                if (needSec > 0)
                {
                    double elapsed = (now - st.LastTime).TotalSeconds;
                    if (elapsed < needSec)
                    {
                        reason = string.Format("{0}的最短间隔为 {1} 秒，本次仅间隔 {2:0.#} 秒（可在系统设置中调整）",
                            what, needSec, elapsed);
                        LastReason = reason;
                        return SwipeVerdict.TooSoon;
                    }
                }
            }

            reason = "";
            LastReason = "";
            return SwipeVerdict.Allow;
        }

        /// <summary>业务成功落库后调用，刷新冷却与全局间隔</summary>
        public void Commit(string epc, string direction, DateTime now)
        {
            EpcState st = Get(epc);
            st.LastDirection = direction;
            st.LastTime = now;
            st.Hits.Clear();
            lastBusiness = now;
            hasBusiness = true;
        }

        /// <summary>用户主动取消（如关闭借出对话框）：不改变冷却，但也不污染业务间隔</summary>
        public void Abort(string epc)
        {
            EpcState st;
            if (map.TryGetValue(epc, out st)) st.Hits.Clear();
        }

        private void CountHit(EpcState st, SwipeRules r, DateTime now)
        {
            DateTime from = now.AddSeconds(-Math.Max(1, r.AbuseWindowSec));
            Prune(st.Hits, from, now);
            st.Hits.Add(now);

            Prune(globalHits, from, now);
            globalHits.Add(now);
        }

        // 列表减去窗口（最后一跳用 now，避免依赖系统时钟推进）
        private static void Prune(List<DateTime> list, DateTime from, DateTime now)
        {
            int keep = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] >= from && list[i] <= now) list[keep++] = list[i];
            if (keep < list.Count) list.RemoveRange(keep, list.Count - keep);
        }

        /// <summary>当前处于锁定状态的标签（供界面展示/解锁）</summary>
        public List<string> LockedEpcs(DateTime now)
        {
            var list = new List<string>();
            foreach (var kv in map)
                if (kv.Value.LockUntil > now) list.Add(kv.Key);
            return list;
        }

        public bool GlobalLocked(DateTime now) { return globalLockUntil > now; }

        /// <summary>解除所有锁定（演示现场误触发时可一键恢复）</summary>
        public void ClearLocks()
        {
            foreach (var kv in map) { kv.Value.LockUntil = DateTime.MinValue; kv.Value.Hits.Clear(); }
            globalHits.Clear();
            globalLockUntil = DateTime.MinValue;
        }

        public void Reset()
        {
            map.Clear();
            globalHits.Clear();
            hasBusiness = false;
            globalLockUntil = DateTime.MinValue;
        }
    }

    // -----------------------------------------------------------------
    //  TagGate  --  刷卡距离限制的软件闸门（纯逻辑，可离线自测）
    //  -----------------------------------------------------------------
    //  每一轮盘点调用：BeginRound() → Seen(epc)* → FinishRound()
    //  规则：
    //   · 连续命中确认：标签需在连续 HitConfirmTimes 轮中被读到（允许漏一轮），
    //     才认定为一次“有效刷卡”；距离边缘的偶发读到会被判为“距离未确认”，
    //     只提示、不产生借还记录 —— 这是避免无意义误刷的关键。
    //   · 边沿触发：同一标签在上报后必须离开视野（连续若干轮读不到）才能再次
    //     上报，彻底解决“标签一直放在读卡器上导致反复触发/借出后立即归还”。
    //   · 上报冷却：同一标签两次上报至少间隔 TagCooldownMs。
    // =====================================================================
    public class TagGate
    {
        private class St
        {
            public int LastRound;
            public int Streak;
            public int Missed;          // 连续漏盘轮数
            public bool Consumed;
            public DateTime LastReport = DateTime.MinValue;
            public DateTime LastReject = DateTime.MinValue;
            public bool RejectNotified;
            // 累计命中窗口：记录该标签在 MaxReadWindowMs 内的累计命中次数
            public int WindowHits;      // 当前窗口内累计命中
            public DateTime WindowStart;  // 第一次进入视野的时间
        }

        /// <summary>连续命中确认轮数（1 = 关闭确认，任何一次读到都算）</summary>
        public int HitConfirmTimes = 2;
        /// <summary>同一标签两次上报的最小间隔（毫秒）</summary>
        public int TagCooldownMs = 600;
        /// <summary>同一标签“距离未确认”提示的最小间隔（毫秒）</summary>
        public int RejectMinIntervalMs = 3000;
        /// <summary>离开视野多少轮后允许再次上报</summary>
        public int RearmAbsentRounds = 1;
        /// <summary>离开视野多少轮后允许再次提示“距离未确认”</summary>
        public int RejectResetRounds = 8;
        /// <summary>允许漏盘的轮数（读取抖动容忍）</summary>
        public int GapTolerance = 1;
        /// <summary>滑动窗口内累计命中次数门槛（1=关闭窗口过滤，只看 HitConfirmTimes）</summary>
        public int MinTotalHits = 3;
        /// <summary>累计命中窗口的时长（毫秒），从首次读到起算</summary>
        public int MaxReadWindowMs = 2500;

        private readonly Dictionary<string, St> map =
            new Dictionary<string, St>(StringComparer.OrdinalIgnoreCase);
        private int round;

        /// <summary>本轮确认、应当作为有效刷卡上报的标签</summary>
        public readonly List<string> Confirmed = new List<string>();
        /// <summary>本轮读到但命中不足、应提示“距离未确认”的标签</summary>
        public readonly List<string> Unconfirmed = new List<string>();

        public void Reset()
        {
            map.Clear();
            round = 0;
            Confirmed.Clear();
            Unconfirmed.Clear();
        }

        public int Round { get { return round; } }

        public void BeginRound()
        {
            round++;
            Confirmed.Clear();
            Unconfirmed.Clear();
        }

        /// <summary>本轮盘点读到了该标签（用 DateTime.Now，方便生产代码调用）</summary>
        public void Seen(string epc) { Seen(epc, DateTime.Now); }

        /// <summary>本轮盘点读到了该标签（可传入 now，便于自测时使用模拟时间）</summary>
        public void Seen(string epc, DateTime now)
        {
            if (string.IsNullOrEmpty(epc)) return;
            St st;
            if (!map.TryGetValue(epc, out st)) { st = new St(); map[epc] = st; }
            st.LastRound = round;
            st.Missed = 0;
            // 已上报过的标签在离场前不再累计命中，保证“每一次刷卡都要重新确认”
            if (st.Consumed) { st.Streak = 0; return; }
            st.Streak++;
            // 累计命中窗口：从首次看到开始计，窗口内累计够 MinTotalHits 次才算稳定
            if (MinTotalHits > 1)
            {
                if (st.WindowStart == DateTime.MinValue) st.WindowStart = now;
                if (st.WindowHits < MinTotalHits) st.WindowHits++;
            }
        }

        /// <summary>本轮结束：产出 Confirmed / Unconfirmed 两张清单</summary>
        public void FinishRound(DateTime now)
        {
            if (HitConfirmTimes < 1) HitConfirmTimes = 1;
            if (MinTotalHits < 1) MinTotalHits = 1;
            if (MaxReadWindowMs < 200) MaxReadWindowMs = 200;

            foreach (var kv in map)
            {
                string epc = kv.Key;
                St st = kv.Value;

                if (st.LastRound == round)      // 本轮读到
                {
                    if (!st.Consumed)
                    {
                        // 窗口已超时（读到太慢）→ 视为“偶发读到”，重置窗口
                        if (st.WindowStart != DateTime.MinValue
                            && MinTotalHits > 1
                            && (now - st.WindowStart).TotalMilliseconds > MaxReadWindowMs)
                        {
                            st.WindowHits = 0;
                            st.WindowStart = now;
                            st.Streak = 0;
                        }

                        if (st.Streak >= HitConfirmTimes)
                        {
                            // 新增：窗口内累计命中次数也要达标（默认 3 次/2.5s）
                            // 这层过滤专门抓“读卡距离过远导致命中次数不够”的情况
                            if (MinTotalHits > 1 && st.WindowHits < MinTotalHits)
                            {
                                // 已连续达标但窗口内总次数还差，继续等，不要提示拒绝
                            }
                            else if (st.LastReport == DateTime.MinValue
                                || (now - st.LastReport).TotalMilliseconds >= TagCooldownMs)
                            {
                                st.LastReport = now;
                                st.Consumed = true;    // 边沿触发：离场前不再上报
                                st.Streak = 0;
                                st.WindowHits = 0;
                                st.WindowStart = DateTime.MinValue;
                                Confirmed.Add(epc);
                            }
                        }
                        else if (!st.RejectNotified
                                 && (st.LastReject == DateTime.MinValue
                                     || (now - st.LastReject).TotalMilliseconds >= RejectMinIntervalMs))
                        {
                            st.LastReject = now;
                            st.RejectNotified = true;
                            Unconfirmed.Add(epc);
                        }
                    }
                }
                else                            // 本轮没读到
                {
                    st.Missed++;
                    // 漏盘超过容忍度 → 视为离开，命中重新计数（这正是过滤远距离偶发读到的关键）
                    if (st.Missed > GapTolerance) st.Streak = 0;
                    if (st.Missed >= RearmAbsentRounds) st.Consumed = false;
                    if (st.Missed >= RejectResetRounds) st.RejectNotified = false;
                    // 离开视野时同时清空累计窗口，避免下次进入时残留旧值
                    if (st.Missed > GapTolerance)
                    {
                        st.WindowHits = 0;
                        st.WindowStart = DateTime.MinValue;
                    }
                }
            }
        }

        /// <summary>清理长期未出现的标签状态，避免字典无限增长</summary>
        public int PruneStale(int keepRounds)
        {
            var dead = new List<string>();
            foreach (var kv in map)
                if (round - kv.Value.LastRound > keepRounds) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) map.Remove(dead[i]);
            return dead.Count;
        }

        public int TrackedCount { get { return map.Count; } }
    }

    // -----------------------------------------------------------------
    //  借用期限 / 逾期计算（首页在借明细、记录页、报表页共用）
    // -----------------------------------------------------------------
    public static class DueUtil
    {
        public const int StateNormal = 0;    // 正常
        public const int StateDueSoon = 1;   // 临期
        public const int StateOverdue = 2;   // 逾期

        /// <summary>按借用期限计算应还时间；days&lt;=0 或时间无法解析时返回空串</summary>
        public static string ComputeDue(string borrowTime, int days)
        {
            if (days <= 0) return "";
            DateTime t;
            if (!DateTime.TryParse(borrowTime, out t)) return "";
            return t.AddDays(days).ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>0=正常 1=临期 2=逾期；未设期限或解析失败按正常处理</summary>
        public static int State(string dueTime, DateTime now, int warnHours)
        {
            DateTime due;
            if (string.IsNullOrEmpty(dueTime) || !DateTime.TryParse(dueTime, out due)) return StateNormal;
            if (now > due) return StateOverdue;
            if (warnHours > 0 && now.AddHours(warnHours) > due) return StateDueSoon;
            return StateNormal;
        }

        public static string StateLabel(int state)
        {
            if (state == StateOverdue) return "逾期";
            if (state == StateDueSoon) return "临期";
            return "正常";
        }

        /// <summary>人类可读的时长描述，如 “3 小时 12 分”“2 天 5 小时”</summary>
        public static string HumanSpan(TimeSpan span)
        {
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalMinutes < 60) return string.Format("{0:0.#} 分钟", span.TotalMinutes);
            if (span.TotalHours < 24) return string.Format("{0} 小时 {1} 分", (int)span.TotalHours, span.Minutes);
            return string.Format("{0} 天 {1} 小时", (int)span.TotalDays, span.Hours);
        }

        /// <summary>紧凑时长（表格列窄时用），如 “1 天”“3 时”“25 分”</summary>
        public static string CompactSpan(TimeSpan span)
        {
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分";
            if (span.TotalHours < 24) return (int)span.TotalHours + " 时";
            return (int)span.TotalDays + " 天";
        }
    }
}
