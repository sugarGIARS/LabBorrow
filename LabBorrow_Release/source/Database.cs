// =====================================================================
//  Database.cs  --  SQLite data access (LabBorrow 实验室设备借还管理)
//  ---------------------------------------------------------------------
//  表结构：
//    devices    设备登记表（主键 epc）
//    records    借还流水表（主键 id 自增）
//    settings   系统设置表（主键 key）
//    users      借用人表（学生卡，主键 id）
//    audit_log  刷卡审计表（拦截/异常留痕，主键 id 自增）
//
//  向后兼容：InitSchema() 会在启动时自动补列（ALTER TABLE ADD COLUMN），
//  老版本数据库直接拿来用即可，无需手工改表，也不会丢数据。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace LabBorrow
{
    public static class Db
    {
        public static string DbPath { get; private set; }
        public static string ConnStr { get; private set; }

        /// <summary>
        /// 新版数据结构（新增列/新增表）是否已就绪——决定 SQL 里能不能引用新列。
        /// </summary>
        public static bool SchemaReady { get; private set; }

        /// <summary>
        /// 数据库当前是否可写。false 表示处于“只读模式”：程序照常打开、照常查看，
        /// 但保存数据会失败并给出修复提示（最常见原因：程序被放在桌面等带低完整性
        /// (Low) 标签的目录，低完整性进程无法写入 %LOCALAPPDATA% 下的数据库）。
        /// </summary>
        public static bool Writable { get; private set; }

        /// <summary>结构未就绪 / 不可写时的原因说明（已翻译成人话）</summary>
        public static string SchemaError { get; private set; }

        /// <summary>当前数据库结构版本；老库（user_version 更小或为 0）升级前会自动留一份备份</summary>
        private const int SchemaVersion = 1;

        static Db()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabBorrow");

            // 允许用环境变量覆盖数据库位置：便于自测（SelfTest.cs）与绿色便携部署，
            // 正常双击运行时该变量为空，行为与旧版本完全一致。
            string overridePath = Environment.GetEnvironmentVariable("LABBORROW_DB");
            if (!string.IsNullOrEmpty(overridePath))
            {
                DbPath = overridePath;
                string od = Path.GetDirectoryName(DbPath);
                if (!string.IsNullOrEmpty(od)) Directory.CreateDirectory(od);
            }
            else
            {
                Directory.CreateDirectory(dir);
                DbPath = Path.Combine(dir, "labborrow.db");
            }

            ConnStr = "Data Source=" + DbPath + ";Version=3;Pooling=true;";

            // 注意：静态构造函数绝对不能抛异常！
            // 否则会变成 TypeInitializationException，程序一打开就崩（历史 bug）。
            Initialize();
        }

        /// <summary>
        /// 初始化 / 升级数据库结构。任何失败都只记录原因并降级为只读模式，不抛出。
        /// 可重复调用：用户修好目录权限后调用一次即可自愈（见 EnsureSchema）。
        /// </summary>
        private static void Initialize()
        {
            SchemaReady = false;
            Writable = false;
            SchemaError = "";
            try
            {
                int ver = File.Exists(DbPath) ? GetUserVersion() : 0;
                bool needUpgrade = ver < SchemaVersion;

                // ---- 升级前自动备份：老版本数据库第一次被新版打开时，先原样留一份 ----
                // 补列建表本身是纯增量操作，不会删数据；这里再加一道保险，方便随时回退。
                if (File.Exists(DbPath) && needUpgrade)
                {
                    try { PreUpgradeBackup(); }
                    catch { /* 备份失败（例如无写权限）不阻断启动 */ }
                }

                InitSchema();
                SchemaReady = true;

                // 结构升级本身就写入了数据 → 说明可写；否则再做一次轻量写探测
                bool wrote = false;
                try { SetUserVersion(SchemaVersion); wrote = needUpgrade; }
                catch { }

                Writable = wrote || WriteProbe();
                if (!Writable && string.IsNullOrEmpty(SchemaError))
                    SchemaError = ReadOnlyHint;
            }
            catch (Exception ex)
            {
                SchemaReady = false;
                Writable = false;
                SchemaError = DescribeDbError(ex);
            }
        }

        /// <summary>写操作前调用：结构未就绪或库只读时重试一次（用户可能刚修好权限）</summary>
        public static bool EnsureSchema()
        {
            if (SchemaReady && Writable) return true;
            Initialize();
            return SchemaReady && Writable;
        }

        /// <summary>
        /// 可写性探测：建一张临时表再删掉。
        /// 只读库上 CREATE TABLE 必然抛 readonly；可写库上留不下任何东西。
        /// （BEGIN IMMEDIATE 在部分驱动/文件属性组合下申请写锁仍会成功，不可靠。）
        /// </summary>
        private static bool WriteProbe()
        {
            try
            {
                using (var c = new SQLiteConnection(ConnStr))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "DROP TABLE IF EXISTS _lb_write_probe";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "CREATE TABLE _lb_write_probe(id INTEGER)";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "DROP TABLE _lb_write_probe";
                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                string m = ex.Message + (ex.InnerException != null ? " " + ex.InnerException.Message : "");
                if (m.IndexOf("readonly", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("attempt to write", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                return true;    // 其它异常（例如偶发占用）不判为只读，避免误报
            }
        }

        private const string ReadOnlyHint =
            "数据库当前为只读，无法保存/升级数据。常见原因：程序被放在「桌面」等带低完整性(Low)标签的目录里，"
          + "低完整性进程不能写入 %LOCALAPPDATA% 下的数据库。解决办法：双击程序目录下的 fix_integrity.bat，"
          + "或把整个目录移到桌面以外（如 C:\\LabBorrow），然后点「修复后点此重试」。";

        /// <summary>把底层异常翻译成用户能看懂、能照做的一句话</summary>
        private static string DescribeDbError(Exception ex)
        {
            string msg = ex == null ? "" : ex.Message;
            if (ex != null && ex.InnerException != null) msg += " " + ex.InnerException.Message;
            if (msg.IndexOf("readonly", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("attempt to write", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReadOnlyHint;
            return msg;
        }

        private static int GetUserVersion()
        {
            try
            {
                using (var c = new SQLiteConnection(ConnStr))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA user_version";
                        object v = cmd.ExecuteScalar();
                        return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
                    }
                }
            }
            catch { return 0; }
        }

        private static void SetUserVersion(int v)
        {
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA user_version=" + v;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void PreUpgradeBackup()
        {
            Directory.CreateDirectory(BackupDir);
            string dst = Path.Combine(BackupDir,
                "labborrow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_before_upgrade.db");
            SQLiteConnection.ClearAllPools();
            File.Copy(DbPath, dst, true);
        }

        /// <summary>数据库所在目录</summary>
        public static string DbDir
        {
            get
            {
                string d = Path.GetDirectoryName(DbPath);
                return string.IsNullOrEmpty(d) ? "." : d;
            }
        }

        /// <summary>
        /// 从备份恢复后重新建表/补列（备份可能是老版本结构）。
        /// 普通运行时由静态构造函数自动调用，无需手工调用。
        /// </summary>
        public static void Reinitialize()
        {
            SQLiteConnection.ClearAllPools();
            InitSchema();
        }

        /// <summary>备份目录：数据库同级的 backups</summary>
        public static string BackupDir { get { return Path.Combine(DbDir, "backups"); } }

        // ----------------------------------------------------------------
        //  Schema: devices(设备) / records(借还流水) / settings(系统设置)
        //          users(借用人) / audit_log(刷卡审计)
        // ----------------------------------------------------------------
        private static void InitSchema()
        {
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS devices (
  epc TEXT PRIMARY KEY,               -- 设备 RFID 标签 EPC（主键）
  name TEXT NOT NULL,                 -- 设备名称
  model TEXT,                         -- 型号
  category TEXT,                      -- 分类
  location TEXT,                      -- 存放位置
  status TEXT NOT NULL DEFAULT 'idle',-- idle|borrowed|maintenance|retired
  current_borrower TEXT,              -- 当前借用人
  borrow_time TEXT,                   -- 借出时间
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_devices_status ON devices(status);
CREATE TABLE IF NOT EXISTS records (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  epc TEXT NOT NULL,
  device_name TEXT,
  borrower TEXT,
  direction TEXT NOT NULL,            -- borrow | return
  event_time TEXT NOT NULL,
  duration_min REAL DEFAULT 0,        -- 本次借用时长（归还时计算）
  notes TEXT
);
CREATE INDEX IF NOT EXISTS idx_records_time ON records(event_time DESC);
CREATE INDEX IF NOT EXISTS idx_records_epc ON records(epc);
CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  card_epc TEXT UNIQUE,               -- 借用人卡（学生卡）EPC，可空
  name TEXT NOT NULL,                 -- 姓名
  user_no TEXT,                       -- 学号 / 工号
  class_name TEXT,                    -- 班级 / 部门
  role TEXT DEFAULT 'student',        -- student | teacher | admin
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_users_card ON users(card_epc);
CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  event_time TEXT NOT NULL,
  epc TEXT,                           -- 相关标签
  kind TEXT NOT NULL,                 -- interval|abuse|unknown|status|distance|other
  reason TEXT,                        -- 拦截原因（界面显示）
  detail TEXT                         -- 补充信息（设备名 / 判定值等）
);
CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_log(event_time DESC);";
                    cmd.ExecuteNonQuery();
                }

                // ---- 老库补列（新版字段），已存在则跳过 ----
                EnsureColumn(c, "devices", "due_time", "TEXT");            // 应归还时间
                EnsureColumn(c, "records", "due_time", "TEXT");            // 该次借出的应还时间
                EnsureColumn(c, "records", "user_card", "TEXT");           // 借用人卡 EPC
                EnsureColumn(c, "records", "overdue", "INTEGER DEFAULT 0");// 归还时是否已逾期
                EnsureColumn(c, "users", "phone", "TEXT");                 // 手机号（催还通知用，可空）
                EnsureColumn(c, "users", "email", "TEXT");                 // 邮箱（自动邮件提醒用，可空）
                EnsureColumn(c, "devices", "mail_notified_at", "TEXT");    // 该设备上次发催还邮件的时间（防重复发）
            }
        }

        /// <summary>缺列则补列，保证老数据库升级后可直接使用</summary>
        private static void EnsureColumn(SQLiteConnection c, string table, string column, string decl)
        {
            bool exists = false;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ")";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(r["name"].ToString(), column, StringComparison.OrdinalIgnoreCase))
                        { exists = true; break; }
            }
            if (exists) return;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + decl;
                cmd.ExecuteNonQuery();
            }
        }

        // -------- Settings --------
        public static string GetSetting(string key, string def = "")
        {
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
                    cmd.Parameters.AddWithValue("@k", key);
                    var v = cmd.ExecuteScalar();
                    return v == null || v == DBNull.Value ? def : (string)v;
                }
            }
        }
        public static void SetSetting(string key, string value)
        {
            EnsureSchema();
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES(@k,@v)";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@v", value ?? "");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>读整数设置；键不存在或内容非法时返回 def（新版参数都用这种方式兜底，无需写库）</summary>
        public static int GetIntSetting(string key, int def)
        {
            string s = GetSetting(key, "");
            int v;
            if (string.IsNullOrEmpty(s) || !int.TryParse(s.Trim(), out v)) return def;
            return v;
        }

        public static void SetIntSetting(string key, int value)
        {
            SetSetting(key, value.ToString());
        }

        public static void DeleteSetting(string key)
        {
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM settings WHERE key=@k";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // -------- Devices --------
        public static Device FindDevice(string epc)
        {
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM devices WHERE epc=@e";
                    cmd.Parameters.AddWithValue("@e", epc);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) return ReadDevice(r);
                }
            }
            return null;
        }

        public static List<Device> ListDevices(string keyword = null, string status = null)
        {
            var list = new List<Device>();
            using (var c = new SQLiteConnection(ConnStr))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    var sql = "SELECT * FROM devices";
                    var wh = new List<string>();
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        wh.Add("(epc LIKE @k OR name LIKE @k OR model LIKE @k OR current_borrower LIKE @k)");
                        cmd.Parameters.AddWithValue("@k", "%" + keyword + "%");
                    }
                    if (!string.IsNullOrWhiteSpace(status) && status != "all")
                    {
                        wh.Add("status=@t");
                        cmd.Parameters.AddWithValue("@t", status);
                    }
                    if (wh.Count > 0) sql += " WHERE " + string.Join(" AND ", wh);
                    sql += " ORDER BY updated_at DESC";
                    cmd.CommandText = sql;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) list.Add(ReadDevice(r));
                }
            }
            return list;
        }

        public static void UpsertDevice(Device d)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO devices(epc,name,model,category,location,status,current_borrower,borrow_time,due_time,notes,created_at,updated_at)
