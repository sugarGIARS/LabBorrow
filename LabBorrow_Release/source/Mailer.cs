using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace LabBorrow
{
    /// <summary>
    /// 自动发邮件：把「该还了」这件事直接送到借用同学的邮箱里。
    ///
    /// 为什么需要它：语音和屏幕大字只能提醒站在机器前的人，
    /// 借走设备的同学回到宿舍就再也看不到归还日期了；
    /// 邮件会自动躺在他天天打开的邮箱里，是最可靠的"到人"提醒。
    ///
    /// 用法：
    ///   1) 系统设置 → ⑫ 邮件提醒，填一次 SMTP（QQ/163/学校邮箱都行）；
    ///   2) 用户管理里给同学填邮箱；
    ///   3) 借出时自动发「借用确认信」（写着归还日期），到期/逾期自动发「催还信」。
    /// 没填邮箱的人会自动跳过，不影响其它功能；没网也只是发不出去，程序照常运行。
    /// </summary>
    internal static class Mailer
    {
        // ---------------------------------------------------------------
        //  密码保护
        //  邮箱密码（其实是 SMTP 授权码）不能明文存在数据库里，
        //  用 Windows 自带的 DPAPI 加密：只有当前这台电脑的当前用户能解开，
        //  换台电脑/换个账户就解不开，数据库文件拷走也拿不到密码。
        // ---------------------------------------------------------------
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var data = Encoding.UTF8.GetBytes(plain);
                var enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return plain; }     // 加密不可用时退化为原样保存（至少功能不坏）
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                var data = Convert.FromBase64String(stored);
                var dec = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch
            {
                // 解不开：可能是老版本存的明文，那就当明文用
                return stored;
            }
        }

        /// <summary>邮箱地址看起来合法吗（只做最基本的检查，避免把整段文字当地址）</summary>
        public static bool IsValidEmail(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            int at = s.IndexOf('@');
            if (at <= 0 || at != s.LastIndexOf('@')) return false;
            if (s.IndexOf('.', at + 1) <= at + 1) return false;
            return s.Length > 5 && !s.Contains(" ") && !s.Contains("，") && !s.Contains(",");
        }

        /// <summary>配置是否填完整（少一样都发不出去，这里先判断，免得每次都报错弹窗）</summary>
        public static bool Configured(SwipeRules r)
        {
            return r != null && r.MailEnabled
                && !string.IsNullOrEmpty(r.MailHost)
                && r.MailPort > 0
                && IsValidEmail(r.MailFrom)
                && !string.IsNullOrEmpty(Mailer.Unprotect(r.MailPass));
        }

        /// <summary>
        /// 发一封邮件。成功返回 null，失败返回错误说明（不抛异常，方便调用处直接提示）。
        /// </summary>
        public static string Send(SwipeRules r, string to, string subject, string body)
        {
            if (r == null) return "未加载设置";
            if (!IsValidEmail(to)) return "收件人邮箱为空或不合法：" + to;
            if (!Configured(r)) return "邮件还没配置好（发件箱/密码/SMTP 服务器）";

            try
            {
                using (var msg = new MailMessage())
                {
                    msg.From = new MailAddress(r.MailFrom,
                        string.IsNullOrEmpty(r.MailFromName) ? r.MailFrom : r.MailFromName, Encoding.UTF8);
                    msg.To.Add(new MailAddress(to));
                    msg.Subject = subject;
                    msg.SubjectEncoding = Encoding.UTF8;
                    msg.Body = body;
                    msg.BodyEncoding = Encoding.UTF8;
                    msg.IsBodyHtml = false;

                    var client = new SmtpClient
                    {
                        Host = r.MailHost,
                        Port = r.MailPort,
                        EnableSsl = r.MailSsl,
                        DeliveryMethod = SmtpDeliveryMethod.Network,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(r.MailUser, Unprotect(r.MailPass)),
                        Timeout = 20000
                    };
                    client.Send(msg);
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ---------------------------------------------------------------
        //  邮件内容
        // ---------------------------------------------------------------

        /// <summary>借出确认信：借用完成就发，写清楚归还日期（给他留个凭据）</summary>
        public static string BorrowMailBody(SwipeRules r, string borrower, string device,
                                            string model, string due, string labName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(borrower + "同学，你好：");
            sb.AppendLine();
            sb.AppendLine("你已成功借用以下设备，请按时归还。");
            sb.AppendLine();
            sb.AppendLine("　设备：" + device + (string.IsNullOrEmpty(model) ? "" : "（" + model + "）"));
            sb.AppendLine("　借出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("　应归还时间：" + due);
            sb.AppendLine();
            sb.AppendLine("请在应归还时间之前归还；逾期会影响后续借用。");
            sb.AppendLine();
            sb.AppendLine("—— " + (string.IsNullOrEmpty(labName) ? "实验室" : labName) + "设备管理系统（自动发送，请勿直接回复）");
            return sb.ToString();
        }

        /// <summary>催还信里的一件设备（自己定义，免得邮件模块依赖提醒模块）</summary>
        public class MailItem
        {
            public string DeviceName = "";
            public string DueTime = "";
            public bool Overdue;
        }

        /// <summary>催还信：把这位同学名下的到期/逾期设备一并列出（一人一封，不刷屏）</summary>
        public static string UrgeMailBody(string borrower, List<MailItem> mine, string labName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(borrower + "同学，你好：");
            sb.AppendLine();
            sb.AppendLine("你在" + (string.IsNullOrEmpty(labName) ? "实验室" : labName) + "借用的设备需要归还了：");
            sb.AppendLine();
            foreach (var it in mine)
            {
                if (it.Overdue)
                    sb.AppendLine("　· " + it.DeviceName + "　已逾期（应还时间 " + ShortDue(it.DueTime) + "）");
                else
                    sb.AppendLine("　· " + it.DeviceName + "　应还时间 " + ShortDue(it.DueTime) + "（即将到期）");
            }
            sb.AppendLine();
            sb.AppendLine("请尽快到实验室办理归还。逾期将影响后续借用。");
            sb.AppendLine();
            sb.AppendLine("如果已经归还，请忽略本邮件。");
            sb.AppendLine();
            sb.AppendLine("—— " + (string.IsNullOrEmpty(labName) ? "实验室" : labName) + "设备管理系统（自动发送，请勿直接回复）");
            return sb.ToString();
        }

        private static string ShortDue(string due)
        {
            if (string.IsNullOrEmpty(due)) return "（未设置）";
            DateTime t;
            if (!DateTime.TryParse(due, out t)) return due;
            return t.ToString("MM-dd HH:mm");
        }
    }
}
