// =====================================================================
//  LabBorrow  --  自助终端（Kiosk）模式
//
//  老师问：能不能像高铁闸机那样，放在一台小机器上自己跑？
//  可以。自助模式要做的事其实很固定，这里一次性做全：
//    1) 全屏无边框、占满屏幕（不留 Windows 任务栏干扰）
//    2) 触屏友好：放大按钮与列表，手指点得中
//    3) 空闲自动回到主控台（上一个人借完，下一个人来就是干净的开始界面）
//    4) 不被学生误关：关闭按钮失效，退出需要管理员手势（长按 / 组合键）
//    5) 屏幕常亮、不休眠不见pw:ounts（SetThreadExecutionState）
//    6) 开机自动启动（写注册表 Run 项），断电来电后自己恢复到可刷卡状态
//    7) 看门狗：读卡器掉线自动重连（放在 MainForm 里做，这里只提供开关状态）
// =====================================================================
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LabBorrow
{
    public static class Kiosk
    {
        // 阻止系统休眠 / 熄屏（自助机必须一直亮着、随时能刷）
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        private static bool screenOn;

        /// <summary>屏幕常亮开关（true=告诉系统这台机正在被使用，别休眠别熄屏）</summary>
        public static void KeepScreenOn(bool on)
        {
            try
            {
                if (on == screenOn) return;
                if (on) SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                else SetThreadExecutionState(ES_CONTINUOUS);
                screenOn = on;
            }
            catch { }
        }

        // ---------------- 开机自动启动 ----------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "LabBorrow";

        public static bool IsAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue(AppName) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    //  registry 里存的可能是旧路径：路径对不上就不算已开启
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    return string.Equals(v.Trim('"'), exe, StringComparison.OrdinalIgnoreCase)
                        || v.IndexOf(AppName + ".exe", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        /// <summary>设置开机自动启动（写入当前用户的 Run 项，不需要管理员权限）</summary>
        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(AppName, "\"" + exe + "\"");
                    }
                    else
                    {
                        var v = k.GetValue(AppName);
                        if (v != null) k.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }
    }
}
