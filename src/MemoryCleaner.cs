// ============================================================
// 内存释放工具 MemoryCleaner v1.0
// 功能：
//   1. 图形界面：实时显示物理内存使用率、一键释放所有进程工作集
//   2. 进程列表：按占用内存排序显示
//   3. 自动清理：内存使用率超过阈值时自动释放
//   4. 命令行模式：-c 静默清理后退出；-t N 超过阈值才清理（可配计划任务）
// 技术：EmptyWorkingSet 清空各进程工作集 + SetProcessWorkingSetSize(-1,-1)
// 编译：csc.exe /target:winexe /win32icon:app.ico MemoryCleaner.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("内存释放工具 MemoryCleaner")]
[assembly: AssemblyDescription("一键释放系统内存：清空进程工作集，支持自动清理与命令行静默模式")]
[assembly: AssemblyProduct("MemoryCleaner")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MemoryCleaner
{
    internal static class NativeMethods
    {
        // ---------- 内存状态 ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static MEMORYSTATUSEX GetMemoryStatus()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref m);
            return m;
        }

        // ---------- 工作集 ----------
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // ---------- 提权 SeDebugPrivilege ----------
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x2;

        public static bool EnableDebugPrivilege()
        {
            // 使用伪句柄 GetCurrentProcess()，避免 Process.Handle 在受限环境下被拒
            IntPtr hToken;
            if (!OpenProcessToken(NativeMethods.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                return false;
            try
            {
                long luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                    return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        // ---------- 命令行模式控制台 ----------
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        public const int STD_OUTPUT_HANDLE = -11;
        public const int STD_ERROR_HANDLE = -12;
    }

    // ============================================================
    // 清理结果
    // ============================================================
    public class CleanResult
    {
        public int Scanned;     // 扫描到的进程数
        public int Ok;          // 成功清空工作集的进程数
        public int Failed;      // 无权限或失败跳过的进程数
        public ulong AvailBefore; // 清理前可用物理内存
        public ulong AvailAfter;  // 清理后可用物理内存
        public ulong Freed;       // 释放的可用内存增量
    }

    // ============================================================
    // 内存清理核心
    // ============================================================
    public static class Cleaner
    {
        public static CleanResult Clean()
        {
            NativeMethods.EnableDebugPrivilege();
            NativeMethods.MEMORYSTATUSEX before = NativeMethods.GetMemoryStatus();

            int scanned = 0, ok = 0, failed = 0;
            Process[] procs = null;
            try { procs = Process.GetProcesses(); }
            catch { procs = new Process[0]; }

            foreach (Process p in procs)
            {
                scanned++;
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = NativeMethods.OpenProcess(
                        NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                        false, (uint)p.Id);
                    if (h == IntPtr.Zero) { failed++; continue; }
                    if (NativeMethods.EmptyWorkingSet(h)) ok++; else failed++;
                }
                catch { failed++; }
                finally
                {
                    if (h != IntPtr.Zero) NativeMethods.CloseHandle(h);
                    try { p.Dispose(); } catch { }
                }
            }

            // 同时清空自身工作集（用伪句柄，兼容受限环境）
            try
            {
                NativeMethods.SetProcessWorkingSetSize(
                    NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }

            NativeMethods.MEMORYSTATUSEX after = NativeMethods.GetMemoryStatus();
            ulong freed = 0;
            if (after.ullAvailPhys > before.ullAvailPhys)
                freed = after.ullAvailPhys - before.ullAvailPhys;

            return new CleanResult
            {
                Scanned = scanned,
                Ok = ok,
                Failed = failed,
                AvailBefore = before.ullAvailPhys,
                AvailAfter = after.ullAvailPhys,
                Freed = freed
            };
        }
    }

    // ============================================================
    // 工具函数
    // ============================================================
    public static class Util
    {
        public static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024.0 && i < units.Length - 1) { v /= 1024.0; i++; }
            if (i == 0) return v.ToString("0") + " " + units[i];
            return v.ToString("0.0") + " " + units[i];
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }

    // ============================================================
    // 主窗体
    // ============================================================
    public class MainForm : Form
    {
        private ProgressBar progUsage;
        private Label lblUsage;
        private Label lblMem;
        private Label lblAdmin;
        private Label lblStatus;
        private ListView lvProcs;
        private Button btnClean;
        private Button btnRefresh;
        private CheckBox chkAuto;
        private NumericUpDown numThreshold;
        private System.Windows.Forms.Timer tmrMem;
        private System.Windows.Forms.Timer tmrProcs;

        private bool _cleaning = false;
        private DateTime _lastClean = DateTime.MinValue;

        public MainForm()
        {
            Text = "内存释放工具 MemoryCleaner v1.0";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(780, 620);
            MinimumSize = new Size(700, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            BuildUi();

            tmrMem = new System.Windows.Forms.Timer();
            tmrMem.Interval = 1000;
            tmrMem.Tick += tmrMem_Tick;
            tmrMem.Start();

            tmrProcs = new System.Windows.Forms.Timer();
            tmrProcs.Interval = 3000;
            tmrProcs.Tick += tmrProcs_Tick;
            tmrProcs.Start();

            RefreshProcList();
            UpdateMemDisplay();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (tmrMem != null) tmrMem.Stop();
            if (tmrProcs != null) tmrProcs.Stop();
            base.OnFormClosing(e);
        }

        private void BuildUi()
        {
            // ---- 顶部：内存使用状况 ----
            GroupBox gbMem = new GroupBox();
            gbMem.Text = "内存使用状况";
            gbMem.Dock = DockStyle.Top;
            gbMem.Height = 112;
            Controls.Add(gbMem);

            lblUsage = new Label();
            lblUsage.Text = "内存使用率 --%";
            lblUsage.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            lblUsage.ForeColor = Color.FromArgb(30, 58, 138);
            lblUsage.Location = new Point(14, 10);
            lblUsage.AutoSize = true;
            gbMem.Controls.Add(lblUsage);

            progUsage = new ProgressBar();
            progUsage.Location = new Point(14, 46);
            progUsage.Height = 22;
            progUsage.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            progUsage.Minimum = 0;
            progUsage.Maximum = 100;
            gbMem.Controls.Add(progUsage);

            lblMem = new Label();
            lblMem.Location = new Point(14, 78);
            lblMem.AutoSize = true;
            lblMem.ForeColor = Color.FromArgb(60, 60, 60);
            gbMem.Controls.Add(lblMem);

            lblAdmin = new Label();
            lblAdmin.Text = "提示：以管理员身份运行可清理更多进程";
            lblAdmin.ForeColor = Color.FromArgb(217, 119, 6);
            lblAdmin.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblAdmin.AutoSize = true;
            lblAdmin.Location = new Point(600, 16);
            gbMem.Controls.Add(lblAdmin);

            // ---- 中部：进程列表 ----
            GroupBox gbProcs = new GroupBox();
            gbProcs.Text = "占用内存最多的进程（按工作集排序）";
            gbProcs.Dock = DockStyle.Fill;
            gbProcs.Padding = new Padding(8, 4, 8, 8);
            Controls.Add(gbProcs);

            lvProcs = new ListView();
            lvProcs.Dock = DockStyle.Fill;
            lvProcs.View = View.Details;
            lvProcs.FullRowSelect = true;
            lvProcs.GridLines = true;
            lvProcs.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvProcs.Columns.Add("进程名", 240);
            lvProcs.Columns.Add("PID", 70);
            lvProcs.Columns.Add("工作集", 110);
            lvProcs.Columns.Add("私有内存", 110);
            gbProcs.Controls.Add(lvProcs);

            // ---- 底部：操作区 ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 84;
            Controls.Add(bottom);

            btnClean = new Button();
            btnClean.Text = "立即释放内存";
            btnClean.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnClean.Size = new Size(150, 38);
            btnClean.Location = new Point(14, 10);
            btnClean.FlatStyle = FlatStyle.System;
            btnClean.BackColor = Color.FromArgb(34, 197, 94);
            btnClean.Click += btnClean_Click;
            bottom.Controls.Add(btnClean);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新列表";
            btnRefresh.Size = new Size(96, 38);
            btnRefresh.Location = new Point(176, 10);
            btnRefresh.FlatStyle = FlatStyle.System;
            btnRefresh.Click += delegate { RefreshProcList(); };
            bottom.Controls.Add(btnRefresh);

            chkAuto = new CheckBox();
            chkAuto.Text = "内存使用率超过";
            chkAuto.AutoSize = true;
            chkAuto.Location = new Point(296, 22);
            chkAuto.CheckedChanged += delegate { UpdateAdminHint(); };
            bottom.Controls.Add(chkAuto);

            numThreshold = new NumericUpDown();
            numThreshold.Minimum = 50;
            numThreshold.Maximum = 99;
            numThreshold.Value = 85;
            numThreshold.Width = 54;
            numThreshold.Location = new Point(410, 18);
            bottom.Controls.Add(numThreshold);

            Label lblPct = new Label();
            lblPct.Text = "% 时自动释放";
            lblPct.AutoSize = true;
            lblPct.Location = new Point(470, 22);
            bottom.Controls.Add(lblPct);

            lblStatus = new Label();
            lblStatus.Location = new Point(14, 56);
            lblStatus.AutoSize = false;
            lblStatus.Width = 740;
            lblStatus.ForeColor = Color.FromArgb(30, 64, 175);
            lblStatus.Text = "就绪：点击“立即释放内存”或勾选自动清理。";
            bottom.Controls.Add(lblStatus);
        }

        private void UpdateAdminHint()
        {
            if (chkAuto.Checked)
            {
                lblAdmin.Text = "提示：以管理员身份运行可清理更多进程；自动清理将每 5 秒检查一次";
            }
            else
            {
                lblAdmin.Text = "提示：以管理员身份运行可清理更多进程";
            }
        }

        // ---------- 内存显示 ----------
        private void UpdateMemDisplay()
        {
            NativeMethods.MEMORYSTATUSEX m = NativeMethods.GetMemoryStatus();
            double used = (double)(m.ullTotalPhys - m.ullAvailPhys);
            uint pct = m.dwMemoryLoad;
            if (progUsage.Style != ProgressBarStyle.Marquee)
                progUsage.Value = (int)Math.Min(100, pct);
            lblUsage.Text = "内存使用率 " + pct + "%";
            lblMem.Text = "总内存: " + Util.FormatBytes(m.ullTotalPhys)
                + "    已用: " + Util.FormatBytes((ulong)used)
                + "    可用: " + Util.FormatBytes(m.ullAvailPhys);
            if (!Util.IsAdministrator())
                lblAdmin.Visible = true;
        }

        // ---------- 进程列表 ----------
        private struct ProcInfo
        {
            public string Name;
            public int Id;
            public long WorkingSet;
            public long PrivateBytes;
            public ProcInfo(string name, int id, long ws, long priv)
            {
                Name = name; Id = id; WorkingSet = ws; PrivateBytes = priv;
            }
        }

        private void RefreshProcList()
        {
            int topIdx = lvProcs.TopItem != null ? lvProcs.TopItem.Index : 0;
            lvProcs.BeginUpdate();
            lvProcs.Items.Clear();
            try
            {
                List<ProcInfo> list = new List<ProcInfo>();
                Process[] procs = null;
                try { procs = Process.GetProcesses(); }
                catch { procs = new Process[0]; }
                foreach (Process p in procs)
                {
                    try
                    {
                        list.Add(new ProcInfo(p.ProcessName, p.Id, p.WorkingSet64, p.PrivateMemorySize64));
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                list.Sort(delegate(ProcInfo a, ProcInfo b) { return b.WorkingSet.CompareTo(a.WorkingSet); });
                int shown = 0;
                foreach (ProcInfo pi in list)
                {
                    if (shown++ >= 50) break;
                    ListViewItem item = new ListViewItem(pi.Name);
                    item.SubItems.Add(pi.Id.ToString());
                    item.SubItems.Add(Util.FormatBytes((ulong)pi.WorkingSet));
                    item.SubItems.Add(Util.FormatBytes((ulong)pi.PrivateBytes));
                    lvProcs.Items.Add(item);
                }
            }
            catch { }
            finally
            {
                lvProcs.EndUpdate();
                if (lvProcs.Items.Count > 0 && topIdx < lvProcs.Items.Count)
                {
                    try { lvProcs.TopItem = lvProcs.Items[topIdx]; } catch { }
                }
            }
        }

        // ---------- 清理 ----------
        private void btnClean_Click(object sender, EventArgs e)
        {
            StartClean();
        }

        private void StartClean()
        {
            if (_cleaning) return;
            _cleaning = true;
            btnClean.Enabled = false;
            progUsage.Style = ProgressBarStyle.Marquee;
            lblStatus.Text = "正在释放内存，请稍候...";

            Task.Factory.StartNew(delegate
            {
                CleanResult r = Cleaner.Clean();
                try
                {
                    BeginInvoke(new Action(delegate { FinishClean(r); }));
                }
                catch { }
            });
        }

        private void FinishClean(CleanResult r)
        {
            _cleaning = false;
            _lastClean = DateTime.Now;
            btnClean.Enabled = true;
            progUsage.Style = ProgressBarStyle.Continuous;
            UpdateMemDisplay();
            string time = DateTime.Now.ToString("HH:mm:ss");
            lblStatus.Text = "释放完成（" + time + "）：扫描 " + r.Scanned
                + " 个进程，成功 " + r.Ok + " 个，跳过 " + r.Failed
                + " 个；可用内存 " + Util.FormatBytes(r.AvailBefore) + " → "
                + Util.FormatBytes(r.AvailAfter) + "（+" + Util.FormatBytes(r.Freed) + "）";
            RefreshProcList();
        }

        // ---------- 定时器 ----------
        private void tmrMem_Tick(object sender, EventArgs e)
        {
            UpdateMemDisplay();
            if (chkAuto.Checked && !_cleaning)
            {
                uint usage = NativeMethods.GetMemoryStatus().dwMemoryLoad;
                if (usage >= (uint)numThreshold.Value &&
                    (DateTime.Now - _lastClean).TotalSeconds >= 15)
                {
                    StartClean();
                }
            }
        }

        private void tmrProcs_Tick(object sender, EventArgs e)
        {
            RefreshProcList();
        }
    }

    // ============================================================
    // 程序入口
    // ============================================================
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool clean = false;
            bool cleanIfAbove = false;
            int threshold = 0;
            string logPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-c" || a == "--clean" || a == "/clean")
                {
                    clean = true;
                }
                else if (a == "-t" || a == "--threshold" || a == "/threshold")
                {
                    if (i + 1 < args.Length)
                    {
                        int v;
                        if (int.TryParse(args[i + 1], out v)) { i++; threshold = v; cleanIfAbove = true; }
                    }
                }
                else if (a == "-l" || a == "--log" || a == "/log")
                {
                    if (i + 1 < args.Length) { i++; logPath = args[i]; }
                }
                else if (a == "-h" || a == "--help" || a == "/?" || a == "/help")
                {
                    ShowHelp();
                    return 0;
                }
            }

            if (clean || cleanIfAbove)
            {
                return RunCli(cleanIfAbove, threshold, logPath);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static bool SetupConsole()
        {
            bool attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            bool own = false;
            if (!attached)
            {
                if (!NativeMethods.AllocConsole()) return false;
                own = true;
            }
            IntPtr hOut = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
            if (hOut != IntPtr.Zero && hOut != new IntPtr(-1))
            {
                SafeFileHandle sh = new SafeFileHandle(hOut, false);
                StreamWriter sw = new StreamWriter(new FileStream(sh, FileAccess.Write), new UTF8Encoding(false));
                sw.AutoFlush = true;
                Console.SetOut(sw);
            }
            IntPtr hErr = NativeMethods.GetStdHandle(NativeMethods.STD_ERROR_HANDLE);
            if (hErr != IntPtr.Zero && hErr != new IntPtr(-1))
            {
                SafeFileHandle sh = new SafeFileHandle(hErr, false);
                StreamWriter sw = new StreamWriter(new FileStream(sh, FileAccess.Write), new UTF8Encoding(false));
                sw.AutoFlush = true;
                Console.SetError(sw);
            }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            return own;
        }

        private static int RunCli(bool thresholdMode, int threshold, string logPath)
        {
            bool own = SetupConsole();
            List<string> lines = new List<string>();
            int exitCode = 0;
            try
            {
                NativeMethods.MEMORYSTATUSEX m0 = NativeMethods.GetMemoryStatus();
                if (thresholdMode)
                {
                    int usage = (int)m0.dwMemoryLoad;
                    lines.Add("[MemoryCleaner] 当前内存使用率: " + usage + "%（阈值 " + threshold + "%）");
                    if (usage < threshold)
                    {
                        lines.Add("[MemoryCleaner] 使用率未超过阈值，无需清理。");
                        WriteReport(lines, logPath);
                        return 0;
                    }
                    lines.Add("[MemoryCleaner] 使用率超过阈值，开始清理...");
                }
                else
                {
                    lines.Add("[MemoryCleaner] 开始清理内存...");
                }

                CleanResult r = Cleaner.Clean();

                lines.Add("[MemoryCleaner] 清理完成：扫描 " + r.Scanned + " 个进程，成功 " + r.Ok + " 个，跳过 " + r.Failed + " 个。");
                lines.Add("[MemoryCleaner] 可用内存：" + Util.FormatBytes(r.AvailBefore) + " → " + Util.FormatBytes(r.AvailAfter) + "（释放 " + Util.FormatBytes(r.Freed) + "）");
                WriteReport(lines, logPath);
                return 0;
            }
            catch (Exception ex)
            {
                exitCode = 1;
                lines.Add("[MemoryCleaner] 出错: " + ex.ToString());
                WriteReport(lines, logPath);
                return exitCode;
            }
            finally
            {
                if (own)
                {
                    try
                    {
                        Console.WriteLine();
                        Console.WriteLine("按任意键退出...");
                        Console.ReadKey(true);
                    }
                    catch { }
                }
            }
        }

        private static void WriteReport(List<string> lines, string logPath)
        {
            try
            {
                foreach (string line in lines)
                {
                    Console.WriteLine(line);
                }
                Console.Out.Flush();
            }
            catch { }
            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("======== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ========");
                    foreach (string line in lines)
                    {
                        sb.AppendLine(line);
                    }
                    File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private static void ShowHelp()
        {
            bool own = SetupConsole();
            try
            {
                Console.WriteLine("内存释放工具 MemoryCleaner v1.0");
                Console.WriteLine("用法：");
                Console.WriteLine("  MemoryCleaner.exe            打开图形界面");
                Console.WriteLine("  MemoryCleaner.exe -c         静默清理内存后退出");
                Console.WriteLine("  MemoryCleaner.exe -t 85      内存使用率超过 85% 时才清理（可配合计划任务）");
                Console.WriteLine("  MemoryCleaner.exe -l 路径    （配合 -c/-t）把报告追加写入日志文件");
                Console.WriteLine("  MemoryCleaner.exe -h         显示本帮助");
            }
            finally
            {
                if (own)
                {
                    try
                    {
                        Console.WriteLine();
                        Console.WriteLine("按任意键退出...");
                        Console.ReadKey(true);
                    }
                    catch { }
                }
            }
        }
    }
}
