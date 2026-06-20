using Microsoft.Win32;
using System;
using System.Windows.Forms;

namespace com.yuanheyuekeji.tabmerge
{
    internal sealed class TabMergeApplicationContext : ApplicationContext
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupName = "TabMerge";

        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem startupMenuItem;
        private readonly ToolStripMenuItem mergeMenuItem;
        private readonly ToolStripMenuItem logMenuItem;
        private readonly ExplorerMergeService mergeService;
        private readonly Control invoker;

        public TabMergeApplicationContext()
        {
            invoker = new Control();
            invoker.CreateControl();

            startupMenuItem = new ToolStripMenuItem("开机启动")
            {
                Checked = IsStartupEnabled()
            };
            startupMenuItem.Click += StartupMenuItem_Click;

            mergeMenuItem = new ToolStripMenuItem("启用合并")
            {
                Checked = true
            };
            mergeMenuItem.Click += MergeMenuItem_Click;

            logMenuItem = new ToolStripMenuItem("开启日志")
            {
                Checked = false
            };
            logMenuItem.Click += LogMenuItem_Click;

            var exitMenuItem = new ToolStripMenuItem("退出");
            exitMenuItem.Click += ExitMenuItem_Click;

            var menu = new ContextMenuStrip();
            menu.Items.Add(startupMenuItem);
            menu.Items.Add(mergeMenuItem);
            menu.Items.Add(logMenuItem);
            menu.Items.Add(exitMenuItem);

            notifyIcon = new NotifyIcon
            {
                ContextMenuStrip = menu,
                Icon = System.Drawing.SystemIcons.Application,
                Text = "TabMerge",
                Visible = true
            };

            mergeService = new ExplorerMergeService(invoker)
            {
                MergeEnabled = mergeMenuItem.Checked
            };
            mergeService.Start();
        }

        private void MergeMenuItem_Click(object sender, EventArgs e)
        {
            mergeMenuItem.Checked = !mergeMenuItem.Checked;
            mergeService.MergeEnabled = mergeMenuItem.Checked;
        }

        private void StartupMenuItem_Click(object sender, EventArgs e)
        {
            bool enabled = !startupMenuItem.Checked;
            SetStartup(enabled);
            startupMenuItem.Checked = IsStartupEnabled();
        }

        private void LogMenuItem_Click(object sender, EventArgs e)
        {
            if (!logMenuItem.Checked)
            {
                if (DebugLogger.TryEnable())
                {
                    logMenuItem.Checked = true;
                }
            }
            else
            {
                DebugLogger.Enabled = false;
                logMenuItem.Checked = false;
            }
        }

        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            if( MessageBox.Show( 
                "确定要退出应用吗? ", "提醒", MessageBoxButtons.OKCancel, MessageBoxIcon.Asterisk
                ) == DialogResult.OK) {
                ExitThread();
            }
          
        } 

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                mergeService.Dispose();
                invoker.Dispose();
            }

            base.Dispose(disposing);
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
            {
                string value = key?.GetValue(StartupName) as string;
                return string.Equals(value, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true))
            {
                if (enabled)
                {
                    key?.SetValue(StartupName, GetStartupCommand());
                }
                else
                {
                    key?.DeleteValue(StartupName, false);
                }
            }
        }

        private static string GetStartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }
    }
}
