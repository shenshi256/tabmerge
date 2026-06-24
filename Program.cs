using System;
using System.Threading;
using System.Windows.Forms;

namespace com.yuanheyuekeji.tabmerge
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\com.yuanheyuekeji.tabmerge", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("程序已在运行中，请查看托盘图标。", "TabMerge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TabMergeApplicationContext());
            }
        }
    }
}
