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
        // 一个资源管理器“窗口组”。
        // 组内保存的是顶层 Explorer 窗口句柄，而不是路径。
        // 原因是路径会随着标签页切换变化，但 HWND 才是我们识别窗口归属的稳定身份。
        private sealed class ExplorerWindowGroup
        {
            // 当前组内的 Explorer 顶层窗口句柄。
            public readonly List<long> WindowHandles = new List<long>();

            // 当前组中最近被激活/最适合作为合并目标的窗口句柄。
            public long ActiveWindowHandle;
        }

        // 周期性扫描新窗口，并刷新窗口组、前台窗口、pending 窗口等状态。
        // 不能只依赖 ShellWindows.WindowRegistered，因为外部程序打开文件夹时该事件不一定稳定触发。
        private readonly Timer mainWindowTimer;

        // 高频记录鼠标左键状态，用来辅助判断“拖出标签页”行为。
        private readonly Timer mouseStateTimer;

        // 所有延迟执行的 Timer，Dispose 时统一停止，避免退出后继续回调。
        private readonly List<Timer> pendingTimers = new List<Timer>();

        // 已被程序识别和处理过的 Explorer 顶层窗口。
        private readonly HashSet<long> knownWindows = new HashSet<long>();

        // 暂不处理的窗口，例如 Win+E 刚打开时的“此电脑”，或路径尚未稳定的新窗口。
        private readonly HashSet<long> ignoredWindows = new HashSet<long>();

        // 新窗口路径稳定检测：记录每个 HWND 最近一次看到的路径和首次看到该路径的时间。
        // 这是为了避免外部 IDE 打开目录时，ShellWindows 过早触发导致读到上一次路径。
        private readonly Dictionary<long, string> pendingStablePaths = new Dictionary<long, string>();
        private readonly Dictionary<long, DateTime> pendingStableTimes = new Dictionary<long, DateTime>();

        // 当前维护的所有窗口组。activeGroup 决定后续新窗口合并到哪里。
        private readonly List<ExplorerWindowGroup> windowGroups = new List<ExplorerWindowGroup>();
        private readonly Control invoker;
        private ShellWindows shellWindows;
        private ExplorerWindowGroup activeGroup;
        private bool disposed;

        // Shell COM 枚举异常时会重建 ShellWindows。该标记防止重建过程重入。
        private bool resettingShellWindows;

        // 启动合并或内部 Ctrl+T 过程中，Shell 可能产生临时 WindowRegistered 事件。
        // 这些事件不是用户新开的窗口，需要暂时屏蔽。
        private bool suppressRegisteredWindows;

        // 最近一次 WindowRegistered 是否可能来自“拖出标签页”。
        // true 时，新窗口会成为新的激活组，而不是被合并回原组。
        private bool possibleDraggedGroup;
        private DateTime lastLeftButtonDownTime;

        public bool MergeEnabled { get; set; } = true;

        public ExplorerMergeService(Control invoker)
        {
            this.invoker = invoker;
            /// <summary>
            /// 周期性扫描新窗口，并刷新窗口组、前台窗口、pending 窗口等状态。
            /// 不能只依赖 ShellWindows.WindowRegistered，因为外部程序打开文件夹时该事件不一定稳定触发
            /// </summary>
            mainWindowTimer = new Timer { Interval = 800 };
            mainWindowTimer.Tick += (sender, args) => HandleNewWindows();
            mouseStateTimer = new Timer { Interval = 100 };
            mouseStateTimer.Tick += (sender, args) => UpdateMouseState();
        }

        public void Start()
        {
            // 监听 ShellWindows 事件，并在启动时先把已有 Explorer 窗口合并成主组。
            DebugLogger.Log("start", "service starting");
            shellWindows = new ShellWindows();
            shellWindows.WindowRegistered += ShellWindows_WindowRegistered;
            shellWindows.WindowRevoked += ShellWindows_WindowRevoked;

            InitializeExistingWindows();
            mouseStateTimer.Start();
            mainWindowTimer.Start();
            DebugLogger.Log("start", "service started");
        }

        private void ShellWindows_WindowRegistered(int lCookie)
        {
            // Explorer 新窗口注册事件。这里不直接处理，先延迟一小段时间，
            // 等窗口从“此电脑”或空白状态导航到真实目标路径。
            DebugLogger.Log("registered", "cookie=" + lCookie);
            if (!MergeEnabled || disposed || suppressRegisteredWindows)
            {
                DebugLogger.Log("registered-skip", "suppressed=" + suppressRegisteredWindows);
                return;
            }

            UpdateMouseState();
            possibleDraggedGroup = IsRecentDragGesture();
            DebugLogger.Log("registered-state", "possibleDraggedGroup=" + possibleDraggedGroup + " groups=" + GetGroupsDebugText());
            invoker.BeginInvoke(new Action(HandleRegisteredWindowsDelayed));
        }

        private void HandleRegisteredWindowsDelayed()
        {
            // ShellWindows 注册事件触发时，Explorer 目标路径经常还没准备好。
            // 延迟扫描可以减少读到“此电脑”或上一次路径的概率。
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
        /// <summary>
        /// 新窗口扫描、pending、路径稳定等待
        /// </summary>
        private void HandleNewWindows()
        {
            if (!MergeEnabled || disposed)
            {
                return;
            }

            // 扫描当前 ShellWindows，找出不在 knownWindows 里的新 Explorer 窗口。
            // 新窗口分三类：
            // 1. “此电脑”/无效路径：放入 ignoredWindows，后续继续观察；
            // 2. 拖拽产生的新窗口：不合并，直接成为新的激活组；
            // 3. 其它新窗口：合并到当前 activeGroup。
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

                bool wasPending = ignoredWindows.Contains(hwnd);
                if (wasPending)
                {
                    // pending 窗口通常是 Win+E 初始的“此电脑”。
                    // 只有变成真实文件系统路径，并且路径稳定后，才继续处理。
                    string pendingPath = GetFolderPath(browser);
                    if (string.IsNullOrEmpty(pendingPath) || !IsFileSystemPath(pendingPath) || !IsPendingPathStable(hwnd, pendingPath))
                    {
                        DebugLogger.Log("scan-pending", "hwnd=" + hwnd + " path=" + pendingPath);
                        ReleaseCom(browser);
                        continue;
                    }

                    ignoredWindows.Remove(hwnd);
                    ClearPendingStablePath(hwnd);
                    DebugLogger.Log("scan-resume", "hwnd=" + hwnd + " path=" + pendingPath);
                }

                DebugLogger.Log("scan-new", "hwnd=" + hwnd);
                knownWindows.Add(hwnd);

                if (!HandleNewWindow(browser, wasPending))
                {
                    knownWindows.Remove(hwnd);
                }
            }
        }
        /// <summary>
        /// 合并窗口、拖拽新组判断
        /// </summary>
        private bool HandleNewWindow(InternetExplorer browser, bool fromPending)
        {
            // 处理一个确定需要处理的新 Explorer 窗口。
            // fromPending=true 表示它经历过“此电脑/路径不稳定”的等待流程，通常应该合并进当前激活组；
            // fromPending=false 且 possibleDraggedGroup=true 表示用户拖出了标签页，应创建新组。
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
                    // 例如“此电脑”的 Shell 路径不是文件系统路径，先挂起等待后续导航到真实目录。
                    ignoredWindows.Add(hwnd);
                    knownWindows.Remove(hwnd);
                    DebugLogger.Log("merge-ignore", "invalid hwnd=" + hwnd);
                    ReleaseCom(browser);
                    return true;
                }

                if (!fromPending && possibleDraggedGroup)
                {
                    // 拖拽标签页产生的新窗口不能合并回原组，否则用户刚拖出去又会被拉回去。
                    // 这里直接把它从旧组移除，并建立一个新的激活组。
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

                // 合并时先隐藏源窗口，避免用户看到闪烁。
                // 等新标签打开后再延迟关闭源窗口。
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
        /// <summary>
        /// 新标签打开方式
        /// </summary>
        private static bool OpenPathInNewTab(InternetExplorer browser, string path)
        {
            // Windows 资源管理器标签页没有稳定的公开 COM API 可直接创建并导航。
            // 这里采用最可靠的前台输入方案：激活目标窗口 -> Ctrl+T -> Ctrl+L -> 粘贴路径 -> Enter。
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
            // 源窗口不能立刻关闭：新标签创建和路径导航都是异步的。
            // 这里最多等待几轮确认；确认不到也会关闭源窗口，因为 COM 只能读到当前激活标签，
            // 不能可靠判断目标路径是否已经存在于其它未激活标签中。
            if (browser == null)
            {
                return;
            }

            IntPtr hwnd = new IntPtr(browser.HWND);
            int attempts = 0;
            Timer timer = new Timer { Interval = 800 };
            pendingTimers.Add(timer);
            timer.Tick += (sender, args) =>
            {
                attempts++;

                try
                {
                    if (disposed || !IsWindow(hwnd))
                    {
                        timer.Stop();
                        pendingTimers.Remove(timer);
                        timer.Dispose();
                        ReleaseCom(browser);
                        RefreshWindowGroups();
                        return;
                    }

                    if (hwnd.ToInt64() == targetHwnd)
                    {
                        timer.Stop();
                        pendingTimers.Remove(timer);
                        timer.Dispose();
                        DebugLogger.Log("close-skip", "source equals target hwnd=" + hwnd);
                        ReleaseCom(browser);
                        RefreshWindowGroups();
                        return;
                    }

                    bool confirmed = IsTargetOpenInWindow(target, targetHwnd);
                    DebugLogger.Log("close-check", "attempt=" + attempts + " hwnd=" + hwnd + " targetHwnd=" + targetHwnd + " confirmed=" + confirmed);
                    if (confirmed)
                    {
                        timer.Stop();
                        pendingTimers.Remove(timer);
                        timer.Dispose();
                        DebugLogger.Log("close", "hwnd=" + hwnd + " targetHwnd=" + targetHwnd + " confirmed=True");
                        PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
                        System.Threading.Thread.Sleep(100);
                        BringTargetToFrontIfStillRelevant(hwnd.ToInt64(), targetHwnd);
                        ReleaseCom(browser);
                        RefreshWindowGroups();
                        return;
                    }

                    if (attempts >= 5)
                    {
                        timer.Stop();
                        pendingTimers.Remove(timer);
                        timer.Dispose();
                        DebugLogger.Log("close", "hwnd=" + hwnd + " targetHwnd=" + targetHwnd + " confirmed=False target=" + target);
                        PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
                        System.Threading.Thread.Sleep(100);
                        BringTargetToFrontIfStillRelevant(hwnd.ToInt64(), targetHwnd);
                        ReleaseCom(browser);
                        RefreshWindowGroups();
                    }
                }
                catch
                {
                    timer.Stop();
                    pendingTimers.Remove(timer);
                    timer.Dispose();
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

        private bool IsPendingPathStable(long hwnd, string path)
        {
            // 同一个 HWND 连续保持同一个路径超过 700ms 才认为稳定。
            // 如果路径变化，就重新计时。
            string normalizedPath = NormalizeFileSystemPath(path);
            string previousPath;
            DateTime firstSeen;
            if (!pendingStablePaths.TryGetValue(hwnd, out previousPath) ||
                !string.Equals(previousPath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                !pendingStableTimes.TryGetValue(hwnd, out firstSeen))
            {
                pendingStablePaths[hwnd] = normalizedPath;
                pendingStableTimes[hwnd] = DateTime.UtcNow;
                return false;
            }

            return (DateTime.UtcNow - firstSeen).TotalMilliseconds >= 700;
        }

        private void ClearPendingStablePath(long hwnd)
        {
            pendingStablePaths.Remove(hwnd);
            pendingStableTimes.Remove(hwnd);
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
        /// <summary>
        /// 启动时合并现有窗口
        /// </summary>
        private void InitializeExistingWindows()
        {
            // 程序启动时，把已存在的 Explorer 窗口真正合并为一个主组。
            // 如果当前前台窗口是 Explorer，就优先作为主窗口；否则取扫描到的第一个窗口。
            // 其它窗口会被打开到主窗口的新标签，并延迟关闭原独立窗口。
            var windows = new List<KeyValuePair<long, string>>();
            long foregroundHwnd = GetForegroundWindow().ToInt64();

            foreach (InternetExplorer browser in EnumerateExplorerWindows())
            {
                try
                {
                    long hwnd = browser.HWND;
                    knownWindows.Add(hwnd);
                    string path = GetFolderPath(browser);
                    if (IsFileSystemPath(path))
                    {
                        windows.Add(new KeyValuePair<long, string>(hwnd, path));
                        DebugLogger.Log("init", "window hwnd=" + hwnd + " path=" + path);
                    }
                }
                finally
                {
                    ReleaseCom(browser);
                }
            }

            if (windows.Count == 0)
            {
                DebugLogger.Log("init", "no active group");
                return;
            }

            long targetHwnd = windows[0].Key;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Key == foregroundHwnd)
                {
                    targetHwnd = foregroundHwnd;
                    break;
                }
            }

            ExplorerWindowGroup group = new ExplorerWindowGroup();
            group.WindowHandles.Add(targetHwnd);
            group.ActiveWindowHandle = targetHwnd;
            windowGroups.Add(group);
            activeGroup = group;
            DebugLogger.Log("init", "target hwnd=" + targetHwnd);

            if (windows.Count == 1)
            {
                DebugLogger.Log("init", "group " + GetGroupDebugText(group));
                return;
            }

            // 启动合并过程中会发送 Ctrl+T，Explorer 可能触发新的注册事件。
            // 这些事件来自我们自己的内部操作，不能按用户新窗口处理。
            suppressRegisteredWindows = true;
            try
            {
                foreach (KeyValuePair<long, string> window in windows)
                {
                    if (window.Key == targetHwnd)
                    {
                        continue;
                    }

                    InternetExplorer targetBrowser = FindBrowserByHandle(targetHwnd, window.Key);
                    InternetExplorer sourceBrowser = FindBrowserByHandle(window.Key, targetHwnd);
                    try
                    {
                        if (targetBrowser == null || sourceBrowser == null)
                        {
                            continue;
                        }

                        ShowWindow(new IntPtr(window.Key), SW_HIDE);
                        if (OpenPathInNewTab(targetBrowser, window.Value))
                        {
                            DebugLogger.Log("init-merge", "source=" + window.Key + " target=" + window.Value + " targetHwnd=" + targetHwnd);
                            CloseWindowDelayed(sourceBrowser, window.Value, targetHwnd);
                            sourceBrowser = null;
                        }
                        else
                        {
                            ShowWindow(new IntPtr(window.Key), SW_SHOW);
                        }
                    }
                    finally
                    {
                        ReleaseCom(targetBrowser);
                        ReleaseCom(sourceBrowser);
                    }
                }
            }
            finally
            {
                suppressRegisteredWindows = false;
            }

            DebugLogger.Log("init", "group " + GetGroupDebugText(group));
        }

        private void RefreshWindowGroups()
        {
            // 周期刷新维护四件事：
            // 1. 清理已关闭窗口；
            // 2. 根据当前前台 Explorer 更新 activeGroup；
            // 3. 如果 activeGroup 被关闭但还有其它组，自动选择一个现有组兜底；
            // 4. 继续观察 pending 窗口是否已经能合并。
            RemoveClosedWindowsFromTrackingSets();
            RemoveClosedWindowsFromGroups();
            UpdateActiveGroupFromForeground();
            EnsureActiveGroupFallback();
            ProcessPendingIgnoredWindows();
        }

        private void ProcessPendingIgnoredWindows()
        {
            // ignoredWindows 不是永久忽略，而是“暂时等待”。
            // 例如 Win+E 先出现“此电脑”，用户再双击 F 盘；这里会等它变成 F:\ 并稳定后再合并。
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
                    if (string.IsNullOrEmpty(path) || !IsFileSystemPath(path) || !IsPendingPathStable(hwnd, path))
                    {
                        DebugLogger.Log("pending-ignore", "hwnd=" + hwnd + " path=" + path);
                        continue;
                    }

                    ignoredWindows.Remove(hwnd);
                    ClearPendingStablePath(hwnd);
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
                    ClearPendingStablePath(hwnd);
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

        private void EnsureActiveGroupFallback()
        {
            // activeGroup 为空通常表示原激活组刚被用户关闭。
            // 如果还有其它组存在，就选最后一个可用组作为兜底目标，
            // 避免下一次 Win+E/外部打开目录时被误判成“新组”。
            if (activeGroup != null && windowGroups.Contains(activeGroup))
            {
                return;
            }

            activeGroup = null;
            if (windowGroups.Count == 0)
            {
                return;
            }

            ExplorerWindowGroup group = windowGroups[windowGroups.Count - 1];
            activeGroup = group;
            if (!group.WindowHandles.Contains(group.ActiveWindowHandle) && group.WindowHandles.Count > 0)
            {
                group.ActiveWindowHandle = group.WindowHandles[group.WindowHandles.Count - 1];
            }

            DebugLogger.Log("group-active", "fallback " + GetGroupDebugText(group));
        }
        /// <summary>
        /// activeGroup 更新、目标窗口查找、焦点保护
        /// </summary>
        private void UpdateActiveGroupFromForeground()
        {
            // 用户点击哪个 Explorer 窗口，哪个窗口所属组就成为 activeGroup。
            // 后续 Win+E、IDE 打开目录等新窗口都会合并到这个组。
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
            // 从激活组里找一个可用窗口作为承载新标签的目标窗口。
            // excludedHwnd 是当前新窗口自身，避免把源窗口当成合并目标。
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
            // 新建独立组前，先从所有旧组移除该窗口，保证一个 HWND 只属于一个组。
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

        private void BringTargetToFrontIfStillRelevant(long sourceHwnd, long targetHwnd)
        {
            // 关闭源窗口后通常需要把目标组带回前台。
            // 但如果用户已经点击了其它窗口，就不能再抢焦点，否则会出现旧组又置顶一次。
            long foregroundHwnd = GetForegroundWindow().ToInt64();
            if (foregroundHwnd != 0 && foregroundHwnd != sourceHwnd && foregroundHwnd != targetHwnd)
            {
                DebugLogger.Log("focus-skip", "foreground=" + foregroundHwnd + " source=" + sourceHwnd + " target=" + targetHwnd);
                return;
            }

            BringWindowToFrontByHandle(targetHwnd);
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
        /// <summary>
        /// COM 枚举异常恢复、拖拽判断、路径读取
        /// </summary>
        private IEnumerable<InternetExplorer> EnumerateExplorerWindows()
        {
            // 枚举 ShellWindows 中所有真正的文件夹 Explorer 窗口。
            // 拖拽标签页或 Explorer 重启时，COM 可能抛 RPC 异常，所以这里统一兜底并重建 ShellWindows。
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
            // Explorer/Shell COM 断开后，旧的 ShellWindows 对象不能继续使用。
            // 这里重新创建 ShellWindows、重新订阅事件，并重新初始化当前窗口组。
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
                pendingStablePaths.Clear();
                pendingStableTimes.Clear();
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
            mouseStateTimer.Stop();
            mouseStateTimer.Dispose();
            mainWindowTimer.Stop();
            foreach (Timer timer in pendingTimers.ToArray())
            {
                timer.Stop();
                timer.Dispose();
            }
            pendingTimers.Clear();
            pendingStablePaths.Clear();
            pendingStableTimes.Clear();

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

        private void UpdateMouseState()
        {
            // 记录 Explorer 前台时的鼠标左键按下时间。
            // 新窗口注册发生在这个时间窗口内时，大概率是拖出标签页。
            if (!IsLeftButtonDown())
            {
                return;
            }

            string className = GetWindowClassName(GetForegroundWindow());
            if (className == "CabinetWClass" || className == "ExploreWClass")
            {
                lastLeftButtonDownTime = DateTime.Now;
            }
        }

        private bool IsRecentDragGesture()
        {
            return IsLeftButtonDown() || (lastLeftButtonDownTime != DateTime.MinValue && (DateTime.Now - lastLeftButtonDownTime).TotalMilliseconds <= 2000);
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
            // 优先从 Document.Folder.Self.Path 取真实目录；
            // 如果取不到，再从 LocationURL 的 file:// URL 兜底转换。
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
