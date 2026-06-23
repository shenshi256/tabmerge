using SHDocVw;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace com.yuanheyuekeji.tabmerge
{
    internal sealed class ExplorerMergeService : IDisposable
    {
        private sealed class ExplorerWindowGroup
        {
            public readonly List<long> WindowHandles = new List<long>();
            public long ActiveWindowHandle;
        }

        private readonly Timer mainWindowTimer;
        private readonly List<Timer> pendingTimers = new List<Timer>();
        private readonly HashSet<long> knownWindows = new HashSet<long>();
        private readonly HashSet<long> ignoredWindows = new HashSet<long>();
        private readonly List<ExplorerWindowGroup> windowGroups = new List<ExplorerWindowGroup>();
        private readonly Control invoker;
        private ShellWindows shellWindows;
        private ExplorerWindowGroup activeGroup;
        private bool disposed;
        private bool resettingShellWindows;
        private bool possibleDraggedGroup;

        public bool MergeEnabled { get; set; } = true;

        public ExplorerMergeService(Control invoker)
        {
            this.invoker = invoker;
            mainWindowTimer = new Timer { Interval = 1500 };
            mainWindowTimer.Tick += (sender, args) => RefreshWindowGroups();
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

            possibleDraggedGroup = IsLeftButtonDown();
            DebugLogger.Log("registered-state", "possibleDraggedGroup=" + possibleDraggedGroup + " groups=" + GetGroupsDebugText());
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
                invoker.BeginInvoke(new Action(RefreshWindowGroups));
            }
        }

        private void HandleNewWindows()
        {
            RefreshWindowGroups();
            DebugLogger.Log("scan-start", "known=" + knownWindows.Count + " ignored=" + ignoredWindows.Count + " groups=" + GetGroupsDebugText());

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                long hwnd = browser.HWND;
                if (knownWindows.Contains(hwnd))
                {
                    DebugLogger.Log("scan-skip", "hwnd=" + hwnd + " known=True ignored=" + ignoredWindows.Contains(hwnd));
                    ReleaseCom(browser);
                    continue;
                }

                if (ignoredWindows.Contains(hwnd))
                {
                    string pendingPath = GetFolderPath(browser);
                    if (string.IsNullOrEmpty(pendingPath) || !IsFileSystemPath(pendingPath))
                    {
                        DebugLogger.Log("scan-pending", "hwnd=" + hwnd + " path=" + pendingPath);
                        ReleaseCom(browser);
                        continue;
                    }

                    ignoredWindows.Remove(hwnd);
                    DebugLogger.Log("scan-resume", "hwnd=" + hwnd + " path=" + pendingPath);
                }

                DebugLogger.Log("scan-new", "hwnd=" + hwnd);
                knownWindows.Add(hwnd);

                if (!HandleNewWindow(browser, false))
                {
                    knownWindows.Remove(hwnd);
                }
            }
        }

        private bool HandleNewWindow(InternetExplorer browser, bool fromPending)
        {
            try
            {
                if (!IsExplorerFolderWindow(browser))
                {
                    ReleaseCom(browser);
                    return true;
                }

                long hwnd = browser.HWND;
                string target = GetFolderPath(browser);
                DebugLogger.Log("merge-target", target);

                if (string.IsNullOrEmpty(target) || !IsFileSystemPath(target))
                {
                    ignoredWindows.Add(hwnd);
                    knownWindows.Remove(hwnd);
                    DebugLogger.Log("merge-ignore", "invalid hwnd=" + hwnd);
                    ReleaseCom(browser);
                    return true;
                }

                if (!fromPending)
                {
                    possibleDraggedGroup = false;
                    MoveWindowToNewActiveGroup(hwnd);
                    DebugLogger.Log("group-active", "detached hwnd=" + hwnd + " groups=" + GetGroupsDebugText());
                    ReleaseCom(browser);
                    return true;
                }

                ExplorerWindowGroup targetGroup = activeGroup;
                DebugLogger.Log("merge-state", "hwnd=" + hwnd + " target=" + target + " active=" + GetGroupDebugText(targetGroup));
                InternetExplorer targetBrowser = GetMergeTargetBrowser(targetGroup, hwnd);
                if (targetBrowser == null)
                {
                    AddWindowToNewActiveGroup(hwnd);
                    DebugLogger.Log("merge-no-target", "new group hwnd=" + hwnd + " groups=" + GetGroupsDebugText());
                    ReleaseCom(browser);
                    return true;
                }

                IntPtr newWindowHwnd = new IntPtr(hwnd);
                ShowWindow(newWindowHwnd, SW_HIDE);
                DebugLogger.Log("hide", "hwnd=" + newWindowHwnd);

                if (!OpenPathInNewTab(targetBrowser, target))
                {
                    ShowWindow(newWindowHwnd, SW_SHOW);
                    SetForegroundWindow(newWindowHwnd);
                    DebugLogger.Log("hide-restore", "hwnd=" + newWindowHwnd);
                    ReleaseCom(targetBrowser);
                    ReleaseCom(browser);
                    return false;
                }

                long targetHwnd = targetBrowser.HWND;
                targetGroup.ActiveWindowHandle = targetHwnd;
                DebugLogger.Log("open-tab", "source=" + hwnd + " target=" + target + " targetHwnd=" + targetHwnd);

                ReleaseCom(targetBrowser);
                CloseWindowDelayed(browser, target, targetHwnd);
                return true;
            }
            catch
            {
                ReleaseCom(browser);
                return false;
            }
        }

        private static bool OpenPathInNewTab(InternetExplorer browser, string path)
        {
            if (browser == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                BringWindowToFront(browser);
                System.Threading.Thread.Sleep(120);
                SendKeys.SendWait("^(t)");
                System.Threading.Thread.Sleep(300);
                SendKeys.SendWait("^(l)");
                System.Threading.Thread.Sleep(100);
                Clipboard.SetText(path);
                DebugLogger.Log("open-tab-input", "path=" + path);
                SendKeys.SendWait("^(v)");
                System.Threading.Thread.Sleep(80);
                SendKeys.SendWait("{ENTER}");
                return true;
            }
            catch
            {
                return false;
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

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;
        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

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

        private void CloseWindowDelayed(InternetExplorer browser, string target, long targetHwnd)
        {
            if (browser == null)
            {
                return;
            }

            IntPtr hwnd = new IntPtr(browser.HWND);
            Timer timer = new Timer { Interval = 2000 };
            pendingTimers.Add(timer);
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                pendingTimers.Remove(timer);
                timer.Dispose();

                try
                {
                    if (!disposed && IsWindow(hwnd) && hwnd.ToInt64() == targetHwnd)
                    {
                        DebugLogger.Log("close-skip", "source equals target hwnd=" + hwnd);
                    }
                    else if (!disposed && IsWindow(hwnd))
                    {
                        bool confirmed = IsTargetOpenInWindow(target, targetHwnd);
                        DebugLogger.Log("close", "hwnd=" + hwnd + " targetHwnd=" + targetHwnd + " confirmed=" + confirmed);
                        PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
                        System.Threading.Thread.Sleep(100);
                        BringWindowToFrontByHandle(targetHwnd);
                    }
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(browser);
                    RefreshWindowGroups();
                }
            };
            timer.Start();
        }

        private bool IsTargetOpenInWindow(string target, long targetHwnd)
        {
            if (string.IsNullOrWhiteSpace(target) || targetHwnd == 0)
            {
                return false;
            }

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                try
                {
                    long hwnd = browser.HWND;
                    if (hwnd != targetHwnd)
                    {
                        continue;
                    }

                    string path = GetFolderPath(browser);
                    bool matched = IsSameFileSystemPath(path, target);
                    DebugLogger.Log("confirm", "target=" + target + " hwnd=" + hwnd + " path=" + path + " matched=" + matched);
                    return matched;
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(browser);
                }
            }

            return false;
        }

        private static bool IsSameFileSystemPath(string left, string right)
        {
            left = NormalizeFileSystemPath(left);
            right = NormalizeFileSystemPath(right);
            return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFileSystemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Trim();
            try
            {
                Uri uri;
                if (Uri.TryCreate(path, UriKind.Absolute, out uri) && uri.IsFile)
                {
                    path = uri.LocalPath;
                }
            }
            catch
            {
            }

            return path.TrimEnd('\\', '/');
        }

        private void InitializeExistingWindows()
        {
            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                try
                {
                    long hwnd = browser.HWND;
                    knownWindows.Add(hwnd);
                    string path = GetFolderPath(browser);
                    if (IsFileSystemPath(path))
                    {
                        AddWindowToNewActiveGroup(hwnd);
                        DebugLogger.Log("init", "group hwnd=" + hwnd + " path=" + path);
                    }
                }
                finally
                {
                    ReleaseCom(browser);
                }
            }

            UpdateActiveGroupFromForeground();
            if (activeGroup == null)
            {
                DebugLogger.Log("init", "no active group");
            }
        }

        private void RefreshWindowGroups()
        {
            RemoveClosedWindowsFromTrackingSets();
            RemoveClosedWindowsFromGroups();
            UpdateActiveGroupFromForeground();
            ProcessPendingIgnoredWindows();
        }

        private void ProcessPendingIgnoredWindows()
        {
            if (ignoredWindows.Count == 0)
            {
                return;
            }

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                bool handled = false;
                try
                {
                    long hwnd = browser.HWND;
                    if (!ignoredWindows.Contains(hwnd))
                    {
                        continue;
                    }

                    string path = GetFolderPath(browser);
                    if (string.IsNullOrEmpty(path) || !IsFileSystemPath(path))
                    {
                        DebugLogger.Log("pending-ignore", "hwnd=" + hwnd + " path=" + path);
                        continue;
                    }

                    ignoredWindows.Remove(hwnd);
                    DebugLogger.Log("pending-resume", "hwnd=" + hwnd + " path=" + path);

                    if (!knownWindows.Contains(hwnd))
                    {
                        knownWindows.Add(hwnd);
                    }

                    handled = true;
                    if (!HandleNewWindow(browser, true))
                    {
                        knownWindows.Remove(hwnd);
                    }
                }
                catch
                {
                }
                finally
                {
                    if (!handled)
                    {
                        ReleaseCom(browser);
                    }
                }
            }
        }

        private void RemoveClosedWindowsFromTrackingSets()
        {
            foreach (long hwnd in new List<long>(knownWindows))
            {
                if (!IsWindow(new IntPtr(hwnd)))
                {
                    knownWindows.Remove(hwnd);
                }
            }

            foreach (long hwnd in new List<long>(ignoredWindows))
            {
                if (!IsWindow(new IntPtr(hwnd)))
                {
                    ignoredWindows.Remove(hwnd);
                }
            }
        }

        private void RemoveClosedWindowsFromGroups()
        {
            for (int i = windowGroups.Count - 1; i >= 0; i--)
            {
                ExplorerWindowGroup group = windowGroups[i];
                for (int j = group.WindowHandles.Count - 1; j >= 0; j--)
                {
                    long hwnd = group.WindowHandles[j];
                    if (!IsWindow(new IntPtr(hwnd)))
                    {
                        group.WindowHandles.RemoveAt(j);
                    }
                }

                if (group.WindowHandles.Count == 0)
                {
                    if (activeGroup == group)
                    {
                        activeGroup = null;
                    }
                    windowGroups.RemoveAt(i);
                }
                else if (!group.WindowHandles.Contains(group.ActiveWindowHandle))
                {
                    group.ActiveWindowHandle = group.WindowHandles[0];
                }
            }
        }

        private void UpdateActiveGroupFromForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return;
            }

            long foregroundHwnd = foreground.ToInt64();
            ExplorerWindowGroup group = FindGroupContaining(foregroundHwnd);
            if (group != null)
            {
                activeGroup = group;
                group.ActiveWindowHandle = foregroundHwnd;
                DebugLogger.Log("group-active", "foreground hwnd=" + foregroundHwnd);
                return;
            }

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                try
                {
                    if (!IsForegroundWindow(browser))
                    {
                        continue;
                    }

                    long hwnd = browser.HWND;
                    if (ignoredWindows.Contains(hwnd))
                    {
                        DebugLogger.Log("group-skip", "foreground ignored hwnd=" + hwnd + " path=" + GetFolderPath(browser));
                        return;
                    }

                    string path = GetFolderPath(browser);
                    if (knownWindows.Contains(hwnd) && IsFileSystemPath(path))
                    {
                        AddWindowToNewActiveGroup(hwnd);
                        DebugLogger.Log("group-active", "foreground new hwnd=" + hwnd);
                    }
                    return;
                }
                finally
                {
                    ReleaseCom(browser);
                }
            }
        }

        private InternetExplorer GetMergeTargetBrowser(ExplorerWindowGroup group, long excludedHwnd)
        {
            if (group == null)
            {
                DebugLogger.Log("target-none", "active group is null");
                return null;
            }

            DebugLogger.Log("target-find", "group=" + GetGroupDebugText(group) + " excluded=" + excludedHwnd);
            InternetExplorer browser = FindBrowserByHandle(group.ActiveWindowHandle, excludedHwnd);
            if (browser != null)
            {
                DebugLogger.Log("target-found", "active hwnd=" + group.ActiveWindowHandle);
                return browser;
            }

            foreach (long hwnd in group.WindowHandles.ToArray())
            {
                browser = FindBrowserByHandle(hwnd, excludedHwnd);
                if (browser != null)
                {
                    group.ActiveWindowHandle = hwnd;
                    DebugLogger.Log("target-found", "fallback hwnd=" + hwnd);
                    return browser;
                }
            }

            DebugLogger.Log("target-miss", "group=" + GetGroupDebugText(group) + " excluded=" + excludedHwnd);
            return null;
        }

        private InternetExplorer FindBrowserByHandle(long handle, long excludedHwnd)
        {
            if (handle == 0 || handle == excludedHwnd)
            {
                return null;
            }

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                long hwnd = 0;
                try
                {
                    hwnd = browser.HWND;
                    if (hwnd == handle && hwnd != excludedHwnd)
                    {
                        return browser;
                    }
                }
                catch
                {
                }

                ReleaseCom(browser);
            }

            return null;
        }

        private ExplorerWindowGroup FindGroupContaining(long hwnd)
        {
            foreach (ExplorerWindowGroup group in windowGroups)
            {
                if (group.WindowHandles.Contains(hwnd))
                {
                    return group;
                }
            }

            return null;
        }

        private void AddWindowToNewActiveGroup(long hwnd)
        {
            RemoveWindowFromGroups(hwnd);

            ExplorerWindowGroup group = new ExplorerWindowGroup();
            group.WindowHandles.Add(hwnd);
            group.ActiveWindowHandle = hwnd;
            windowGroups.Add(group);
            activeGroup = group;
        }

        private void MoveWindowToNewActiveGroup(long hwnd)
        {
            AddWindowToNewActiveGroup(hwnd);
        }

        private void RemoveWindowFromGroups(long hwnd)
        {
            for (int i = windowGroups.Count - 1; i >= 0; i--)
            {
                ExplorerWindowGroup group = windowGroups[i];
                group.WindowHandles.Remove(hwnd);
                if (group.WindowHandles.Count == 0)
                {
                    if (activeGroup == group)
                    {
                        activeGroup = null;
                    }
                    windowGroups.RemoveAt(i);
                }
                else if (group.ActiveWindowHandle == hwnd)
                {
                    group.ActiveWindowHandle = group.WindowHandles[0];
                }
            }
        }

        private string GetGroupsDebugText()
        {
            if (windowGroups.Count == 0)
            {
                return "[]";
            }

            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < windowGroups.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("; ");
                }

                ExplorerWindowGroup group = windowGroups[i];
                sb.Append(activeGroup == group ? "*" : string.Empty);
                sb.Append(GetGroupDebugText(group));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string GetGroupDebugText(ExplorerWindowGroup group)
        {
            if (group == null)
            {
                return "null";
            }

            return "active=" + group.ActiveWindowHandle + ", handles=" + string.Join(",", group.WindowHandles.ToArray());
        }

        private void BringWindowToFrontByHandle(long hwnd)
        {
            if (hwnd == 0 || !IsWindow(new IntPtr(hwnd)))
            {
                return;
            }

            InternetExplorer browser = FindBrowserByHandle(hwnd, 0);
            try
            {
                BringWindowToFront(browser);
            }
            finally
            {
                ReleaseCom(browser);
            }
        }

        private IEnumerable<InternetExplorer> EnumerateExplorerWindows()
        {
            var browsers = new List<InternetExplorer>();
            if (shellWindows == null)
            {
                return browsers;
            }

            try
            {
                foreach (object item in shellWindows)
                {
                    InternetExplorer browser = item as InternetExplorer;
                    if (IsExplorerFolderWindow(browser))
                    {
                        browsers.Add(browser);
                    }
                    else
                    {
                        ReleaseCom(browser);
                    }
                }
            }
            catch (COMException ex)
            {
                DebugLogger.Log("shell-rpc", "enumerate failed hresult=0x" + ex.HResult.ToString("X8"));
                ReleaseComList(browsers);
                browsers.Clear();
                if (!resettingShellWindows)
                {
                    ResetShellWindows();
                }
            }
            catch (InvalidComObjectException ex)
            {
                DebugLogger.Log("shell-rpc", "invalid com object " + ex.Message);
                ReleaseComList(browsers);
                browsers.Clear();
                if (!resettingShellWindows)
                {
                    ResetShellWindows();
                }
            }

            return browsers;
        }

        private void ResetShellWindows()
        {
            if (disposed || resettingShellWindows)
            {
                return;
            }

            resettingShellWindows = true;
            try
            {
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

                ReleaseCom(shellWindows);
                shellWindows = new ShellWindows();
                shellWindows.WindowRegistered += ShellWindows_WindowRegistered;
                shellWindows.WindowRevoked += ShellWindows_WindowRevoked;
                knownWindows.Clear();
                ignoredWindows.Clear();
                windowGroups.Clear();
                activeGroup = null;
                InitializeExistingWindows();
                DebugLogger.Log("shell-rpc", "shell windows reset");
            }
            catch (Exception ex)
            {
                DebugLogger.Log("shell-rpc", "reset failed " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                resettingShellWindows = false;
            }
        }

        private static void ReleaseComList(IEnumerable<InternetExplorer> browsers)
        {
            foreach (InternetExplorer browser in browsers)
            {
                ReleaseCom(browser);
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

            ReleaseCom(shellWindows);
        }

        private static bool IsForegroundWindow(InternetExplorer browser)
        {
            try
            {
                return browser != null && new IntPtr(browser.HWND) == GetForegroundWindow();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLeftButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
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
