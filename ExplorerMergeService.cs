using SHDocVw;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace com.yuanheyuekeji.tabmerge
{
    internal sealed class ExplorerMergeService : IDisposable
    {
        private readonly Timer mainWindowTimer;
        private readonly List<Timer> pendingTimers = new List<Timer>();
        private readonly HashSet<long> knownWindows = new HashSet<long>();
        private readonly Control invoker;
        private ShellWindows shellWindows;
        private InternetExplorer mainBrowser;
        private bool disposed;

        public bool MergeEnabled { get; set; } = true;

        public ExplorerMergeService(Control invoker)
        {
            this.invoker = invoker;
            mainWindowTimer = new Timer { Interval = 1500 };
            mainWindowTimer.Tick += (sender, args) => RefreshMainWindow();
        }

        public void Start()
        {
            DebugLogger.Log("start", "service starting");
            shellWindows = new ShellWindows();
            shellWindows.WindowRegistered += ShellWindows_WindowRegistered;
            shellWindows.WindowRevoked += ShellWindows_WindowRevoked;

            InitializeExistingWindows();
            mainWindowTimer.Start();
            DebugLogger.Log("start", "service started");
        }

        private void ShellWindows_WindowRegistered(int lCookie)
        {
            DebugLogger.Log("registered", "cookie=" + lCookie);
            if (!MergeEnabled || disposed)
            {
                return;
            }

            invoker.BeginInvoke(new Action(HandleRegisteredWindowsDelayed));
        }

        private void HandleRegisteredWindowsDelayed()
        {
            Timer timer = new Timer { Interval = 600 };
            pendingTimers.Add(timer);
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                pendingTimers.Remove(timer);
                timer.Dispose();
                HandleNewWindows();
            };
            timer.Start();
        }

        private void ShellWindows_WindowRevoked(int lCookie)
        {
            DebugLogger.Log("revoked", "cookie=" + lCookie);
            if (!disposed)
            {
                invoker.BeginInvoke(new Action(RefreshMainWindow));
            }
        }

        private void HandleNewWindows()
        {
            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                long hwnd = browser.HWND;
                if (knownWindows.Contains(hwnd))
                {
                    ReleaseCom(browser);
                    continue;
                }

                // Add BEFORE merge so SendKeys message pump doesn't cause duplicate processing
                knownWindows.Add(hwnd);

                if (!MergeNewWindow(browser))
                {
                    // Merge failed (e.g. invalid path) - remove so retry can pick it up
                    knownWindows.Remove(hwnd);
                }
            }
        }

        private bool MergeNewWindow(InternetExplorer browser)
        {
            try
            {
                if (!IsExplorerFolderWindow(browser))
                {
                    ReleaseCom(browser);
                    return true;
                }

                string target = GetFolderPath(browser);
                DebugLogger.Log("merge-target", target);
                if (string.IsNullOrEmpty(target) || !IsFileSystemPath(target))
                {
                    DebugLogger.Log("merge-wait", "invalid");
                    ReleaseCom(browser);
                    ScheduleRetry();
                    return false;
                }

                if (!IsExplorerFolderWindow(mainBrowser))
                {
                    ReleaseCom(mainBrowser);
                    mainBrowser = browser;
                    return true;
                }

                if (IsSameWindow(browser, mainBrowser))
                {
                    ReleaseCom(browser);
                    return true;
                }

                OpenPathInNewTab(mainBrowser, target);
                DebugLogger.Log("open-tab", "target=" + target);

                CloseWindowDelayed(browser);
                return true;
            }
            catch
            {
                ReleaseCom(browser);
                return false;
            }
        }

        private void ScheduleRetry()
        {
            Timer timer = new Timer { Interval = 400 };
            pendingTimers.Add(timer);
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                pendingTimers.Remove(timer);
                timer.Dispose();
                HandleNewWindows();
            };
            timer.Start();
        }

        private static void OpenPathInNewTab(InternetExplorer browser, string path)
        {
            if (browser == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            BringWindowToFront(browser);
            System.Threading.Thread.Sleep(100);

            // Ctrl+T to create a new tab
            SendKeys.SendWait("^(t)");
            System.Threading.Thread.Sleep(400);

            // Ctrl+L to explicitly focus the address bar
            SendKeys.SendWait("^(l)");
            System.Threading.Thread.Sleep(150);

            // Now the focused element should be the address bar Edit control
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    ValuePattern vp = null;
                    try { vp = focused.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern; }
                    catch { }

                    if (vp != null)
                    {
                        DebugLogger.Log("uia", "SetValue=" + path);
                        vp.SetValue(path);
                        System.Threading.Thread.Sleep(50);
                        SendKeys.SendWait("{ENTER}");
                        DebugLogger.Log("uia", "done");
                        return;
                    }
                    else
                    {
                        DebugLogger.Log("uia", "no ValuePattern, type=" + focused.Current.ControlType.ProgrammaticName);
                    }
                }
            }
            catch
            {
                DebugLogger.Log("uia", "exception");
            }
        }

        private static void BringWindowToFront(InternetExplorer browser)
        {
            try
            {
                if (browser == null)
                {
                    return;
                }

                IntPtr hwnd = new IntPtr(browser.HWND);
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                {
                    return;
                }

                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }

                SetForegroundWindow(hwnd);
            }
            catch
            {
            }
        }

        private const int SW_RESTORE = 9;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private static bool IsFileSystemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            path = path.Trim();
            if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            Uri uri;
            if (Uri.TryCreate(path, UriKind.Absolute, out uri))
            {
                return uri.IsFile && !string.IsNullOrEmpty(uri.LocalPath);
            }

            return path.StartsWith(@"\\", StringComparison.Ordinal) ||
                   (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }

        private void CloseWindowDelayed(InternetExplorer browser)
        {
            if (browser == null)
            {
                return;
            }

            IntPtr hwnd = new IntPtr(browser.HWND);
            Timer timer = new Timer { Interval = 1200 };
            pendingTimers.Add(timer);
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                pendingTimers.Remove(timer);
                timer.Dispose();

                try
                {
                    if (!disposed && IsWindow(hwnd))
                    {
                        DebugLogger.Log("close", "hwnd=" + hwnd);
                        PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
                        System.Threading.Thread.Sleep(100);
                        BringWindowToFront(mainBrowser);
                    }
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(browser);
                }
            };
            timer.Start();
        }

        private void InitializeExistingWindows()
        {
            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                knownWindows.Add(browser.HWND);
                if (mainBrowser == null)
                {
                    mainBrowser = browser;
                    DebugLogger.Log("init", "main hwnd=" + browser.HWND + " path=" + GetFolderPath(browser));
                }
                else
                {
                    ReleaseCom(browser);
                }
            }
            if (mainBrowser == null)
            {
                DebugLogger.Log("init", "no main window");
            }
        }

        private void RefreshMainWindow()
        {
            if (IsExplorerFolderWindow(mainBrowser))
            {
                knownWindows.Add(mainBrowser.HWND);
                return;
            }

            ReleaseCom(mainBrowser);
            mainBrowser = null;

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                if (mainBrowser == null)
                {
                    mainBrowser = browser;
                    knownWindows.Add(browser.HWND);
                    DebugLogger.Log("main-refresh", "new main hwnd=" + browser.HWND);
                }
                else
                {
                    ReleaseCom(browser);
                }
            }
        }

        private IEnumerable<InternetExplorer> EnumerateExplorerWindows()
        {
            if (shellWindows == null)
            {
                yield break;
            }

            foreach (object item in shellWindows)
            {
                InternetExplorer browser = item as InternetExplorer;
                if (IsExplorerFolderWindow(browser))
                {
                    yield return browser;
                }
                else
                {
                    ReleaseCom(browser);
                }
            }
        }

        private bool IsExplorerFolderWindow(InternetExplorer browser)
        {
            try
            {
                if (browser == null || !IsWindow(new IntPtr(browser.HWND)))
                {
                    return false;
                }

                string fullName = browser.FullName ?? string.Empty;

                if (!fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string className = GetWindowClassName(new IntPtr(browser.HWND));
                return className == "CabinetWClass" || className == "ExploreWClass";
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            disposed = true;
            mainWindowTimer.Stop();
            foreach (Timer timer in pendingTimers.ToArray())
            {
                timer.Stop();
                timer.Dispose();
            }
            pendingTimers.Clear();

            if (shellWindows != null)
            {
                try
                {
                    shellWindows.WindowRegistered -= ShellWindows_WindowRegistered;
                    shellWindows.WindowRevoked -= ShellWindows_WindowRevoked;
                }
                catch
                {
                }
            }

            ReleaseCom(mainBrowser);
            ReleaseCom(shellWindows);
        }

        private static bool IsSameWindow(InternetExplorer left, InternetExplorer right)
        {
            try
            {
                return left != null && right != null && left.HWND == right.HWND;
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string GetFolderPath(InternetExplorer browser)
        {
            try
            {
                dynamic document = browser.Document;
                string path = document?.Folder?.Self?.Path as string;
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }
            catch
            {
            }

            try
            {
                string url = browser.LocationURL ?? string.Empty;
                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(url).LocalPath;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void ReleaseCom(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
    }
}
