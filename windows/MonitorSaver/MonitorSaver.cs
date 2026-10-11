// MonitorSaver v3.1
// Ctrl+Shift+Alt+L  ->  세션 잠금 + 모니터 끄기. 복귀는 아무 입력이나(마우스/키보드 무관).
// 전원 설정(절전/최대절전/화면 끄기 시간 등)은 일절 변경하지 않는다.
//
// ┌─ v1/v2 가 왜 사고를 냈는지, 그리고 v3 가 무엇을 하지 않는지 ─────────────────┐
// │ v1 은 "소등 -> 깨어나면 되끄기(반복) -> 키 감지 -> 잠금 -> 잠긴 뒤 켜기 시도"  │
// │ 였다. 실측 로그상 3초 동안 DPMS 를 8번 토글했고, 잠금 27ms 전에 소등 명령을    │
// │ 보냈으며, 잠긴 뒤에는 화면을 켤 권한이 없어 검은 잠금화면에 갇혔다.            │
// │ DisplayPort 에 물린 OLED 모니터는 DPMS 토글에 특히 취약하다.                  │
// │                                                                            │
// │ 그래서 v3 는 화면과 절대 싸우지 않는다:                                        │
// │   - 저수준 훅 없음 (마우스 차단 없음)                                          │
// │   - 감시 루프 없음, 재소등 없음                                                │
// │   - 화면을 강제로 켜려는 시도 없음                                             │
// │   - 소등 요청은 한 시점에 한 번, 이후 반복 없음                                │
// │ 깨우기는 Windows 기본 동작에 맡긴다. 유휴 시간이 지나 화면이 꺼졌다가 정상     │
// │ 복귀하는 바로 그 경로를 그대로 쓰는 것이 가장 안전하다.                        │
// └────────────────────────────────────────────────────────────────────────────┘
//
// 동작 순서
//   핫키 -> 핫키 키가 모두 떨어질 때까지 대기 (+KeyReleaseGraceMs)
//        -> LockWorkStation() -> 잠금 통지(WTS_SESSION_LOCK) 대기 (최대 LockWaitMs)
//        -> LockSettleMs 안정화 -> MONITOR_OFF 1회 -> 끝. 이후 아무것도 하지 않는다.
//
//   (손을 뗄 때까지 기다리는 이유: 핫키에서 손을 떼는 동작 자체가 화면을 다시 켜기
//    때문이다. 싸우는 대신 손이 떨어진 뒤에 끈다.)
//
//   v3.1: 잠금이 확인되지 않으면 화면을 끄지 않는다. 잠긴 줄 알고 자리를 비웠는데
//         실제로는 열린 채 화면만 꺼져 있는 상태를 만들지 않기 위함이다.
//         (그 경우 화면은 켜진 채 남고, 트레이 풍선으로 알린다)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace MonitorSaver
{
    internal static class Native
    {
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int MONITOR_OFF = 2;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

        // 잠금이 "실제로 걸렸는지"를 추측(고정 대기)하지 않고 통지로 확인하기 위한 것.
        // 데스크톱이 전환되는 중에 화면 전원을 건드리는 것이 v1 사고의 원인 중 하나였다.
        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int NOTIFY_FOR_THIS_SESSION = 0;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        // 워치독용. .NET 의 Process.ExitCode 는 자기가 시작하지 않은 프로세스에서 예외를 던지는
        // 경우가 있어(실측 확인), 커널 핸들로 직접 대기하고 종료 코드를 읽는다.
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000;
        public const uint INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 모니터 끄기 1단계: 자기 창으로 보낸다 — 즉시 처리된다.
        /// UI 스레드에서 부른다. 그래야 잠금 해제 통지(WndProc)와 순서가 엇갈리지 않는다.
        /// </summary>
        public static void MonitorOffOwn(IntPtr ownHwnd)
        {
            if (ownHwnd != IntPtr.Zero)
                SendMessage(ownHwnd, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(MONITOR_OFF));
        }

        /// <summary>
        /// 모니터 끄기 2단계(보조, 잠금을 쓰지 않을 때만): 브로드캐스트. 반드시 별도 스레드에서 부를 것.
        /// 타임아웃은 창마다 적용되므로 멈춘 창이 많으면 길어질 수 있어 짧게 잡는다.
        /// </summary>
        public static void MonitorOffBroadcast()
        {
            IntPtr res;
            SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER),
                new IntPtr(MONITOR_OFF), SMTO_ABORTIFHUNG, 150, out res);
        }

        public static uint LastInputTick()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        public static string Path;

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    if (Path == null) return;
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > 128 * 1024)
                    {
                        string keep = File.ReadAllText(Path);
                        // 뒤쪽 절반만 남기되, 줄 중간에서 자르지 않도록 다음 줄 시작부터 남긴다.
                        int cut = keep.IndexOf('\n', keep.Length / 2);
                        File.WriteAllText(Path, cut >= 0 ? keep.Substring(cut + 1) : "", Encoding.UTF8);
                    }
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal sealed class Config
    {
        public string Hotkey = "Ctrl+Shift+Alt+L";
        public bool LockFirst = true;           // 0 으로 두면 잠그지 않고 소등만 (동작 확인용)
        public int KeyReleaseGraceMs = 200;     // 핫키에서 손을 뗀 뒤 여유
        public int MaxKeyWaitMs = 3000;         // 키가 안 떨어져도 이 시간이 지나면 진행
        public int LockWaitMs = 2500;           // 잠금 완료 통지를 기다리는 최대 시간
        public int LockSettleMs = 400;          // 잠금 확인 후 소등까지 (데스크톱 전환 중 DPMS 전환 회피)
        public bool KeepSystemAwake = false;    // 실행 중에만 시스템 절전 억제(설정 변경 아님)
        public bool ShowTrayIcon = true;        // 0 이면 트레이 아이콘도 숨긴다(완전 무음)
        public bool AutoRestart = false;        // 1 이면 비정상 종료 시 감시 프로세스가 재실행

        public static Config Load(string path)
        {
            var c = new Config();
            try
            {
                if (!File.Exists(path)) { Save(path, c); return c; }
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "hotkey": if (v.Length > 0) c.Hotkey = v; break;
                        case "lockfirst": c.LockFirst = IsTrue(v); break;
                        case "keyreleasegracems": c.KeyReleaseGraceMs = ParseInt(v, c.KeyReleaseGraceMs, 0, 5000); break;
                        case "maxkeywaitms": c.MaxKeyWaitMs = ParseInt(v, c.MaxKeyWaitMs, 200, 20000); break;
                        case "lockwaitms": c.LockWaitMs = ParseInt(v, c.LockWaitMs, 300, 10000); break;
                        case "locksettlems": c.LockSettleMs = ParseInt(v, c.LockSettleMs, 0, 10000); break;
                        case "keepsystemawake": c.KeepSystemAwake = IsTrue(v); break;
                        case "showtrayicon": c.ShowTrayIcon = IsTrue(v); break;
                        case "autorestart": c.AutoRestart = IsTrue(v); break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("config load failed: " + ex.Message); }
            return c;
        }

        private static bool IsTrue(string v) { return v == "1" || v.ToLowerInvariant() == "true"; }

        private static int ParseInt(string s, int def, int min, int max)
        {
            int r;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return def;
            if (r < min) r = min;
            if (r > max) r = max;
            return r;
        }

        public static void Save(string path, Config c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; ===== MonitorSaver 설정 =====");
            sb.AppendLine("; 수정 후 트레이 아이콘 우클릭 > \"설정 다시 읽기\"");
            sb.AppendLine(";");
            sb.AppendLine("; 동작: 핫키 -> (손 뗄 때까지 대기) -> 세션 잠금 -> 잠금 확인 -> 모니터 끄기(1회) -> 끝.");
            sb.AppendLine(";       이후 아무것도 하지 않는다. 깨우기는 Windows 기본 동작(아무 입력)에 맡긴다.");
            sb.AppendLine(";       화면을 다시 끄거나 강제로 켜려는 시도는 일절 하지 않는다.");
            sb.AppendLine(";       잠금이 확인되지 않으면 화면을 끄지 않는다(트레이 풍선으로 알림, 아이콘을 숨겼으면 로그에만).");
            sb.AppendLine();
            sb.AppendLine("; 핫키. 예: Ctrl+Shift+Alt+L");
            sb.AppendLine("Hotkey=" + c.Hotkey);
            sb.AppendLine();
            sb.AppendLine("; 1 = 소등 전에 세션을 잠근다. 0 = 잠그지 않고 소등만 (동작 확인용)");
            sb.AppendLine(";");
            sb.AppendLine(";   새 PC 에서 처음 쓸 때 불안하면 0 으로 먼저 확인해도 됩니다.");
            sb.AppendLine(";   잠기지 않으므로 화면이 안 돌아와도 갇힐 일이 없습니다.");
            sb.AppendLine(";   아무 키나 눌러 잘 돌아오는 걸 확인한 뒤 1 로 되돌리세요.");
            sb.AppendLine(";   (트레이 아이콘 우클릭 > \"잠금 사용\" 으로도 바꿀 수 있습니다)");
            sb.AppendLine("LockFirst=" + (c.LockFirst ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("; 소등 전 대기. 핫키에서 손을 떼는 동작 자체가 화면을 다시 켜기 때문에,");
            sb.AppendLine("; 핫키 키들이 모두 떨어질 때까지만 기다렸다가 진행한다(보통 0.1~0.3초).");
            sb.AppendLine("; 손을 뗀 뒤 추가로 두는 여유(ms)");
            sb.AppendLine("KeyReleaseGraceMs=" + c.KeyReleaseGraceMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; 키가 떨어지지 않아도 이 시간이 지나면 그냥 진행한다(ms)");
            sb.AppendLine("MaxKeyWaitMs=" + c.MaxKeyWaitMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; 잠금이 실제로 걸렸다는 통지(WTS_SESSION_LOCK)를 기다리는 최대 시간(ms).");
            sb.AppendLine("; 이 시간이 지나 확인할 때까지 통지가 없으면 화면을 끄지 않는다(잠기지 않은 채 꺼지는 것을 막기 위함).");
            sb.AppendLine("LockWaitMs=" + c.LockWaitMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; 잠금이 확인된 뒤 소등까지 추가 대기(ms).");
            sb.AppendLine("; 데스크톱이 전환되는 순간에 화면 전원을 건드리지 않기 위함.");
            sb.AppendLine("; (v1 사고의 원인 중 하나가 잠금 27ms 전에 소등 명령을 보낸 것이었다)");
            sb.AppendLine("LockSettleMs=" + c.LockSettleMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; 1 = 프로그램이 실행되는 동안만 시스템 절전을 억제한다.");
            sb.AppendLine(";     (전원 관리 설정 자체는 변경하지 않는다)");
            sb.AppendLine("KeepSystemAwake=" + (c.KeepSystemAwake ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("; 1 = 비정상 종료되면 감시 프로세스가 자동으로 다시 실행한다.");
            sb.AppendLine(";     정상 종료(트레이 \"종료\")나 1분 미만 생존 시에는 재실행하지 않는다.");
            sb.AppendLine("AutoRestart=" + (c.AutoRestart ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("; 1 = 트레이 아이콘 표시. 0 = 아이콘도 숨긴다(완전 무음).");
            sb.AppendLine(";     0 으로 두면 설정 변경/종료를 트레이에서 할 수 없다.");
            sb.AppendLine(";     종료: MonitorSaver.exe -quit");
            sb.AppendLine("ShowTrayIcon=" + (c.ShowTrayIcon ? "1" : "0"));
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Log.Write("config save failed: " + ex.Message); }
        }
    }

    internal sealed class MainForm : Form
    {
        public const string WndTitle = "MonitorSaver::HiddenWindow";
        public const uint WM_APP_BLANK = 0x8001;
        public const uint WM_APP_ALREADY = 0x8004;   // 두 번째 인스턴스가 알려온다
        public const uint WM_APP_QUIT = 0x8005;      // -quit : 정상 종료 요청
        private const int HOTKEY_ID = 0xB10C;

        // 자동 실행은 작업 스케줄러로 한다.
        // HKCU\...\Run 을 썼다가 재부팅 후 실제로 실행되지 않는 사고가 있었다(2026-08-11).
        // Run 키와 시작 폴더는 둘 다 탐색기가 처리하므로 같이 실패할 수 있다.
        // 작업 스케줄러는 별도 서비스가 처리하고 실패 시 재시도도 된다.
        private const string TaskName = "MonitorSaver";

        private readonly string baseDir;
        private readonly string iniPath;
        private Config cfg;

        private NotifyIcon tray;
        private ToolStripMenuItem autoStartItem;
        private ToolStripMenuItem lockFirstItem;
        private Timer waitTimer;
        private Timer heartbeat;
        private Timer logHeartbeat;
        private string heartbeatPath;
        private Process watchdog;
        private readonly DateTime startedAt = DateTime.Now;

        private void WriteHeartbeat()
        {
            try
            {
                File.WriteAllText(heartbeatPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    "  uptime=" + UptimeText() + Environment.NewLine);
            }
            catch { }
        }

        // [모니터링 3] 감시 프로세스. 본체가 죽으면 "종료 코드"를 기록한다.
        // 외부 강제 종료(작업 관리자 등)는 본체 안에서는 절대 감지할 수 없고, 이 방법뿐이다.
        // 주의: 이 워치독은 기록만 하고(AutoRestart=1 이면 본체를 다시 띄운다), v1 처럼 화면을 잠그거나 만지지 않는다.
        private void StartWatchdog()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                var psi = new ProcessStartInfo(Application.ExecutablePath,
                    "-watchdog " + pid.ToString(CultureInfo.InvariantCulture));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                watchdog = Process.Start(psi);
                Log.Write("watchdog started (pid=" + (watchdog != null ? watchdog.Id : -1) + ") watching " + pid);
            }
            catch (Exception ex) { Log.Write("watchdog start failed: " + ex.Message); }
        }

        private string UptimeText()
        {
            TimeSpan up = DateTime.Now - startedAt;
            return ((int)up.TotalHours).ToString(CultureInfo.InvariantCulture) + "h" +
                   up.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        // 소등까지의 단계
        private const int PhIdle = 0;
        private const int PhWaitRelease = 1;   // 핫키 키가 떨어지길 기다림
        private const int PhGrace = 2;         // 손 뗀 뒤 여유
        private const int PhWaitLock = 3;      // 잠금 완료 통지를 기다림
        private const int PhLockSettle = 4;    // 잠금 확인 후 안정화

        private int phase = PhIdle;
        private int phaseStart;
        private int requestStart;
        private volatile bool lockConfirmed;
        private bool sessionNotifyRegistered;  // WTS 통지 등록 성공 여부. 실패면 잠금을 확인할 수 없다.
        private bool sessionLocked;            // WTS 통지로 추적한 현재 잠금 상태(시작 시점은 잠기지 않은 것으로 본다)
        private bool monitorOffNeedsLock;      // 자기 창 소등을 보내는 동안만 true — WndProc 가 잠금 상태를 마지막으로 확인한다
        private bool monitorOffSkipped;        // WndProc 가 잠금 해제를 보고 소등을 막았으면 true
        private readonly List<int> watchKeys = new List<int>();

        public MainForm(string dir)
        {
            baseDir = dir;
            iniPath = Path.Combine(baseDir, "MonitorSaver.ini");
            cfg = Config.Load(iniPath);

            Text = WndTitle;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);

            IntPtr h = Handle;
            Log.Write("started v" + Program.Version + ". pid=" + Process.GetCurrentProcess().Id +
                      " hwnd=0x" + h.ToInt64().ToString("X"));

            BuildTray();
            RegisterTheHotkey();
            EnsureSessionNotification();
            StartWatchdog();

            waitTimer = new Timer();
            waitTimer.Interval = 40;   // 손 떼는 순간을 빠르게 잡기 위해 촘촘하게
            waitTimer.Tick += WaitTick;

            // [모니터링 1] 생존 파일. 30초마다 덮어쓴다. 이 파일의 수정 시각 = 마지막으로 살아 있던 시각.
            //              강제 종료로 아무 핸들러도 못 돌았을 때 사망 시각을 알아내는 유일한 수단이다.
            heartbeatPath = Path.Combine(baseDir, "heartbeat.txt");
            heartbeat = new Timer();
            heartbeat.Interval = 30 * 1000;
            heartbeat.Tick += delegate { WriteHeartbeat(); };
            heartbeat.Start();
            WriteHeartbeat();

            // [모니터링 2] 로그에도 15분마다 흔적을 남긴다(사람이 읽는 용도).
            logHeartbeat = new Timer();
            logHeartbeat.Interval = 15 * 60 * 1000;
            logHeartbeat.Tick += delegate { Log.Write("alive (uptime " + UptimeText() + ")"); };
            logHeartbeat.Start();

            ApplyExecutionState();

            // v3 로 재작성하면서 빠졌던 예외 로깅을 되살린다.
            // 이게 없으면 프로세스가 죽어도 로그에 아무것도 남지 않는다.
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs ex)
            {
                Log.Write("!! ThreadException: " + ex.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ex)
            {
                Log.Write("!! UnhandledException (terminating=" + ex.IsTerminating + "): " + ex.ExceptionObject);
            };
            AppDomain.CurrentDomain.ProcessExit += delegate { Log.Write("process exit (uptime " + UptimeText() + ")"); };
            Application.ApplicationExit += delegate { Log.Write("application exit"); };
            SystemEvents.SessionEnding += delegate(object s, SessionEndingEventArgs ex)
            {
                Log.Write("session ending: " + ex.Reason);
            };
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        // ---------------- 트레이 ----------------

        private void BuildTray()
        {
            if (!cfg.ShowTrayIcon) { Log.Write("tray icon hidden (ShowTrayIcon=0)"); return; }

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("지금 화면 끄기 (" + cfg.Hotkey + ")", null, delegate { RequestBlank(); }));
            menu.Items.Add(new ToolStripSeparator());
            lockFirstItem = new ToolStripMenuItem("잠금 사용", null, delegate { ToggleLockFirst(); });
            lockFirstItem.Checked = cfg.LockFirst;
            menu.Items.Add(lockFirstItem);
            autoStartItem = new ToolStripMenuItem("로그온 시 자동 실행 (작업 스케줄러)", null, delegate { ToggleAutoStart(); });
            autoStartItem.Checked = IsAutoStartEnabled();
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("설정 파일 열기", null, delegate { OpenWithNotepad(iniPath); }));
            menu.Items.Add(new ToolStripMenuItem("설정 다시 읽기", null, delegate { ReloadConfig(); }));
            menu.Items.Add(new ToolStripMenuItem("로그 열기", null, delegate { OpenWithNotepad(Log.Path); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("종료", null, delegate { Close(); }));

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { RequestBlank(); };
            UpdateTrayText();
        }

        private void UpdateTrayText()
        {
            if (tray == null) return;
            tray.Text = "MonitorSaver  " + cfg.Hotkey + (cfg.LockFirst ? "  (잠금 사용)" : "  (잠금 안 함)");
        }

        /// <summary>시작 프로그램으로 조용히 돌아야 하므로, 알림은 모달 창 대신 풍선으로 띄운다.</summary>
        private void Notify(string text, ToolTipIcon icon)
        {
            if (tray != null) tray.ShowBalloonTip(4000, "MonitorSaver", text, icon);
            else Log.Write("notify(no tray): " + text);
        }

        private void ToggleLockFirst()
        {
            cfg.LockFirst = !cfg.LockFirst;
            Config.Save(iniPath, cfg);
            if (lockFirstItem != null) lockFirstItem.Checked = cfg.LockFirst;
            UpdateTrayText();
            Log.Write("LockFirst = " + cfg.LockFirst);
            Notify(cfg.LockFirst
                    ? "잠금 사용: 단축키를 누르면 세션을 잠근 뒤 화면을 끕니다."
                    : "잠금 안 함: 단축키를 누르면 화면만 끕니다.",
                ToolTipIcon.Info);
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var body = new SolidBrush(Color.FromArgb(235, 238, 240, 245)))
                    using (var screen = new SolidBrush(Color.FromArgb(255, 22, 24, 30)))
                    {
                        g.FillRectangle(body, 2, 4, 28, 20);
                        g.FillRectangle(screen, 5, 7, 22, 14);
                        g.FillRectangle(body, 13, 24, 6, 4);
                        g.FillRectangle(body, 8, 27, 16, 3);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void OpenWithNotepad(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
        }

        private void ReloadConfig()
        {
            cfg = Config.Load(iniPath);
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            RegisterTheHotkey();
            ApplyExecutionState();
            if (lockFirstItem != null) lockFirstItem.Checked = cfg.LockFirst;
            UpdateTrayText();
            Log.Write("config reloaded. hotkey=" + cfg.Hotkey + " lockFirst=" + cfg.LockFirst);
            Notify("설정을 다시 읽었습니다.\n핫키: " + cfg.Hotkey + (cfg.LockFirst ? " / 잠금 사용" : " / 잠금 안 함"),
                ToolTipIcon.Info);
        }

        /// <summary>schtasks 를 조용히 실행하고 종료 코드를 돌려준다. -1 이면 실행 자체 실패.</summary>
        private static int RunSchtasks(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        // 등록 여부 = 작업의 존재 여부. 언어별 상태 문자열을 파싱하지 않으므로 한글/영문 Windows 모두 동작한다.
        private static bool IsAutoStartEnabled()
        {
            string o;
            return RunSchtasks("/Query /TN \"" + TaskName + "\"", out o) == 0;
        }

        /// <summary>자동 실행 등록/해제. 트레이 메뉴와 -autostart 스위치가 같은 경로를 쓴다.</summary>
        public static bool SetAutoStart(bool on)
        {
            try
            {
                string o;
                if (!on)
                {
                    int rc = RunSchtasks("/Delete /TN \"" + TaskName + "\" /F", out o);
                    Log.Write("autostart delete rc=" + rc + " " + o.Trim());
                }
                else
                {
                    string xml = Path.Combine(Path.GetTempPath(), "MonitorSaver_task.xml");
                    File.WriteAllText(xml, BuildTaskXml(), Encoding.Unicode);   // schtasks /XML 은 UTF-16 을 요구한다
                    int rc = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + xml + "\" /F", out o);
                    Log.Write("autostart create rc=" + rc + " " + o.Trim());
                    try { File.Delete(xml); } catch { }
                }
                return IsAutoStartEnabled();
            }
            catch (Exception ex) { Log.Write("autostart change failed: " + ex); return IsAutoStartEnabled(); }
        }

        private void ToggleAutoStart()
        {
            bool now = SetAutoStart(!IsAutoStartEnabled());
            if (autoStartItem != null) autoStartItem.Checked = now;
            Notify(now ? "로그온 시 자동 실행하도록 등록했습니다. (작업 스케줄러)"
                       : "자동 실행 등록을 해제했습니다.", ToolTipIcon.Info);
        }

        private static string BuildTaskXml()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.Append("<RegistrationInfo><Description>MonitorSaver - hotkey lock + monitor off</Description></RegistrationInfo>");
            sb.Append("<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>");
            sb.Append("<Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId>");
            sb.Append("<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>");
            sb.Append("<Settings>");
            sb.Append("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.Append("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.Append("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.Append("<AllowHardTerminate>true</AllowHardTerminate>");
            sb.Append("<StartWhenAvailable>true</StartWhenAvailable>");
            sb.Append("<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
            sb.Append("<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>");
            sb.Append("<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>");
            sb.Append("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>");
            sb.Append("<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>");
            sb.Append("</Settings>");
            sb.Append("<Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command>");
            sb.Append("<WorkingDirectory>" + Esc(dir) + "</WorkingDirectory></Exec></Actions>");
            sb.Append("</Task>");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void ApplyExecutionState()
        {
            uint flags = Native.ES_CONTINUOUS | (cfg.KeepSystemAwake ? Native.ES_SYSTEM_REQUIRED : 0);
            Native.SetThreadExecutionState(flags);
        }

        /// <summary>
        /// 잠금 통지(WTS) 등록. 로그온 직후에는 원격 데스크톱 서비스가 아직 준비되지 않아 실패할 수 있으므로,
        /// 시작 시 실패하면 핫키를 누를 때마다 다시 시도한다. 등록이 안 되면 잠금을 확인할 수 없고,
        /// 확인할 수 없으면 화면을 끄지 않는다.
        /// </summary>
        private bool EnsureSessionNotification()
        {
            if (sessionNotifyRegistered) return true;
            if (Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION))
            {
                sessionNotifyRegistered = true;
                Log.Write("session notification registered");
                return true;
            }
            Log.Write("WTSRegisterSessionNotification 실패 err=" + Marshal.GetLastWin32Error() +
                      " (잠금 확인 불가 - 잠금 사용 시 화면을 끄지 않음, 다음 핫키 때 재시도)");
            return false;
        }

        // ---------------- 핫키 ----------------

        private void RegisterTheHotkey()
        {
            uint mods, vk;
            if (!ParseHotkey(cfg.Hotkey, out mods, out vk))
            {
                Log.Write("hotkey parse failed: " + cfg.Hotkey + " -> fallback Ctrl+Shift+Alt+L");
                mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_ALT;
                vk = (uint)Keys.L;
            }
            if (Native.RegisterHotKey(Handle, HOTKEY_ID, mods | Native.MOD_NOREPEAT, vk))
            {
                Log.Write("hotkey registered: " + cfg.Hotkey);
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Log.Write("RegisterHotKey FAILED err=" + err + " hotkey=" + cfg.Hotkey);
                // 로그온 시 모달 창이 뜨면 "조용한 시작 프로그램"이 아니게 된다. 풍선으로만 알린다.
                Notify("핫키 " + cfg.Hotkey + " 등록 실패 (오류 " + err + ").\n" +
                       "다른 프로그램이 같은 조합을 쓰고 있을 수 있습니다.\n" +
                       "설정에서 Hotkey 를 바꾸고 \"설정 다시 읽기\"를 하세요.", ToolTipIcon.Warning);
            }
        }

        private static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string keyPart = null;
            foreach (string p0 in s.Split('+'))
            {
                string p = p0.Trim();
                if (p.Length == 0) continue;
                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= Native.MOD_CONTROL; break;
                    case "shift": mods |= Native.MOD_SHIFT; break;
                    case "alt": mods |= Native.MOD_ALT; break;
                    case "win":
                    case "windows": mods |= Native.MOD_WIN; break;
                    default: keyPart = p; break;
                }
            }
            if (keyPart == null || mods == 0) return false;
            // 숫자 한 글자는 숫자 키(D0~D9)로 본다. Enum.Parse 는 "1" 을 값 1(= 마우스 왼쪽 버튼)로 해석한다.
            if (keyPart.Length == 1 && keyPart[0] >= '0' && keyPart[0] <= '9') keyPart = "D" + keyPart;
            // 그 밖의 숫자 문자열은 키 이름이 아니라 내부 값으로 해석되므로 받지 않는다.
            int numeric;
            if (int.TryParse(keyPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric)) return false;
            try
            {
                Keys k = (Keys)Enum.Parse(typeof(Keys), keyPart, true);
                vk = (uint)k;
                return vk != 0;
            }
            catch { return false; }
        }

        // ---------------- 본 동작 ----------------

        private void RequestBlank()
        {
            if (phase != PhIdle) { Log.Write("already in progress"); return; }
            cfg = Config.Load(iniPath);
            BuildWatchKeys();
            Log.Write("=== hotkey (lockFirst=" + cfg.LockFirst + ") ===");

            requestStart = Environment.TickCount;
            phase = PhWaitRelease;
            phaseStart = requestStart;
            waitTimer.Start();
        }

        /// <summary>핫키를 구성하는 키들의 가상키 코드. 이것들이 모두 떨어지면 손을 뗀 것이다.</summary>
        private void BuildWatchKeys()
        {
            watchKeys.Clear();
            uint mods, vk;
            if (!ParseHotkey(cfg.Hotkey, out mods, out vk))
            {
                mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_ALT;
                vk = (uint)Keys.L;
            }
            if ((mods & Native.MOD_CONTROL) != 0) watchKeys.Add(Native.VK_CONTROL);
            if ((mods & Native.MOD_SHIFT) != 0) watchKeys.Add(Native.VK_SHIFT);
            if ((mods & Native.MOD_ALT) != 0) watchKeys.Add(Native.VK_MENU);
            if ((mods & Native.MOD_WIN) != 0) { watchKeys.Add(Native.VK_LWIN); watchKeys.Add(Native.VK_RWIN); }
            watchKeys.Add((int)vk);
        }

        private bool AllHotkeysReleased()
        {
            foreach (int k in watchKeys)
                if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return false;
            return true;
        }

        // 핫키에서 손을 뗐는지만 확인하고 진행한다. 다른 입력(마우스 등)은 기다리지 않는다.
        private void WaitTick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;

            switch (phase)
            {
                case PhIdle:
                    waitTimer.Stop();
                    return;

                case PhWaitRelease:
                    if (!AllHotkeysReleased() && now - phaseStart < cfg.MaxKeyWaitMs) return;
                    phase = PhGrace;
                    phaseStart = now;
                    return;

                case PhGrace:
                    if (now - phaseStart < cfg.KeyReleaseGraceMs) return;
                    if (cfg.LockFirst)
                    {
                        if (sessionLocked)
                        {
                            // 이미 잠겨 있다(예: 잠근 뒤 스크립트가 -blank 호출). 잠금 통지는 다시 오지 않으므로 바로 안정화로.
                            Log.Write("이미 잠겨 있음 - 잠금 생략");
                            phase = PhLockSettle;
                            phaseStart = now;
                            return;
                        }
                        // 잠금을 확인할 수단이 없으면 잠그기 전에 멈춘다. 잠긴 줄 알고 자리를 비우는 일을 막는다.
                        if (!EnsureSessionNotification())
                        {
                            AbortBlank("잠금을 확인할 수 없어 화면을 끄지 않았습니다.\nWin+L 로 직접 잠가 주세요.");
                            return;
                        }
                        lockConfirmed = false;
                        if (!Native.LockWorkStation())
                        {
                            Log.Write("LockWorkStation failed err=" + Marshal.GetLastWin32Error());
                            AbortBlank("잠금에 실패해서 화면을 끄지 않았습니다.\nWin+L 로 직접 잠가 주세요.");
                            return;
                        }
                        Log.Write("LockWorkStation() 호출 (" + (now - requestStart) + "ms)");
                        phase = PhWaitLock;
                        phaseStart = now;
                        return;
                    }
                    BlankNow(now);
                    return;

                case PhWaitLock:
                    // 잠금이 실제로 걸린 뒤에만 다음 단계로. 상한 안에 통지가 안 오면 화면을 끄지 않는다.
                    // (잠금이 늦게라도 걸렸다면 잠금화면이 켜진 채 남을 뿐이고, Windows 가 유휴 시간 뒤에 끈다)
                    if (!lockConfirmed && now - phaseStart < cfg.LockWaitMs) return;
                    if (!lockConfirmed)
                    {
                        AbortBlank("잠금이 확인되지 않아 화면을 끄지 않았습니다.\n잠금 상태를 확인해 주세요.");
                        return;
                    }
                    Log.Write("잠금 확인됨 (" + (now - requestStart) + "ms)");
                    phase = PhLockSettle;
                    phaseStart = now;
                    return;

                case PhLockSettle:
                    if (now - phaseStart < cfg.LockSettleMs) return;
                    BlankNow(now);   // 잠금 상태는 BlankNow 가 끄기 직전에 다시 확인한다
                    return;
            }
        }

        /// <summary>화면을 끄지 않고 멈춘다. 잠금 사용 중인데 잠금을 확인하지 못했을 때만 쓴다.</summary>
        private void AbortBlank(string message)
        {
            phase = PhIdle;
            waitTimer.Stop();
            Log.Write("소등 취소(잠금 미확인): " + message.Replace("\n", " "));
            Notify(message, ToolTipIcon.Warning);
        }

        private void BlankNow(int now)
        {
            bool requireLock = cfg.LockFirst;
            // 잠금 사용인데 그 사이 잠금이 풀렸다면 끄지 않는다(안정화 대기 중 해제 등). 사용자에게 알리기 위한 확인이고,
            // 마지막 판정은 소등 메시지를 실제로 처리하는 WndProc 에서 한 번 더 한다.
            if (requireLock && !sessionLocked)
            {
                AbortBlank("잠금이 풀려서 화면을 끄지 않았습니다.");
                return;
            }
            phase = PhIdle;
            waitTimer.Stop();
            Log.Write("blanking once (" + (now - requestStart) + "ms after hotkey)");

            int t0 = Environment.TickCount;
            monitorOffNeedsLock = requireLock;
            monitorOffSkipped = false;
            try { Native.MonitorOffOwn(Handle); }
            catch (Exception ex) { Log.Write("MonitorOff(own) failed: " + ex.Message); }
            finally { monitorOffNeedsLock = false; }
            if (monitorOffSkipped)
            {
                Log.Write("소등 취소(잠금 미확인): 소등 직전에 잠금이 풀림");
                Notify("잠금이 풀려서 화면을 끄지 않았습니다.", ToolTipIcon.Warning);
                return;
            }

            // 잠금 사용 시에는 자기 창 소등만 한다. 보조 브로드캐스트는 다른 스레드에서 오래 걸릴 수 있어
            // 그 사이 잠금이 풀려도 멈출 수 없기 때문이다(잠긴 줄 알았는데 열린 채 꺼지는 상태를 만들지 않는다).
            // 잠금을 쓰지 않을 때만 v3 와 같이 브로드캐스트를 보조로 보낸다.
            if (requireLock)
            {
                Log.Write("monitor off sent (" + (Environment.TickCount - t0) + "ms, own window) - hands off from here");
                return;
            }
            var t = new Thread(delegate()
            {
                try { Native.MonitorOffBroadcast(); }
                catch (Exception ex) { Log.Write("MonitorOff(broadcast) failed: " + ex.Message); }
                Log.Write("monitor off sent (" + (Environment.TickCount - t0) + "ms) - hands off from here");
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- 메시지 ----------------

        protected override void WndProc(ref Message m)
        {
            // 자기 창 소등의 마지막 관문. 검사와 실제 처리(base.WndProc) 사이에 다른 코드(로그 잠금 등)를 두지 않는다 —
            // 그래야 잠금 해제 통지가 그 틈에 처리되는 일이 없다.
            if ((uint)m.Msg == Native.WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == Native.SC_MONITORPOWER
                && monitorOffNeedsLock && !sessionLocked)
            {
                monitorOffSkipped = true;
                return;
            }
            if ((uint)m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { RequestBlank(); return; }
            if ((uint)m.Msg == WM_APP_BLANK) { RequestBlank(); return; }
            if ((uint)m.Msg == Native.WM_WTSSESSION_CHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == Native.WTS_SESSION_LOCK)
                {
                    lockConfirmed = true;
                    sessionLocked = true;
                    if (phase != PhIdle) Log.Write("session LOCK 통지 수신");
                }
                else if (ev == Native.WTS_SESSION_UNLOCK)
                {
                    sessionLocked = false;
                    Log.Write("session UNLOCK");
                    // 잠금 사용으로 진행 중이던 요청은 여기서 끝낸다. 사람이 돌아와 잠금을 풀었다는 뜻이다.
                    if (phase != PhIdle && cfg.LockFirst)
                        AbortBlank("잠금이 풀려서 화면을 끄지 않았습니다.");
                }
            }
            if ((uint)m.Msg == WM_APP_QUIT) { Log.Write("quit requested"); Close(); return; }
            if ((uint)m.Msg == WM_APP_ALREADY)
            {
                Notify("이미 실행 중입니다. 단축키 " + cfg.Hotkey + " 로 동작합니다.", ToolTipIcon.Info);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log.Write("closing");
            phase = PhIdle;
            try
            {
                waitTimer.Stop();
                Native.UnregisterHotKey(Handle, HOTKEY_ID);
                Native.WTSUnRegisterSessionNotification(Handle);
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            }
            catch { }
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        public const string Version = "3.1";

        [STAThread]
        private static void Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            Log.Path = Path.Combine(dir, "MonitorSaver.log");

            bool wantBlank = false;
            bool wantQuit = false;
            string autoStartArg = null;
            int watchdogPid = -1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("-blank", StringComparison.OrdinalIgnoreCase) || a.Equals("/blank", StringComparison.OrdinalIgnoreCase))
                    wantBlank = true;
                else if (a.Equals("-quit", StringComparison.OrdinalIgnoreCase) || a.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    wantQuit = true;
                else if (a.Equals("-watchdog", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out watchdogPid);
                else if (a.Equals("-autostart", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string v = args[i + 1].ToLowerInvariant();
                    if (v == "on" || v == "off") autoStartArg = v;
                }
            }

            if (watchdogPid > 0) { RunWatchdog(dir, watchdogPid); return; }

            if (autoStartArg != null)
            {
                bool want = (autoStartArg == "on");
                bool now = MainForm.SetAutoStart(want);
                Log.Write("-autostart " + autoStartArg + " -> " + (now ? "등록됨" : "해제됨"));
                return;
            }

            if (wantQuit)
            {
                IntPtr h = Native.FindWindow(null, MainForm.WndTitle);
                if (h != IntPtr.Zero) Native.PostMessage(h, MainForm.WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
                else Log.Write("-quit: 실행 중인 인스턴스 없음");
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\MonitorSaver_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 로그온 시 중복 실행되더라도 모달 창을 띄우지 않는다. 조용히 넘긴다.
                    IntPtr hwnd = Native.FindWindow(null, MainForm.WndTitle);
                    if (hwnd != IntPtr.Zero)
                        Native.PostMessage(hwnd, wantBlank ? MainForm.WM_APP_BLANK : MainForm.WM_APP_ALREADY,
                            IntPtr.Zero, IntPtr.Zero);
                    Log.Write("second instance -> handed to running instance (blank=" + wantBlank + ")");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm(dir);
                if (wantBlank) Native.PostMessage(form.Handle, MainForm.WM_APP_BLANK, IntPtr.Zero, IntPtr.Zero);
                Application.Run(form);
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>
        /// 본체가 사라진 "이유"를 남긴다(AutoRestart=1 이면 비정상 종료 시 다시 띄운다). 화면이나 세션은 절대 건드리지 않는다.
        /// 외부 강제 종료(작업 관리자 등)는 본체 내부 핸들러가 돌지 않으므로 여기서만 잡을 수 있다.
        /// </summary>
        private static void RunWatchdog(string dir, int pid)
        {
            int myPid = Process.GetCurrentProcess().Id;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    Log.Write("WATCHDOG(" + myPid + "): OpenProcess 실패 err=" + Marshal.GetLastWin32Error() +
                              " (pid=" + pid + " 이미 없음?)");
                    return;
                }

                DateTime since = DateTime.Now;
                Log.Write("WATCHDOG(" + myPid + "): watching pid=" + pid);

                Native.WaitForSingleObject(h, Native.INFINITE);

                uint raw;
                bool got = Native.GetExitCodeProcess(h, out raw);
                int code = unchecked((int)raw);

                TimeSpan lived = DateTime.Now - since;
                Log.Write("WATCHDOG(" + myPid + "): >>> pid=" + pid + " 종료됨 <<<  " +
                          (got ? "exitCode=" + code + " (0x" + raw.ToString("X8") + ")"
                               : "exitCode 읽기 실패 err=" + Marshal.GetLastWin32Error()) + "  " +
                          "감시시간=" + ((int)lived.TotalMinutes) + "분  " + (got ? Explain(code) : ""));

                // 0x40010004 = 로그오프/시스템 종료 때 Windows 가 프로세스를 정리하며 남기는 코드.
                // 이건 비정상 종료가 아니므로 다시 띄우지 않는다(종료 중에 재실행을 시도하게 된다).
                if (ShouldAutoRestart(dir) && got && code != 0 && raw != 0x40010004 && lived.TotalSeconds > 60)
                {
                    Log.Write("WATCHDOG(" + myPid + "): AutoRestart=1 -> 다시 실행합니다");
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath);
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        Process.Start(psi);
                    }
                    catch (Exception ex) { Log.Write("WATCHDOG: restart failed: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Write("WATCHDOG(" + myPid + ") error: " + ex); }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
        }

        private static string Explain(int code)
        {
            switch (unchecked((uint)code))
            {
                case 0x00000000: return "-> 정상 종료 (트레이 \"종료\" 또는 -quit, 스스로 끝냄)";
                case 0x00000001: return "-> ★ 외부에서 강제 종료됨 (작업 관리자 \"작업 끝내기\" / taskkill /F)";
                case 0xFFFFFFFF: return "-> ★ 외부에서 강제 종료됨 (PowerShell Stop-Process -Force 등)";
                case 0xC0000005: return "-> 액세스 위반 크래시";
                case 0xC000013A: return "-> Ctrl+C / 콘솔 종료 신호";
                case 0xC00000FD: return "-> 스택 오버플로";
                case 0xE0434352: return "-> .NET 처리되지 않은 예외 (로그의 !! 항목 확인)";
                case 0x40010004: return "-> 시스템/디버거에 의한 종료";
                default:
                    // 종료 코드가 0xC0000000 대역이면 대개 커널 예외(크래시)다.
                    return (unchecked((uint)code) >= 0xC0000000)
                        ? "-> 크래시로 추정 (NTSTATUS 예외 코드)"
                        : "-> 알 수 없는 종료 코드. 이 값을 알려주세요.";
            }
        }

        private static bool ShouldAutoRestart(string dir)
        {
            try
            {
                string ini = Path.Combine(dir, "MonitorSaver.ini");
                if (!File.Exists(ini)) return false;
                foreach (string raw in File.ReadAllLines(ini))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim().ToLowerInvariant() != "autorestart") continue;
                    string v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    return v == "1" || v == "true";
                }
            }
            catch { }
            return false;
        }
    }
}