VALUES(@e,@n,@m,@c,@l,@s,@b,@bt,@dt,@no,@ca,@ua)
ON CONFLICT(epc) DO UPDATE SET
 name=excluded.name, model=excluded.model, category=excluded.category, location=excluded.location,
 status=excluded.status, current_borrower=excluded.current_borrower, borrow_time=excluded.borrow_time,
 due_time=excluded.due_time,
 notes=excluded.notes, updated_at=excluded.updated_at;";
                    cmd.Parameters.AddWithValue("@e", d.Epc);
                    cmd.Parameters.AddWithValue("@n", d.Name);
                    cmd.Parameters.AddWithValue("@m", d.Model ?? "");
                    cmd.Parameters.AddWithValue("@c", d.Category ?? "");
                    cmd.Parameters.AddWithValue("@l", d.Location ?? "");
                    cmd.Parameters.AddWithValue("@s", d.Status);
                    cmd.Parameters.AddWithValue("@b", d.CurrentBorrower ?? "");
                    cmd.Parameters.AddWithValue("@bt", d.BorrowTime ?? "");
                    cmd.Parameters.AddWithValue("@dt", d.DueTime ?? "");
                    cmd.Parameters.AddWithValue("@no", d.Notes ?? "");
                    cmd.Parameters.AddWithValue("@ca", d.CreatedAt);
                    cmd.Parameters.AddWithValue("@ua", d.UpdatedAt);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static int DeleteDevice(string epc)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM devices WHERE epc=@e";
                    cmd.Parameters.AddWithValue("@e", epc);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        private static Device ReadDevice(IDataReader r)
        {
            return new Device
            {
                Epc = r["epc"].ToString(),
                Name = r["name"].ToString(),
                Model = r["model"].ToString(),
                Category = r["category"].ToString(),
                Location = r["location"].ToString(),
                Status = r["status"].ToString(),
                CurrentBorrower = r["current_borrower"].ToString(),
                BorrowTime = r["borrow_time"].ToString(),
                DueTime = Col(r, "due_time"),
                Notes = r["notes"].ToString(),
                CreatedAt = r["created_at"].ToString(),
                UpdatedAt = r["updated_at"].ToString(),
            };
        }

        // 兼容写法：列不存在时返回空串而不是抛异常
        private static string Col(IDataReader r, string name)
        {
            try
            {
                int i = r.GetOrdinal(name);
                if (r.IsDBNull(i)) return "";
                return r.GetValue(i).ToString();
            }
            catch { return ""; }
        }

        // -------- 在借明细（首页看板 + 逾期高亮） --------
        public class LentItem
        {
            public string Epc, Name, Borrower, BorrowTime, DueTime, UserCard, Location;
            public string MailNotifiedAt;  // 上次对该设备发催还邮件的时间（空=从没发过）
            public double BorrowedHours;   // 已借时长（小时）
            public double OverdueHours;    // 已逾期时长（小时），未逾期为 0
            public int State;              // DueUtil.StateNormal / StateDueSoon / StateOverdue
        }

        public static List<LentItem> ListLent(int warnHours)
        {
            var list = new List<LentItem>();
            var now = DateTime.Now;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    // 旧结构（或只读模式）没有 due_time 列：用空串占位，保证看板仍可显示
                    cmd.CommandText = SchemaReady
                        ? @"SELECT epc,name,current_borrower,borrow_time,due_time,location,mail_notified_at
                             FROM devices WHERE status='borrowed'"
                        : @"SELECT epc,name,current_borrower,borrow_time,'' AS due_time,location,'' AS mail_notified_at
                             FROM devices WHERE status='borrowed'";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var it = new LentItem
                            {
                                Epc = r["epc"].ToString(),
                                Name = r["name"].ToString(),
                                Borrower = r["current_borrower"].ToString(),
                                BorrowTime = Col(r, "borrow_time"),
                                DueTime = Col(r, "due_time"),
                                Location = Col(r, "location"),
                                MailNotifiedAt = Col(r, "mail_notified_at"),
                            };
                            DateTime bt;
                            if (DateTime.TryParse(it.BorrowTime, out bt))
                                it.BorrowedHours = Math.Max(0, (now - bt).TotalHours);
                            it.State = DueUtil.State(it.DueTime, now, warnHours);
                            if (it.State == DueUtil.StateOverdue)
                            {
                                DateTime due;
                                if (DateTime.TryParse(it.DueTime, out due))
                                    it.OverdueHours = Math.Max(0, (now - due).TotalHours);
                            }
                            list.Add(it);
                        }
                }
            }
            // 逾期最久的排最前，其次按借出时间
            list.Sort((a, b) =>
            {
                if (a.State != b.State) return b.State - a.State;
                return b.BorrowedHours.CompareTo(a.BorrowedHours);
            });
            return list;
        }

        /// <summary>
        /// 记下「这台设备已经发过催还邮件」的时间。
        /// 借出去的设备在归还前只会在间隔到了之后才再发一次，不会每天骚扰同一个人。
        /// </summary>
        public static void MarkMailNotified(string epc, string time)
        {
            if (!SchemaReady || string.IsNullOrEmpty(epc)) return;
            try
            {
                using (var cn = new SQLiteConnection(ConnStr))
                {
                    cn.Open();
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE devices SET mail_notified_at=@t WHERE epc=@e";
                        cmd.Parameters.AddWithValue("@t", time ?? "");
                        cmd.Parameters.AddWithValue("@e", epc);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        // -------- Records --------
        public static void AddRecord(BorrowRecord rec)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO records(epc,device_name,borrower,direction,event_time,duration_min,notes,due_time,user_card,overdue)
                                        VALUES(@e,@d,@b,@di,@et,@du,@n,@dt,@uc,@ov)";
                    cmd.Parameters.AddWithValue("@e", rec.Epc);
                    cmd.Parameters.AddWithValue("@d", rec.DeviceName ?? "");
                    cmd.Parameters.AddWithValue("@b", rec.Borrower ?? "");
                    cmd.Parameters.AddWithValue("@di", rec.Direction);
                    cmd.Parameters.AddWithValue("@et", rec.EventTime);
                    cmd.Parameters.AddWithValue("@du", rec.DurationMin);
                    cmd.Parameters.AddWithValue("@n", rec.Notes ?? "");
                    cmd.Parameters.AddWithValue("@dt", rec.DueTime ?? "");
                    cmd.Parameters.AddWithValue("@uc", rec.UserCard ?? "");
                    cmd.Parameters.AddWithValue("@ov", rec.Overdue ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static List<BorrowRecord> ListRecords(DateTime? from = null, DateTime? to = null, int limit = 500)
        {
            var list = new List<BorrowRecord>();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    // returned：该借出记录之后是否存在同一 EPC 的归还记录（未归还 = false）
                    var sql = @"SELECT r.*, EXISTS(SELECT 1 FROM records x
                                WHERE x.epc = r.epc AND x.direction = 'return' AND x.id > r.id) AS returned
                                FROM records r";
                    var wh = new List<string>();
                    if (from.HasValue) { wh.Add("r.event_time >= @f"); cmd.Parameters.AddWithValue("@f", from.Value.ToString("yyyy-MM-dd 00:00:00")); }
                    if (to.HasValue)   { wh.Add("r.event_time <= @t"); cmd.Parameters.AddWithValue("@t", to.Value.ToString("yyyy-MM-dd 23:59:59")); }
                    if (wh.Count > 0) sql += " WHERE " + string.Join(" AND ", wh);
                    sql += " ORDER BY r.id DESC LIMIT " + limit;
                    cmd.CommandText = sql;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new BorrowRecord
                            {
                                Id = Convert.ToInt32(r["id"]),
                                Epc = r["epc"].ToString(),
                                DeviceName = r["device_name"].ToString(),
                                Borrower = r["borrower"].ToString(),
                                Direction = r["direction"].ToString(),
                                EventTime = r["event_time"].ToString(),
                                DurationMin = Convert.ToDouble(r["duration_min"]),
                                Notes = r["notes"].ToString(),
                                Returned = Convert.ToInt32(r["returned"]) == 1,
                                DueTime = Col(r, "due_time"),
                                UserCard = Col(r, "user_card"),
                                Overdue = Col(r, "overdue") == "1",
                            });
                }
            }
            return list;
        }

        // -------- 删除借还记录 --------
        // 按 id 批量删除借还流水（不影响设备表当前状态）
        public static int DeleteRecords(IEnumerable<int> ids)
        {
            var idList = (ids ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (idList.Count == 0) return 0;
            EnsureSchema();
            int deleted = 0;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var tx = cn.BeginTransaction())
                {
                    try
                    {
                        using (var cmd = cn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM records WHERE id=@id";
                            var p = cmd.CreateParameter();
                            p.ParameterName = "@id";
                            cmd.Parameters.Add(p);
                            foreach (int id in idList)
                            {
                                p.Value = id;
                                deleted += cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
            return deleted;
        }

        // 历史借用人（用于借出对话框自动补全）
        public static List<string> ListBorrowers()
        {
            var list = new List<string>();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT DISTINCT borrower FROM records WHERE borrower<>'' "
                                    + @"UNION SELECT DISTINCT current_borrower FROM devices WHERE current_borrower<>''"
                                    + (SchemaReady ? @" UNION SELECT DISTINCT name FROM users WHERE name<>''" : "");
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) list.Add(r[0].ToString());
                }
            }
            return list;
        }

        // =================================================================
        //  users  借用人（学生卡）
        // =================================================================
        public static List<User> ListUsers(string keyword = null)
        {
            var list = new List<User>();
            if (!SchemaReady) return list;   // 旧结构/只读模式：users 表可能不存在
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    var sql = "SELECT * FROM users";
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        sql += " WHERE (name LIKE @k OR user_no LIKE @k OR card_epc LIKE @k OR class_name LIKE @k)";
                        cmd.Parameters.AddWithValue("@k", "%" + keyword + "%");
                    }
                    sql += " ORDER BY updated_at DESC, id DESC";
                    cmd.CommandText = sql;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) list.Add(ReadUser(r));
                }
            }
            return list;
        }

        public static User FindUser(int id)
        {
            if (!SchemaReady) return null;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM users WHERE id=@i";
                    cmd.Parameters.AddWithValue("@i", id);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) return ReadUser(r);
                }
            }
            return null;
        }

        /// <summary>按借用人卡 EPC 找借用人（双卡模式的核心查询），找不到返回 null</summary>
        public static User FindUserByCard(string cardEpc)
        {
            if (string.IsNullOrWhiteSpace(cardEpc) || !SchemaReady) return null;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM users WHERE card_epc IS NOT NULL AND card_epc<>'' AND upper(card_epc)=upper(@e)";
                    cmd.Parameters.AddWithValue("@e", cardEpc.Trim());
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) return ReadUser(r);
                }
            }
            return null;
        }

        private static User ReadUser(IDataReader r)
        {
            return new User
            {
                Id = Convert.ToInt32(r["id"]),
                CardEpc = Col(r, "card_epc"),
                Name = r["name"].ToString(),
                UserNo = Col(r, "user_no"),
                ClassName = Col(r, "class_name"),
                Role = Col(r, "role"),
                Phone = Col(r, "phone"),
                Email = Col(r, "email"),
                Notes = Col(r, "notes"),
                CreatedAt = Col(r, "created_at"),
                UpdatedAt = Col(r, "updated_at"),
            };
        }

        public static void InsertUser(User u)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO users(card_epc,name,user_no,class_name,phone,email,role,notes,created_at,updated_at)
                                        VALUES(@c,@n,@no,@cl,@ph,@em,@ro,@nt,@ca,@ua)";
                    AddUserParams(cmd, u);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void UpdateUser(User u)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE users SET card_epc=@c, name=@n, user_no=@no, class_name=@cl,
                                        phone=@ph, email=@em, role=@ro, notes=@nt, updated_at=@ua WHERE id=@id";
                    AddUserParams(cmd, u);
                    cmd.Parameters.AddWithValue("@id", u.Id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void AddUserParams(SQLiteCommand cmd, User u)
        {
            cmd.Parameters.AddWithValue("@c", string.IsNullOrWhiteSpace(u.CardEpc) ? (object)DBNull.Value : u.CardEpc.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("@n", u.Name ?? "");
            cmd.Parameters.AddWithValue("@no", u.UserNo ?? "");
            cmd.Parameters.AddWithValue("@cl", u.ClassName ?? "");
            cmd.Parameters.AddWithValue("@ph", u.Phone ?? "");
            cmd.Parameters.AddWithValue("@em", u.Email ?? "");
            cmd.Parameters.AddWithValue("@ro", string.IsNullOrEmpty(u.Role) ? "student" : u.Role);
            cmd.Parameters.AddWithValue("@nt", u.Notes ?? "");
            cmd.Parameters.AddWithValue("@ca", u.CreatedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@ua", u.UpdatedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        public static int DeleteUser(int id)
        {
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM users WHERE id=@i";
                    cmd.Parameters.AddWithValue("@i", id);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>卡号是否已被别的借用人占用（录入时的重复校验）</summary>
        public static bool CardEpcTaken(string cardEpc, int exceptId)
        {
            if (string.IsNullOrWhiteSpace(cardEpc) || !SchemaReady) return false;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM users WHERE card_epc IS NOT NULL AND upper(card_epc)=upper(@e) AND id<>@i";
                    cmd.Parameters.AddWithValue("@e", cardEpc.Trim());
                    cmd.Parameters.AddWithValue("@i", exceptId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        // =================================================================
        //  audit_log  刷卡审计（被拦截的刷卡留痕，作为防恶意刷数据的证据）
        // =================================================================
        public class AuditEntry
        {
            public int Id;
            public string EventTime, Epc, Kind, Reason, Detail;
            public string KindLabel
            {
                get
                {
                    switch (Kind)
                    {
                        case "interval": return "间隔拦截";
                        case "abuse": return "防刷锁定";
                        case "unknown": return "未登记标签";
                        case "status": return "状态拦截";
                        case "distance": return "距离未确认";
                        default: return Kind;
                    }
                }
            }
        }

        public static void AddAudit(string epc, string kind, string reason, string detail)
        {
            try
            {
                using (var cn = new SQLiteConnection(ConnStr))
                {
                    cn.Open();
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = @"INSERT INTO audit_log(event_time,epc,kind,reason,detail)
                                            VALUES(@t,@e,@k,@r,@d)";
                        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@e", epc ?? "");
                        cmd.Parameters.AddWithValue("@k", kind ?? "other");
                        cmd.Parameters.AddWithValue("@r", reason ?? "");
                        cmd.Parameters.AddWithValue("@d", detail ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { /* 审计写失败不影响主流程 */ }
        }

        public static List<AuditEntry> ListAudit(int limit = 200)
        {
            var list = new List<AuditEntry>();
            if (!SchemaReady) return list;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM audit_log ORDER BY id DESC LIMIT " + Math.Max(1, limit);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new AuditEntry
                            {
                                Id = Convert.ToInt32(r["id"]),
                                EventTime = r["event_time"].ToString(),
                                Epc = Col(r, "epc"),
                                Kind = Col(r, "kind"),
                                Reason = Col(r, "reason"),
                                Detail = Col(r, "detail"),
                            });
                }
            }
            return list;
        }

        public static int CountAudit()
        {
            if (!SchemaReady) return 0;
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM audit_log";
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public static int ClearAudit()
        {
            if (!SchemaReady) return 0;
            EnsureSchema();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM audit_log";
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        // =================================================================
        //  Stats / 报表
        // =================================================================
        public class TodayStats
        {
            public int Borrows;      // 今日借出次数
            public int Returns;      // 今日归还次数
            public int OutNow;       // 当前外借设备数
            public int TotalDevices; // 设备总数
            public int OverdueNow;   // 当前已逾期未还数
        }

        public static TodayStats GetTodayStats()
        {
            var s = new TodayStats();
            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT direction, COUNT(*) AS cnt FROM records
                                        WHERE event_time LIKE @d || '%' GROUP BY direction";
                    cmd.Parameters.AddWithValue("@d", DateTime.Now.ToString("yyyy-MM-dd"));
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string dir = r["direction"].ToString();
                            int cnt = Convert.ToInt32(r["cnt"]);
                            if (dir == "borrow") s.Borrows = cnt;
                            if (dir == "return") s.Returns = cnt;
                        }
                    }
                }
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT status, COUNT(*) AS cnt FROM devices GROUP BY status";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string st = r["status"].ToString();
                            int cnt = Convert.ToInt32(r["cnt"]);
                            s.TotalDevices += cnt;
                            if (st == "borrowed") s.OutNow = cnt;
                        }
                    }
                }
                // 逾期 = 外借中且应还时间早于当前（应还时间为空视为未设期限）
                // 只读/旧结构下没有 due_time 列，跳过该统计而不是抛异常
                if (SchemaReady)
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT COUNT(*) FROM devices
                                            WHERE status='borrowed' AND due_time IS NOT NULL AND due_time<>''
                                              AND due_time < @now";
                        cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        s.OverdueNow = Convert.ToInt32(cmd.ExecuteScalar());
                    }
            }
            return s;
        }

        public class KV
        {
            public string Label;
            public double Value;
            public KV(string l, double v) { Label = l; Value = v; }
        }

        public class ReportData
        {
            public DateTime From, To;
            public int Borrows, Returns, CurrentOut, TotalDevices, OverdueNow;
            public int OverdueReturns;             // 区间内“归还时已逾期”的次数
            public double AvgDurationMin;          // 区间内已归还记录的平均借用时长
            public double TotalDurationHours;      // 累计借用时长（小时）
            public List<KV> DailyBorrow = new List<KV>();
            public List<KV> DailyReturn = new List<KV>();
            public List<KV> TopDevices = new List<KV>();
            public List<KV> TopBorrowers = new List<KV>();
            public List<KV> ByCategory = new List<KV>();
        }

        public static ReportData GetReport(DateTime from, DateTime to, int warnHours, int topN = 10)
        {
            var rep = new ReportData();
            rep.From = from; rep.To = to;
            string f = from.ToString("yyyy-MM-dd 00:00:00");
            string t = to.ToString("yyyy-MM-dd 23:59:59");
            var now = DateTime.Now;
            int days = Math.Max(1, (int)(to.Date - from.Date).TotalDays + 1);

            using (var cn = new SQLiteConnection(ConnStr))
            {
                cn.Open();

                using (var cmd = cn.CreateCommand())
                {
                    // 只读/旧结构下没有 overdue 列：用常量 0 占位
                    cmd.CommandText = SchemaReady
                        ? @"SELECT direction, COUNT(*) cnt, SUM(duration_min) dur,
                                   SUM(CASE WHEN overdue=1 THEN 1 ELSE 0 END) ovd
                            FROM records WHERE event_time>=@f AND event_time<=@t GROUP BY direction"
                        : @"SELECT direction, COUNT(*) cnt, SUM(duration_min) dur, 0 AS ovd
                            FROM records WHERE event_time>=@f AND event_time<=@t GROUP BY direction";
                    cmd.Parameters.AddWithValue("@f", f);
                    cmd.Parameters.AddWithValue("@t", t);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            string dir = r["direction"].ToString();
                            int cnt = r["cnt"] == DBNull.Value ? 0 : Convert.ToInt32(r["cnt"]);
                            if (dir == "borrow") rep.Borrows = cnt;
                            else if (dir == "return")
                            {
                                rep.Returns = cnt;
                                rep.OverdueReturns = r["ovd"] == DBNull.Value ? 0 : Convert.ToInt32(r["ovd"]);
                                double sum = r["dur"] == DBNull.Value ? 0 : Convert.ToDouble(r["dur"]);
                                rep.TotalDurationHours = sum / 60.0;
                                rep.AvgDurationMin = cnt > 0 ? sum / cnt : 0;
                            }
                        }
                }

                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT status, COUNT(*) cnt FROM devices GROUP BY status";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            int cnt = Convert.ToInt32(r["cnt"]);
                            rep.TotalDevices += cnt;
                            if (r["status"].ToString() == "borrowed") rep.CurrentOut = cnt;
                        }
                }
                if (SchemaReady)
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT COUNT(*) FROM devices WHERE status='borrowed'
                                            AND due_time IS NOT NULL AND due_time<>'' AND due_time < @now";
                        cmd.Parameters.AddWithValue("@now", now.ToString("yyyy-MM-dd HH:mm:ss"));
                        rep.OverdueNow = Convert.ToInt32(cmd.ExecuteScalar());
                    }

                // ---- 按天趋势（补零，保证柱状图 X 轴连续） ----
                var dBorrow = new Dictionary<string, int>();
                var dReturn = new Dictionary<string, int>();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT substr(event_time,1,10) d, direction, COUNT(*) cnt
                                        FROM records WHERE event_time>=@f AND event_time<=@t
                                        GROUP BY d, direction";
                    cmd.Parameters.AddWithValue("@f", f);
                    cmd.Parameters.AddWithValue("@t", t);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            string d = r["d"].ToString();
                            int cnt = Convert.ToInt32(r["cnt"]);
                            if (r["direction"].ToString() == "borrow") dBorrow[d] = cnt;
                            else dReturn[d] = cnt;
                        }
                }
                for (int i = 0; i < days; i++)
                {
                    string d = from.Date.AddDays(i).ToString("yyyy-MM-dd");
                    int b = dBorrow.ContainsKey(d) ? dBorrow[d] : 0;
                    int rr = dReturn.ContainsKey(d) ? dReturn[d] : 0;
                    rep.DailyBorrow.Add(new KV(d.Substring(5), b));
                    rep.DailyReturn.Add(new KV(d.Substring(5), rr));
                }

                rep.TopDevices = TopList(cn, @"SELECT (CASE WHEN device_name IS NULL OR device_name='' THEN epc ELSE device_name END) k,
                                             COUNT(*) c FROM records WHERE direction='borrow' AND event_time>=@f AND event_time<=@t
                                             GROUP BY k ORDER BY c DESC LIMIT " + topN, f, t);
                rep.TopBorrowers = TopList(cn, @"SELECT (CASE WHEN borrower IS NULL OR borrower='' THEN '(未填写)' ELSE borrower END) k,
                                             COUNT(*) c FROM records WHERE direction='borrow' AND event_time>=@f AND event_time<=@t
                                             GROUP BY k ORDER BY c DESC LIMIT " + topN, f, t);
                rep.ByCategory = TopList(cn, @"SELECT (CASE WHEN d.category IS NULL OR d.category='' THEN '(未分类)' ELSE d.category END) k,
                                             COUNT(*) c FROM records r LEFT JOIN devices d ON d.epc=r.epc
                                             WHERE r.direction='borrow' AND r.event_time>=@f AND r.event_time<=@t
                                             GROUP BY k ORDER BY c DESC", f, t);
            }
            return rep;
        }

        private static List<KV> TopList(SQLiteConnection cn, string sql, string f, string t)
        {
            var list = new List<KV>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@f", f);
                cmd.Parameters.AddWithValue("@t", t);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new KV(r["k"].ToString(), Convert.ToDouble(r["c"])));
            }
            return list;
        }

        // =================================================================
        //  备份 / 恢复
        // =================================================================
        /// <summary>立即备份当前数据库，返回备份文件完整路径</summary>
        public static string BackupNow(string tag)
        {
            EnsureSchema();
            Directory.CreateDirectory(BackupDir);
            string name = "labborrow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                          + (string.IsNullOrEmpty(tag) ? "" : "_" + tag) + ".db";
            string dst = Path.Combine(BackupDir, name);
            int n = 1;
            while (File.Exists(dst))   // 同一秒内多次备份也不覆盖
                dst = Path.Combine(BackupDir, Path.GetFileNameWithoutExtension(name) + "_" + (n++) + ".db");

            SQLiteConnection.ClearAllPools();     // 关闭连接池句柄，保证文件可复制
            if (File.Exists(DbPath)) File.Copy(DbPath, dst, true);
            else Touch();
            return dst;
        }

        private static void Touch()
        {
            using (var c = new SQLiteConnection(ConnStr)) { c.Open(); }
        }

        public class BackupFile
        {
            public string Path;
            public string Name;
            public DateTime Time;
            public long Size;
            public override string ToString()
            {
                return string.Format("{0}   ({1:0.0} KB)", Time.ToString("yyyy-MM-dd HH:mm:ss"), Size / 1024.0);
            }
        }

        public static List<BackupFile> ListBackups()
        {
            var list = new List<BackupFile>();
            try
            {
                if (!Directory.Exists(BackupDir)) return list;
                foreach (var f in Directory.GetFiles(BackupDir, "*.db"))
                {
                    var fi = new FileInfo(f);
                    list.Add(new BackupFile
                    {
                        Path = fi.FullName,
                        Name = fi.Name,
                        Time = fi.LastWriteTime,
                        Size = fi.Length
                    });
                }
                list.Sort((a, b) => b.Time.CompareTo(a.Time));
            }
            catch { }
            return list;
        }

        /// <summary>只保留最近 keep 份“自动备份”（文件名含 _auto），手动/升级前/恢复前备份永不自动删除</summary>
        public static int PruneAutoBackups(int keep)
        {
            int removed = 0;
            try
            {
                var autos = ListBackups().FindAll(delegate (BackupFile b) { return b.Name.IndexOf("_auto", StringComparison.OrdinalIgnoreCase) >= 0; });
                for (int i = keep; i < autos.Count; i++)
                {
                    try { File.Delete(autos[i].Path); removed++; }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>从备份文件恢复；恢复前会自动再备份当前库，避免误操作丢数据</summary>
        public static string RestoreFrom(string backupFile)
        {
            if (!File.Exists(backupFile)) throw new FileNotFoundException("备份文件不存在：" + backupFile);
            string safety = null;
            SQLiteConnection.ClearAllPools();
            if (File.Exists(DbPath)) safety = BackupNow("before_restore");
            SQLiteConnection.ClearAllPools();
            File.Copy(backupFile, DbPath, true);
            SQLiteConnection.ClearAllPools();
            return safety;
        }
    }

    public class Device
    {
        public string Epc, Name, Model, Category, Location, Status, CurrentBorrower, BorrowTime, DueTime, Notes, CreatedAt, UpdatedAt;
    }

    public class BorrowRecord
    {
        public int Id;
        public string Epc, DeviceName, Borrower, Direction, EventTime, Notes;
        public string DueTime, UserCard;
        public double DurationMin;
        public bool Returned;   // 借出记录是否已有对应归还（仅 borrow 记录有意义）
        public bool Overdue;    // 归还时是否已逾期
    }

    public class User
    {
        public int Id;
        public string CardEpc, Name, UserNo, ClassName, Role, Notes, CreatedAt, UpdatedAt;
        public string Phone;   // 手机号（可空）：到期催还时用来联系本人
        public string Email;   // 邮箱（可空）：自动邮件提醒用

        public string RoleLabel
        {
            get
            {
                if (Role == "teacher") return "教师";
                if (Role == "admin") return "管理员";
                return "学生";
            }
        }

        public override string ToString()
        {
            string s = Name;
            if (!string.IsNullOrEmpty(UserNo)) s += "（" + UserNo + "）";
            return s;
        }
    }
}
