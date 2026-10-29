// =====================================================================
//  Sound.cs  --  刷卡提示音效
//  ---------------------------------------------------------------------
//  设计要点（现场演示要稳，所以刻意不依赖任何第三方运行库）：
//   1. 借出 / 归还 / 拦截 / 异常锁定 四类事件各自独立可配；
//   2. 每一类都可以指定自己的音频文件（mp3 / wav），文件不存在或播放失败时
//      自动回退到程序内置合成音（正弦波提示音），保证任何机器上都"有声音"；
//   3. 文件播放走 winmm.dll 的 MCI 接口（Windows 全版本自带，能放 mp3），
//      MCI 不可用时再回退到 Windows Media Player 的 COM 接口；
//   4. 全部在后台线程播放，绝不会卡住刷卡主线程；任何异常都被吞掉，
//      声音放不出来也不能影响借还登记。
//
//  为什么不用 SoundPlayer 放 mp3：System.Media.SoundPlayer 只支持 wav，
//  放不了 mp3；而 mciSendString 是 Windows 自带的，不需要打包额外 dll。
// =====================================================================
using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LabBorrow
{
    public static class Sfx
    {
        /// <summary>音效类别</summary>
        public enum Kind
        {
            Borrow = 0,     // 借出成功
            Return = 1,     // 归还成功
            Blocked = 2,    // 被规则拦截（间隔不足 / 设备故障）
            Abuse = 3       // 判定为恶意刷数据并锁定
        }

        // ---------------- winmm.dll (MCI) ----------------
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr cb);

        private static int seq;
        private static readonly object seqLock = new object();
        // 持有 WMP COM 实例引用，防止被 GC 回收导致播放中断
        private static object lastWmp;

        /// <summary>
        /// 可选的诊断回调：MainForm 会接到主控台「实时事件」列表里，
        /// 这样现场能直接看到这次音效是用 MCI、WMP 还是内置合成音播的，便于排查"没声音"。
        /// </summary>
        public static Action<string> Diagnostics;

        /// <summary>
        /// 是否把「播放成功」也写进诊断日志。默认只记录异常（找不到文件/播放方式回退），
        /// 否则每次刷卡都往主控台事件列表塞一行，列表会被这些噪音刷满。
        /// 「试听」按钮会临时打开它。
        /// </summary>
        public static bool VerboseLog;

        private static void Note(string msg, bool important)
        {
            if (!important && !VerboseLog) return;
            try { if (Diagnostics != null) Diagnostics(msg); } catch { }
        }

        // ================================================================
        //  对外唯一入口
        // ================================================================
        /// <summary>
        /// 播放指定类别音效。customFile 为空或文件不存在时播放内置合成音。
        /// 立即返回，不阻塞调用方（刷卡主线程）。
        /// </summary>
        public static void Play(Kind kind, string customFile, int volumePct, int maxSec)
        {
            var th = new Thread(() =>
            {
                try
                {
                    // 没配文件就在程序目录里找一份（见 FindLocalSound），
                    // 这样"把 mp3 丢到软件根目录"就能直接用，不必改数据库
                    string file = ResolveFile(kind, customFile);

                    if (!string.IsNullOrEmpty(file) && File.Exists(file))
                    {
                        // ① Windows Media Player 6.4（msdxm.ocx）——进程内 COM。
                        //    实测：程序被系统降级（低完整性）运行时，MCI 直接失败、
                        //    WMP12(WMPlayer.OCX) 会永远停在过渡状态不出声，
                        //    只有这个进程内的 6.4 组件能真正播起来，故放在第一位。
                        if (PlayWmp64(file, volumePct, maxSec))
                        {
                            Note("音效[" + kind + "] 播放：" + Path.GetFileName(file), false);
                            return;
                        }
                        // ② MCI（winmm.dll）：速度快，但不少机器打不开 mp3
                        string alias;
                        int waitMs, mciErr;
                        if (StartFile(file, volumePct, maxSec, out alias, out waitMs, out mciErr))
                        {
                            Note("音效[" + kind + "] 播放：" + Path.GetFileName(file), false);
                            Thread.Sleep(Math.Max(200, waitMs) + 400);
                            CloseAlias(alias);
                            return;
                        }
                        // ③ WMP12 COM：原生支持 Unicode 路径，但部分机器会卡在过渡状态
                        if (PlayWmp(file, volumePct, maxSec))
                        {
                            Note("音效[" + kind + "] 播放：" + Path.GetFileName(file), false);
                            return;
                        }
                        // 三种都"没真正出声"才回退合成音，并把原因写清楚
                        Note("音效[" + kind + "] 音乐文件放不出来（WMP6.4/MCI 0x" + mciErr.ToString("X")
                             + "/WMP12 都失败），本次先用内置合成音。常见原因是程序被 Windows 降级运行"
                             + "（解压在桌面）：运行程序目录里的 fix_integrity.bat 后重启即可。", true);
                    }
                    else
                    {
                        Note("音效[" + kind + "] 内置合成音", false);
                    }
                    PlayTone(kind, volumePct);          // ④ 回退：内置合成音
                }
                catch { /* 声音是锦上添花，绝不能影响业务 */ }
            });
            th.IsBackground = true;
            th.Start();
        }

        // ================================================================
        //  内置合成音（正弦波，无需任何外部文件）
        // ================================================================
        private static void PlayTone(Kind kind, int volumePct)
        {
            int[] freqs;
            switch (kind)
            {
                case Kind.Return: freqs = new[] { 1318, 988 }; break;            // 下行：归还
                case Kind.Blocked: freqs = new[] { 330, 262 }; break;            // 低沉双音：拦截
                case Kind.Abuse: freqs = new[] { 466, 349, 466, 349 }; break;    // 急促警报：恶意刷数据
                default: freqs = new[] { 988, 1318 }; break;                     // 上行：借出
            }
            using (var ms = BuildWav(freqs, 150, 45, volumePct))
            using (var player = new SoundPlayer(ms))
            {
                player.Load();
                player.PlaySync();
            }
        }

        /// <summary>合成 PCM wav（22050Hz / 16bit / 单声道），带淡入淡出避免"啪"声</summary>
        private static MemoryStream BuildWav(int[] freqs, int noteMs, int gapMs, int volumePct)
        {
            const int rate = 22050;
            int noteSamples = rate * noteMs / 1000;
            int gapSamples = rate * gapMs / 1000;
            int total = (noteSamples + gapSamples) * freqs.Length;
            int dataBytes = total * 2;

            var ms = new MemoryStream(44 + dataBytes);
            var bw = new BinaryWriter(ms);
            bw.Write(0x46464952);                   // "RIFF"
            bw.Write(36 + dataBytes);
            bw.Write(0x45564157);                   // "WAVE"
            bw.Write(0x20746d66);                   // "fmt "
            bw.Write(16);                           // fmt chunk size
            bw.Write((short)1);                     // PCM
            bw.Write((short)1);                     // 单声道
            bw.Write(rate);
            bw.Write(rate * 2);                     // byte rate
            bw.Write((short)2);                     // block align
            bw.Write((short)16);                    // bits per sample
            bw.Write(0x61746164);                   // "data"
            bw.Write(dataBytes);

            double amp = 0.28 * 32767.0 * Math.Max(0, Math.Min(100, volumePct)) / 100.0;
            for (int k = 0; k < freqs.Length; k++)
            {
                WriteNote(bw, freqs[k], noteSamples, rate, amp);
                for (int i = 0; i < gapSamples; i++) bw.Write((short)0);
            }
            bw.Flush();
            ms.Position = 0;
            return ms;
        }

        private static void WriteNote(BinaryWriter bw, int freq, int n, int rate, double amp)
        {
            int fade = Math.Min(n / 6, rate * 6 / 1000);
            for (int i = 0; i < n; i++)
            {
                double env = 1.0;
                if (i < fade) env = i / (double)fade;
                else if (i > n - fade) env = (n - i) / (double)fade;
                double v = Math.Sin(2 * Math.PI * freq * i / rate) * amp * env;
                bw.Write((short)Math.Max(-32767.0, Math.Min(32767.0, v)));
            }
        }

        // ================================================================
        //  外部音频文件（mp3 / wav）
        // ================================================================
        /// <summary>
        /// 开始播放文件。成功返回 true，并通过 waitMs 告诉调用方大约要播多久
        /// （用于播完以后关闭 MCI 设备句柄）。
        /// </summary>
        private static bool StartFile(string path, int volumePct, int maxSec, out string alias, out int waitMs, out int errCode)
        {
            alias = "";
            waitMs = 0;
            errCode = 0;
            lock (seqLock) { alias = "lb_sfx_" + (++seq); }

            // MCI 需要知道媒体类型；扩展名判断失败也有第二种不带 type 的写法兜底
            string type = GuessMciType(path);
            int r = mciSendString("open \"" + path + "\" type " + type + " alias " + alias, null, 0, IntPtr.Zero);
            if (r != 0)
                r = mciSendString("open \"" + path + "\" alias " + alias, null, 0, IntPtr.Zero);
            if (r != 0) { errCode = r; return false; }

            int vol = Math.Max(0, Math.Min(100, volumePct)) * 10;      // MCI 音量是 0~1000
            mciSendString("setaudio " + alias + " volume to " + vol, null, 0, IntPtr.Zero);

            if (maxSec > 0)
                r = mciSendString("play " + alias + " from 0 to " + (maxSec * 1000), null, 0, IntPtr.Zero);
            else
                r = mciSendString("play " + alias, null, 0, IntPtr.Zero);
            if (r != 0)
            {
                CloseAlias(alias);
                return false;
            }

            int lenMs = QueryLength(alias);
            waitMs = maxSec > 0 ? Math.Min(lenMs, maxSec * 1000) : lenMs;
            if (waitMs <= 0) waitMs = maxSec > 0 ? maxSec * 1000 : 5000;
            return true;
        }

        private static string GuessMciType(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return "mpegvideo";
            switch (ext.ToLowerInvariant())
            {
                case ".mp3": return "mpegvideo";
                case ".wav": return "waveaudio";
                case ".wma": return "mpegvideo";
                case ".mid":
                case ".midi": return "sequencer";
                case ".avi":
                case ".mp4": return "mpegvideo";
                default: return "mpegvideo";
            }
        }

        private static int QueryLength(string alias)
        {
            try
            {
                var sb = new StringBuilder(64);
                if (mciSendString("status " + alias + " length", sb, sb.Capacity, IntPtr.Zero) == 0)
                {
                    int ms;
                    if (int.TryParse(sb.ToString().Trim(), out ms)) return ms;
                }
            }
            catch { }
            return 0;
        }

        private static void CloseAlias(string alias)
        {
            try
            {
                mciSendString("stop " + alias, null, 0, IntPtr.Zero);
                mciSendString("close " + alias, null, 0, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// 兜底：Windows Media Player COM（后期绑定，不需要引用任何 dll）。
        /// COM 对象要求在 STA 线程里创建，所以这里单独起一个 STA 线程执行。
        /// 实测它就是能播中文路径 mp3 的那条路。
        /// </summary>
        // ================================================================
        //  程序目录自动识别音效文件
        //  用法：把 mp3/wav 直接丢到软件根目录（或里面的 sounds 文件夹）即可，
        //  文件名里带关键字就会自动对上号，不需要进设置页配置、也不依赖数据库可写。
        // ================================================================
        private static readonly string[] AudioExts = { ".mp3", ".wav", ".wma", ".mid", ".midi", ".m4a", ".aac", ".flac", ".ogg" };

        /// <summary>确定本次要播放的文件：优先用配置值，没配/文件没了就去程序目录找</summary>
        private static string ResolveFile(Kind kind, string customFile)
        {
            if (!string.IsNullOrEmpty(customFile) && File.Exists(customFile)) return customFile;
            string found = FindLocalSound(kind);
            if (!string.IsNullOrEmpty(found))
                Note("音效[" + kind + "] 未配置文件，自动使用程序目录里的 " + Path.GetFileName(found), false);
            return found;
        }

        /// <summary>
        /// 在「程序根目录」和「根目录\sounds」里按关键字找音效文件。
        /// 关键字：异常/锁定/man/abuse → 异常；借出/borrow → 借出；归还/还/return → 归还；拦截/阻止/blocked → 拦截。
        /// 找不到时，若目录里只有一个音频文件，就当作这一类用（最常见的用法）。
        /// </summary>
        public static string FindLocalSound(Kind kind)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var dirs = new[] { baseDir, Path.Combine(baseDir, "sounds") };
                var hits = new System.Collections.Generic.List<string>();

                foreach (var d in dirs)
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d))
                    {
                        string ext = Path.GetExtension(f);
                        if (string.IsNullOrEmpty(ext)) continue;
                        if (Array.IndexOf(AudioExts, ext.ToLowerInvariant()) < 0) continue;
                        hits.Add(f);
                    }
                }
                if (hits.Count == 0) return "";

                string[] keys;
                switch (kind)
                {
                    case Kind.Abuse: keys = new[] { "abuse", "异常", "锁定", "man", "曼巴", "警报", "alarm" }; break;
                    case Kind.Borrow: keys = new[] { "borrow", "借出", "借", "lend", "out" }; break;
                    case Kind.Return: keys = new[] { "return", "归还", "还", "back", "in" }; break;
                    default: keys = new[] { "blocked", "block", "拦截", "阻止", "禁止" }; break;
                }

                foreach (var f in hits)
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    foreach (var k in keys)
                        if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return f;
                }
                // 目录里只有一个音频文件：不管叫什么名字，就它了
                if (hits.Count == 1) return hits[0];
                return "";
            }
            catch { return ""; }
        }

        // ================================================================
        //  WMP 6.4（msdxm.ocx）：进程内 COM，实测在受限环境下也能真正出声
        // ================================================================
        private static object lastWmp64;

        private static bool PlayWmp64(string path, int volumePct, int maxSec)
        {
            bool started = false;
            int lastState = -1;
            Exception failure = null;
            int waitMs = maxSec > 0 ? maxSec * 1000 : 0;
            try
            {
                var th = new Thread(() =>
                {
                    try
                    {
                        const BindingFlags BFGet = BindingFlags.GetProperty | BindingFlags.InvokeMethod;
                        const BindingFlags BFSet = BindingFlags.SetProperty;
                        Type t = Type.GetTypeFromProgID("MediaPlayer.MediaPlayer.1");
                        if (t == null) return;                       // 组件不存在，交给后面的方案
                        object player = Activator.CreateInstance(t);

                        // 音量：WMP6.4 用的是 dB 值（-9640 ~ 0），不是 0-100
                        try
                        {
                            int vol = (int)(-10000 * (1 - Math.Max(0, Math.Min(100, volumePct)) / 100.0));
                            if (vol < -9640) vol = -9640;
                            t.InvokeMember("Volume", BFSet, null, player, new object[] { vol });
                        }
                        catch { }

                        t.InvokeMember("FileName", BFSet, null, player, new object[] { path });
                        t.InvokeMember("Play", BFGet, null, player, null);
                        lastWmp64 = player;      // 保持引用，防止 GC 中断播放

                        // 同样只认真正进入播放（PlayState=3）
                        for (int i = 0; i < 8; i++)
                        {
                            Thread.Sleep(300);
                            try
                            {
                                lastState = Convert.ToInt32(t.InvokeMember("PlayState", BFGet, null, player, null));
                                if (lastState == 3) { started = true; break; }
                                if (lastState == 1 || lastState == 0) break;
                            }
                            catch { }
                        }

                        if (started && waitMs > 0)
                        {
                            Thread.Sleep(waitMs);
                            try { t.InvokeMember("Stop", BFGet, null, player, null); } catch { }
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                th.Start();
                th.Join(4000);
                if (started) return true;
                if (failure != null) Note("WMP6.4 播放失败：" + failure.Message, false);
                else Note("WMP6.4 未真正开始播放（PlayState=" + lastState + "）", false);
                return false;
            }
            catch { return false; }
        }

        private static bool PlayWmp(string path, int volumePct, int maxSec)
        {
            Exception failure = null;
            bool started = false;      // 是否「真的」开始出声
            int lastState = -1;
            int waitMs = maxSec > 0 ? maxSec * 1000 : 0;
            try
            {
                var th = new Thread(() =>
                {
                    try
                    {
                        // 注意：InvokeMember 不允许同时指定 Get 和 Set，必须分开两套 flags
                        const BindingFlags BFGet = BindingFlags.GetProperty | BindingFlags.InvokeMethod;
                        const BindingFlags BFSet = BindingFlags.SetProperty;
                        Type t = Type.GetTypeFromProgID("WMPlayer.OCX", true);
                        object player = Activator.CreateInstance(t);

                        // 音量
                        object st = t.InvokeMember("settings", BFGet, null, player, null);
                        st.GetType().InvokeMember("volume", BFSet, null, st,
                            new object[] { Math.Max(0, Math.Min(100, volumePct)) });
                        // invisible：确保后台播放，不会弹出播放器窗口
                        try { t.InvokeMember("uiMode", BFSet, null, player, new object[] { "invisible" }); }
                        catch { /* 个别版本不支持该属性，忽略即可 */ }

                        t.InvokeMember("URL", BFSet, null, player, new object[] { path });
                        object ctl = t.InvokeMember("controls", BFGet, null, player, null);
                        ctl.GetType().InvokeMember("play", BFGet, null, ctl, null);

                        lastWmp = player;      // 保持引用，防止 GC 中断播放

                        // 关键：以前只看"线程还活着"就把 9(过渡中) 也算成功，
                        // 结果程序被系统降级（低完整性）调不动播放器时，
                        // 界面显示"已播放"、实际一点声音都没有。
                        // 实测这种状态下 WMP 会一直停在 playState=9 不再前进，
                        // 所以只有真正变成 3(playing) 才算成功。
                        for (int i = 0; i < 10; i++)
                        {
                            Thread.Sleep(400);
                            try
                            {
                                object ps = t.InvokeMember("playState", BFGet, null, player, null);
                                lastState = Convert.ToInt32(ps);
                                if (lastState == 3) { started = true; break; }     // 只有真正在放才算数
                                if (lastState == 1 || lastState == 8) break;        // 已停止 / 播完
                            }
                            catch { }
                        }

                        if (started && waitMs > 0)
                        {
                            Thread.Sleep(waitMs);
                            try { ctl.GetType().InvokeMember("stop", BFGet, null, ctl, null); } catch { }
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                th.Start();
                th.Join(4500);                 // 等状态检测结果（本就在后台线程，不影响界面）
                if (started) return true;
                if (failure != null) Note("WMP 兜底失败：" + failure.Message, true);
                else Note("WMP 未真正开始播放（playState=" + lastState + "）", true);
                return false;
            }
            catch { return false; }
        }
    }
}
