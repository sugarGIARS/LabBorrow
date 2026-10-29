// =====================================================================
//  LabBorrow  --  实验室设备借还管理系统 (RFID)
//  Target: .NET Framework 4.0, x86, WinForms
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LabBorrow
{
    internal static class Program
    {
        // embedded native / managed dependencies are extracted at startup
        private static string LibDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabBorrow", "libs");

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        static void Main()
        {
            try { ExtractEmbeddedLibs(); }
            catch (Exception ex)
            {
                MessageBox.Show("释放运行库失败:\n" + ex.Message, "LabBorrow",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetDllDirectory(LibDir);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);

            Application.Run(new MainForm());
        }

        private static void ShowFatal(Exception ex)
        {
            MessageBox.Show(ex == null ? "未知错误" : ex.ToString(), "LabBorrow 错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ----------------------------------------------------------------
        //  Resolve embedded libraries (System.Data.SQLite.dll, etc.)
        // ----------------------------------------------------------------
        private static readonly Dictionary<string, Assembly> Resolved = new Dictionary<string, Assembly>();

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string file = Path.Combine(LibDir, name + ".dll");
            Assembly asm;
            if (File.Exists(file) && Resolved.TryGetValue(name, out asm))
                return asm;
            if (File.Exists(file))
            {
                asm = Assembly.LoadFrom(file);
                Resolved[name] = asm;
                return asm;
            }
            return null;
        }

        // ----------------------------------------------------------------
        //  Extract embedded resources to LocalAppData\LabBorrow\libs
        //  If the target DLL is locked by another process, fall back to a
        //  process-private shadow directory so the app can still start.
        // ----------------------------------------------------------------
        private static void ExtractEmbeddedLibs()
        {
            Directory.CreateDirectory(LibDir);
            var asm = Assembly.GetExecutingAssembly();
            string[] embedded = { "Basic.dll", "UHFReader18CSharp.dll", "System.Data.SQLite.dll" };

            // First attempt: extract to the shared libs directory.
            bool ok = TryExtractTo(LibDir, embedded, asm);
            if (!ok)
            {
                // Fallback: use a per-process directory to avoid locked files.
                LibDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LabBorrow", "libs_" + Process.GetCurrentProcess().Id);
                Directory.CreateDirectory(LibDir);
                ok = TryExtractTo(LibDir, embedded, asm);
                if (!ok)
                    throw new IOException("无法释放运行库，文件被其他进程占用，请关闭其他 LabBorrow 实例后重试。");
            }
        }

        private static bool TryExtractTo(string dir, string[] names, Assembly asm)
        {
            try
            {
                foreach (var name in names)
                {
                    string dst = Path.Combine(dir, name);
                    using (var rs = asm.GetManifestResourceStream("LabBorrow.libs." + name))
                    {
                        if (rs == null) continue;
                        byte[] newHash = ComputeHash(rs);
                        rs.Position = 0;

                        if (File.Exists(dst))
                        {
                            try
                            {
                                using (var fs = File.OpenRead(dst))
                                {
                                    if (ComputeHash(fs).SequenceEqual(newHash))
                                        continue;
                                }
                            }
                            catch { }

                            // Remove or rename the old file if it differs.
                            try { File.Delete(dst); }
                            catch
                            {
                                string moved = dst + ".old." + Guid.NewGuid().ToString("N");
                                try { File.Move(dst, moved); }
                                catch { return false; }
                            }
                        }

                        using (var fs = File.Create(dst))
                            rs.CopyTo(fs);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static byte[] ComputeHash(Stream s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return sha.ComputeHash(s);
        }
    }
}