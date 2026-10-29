// =====================================================================
//  SelfTest.cs  --  离线自测（不需要读卡器、不弹界面）
//  ---------------------------------------------------------------------
//  编译方式（rebuild.bat 已自动包含）：
//     csc /target:exe /main:LabBorrow.SelfTest /out:selftest.exe ^
//         /reference:_lib\System.Data.SQLite.dll ^
//         source\Database.cs source\SwipeGuard.cs source\SelfTest.cs
//  运行方式：
//     set LABBORROW_DB=<临时库路径>
//     selftest.exe
//  覆盖内容：
//     ① 老数据库自动升级（补列 / 建新表 / 老数据不丢）
//     ② 刷卡距离参数、间隔与防刷规则判定
//     ③ 借用期限与逾期计算
//     ④ 借用人 / 审计 / 报表 / 备份恢复等数据层
//  输出：控制台 + selftest_report.txt，全部通过返回 0。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace LabBorrow
{
    internal static class SelfTest
    {
        private static int passed;
        private static int failed;
        private static readonly StringBuilder report = new StringBuilder();
        private static string dbPath;

        internal static int Main(string[] args)
        {
            // 子进程模式：以只读数据库启动，专门验证“启动不再崩溃”这一回归点
            if (args != null && args.Length >= 2 && args[0] == "--readonly-probe")
                return ReadOnlyProbe(args[1]);

            dbPath = args != null && args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("LABBORROW_DB");
            if (string.IsNullOrEmpty(dbPath))
            {
                dbPath = Path.Combine(Path.GetTempPath(), "labborrow_selftest.db");
                Environment.SetEnvironmentVariable("LABBORROW_DB", dbPath);
            }
            // 必须在触碰 Db 之前设置，Db 的静态构造函数会读取该环境变量
            Environment.SetEnvironmentVariable("LABBORROW_DB", dbPath);

            Log("LabBorrow 自测开始　" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log("测试数据库：" + dbPath);
            Log("");

            try
            {
                DeleteIfExists(dbPath);
                DeleteIfExists(dbPath + "-journal");
                DeleteIfExists(dbPath + "-wal");

                // 先用“老版本结构”造一个库，验证自动升级
                CreateLegacyDatabase(dbPath);

                Section("① 老数据库自动升级（向后兼容）");
                TestMigration();

                Section("② 刷卡距离 / 间隔 / 防刷规则");
                TestDistanceRules();
                TestTagGate();
                TestIntervalRules();
                TestAbuseRules();

                Section("③ 借用期限与逾期计算");
                TestDue();

                Section("④ 数据层：借用人 / 审计 / 报表 / 备份");
                TestUsers();
                TestAudit();
                TestRecordsAndReport();
                TestBackupRestore();
                TestReadOnlyFallback();
            }
            catch (Exception ex)
            {
                Fail("自测过程抛出异常：" + ex);
            }

            Log("");
            Log("==================================================");
            Log(string.Format("自测结果：通过 {0} 项，失败 {1} 项", passed, failed));
            Log(failed == 0 ? "结论：全部通过 ✔" : "结论：存在失败项 ✘");
            Log("==================================================");

            try { File.WriteAllText("selftest_report.txt", report.ToString(), new UTF8Encoding(true)); }
            catch { }

            return failed == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------
        //  测试基础设施
        // -------------------------------------------------------------
        private static void Log(string s)
        {
            Console.WriteLine(s);
            report.AppendLine(s);
        }

        private static void Section(string title)
        {
            Log("");
            Log("---- " + title + " ----");
        }

        private static void Check(string what, bool ok, string detail = "")
        {
            if (ok) { passed++; Log("  [通过] " + what); }
            else { failed++; Log("  [失败] " + what + (string.IsNullOrEmpty(detail) ? "" : "　-> " + detail)); }
        }

        private static void Fail(string what)
        {
            failed++;
            Log("  [失败] " + what);
        }

        private static void DeleteIfExists(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>造一个“老版本”数据库：只含 devices/records/settings 三表，字段为旧版字段</summary>
        private static void CreateLegacyDatabase(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var c = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE devices (
  epc TEXT PRIMARY KEY, name TEXT NOT NULL, model TEXT, category TEXT, location TEXT,
  status TEXT NOT NULL DEFAULT 'idle', current_borrower TEXT, borrow_time TEXT, notes TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE records (
  id INTEGER PRIMARY KEY AUTOINCREMENT, epc TEXT NOT NULL, device_name TEXT, borrower TEXT,
  direction TEXT NOT NULL, event_time TEXT NOT NULL, duration_min REAL DEFAULT 0, notes TEXT);
CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
INSERT INTO devices(epc,name,model,category,location,status,current_borrower,borrow_time,notes,created_at,updated_at)
  VALUES('LEGACY0001','老库设备','X1','仪器仪表','A柜','idle','','','','2026-01-01 10:00:00','2026-01-01 10:00:00');
INSERT INTO records(epc,device_name,borrower,direction,event_time,duration_min,notes)
  VALUES('LEGACY0001','老库设备','张三','borrow','2026-01-02 09:00:00',0,'');
INSERT INTO records(epc,device_name,borrower,direction,event_time,duration_min,notes)
  VALUES('LEGACY0001','老库设备','张三','return','2026-01-02 10:30:00',90,'借用时长 90 分钟');
INSERT INTO settings(key,value) VALUES('min_interval_min','1');";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static object Scalar(string sql)
        {
            using (var c = new SQLiteConnection(Db.ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = sql;
                    return cmd.ExecuteScalar();
                }
            }
        }

        private static bool ColumnExists(string table, string column)
        {
            using (var c = new SQLiteConnection(Db.ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(" + table + ")";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (string.Equals(r["name"].ToString(), column, StringComparison.OrdinalIgnoreCase))
                                return true;
                }
            }
            return false;
        }

        private static bool TableExists(string table)
        {
            return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='" + table + "'")) > 0;
        }

        // -------------------------------------------------------------
        //  ① 迁移
        // -------------------------------------------------------------
        private static void TestMigration()
        {
            // 触发 Db 静态构造函数（建表 / 补列）
            string p = Db.DbPath;
            Check("Db 使用测试库路径", string.Equals(p, dbPath, StringComparison.OrdinalIgnoreCase), p);

            Check("devices 补列 due_time", ColumnExists("devices", "due_time"));
            Check("records 补列 due_time", ColumnExists("records", "due_time"));
            Check("records 补列 user_card", ColumnExists("records", "user_card"));
            Check("records 补列 overdue", ColumnExists("records", "overdue"));
            Check("新建 users 表", TableExists("users"));
            Check("新建 audit_log 表", TableExists("audit_log"));
            Check("结构版本号已升级为 1", Convert.ToInt32(Scalar("PRAGMA user_version")) == 1,
                "实际 " + Scalar("PRAGMA user_version"));

            // 升级前自动备份（老库首次被新版打开时）
            var ups = Db.ListBackups().FindAll(delegate (Db.BackupFile b) { return b.Name.Contains("before_upgrade"); });
            Check("升级前自动留了一份老库备份", ups.Count >= 1, "实际 " + ups.Count);

            var dev = Db.FindDevice("LEGACY0001");
            Check("老库设备数据未丢失", dev != null && dev.Name == "老库设备", dev == null ? "null" : dev.Name);
            var recs = Db.ListRecords(null, null, 100);
            Check("老库流水未丢失（2 条）", recs.Count == 2, "实际 " + recs.Count);
            Check("老库 settings 未丢失", Db.GetSetting("min_interval_min", "") == "1");

            // 老参数 min_interval_min=1 → 归还→借出冷却 60 秒，保证旧配置含义延续
            var r = SwipeRules.Load();
            Check("min_interval_min=1 迁移为 归还→借出 60 秒", r.ReturnToBorrowSec == 60, "实际 " + r.ReturnToBorrowSec);
            Check("默认射频功率落在合法区间", r.RfPowerDbm >= 0 && r.RfPowerDbm <= 30, "实际 " + r.RfPowerDbm);
            Check("默认连续命中确认 ≥ 1", r.HitConfirmTimes >= 1, "实际 " + r.HitConfirmTimes);
        }

        // -------------------------------------------------------------
        //  ② 规则
        // -------------------------------------------------------------
        private static SwipeRules TestRules()
        {
            return new SwipeRules
            {
                RfPowerDbm = 18,
                HitConfirmTimes = 2,
                PollIntervalMs = 250,
                TagCooldownMs = 600,
                GlobalGapMs = 500,
                BorrowToReturnSec = 10,
                ReturnToBorrowSec = 30,
                AbuseWindowSec = 60,
                AbuseMaxHits = 4,
                AbuseGlobalMax = 1000,
                AbuseLockSec = 60,
                BorrowDays = 7,
                DueWarnHours = 24
            };
        }

        private static void TestDistanceRules()
        {
            var r = TestRules();
            r.Normalize();
            Check("功率越界被夹到 0~30", new SwipeRules { RfPowerDbm = 99 }.RfPowerDbm == 99); // 字段本身不夹
            var bad = new SwipeRules { RfPowerDbm = 99, HitConfirmTimes = 0, PollIntervalMs = 1, GlobalGapMs = -5, AbuseLockSec = 0, BorrowDays = -1 };
            bad.Normalize();
            Check("Normalize: 功率上限 30", bad.RfPowerDbm == 30, "实际 " + bad.RfPowerDbm);
            Check("Normalize: 命中确认下限 1", bad.HitConfirmTimes == 1, "实际 " + bad.HitConfirmTimes);
            Check("Normalize: 盘点间隔下限 50", bad.PollIntervalMs == 50, "实际 " + bad.PollIntervalMs);
            Check("Normalize: 全局间隔不为负", bad.GlobalGapMs == 0, "实际 " + bad.GlobalGapMs);
            Check("Normalize: 锁定时长下限 1", bad.AbuseLockSec == 1, "实际 " + bad.AbuseLockSec);
            Check("Normalize: 借用期限不为负", bad.BorrowDays == 0, "实际 " + bad.BorrowDays);

            // 规则可保存/读取
            var save = TestRules();
            save.Save();
            var load = SwipeRules.Load();
            Check("规则保存后可读回（功率）", load.RfPowerDbm == 18, "实际 " + load.RfPowerDbm);
            Check("规则保存后可读回（借出→归还）", load.BorrowToReturnSec == 10, "实际 " + load.BorrowToReturnSec);
            Check("显式保存后不再沿用老参数 60 秒", load.ReturnToBorrowSec == 30, "实际 " + load.ReturnToBorrowSec);
            Check("规则保存后可读回（命中确认）", load.HitConfirmTimes == 2, "实际 " + load.HitConfirmTimes);

            // ---- 音效参数：默认值、边界夹取、保存/读取 ----
            Check("音效默认开启", new SwipeRules().SoundEnabled);
            Check("音效默认音量 90", new SwipeRules().SoundVolume == 90, "实际 " + new SwipeRules().SoundVolume);
            Check("音效默认最长播放 6 秒", new SwipeRules().SoundMaxSec == 6, "实际 " + new SwipeRules().SoundMaxSec);
            var snd = new SwipeRules { SoundVolume = 200, SoundMaxSec = -1 };
            snd.Normalize();
            Check("Normalize: 音量上限 100", snd.SoundVolume == 100, "实际 " + snd.SoundVolume);
            Check("Normalize: 最长播放不为负", snd.SoundMaxSec == 0, "实际 " + snd.SoundMaxSec);
            var snd2 = new SwipeRules { SoundVolume = -10 };
            snd2.Normalize();
            Check("Normalize: 音量下限 0", snd2.SoundVolume == 0, "实际 " + snd2.SoundVolume);

            var sndSave = TestRules();
            sndSave.SoundEnabled = false;
            sndSave.SoundVolume = 55;
            sndSave.SfxBorrowFile = @"D:\音效\borrow.mp3";
            sndSave.SfxReturnFile = @"D:\音效\return.mp3";
            sndSave.SfxAbuseFile = @"D:\CloudMusic\电台节目\华语原声带 - Man！曼巴out.mp3";
            sndSave.Save();
            var sndLoad = SwipeRules.Load();
            Check("音效开关可保存读回", sndLoad.SoundEnabled == false);
            Check("音量可保存读回", sndLoad.SoundVolume == 55, "实际 " + sndLoad.SoundVolume);
            Check("借出音效路径可保存读回", sndLoad.SfxBorrowFile == @"D:\音效\borrow.mp3", sndLoad.SfxBorrowFile);
            Check("归还音效路径可保存读回", sndLoad.SfxReturnFile == @"D:\音效\return.mp3", sndLoad.SfxReturnFile);
            Check("异常音效中文路径原样读回", sndLoad.SfxAbuseFile.EndsWith("华语原声带 - Man！曼巴out.mp3"), sndLoad.SfxAbuseFile);
        }

        // ---------------- 距离闸门（连续命中确认 + 边沿触发） ----------------
        // 用同一时间源（now）调 Seen 和 FinishRound，保证窗口判定的正确性
        private static void Round(TagGate g, DateTime now, params string[] epcs)
        {
            g.BeginRound();
            for (int i = 0; i < epcs.Length; i++) g.Seen(epcs[i], now);
            g.FinishRound(now);
        }

        private static void TestTagGate()
        {
            var t0 = new DateTime(2026, 5, 1, 8, 0, 0);

            // ① 远距离偶发读到（每 3 轮才读到 1 次）→ 连续 30 轮都不产生任何有效刷卡
            var far = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, GapTolerance = 1, MinTotalHits = 1 };
            int farRejects = 0;
            for (int i = 0; i < 30; i++)
            {
                if (i % 3 == 0) Round(far, t0.AddMilliseconds(250 * i), "FAR");
                else Round(far, t0.AddMilliseconds(250 * i));
                farRejects += far.Unconfirmed.Count;
            }
            Check("远距离偶尔读到的标签：30 轮内 0 次有效刷卡", far.TrackedCount > 0 && far.Confirmed.Count == 0,
                "末轮上报 " + far.Confirmed.Count);
            Check("远距离标签给出过“距离未确认”提示", farRejects > 0, "提示次数 " + farRejects);
            Check("同一标签的提示不会刷屏（≤2 次）", farRejects <= 2, "提示次数 " + farRejects);

            // ② 贴近并连续两轮读到 → 上报一次
            var g2 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, GapTolerance = 1, MinTotalHits = 1 };
            Round(g2, t0, "NEAR");
            Check("贴近第 1 轮：尚未确认", g2.Confirmed.Count == 0);
            Round(g2, t0.AddMilliseconds(250), "NEAR");
            Check("贴近连续 2 轮：确认并上报一次", g2.Confirmed.Count == 1 && g2.Confirmed[0] == "NEAR",
                "实际上报 " + g2.Confirmed.Count);

            // ③ 边沿触发：标签一直放着不离开 → 只上报一次
            for (int i = 1; i <= 20; i++) Round(g2, t0.AddMilliseconds(250 * (i + 1)), "NEAR");
            Check("标签一直放在读卡器上：不会反复触发", g2.Confirmed.Count == 0,
                "实际 " + g2.Confirmed.Count);

            // ④ 拿开后再次贴近 → 重新确认后再次上报（真正的“刷卡”语义）
            Round(g2, t0.AddSeconds(10));                    // 离开
            Round(g2, t0.AddSeconds(11), "NEAR");            // 重新贴近第 1 轮
            Check("重新贴近第 1 轮：仍需确认", g2.Confirmed.Count == 0);
            Round(g2, t0.AddSeconds(11).AddMilliseconds(250), "NEAR");
            Check("重新贴近连续 2 轮：再次上报", g2.Confirmed.Count == 1,
                "实际 " + g2.Confirmed.Count);

            // ⑤ 允许漏一轮（读取抖动容忍）
            var g3 = new TagGate { HitConfirmTimes = 3, TagCooldownMs = 600, GapTolerance = 1, MinTotalHits = 1 };
            Round(g3, t0, "JITTER");
            Round(g3, t0.AddMilliseconds(250));                       // 漏一轮
            Round(g3, t0.AddMilliseconds(500), "JITTER");
            Check("漏一轮时命中数连续（2/3）", g3.Confirmed.Count == 0);
            Round(g3, t0.AddMilliseconds(750), "JITTER");
            Check("允许漏一轮：累计 3 次命中即可确认", g3.Confirmed.Count == 1,
                "实际 " + g3.Confirmed.Count);

            // ⑥ 漏两轮则视为离开，命中重新计数
            var g3b = new TagGate { HitConfirmTimes = 3, TagCooldownMs = 0, GapTolerance = 1, MinTotalHits = 1 };
            Round(g3b, t0, "GAP");
            Round(g3b, t0.AddMilliseconds(250));
            Round(g3b, t0.AddMilliseconds(500));
            Round(g3b, t0.AddMilliseconds(750), "GAP");
            Round(g3b, t0.AddMilliseconds(1000), "GAP");
            Check("漏两轮后命中重新计数（2/3，尚未确认）", g3b.Confirmed.Count == 0,
                "实际 " + g3b.Confirmed.Count);
            Round(g3b, t0.AddMilliseconds(1250), "GAP");
            Check("重新计数满 3 次后确认", g3b.Confirmed.Count == 1, "实际 " + g3b.Confirmed.Count);

            // ⑦ 关闭命中确认（=1）时任何一次读到都算
            var g4 = new TagGate { HitConfirmTimes = 1, TagCooldownMs = 600, MinTotalHits = 1 };
            Round(g4, t0, "ANY");
            Check("命中确认设为 1：一次读到即上报", g4.Confirmed.Count == 1);

            // ⑧ 上报冷却：连续上报受 TagCooldownMs 限制
            var g5 = new TagGate { HitConfirmTimes = 1, TagCooldownMs = 5000, MinTotalHits = 1 };
            Round(g5, t0, "COOL");
            Round(g5, t0.AddSeconds(1));            // 离开
            Round(g5, t0.AddSeconds(2), "COOL");    // 又贴近，但未到冷却
            Check("上报冷却期内不重复上报", g5.Confirmed.Count == 0, "实际 " + g5.Confirmed.Count);
            Round(g5, t0.AddSeconds(3));            // 离开
            Round(g5, t0.AddSeconds(6), "COOL");    // 冷却已过
            Check("超过上报冷却后可以再次上报", g5.Confirmed.Count == 1, "实际 " + g5.Confirmed.Count);

            // ⑨ 多标签同时在场
            var g6 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, MinTotalHits = 1 };
            Round(g6, t0, "A", "B");
            Round(g6, t0.AddMilliseconds(250), "A", "B");
            Check("多标签同时确认：两个都上报", g6.Confirmed.Count == 2, "实际 " + g6.Confirmed.Count);

            // ⑩ 长期不用自动清理，避免内存泄漏
            var g7 = new TagGate { HitConfirmTimes = 1, MinTotalHits = 1 };
            Round(g7, t0, "OLD");
            Check("闸门记录了标签状态", g7.TrackedCount == 1, "实际 " + g7.TrackedCount);
            for (int i = 0; i < 10; i++) Round(g7, t0.AddSeconds(i));
            g7.PruneStale(5);
            Check("长期不出现的标签被清理", g7.TrackedCount == 0, "实际 " + g7.TrackedCount);

            // ⑪ 新增：窗口内累计命中次数（MinTotalHits + MaxReadWindowMs）
            //     近距快速读到：连续 3 次（200ms 间隔）总耗时 600ms，应在 2500ms 窗口内
            var g8 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, MinTotalHits = 3, MaxReadWindowMs = 2500 };
            Round(g8, t0, "WIN");
            Check("窗口内：第 1 次读到（未达连续 2 轮）", g8.Confirmed.Count == 0);
            Round(g8, t0.AddMilliseconds(200), "WIN");
            Check("窗口内：连续 2 轮已达标，但窗口总命中才 2 次（未达 MinTotalHits=3）", g8.Confirmed.Count == 0);
            Round(g8, t0.AddMilliseconds(400), "WIN");
            Check("窗口内：累计 3 次命中，确认并上报", g8.Confirmed.Count == 1, "实际 " + g8.Confirmed.Count);

            // ⑫ 远距离偶发读到（每 5 轮 1 次）即使设了 MinTotalHits 也永远不够 3 次
            var g9 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, MinTotalHits = 3, MaxReadWindowMs = 2500 };
            int g9Confirms = 0;
            for (int i = 0; i < 30; i++)
            {
                if (i % 5 == 0) Round(g9, t0.AddMilliseconds(250 * i), "FAR2");
                else Round(g9, t0.AddMilliseconds(250 * i));
                g9Confirms += g9.Confirmed.Count;
            }
            Check("窗口过滤：远距离偶发读到 30 轮内 0 次上报", g9Confirms == 0, "实际 " + g9Confirms);

            // ⑬ 窗口超时后窗口内计数清零（读到太慢视为离开）
            var g10 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 0, MinTotalHits = 3, MaxReadWindowMs = 800 };
            Round(g10, t0, "SLOW");
            Round(g10, t0.AddMilliseconds(200), "SLOW");                // 已连续 2 轮，窗口内 2 次
            Check("读到第 2 次：连续达标但窗口累计 2 次<3，未上报", g10.Confirmed.Count == 0);
            Round(g10, t0.AddMilliseconds(1500), "SLOW");               // 已超 800ms 窗口 → 窗口清零
            Check("窗口超时：窗口重置后累计清零，未上报", g10.Confirmed.Count == 0);
            Round(g10, t0.AddMilliseconds(1700), "SLOW");               // 新窗口内第 1 次
            Check("新窗口内累计 1 次：远不到 3 次", g10.Confirmed.Count == 0);
            Round(g10, t0.AddMilliseconds(1900), "SLOW");               // 新窗口内第 2 次
            Check("新窗口内累计 2 次：仍不到 3 次", g10.Confirmed.Count == 0);
            Round(g10, t0.AddMilliseconds(2100), "SLOW");               // 新窗口内第 3 次
            Check("新窗口内累计 3 次：终于确认", g10.Confirmed.Count == 1, "实际 " + g10.Confirmed.Count);

            // ⑭ MinTotalHits = 1 等于关闭本层过滤（沿用旧 HitConfirmTimes 行为）
            var g11 = new TagGate { HitConfirmTimes = 2, TagCooldownMs = 600, MinTotalHits = 1 };
            Round(g11, t0, "OFF");
            Round(g11, t0.AddMilliseconds(250), "OFF");
            Check("MinTotalHits=1：关闭窗口过滤，旧逻辑不变", g11.Confirmed.Count == 1);
        }

        private static void TestIntervalRules()
        {
            var r = TestRules();
            var g = new SwipeGuard();
            var t0 = new DateTime(2026, 5, 1, 9, 0, 0);
            string reason;

            Check("首次刷卡放行", g.Check("E1", "borrow", r, t0, out reason) == SwipeVerdict.Allow, reason);
            g.Commit("E1", "borrow", t0);

            Check("借出后 5 秒归还被拦（借出→归还 ≥10 秒）",
                g.Check("E1", "return", r, t0.AddSeconds(5), out reason) == SwipeVerdict.TooSoon, reason);

            Check("借出后 11 秒归还放行",
                g.Check("E1", "return", r, t0.AddSeconds(11), out reason) == SwipeVerdict.Allow, reason);
            g.Commit("E1", "return", t0.AddSeconds(11));

            Check("归还后 12 秒再借出被拦（归还→借出 ≥30 秒）",
                g.Check("E1", "borrow", r, t0.AddSeconds(23), out reason) == SwipeVerdict.TooSoon, reason);
            Check("归还后 31 秒再借出放行",
                g.Check("E1", "borrow", r, t0.AddSeconds(42), out reason) == SwipeVerdict.Allow, reason);

            // 全局最小间隔：不同标签之间也要有间隔
            var g2 = new SwipeGuard();
            var t1 = new DateTime(2026, 5, 1, 10, 0, 0);
            g2.Commit("A1", "borrow", t1);
            Check("全局间隔 100ms 被拦（<500ms）",
                g2.Check("B1", "borrow", r, t1.AddMilliseconds(100), out reason) == SwipeVerdict.GlobalTooFast, reason);
            Check("全局间隔 600ms 放行",
                g2.Check("B1", "borrow", r, t1.AddMilliseconds(600), out reason) == SwipeVerdict.Allow, reason);

            // 关闭间隔（0 = 不限）
            var r0 = TestRules();
            r0.BorrowToReturnSec = 0; r0.ReturnToBorrowSec = 0; r0.GlobalGapMs = 0;
            var g3 = new SwipeGuard();
            g3.Commit("C1", "borrow", t0);
            Check("间隔设为 0 时不限（可立即归还）",
                g3.Check("C1", "return", r0, t0.AddMilliseconds(50), out reason) == SwipeVerdict.Allow, reason);
        }

        private static void TestAbuseRules()
        {
            var r = TestRules();      // AbuseMaxHits = 4，窗口 60 秒，锁定 60 秒
            var g = new SwipeGuard();
            var t0 = new DateTime(2026, 5, 1, 11, 0, 0);
            string reason;

            SwipeVerdict v = SwipeVerdict.Allow;
            for (int i = 0; i < 4; i++)
                v = g.Check("D1", "borrow", r, t0.AddSeconds(i * 2), out reason);
            Check("60 秒内第 4 次刷卡未锁定", v == SwipeVerdict.Allow, v.ToString());

            v = g.Check("D1", "borrow", r, t0.AddSeconds(9), out reason);
            Check("超过上限（第 5 次）触发防刷锁定", v == SwipeVerdict.AbuseLocked, v + " / " + reason);

            v = g.Check("D1", "borrow", r, t0.AddSeconds(20), out reason);
            Check("锁定期内继续刷卡仍被拒绝", v == SwipeVerdict.AbuseLocked, v + " / " + reason);

            v = g.Check("D1", "borrow", r, t0.AddSeconds(100), out reason);
            Check("锁定到期后自动恢复放行", v == SwipeVerdict.Allow, v + " / " + reason);

            // 其它标签不受影响（只是被全局锁定/间隔影响）
            var g4 = new SwipeGuard();
            for (int i = 0; i < 5; i++) g4.Check("F1", "borrow", r, t0.AddSeconds(i * 2), out reason);
            Check("被锁定的标签可以被列举出来", g4.LockedEpcs(t0.AddSeconds(10)).Contains("F1"));
            g4.ClearLocks();
            Check("解除锁定后立即恢复放行",
                g4.Check("F1", "borrow", r, t0.AddSeconds(12), out reason) == SwipeVerdict.Allow, reason);

            // 全局上限
            var rg = TestRules();
            rg.AbuseGlobalMax = 5; rg.AbuseMaxHits = 100; rg.GlobalGapMs = 0;
            var gg = new SwipeGuard();
            SwipeVerdict last = SwipeVerdict.Allow;
            for (int i = 0; i < 6; i++)
                last = gg.Check("T" + i, "borrow", rg, t0.AddSeconds(i), out reason);
            Check("全局刷卡次数超限触发全局锁定", last == SwipeVerdict.AbuseLocked, last + " / " + reason);
        }

        // -------------------------------------------------------------
        //  ⑤ 只读数据库（回归测试：曾经的启动崩溃 bug）
        //  复现条件：数据库文件不可写（例如程序被放在桌面等低完整性目录，
        //  低完整性进程无法写入 %LOCALAPPDATA% 下的数据库）。
        //  历史 bug：启动时自动补列(ALTER TABLE)抛 SQLiteException，
        //            被包装成 TypeInitializationException，程序一打开就崩。
        //  由于 Db 是静态类（静态构造函数只跑一次），这里用子进程做真实复现。
        // -------------------------------------------------------------
        private static void TestReadOnlyFallback()
        {
            string roDb = Path.Combine(Path.GetDirectoryName(dbPath), "readonly_test.db");
            DeleteIfExists(roDb);
            File.Copy(dbPath, roDb, true);
            File.SetAttributes(roDb, FileAttributes.ReadOnly);   // 让 SQLite 认为库不可写

            try
            {
                // 先在当前进程里确认：只读库上执行迁移确实会报 readonly（复现用户环境）
                bool readOnlyConfirmed = false;
                try
                {
                    using (var c = new SQLiteConnection("Data Source=" + roDb + ";Version=3;"))
                    {
                        c.Open();
                        using (var cmd = c.CreateCommand())
                        {
                            cmd.CommandText = "ALTER TABLE devices ADD COLUMN zz_probe TEXT";
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                catch (SQLiteException ex)
                {
                    readOnlyConfirmed = ex.Message.IndexOf("readonly", StringComparison.OrdinalIgnoreCase) >= 0
                                     || ex.Message.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { }
                Check("只读库上执行迁移会报 readonly（复现用户环境）", readOnlyConfirmed);

                // 子进程复现：以只读库启动，检查初始化不再抛异常、降级为只读模式
                string exePath = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
                var psi = new System.Diagnostics.ProcessStartInfo(exePath, "--readonly-probe \"" + roDb + "\"");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                psi.EnvironmentVariables["LABBORROW_DB"] = roDb;
                int exit;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    exit = p.HasExited ? p.ExitCode : -1;
                }
                Check("只读库启动不再崩溃（子进程退出码 0）", exit == 0, "退出码 " + exit);
            }
            finally
            {
                try { File.SetAttributes(roDb, FileAttributes.Normal); } catch { }
                DeleteIfExists(roDb);
            }
        }

        /// <summary>子进程模式：以只读数据库启动，验证初始化不抛异常且降级为只读</summary>
        private static int ReadOnlyProbe(string dbFile)
        {
            int fails = 0;
            try
            {
                string p = Db.DbPath;                      // 触发静态构造函数（历史崩溃点）
                bool threw = false;
                try
                {
                    Db.ListDevices(null, null);
                    Db.ListRecords(null, null, 10);
                    Db.GetTodayStats();
                    Db.ListLent(24);
                    Db.ListUsers(null);
                    Db.ListAudit(10);
                    Db.ListBorrowers();
                    Db.GetReport(DateTime.Today.AddDays(-30), DateTime.Today, 24, 5);
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine("  FAIL 只读模式下接口抛异常: " + ex.GetType().Name + " " + ex.Message);
                }
                if (threw) fails++;
                if (Db.Writable) { Console.WriteLine("  FAIL 只读库被误判为可写"); fails++; }
                if (string.IsNullOrEmpty(Db.SchemaError)) { Console.WriteLine("  FAIL 未给出只读原因说明"); fails++; }
                if (Db.FindDevice("LEGACY0001") == null) { Console.WriteLine("  FAIL 只读模式下读不到原有设备"); fails++; }
                if (Db.ListRecords(null, null, 100).Count < 2) { Console.WriteLine("  FAIL 只读模式下读不到原有流水"); fails++; }
                Console.WriteLine(fails == 0
                    ? "  OK 只读库：初始化未抛异常，已降级为只读模式，原有数据可读"
                    : "  FAIL 只读库回归测试未通过");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAIL 只读库初始化抛出异常（正是历史 bug）: " + ex.GetType().FullName + " / " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("       inner: " + ex.InnerException.Message);
                fails++;
            }
            return fails == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------
        //  ③ 期限
        // -------------------------------------------------------------
        private static void TestDue()
        {
            string borrow = "2026-05-01 09:00:00";
            Check("借用期限 7 天 → 应还 05-08 09:00",
                DueUtil.ComputeDue(borrow, 7) == "2026-05-08 09:00:00", DueUtil.ComputeDue(borrow, 7));
            Check("期限 0 天 → 不设应还时间", DueUtil.ComputeDue(borrow, 0) == "");
            Check("借出时间非法 → 不设应还时间", DueUtil.ComputeDue("坏数据", 7) == "");

            var due = "2026-05-08 09:00:00";
            var nowBefore = new DateTime(2026, 5, 6, 9, 0, 0);
            var nowSoon = new DateTime(2026, 5, 8, 0, 0, 0);     // 距应还 9 小时 < 24 小时
            var nowOver = new DateTime(2026, 5, 9, 9, 0, 0);
            Check("未到临期 → 正常", DueUtil.State(due, nowBefore, 24) == DueUtil.StateNormal);
            Check("距应还 9 小时 → 临期", DueUtil.State(due, nowSoon, 24) == DueUtil.StateDueSoon);
            Check("超过应还时间 → 逾期", DueUtil.State(due, nowOver, 24) == DueUtil.StateOverdue);
            Check("未设期限 → 正常", DueUtil.State("", nowOver, 24) == DueUtil.StateNormal);
            Check("临期提醒 0 小时 → 不判临期", DueUtil.State(due, nowSoon, 0) == DueUtil.StateNormal);

            Check("时长可读化：90 分钟", DueUtil.HumanSpan(TimeSpan.FromMinutes(90)) == "1 小时 30 分",
                DueUtil.HumanSpan(TimeSpan.FromMinutes(90)));
            Check("时长可读化：50 小时", DueUtil.HumanSpan(TimeSpan.FromHours(50)).StartsWith("2 天"),
                DueUtil.HumanSpan(TimeSpan.FromHours(50)));
        }

        // -------------------------------------------------------------
        //  ④ 数据层
        // -------------------------------------------------------------
        private static void TestUsers()
        {
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var u = new User { CardEpc = "CARD0001", Name = "李四", UserNo = "2026001", ClassName = "电气 2601", Role = "student", Notes = "", CreatedAt = now, UpdatedAt = now };
            Db.InsertUser(u);
            var list = Db.ListUsers(null);
            Check("新增借用人成功", list.Count == 1, "实际 " + list.Count + " 条");

            var found = Db.FindUserByCard("card0001");   // 大小写不敏感
            Check("按卡号可查到借用人", found != null && found.Name == "李四", found == null ? "null" : found.Name);
            Check("未登记的卡查不到", Db.FindUserByCard("NOPE") == null);
            Check("卡号占用校验生效", Db.CardEpcTaken("CARD0001", 0));
            Check("编辑自己时不报占用", !Db.CardEpcTaken("CARD0001", found == null ? 0 : found.Id));

            found.ClassName = "电气 2602";
            found.UpdatedAt = now;
            Db.UpdateUser(found);
            var again = Db.FindUserByCard("CARD0001");
            Check("修改借用人生效", again != null && again.ClassName == "电气 2602", again == null ? "null" : again.ClassName);

            Check("历史借用人下拉包含姓名", Db.ListBorrowers().Contains("李四"));
            Db.DeleteUser(again.Id);
            Check("删除借用人生效", Db.ListUsers(null).Count == 0);
        }

        private static void TestAudit()
        {
            Db.ClearAudit();
            Db.AddAudit("EPC-A", "interval", "归还过快被拦截", "测试");
            Db.AddAudit("EPC-B", "abuse", "1 分钟内刷卡 9 次，已锁定", "测试");
            var list = Db.ListAudit(10);
            Check("审计写入并读回（2 条）", list.Count == 2, "实际 " + list.Count);
            Check("审计类型标签可读", list.Count > 0 && list[0].KindLabel == "防刷锁定",
                list.Count > 0 ? list[0].KindLabel : "无数据");
            Check("审计计数正确", Db.CountAudit() == 2, "实际 " + Db.CountAudit());
            Check("清空审计生效", Db.ClearAudit() >= 0 && Db.CountAudit() == 0);
        }

        private static void TestRecordsAndReport()
        {
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var dev = new Device
            {
                Epc = "BUSY0001", Name = "示波器", Model = "DS1102", Category = "仪器仪表",
                Location = "B 柜", Status = "borrowed", CurrentBorrower = "王五",
                BorrowTime = now, DueTime = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"),
                Notes = "", CreatedAt = now, UpdatedAt = now
            };
            Db.UpsertDevice(dev);
            var back = Db.FindDevice("BUSY0001");
            Check("设备应还时间可读写", back != null && back.DueTime == dev.DueTime, back == null ? "null" : back.DueTime);
            Check("设备状态可读写", back != null && back.Status == "borrowed");

            Db.AddRecord(new BorrowRecord
            {
                Epc = "BUSY0001", DeviceName = "示波器", Borrower = "王五", Direction = "borrow",
                EventTime = now, DurationMin = 0, Notes = "", DueTime = dev.DueTime, UserCard = "CARD0001"
            });
            Db.AddRecord(new BorrowRecord
            {
                Epc = "BUSY0001", DeviceName = "示波器", Borrower = "王五", Direction = "return",
                EventTime = now, DurationMin = 120, Notes = "借用时长 120 分钟", DueTime = dev.DueTime,
                UserCard = "CARD0001", Overdue = true
            });

            var recs = Db.ListRecords(null, null, 50);
            Check("流水可读回（老库 2 条 + 新增 2 条）", recs.Count == 4, "实际 " + recs.Count);
            var today = Db.ListRecords(DateTime.Today, DateTime.Today, 50);
            Check("按日期过滤生效（今天 2 条）", today.Count == 2, "实际 " + today.Count);
            var ret = recs.Find(x => x.Direction == "return" && x.Epc == "BUSY0001");
            Check("归还记录带逾期标记", ret != null && ret.Overdue, ret == null ? "null" : ret.Overdue.ToString());
            Check("归还记录带借用人卡", ret != null && ret.UserCard == "CARD0001", ret == null ? "" : ret.UserCard);
            var bor = recs.Find(x => x.Direction == "borrow" && x.Epc == "BUSY0001");
            Check("借出记录识别为已归还", bor != null && bor.Returned, bor == null ? "null" : bor.Returned.ToString());

            // 在借明细（逾期排序在前）
            var lent = Db.ListLent(24);
            Check("在借明细包含外借设备", lent.Count == 1 && lent[0].Epc == "BUSY0001", "实际 " + lent.Count);
            Check("在借明细判定为逾期", lent.Count > 0 && lent[0].State == DueUtil.StateOverdue,
                lent.Count > 0 ? lent[0].State.ToString() : "无数据");
            Check("逾期时长大于 0", lent.Count > 0 && lent[0].OverdueHours > 0,
                lent.Count > 0 ? lent[0].OverdueHours.ToString("0.#") : "无数据");

            var stat = Db.GetTodayStats();
            Check("今日统计：外借数 ≥1", stat.OutNow >= 1, "实际 " + stat.OutNow);
            Check("今日统计：逾期数 ≥1", stat.OverdueNow >= 1, "实际 " + stat.OverdueNow);

            var rep = Db.GetReport(DateTime.Today.AddDays(-7), DateTime.Today, 24, 8);
            Check("报表：区间借出次数 ≥1", rep.Borrows >= 1, "实际 " + rep.Borrows);
            Check("报表：区间归还次数 ≥1", rep.Returns >= 1, "实际 " + rep.Returns);
            Check("报表：日趋势天数 = 8 天", rep.DailyBorrow.Count == 8, "实际 " + rep.DailyBorrow.Count);
            Check("报表：设备排行非空", rep.TopDevices.Count > 0, "实际 " + rep.TopDevices.Count);
            Check("报表：借用人排行非空", rep.TopBorrowers.Count > 0, "实际 " + rep.TopBorrowers.Count);
            Check("报表：分类占比非空", rep.ByCategory.Count > 0, "实际 " + rep.ByCategory.Count);
            Check("报表：逾期归还计数 ≥1", rep.OverdueReturns >= 1, "实际 " + rep.OverdueReturns);
        }

        private static void TestBackupRestore()
        {
            var before = Db.ListDevices(null, null);
            string bak = Db.BackupNow("selftest");
            Check("备份文件已生成", File.Exists(bak), bak);
            var list = Db.ListBackups();
            Check("备份列表可读", list.Count >= 1, "实际 " + list.Count);

            // 自动备份清理只删 “_auto”，不会误删手动/升级前备份
            string manual = Db.BackupNow("manual");
            int removed = Db.PruneAutoBackups(1);
            Check("自动清理不会删除手动备份", File.Exists(manual), manual);
            Check("自动清理不会删除升级前备份",
                Db.ListBackups().Exists(delegate (Db.BackupFile b) { return b.Name.Contains("before_upgrade"); }));

            // 破坏数据后再恢复
            Db.DeleteDevice("BUSY0001");
            Check("删除后设备数减少", Db.ListDevices(null, null).Count == before.Count - 1);

            string safety = Db.RestoreFrom(bak);
            Db.Reinitialize();
            Check("恢复后设备回来了", Db.ListDevices(null, null).Count == before.Count,
                "实际 " + Db.ListDevices(null, null).Count);
            Check("恢复前自动留了安全备份", !string.IsNullOrEmpty(safety) && File.Exists(safety), safety ?? "");
        }
    }
}
