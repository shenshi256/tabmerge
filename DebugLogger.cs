using System;
using System.IO;
using System.Windows.Forms;

namespace com.yuanheyuekeji.tabmerge
{
    internal static class DebugLogger
    {
        private static readonly object syncLock = new object();
        private static string logPath;
        private static bool initialized;

        public static bool Enabled { get; set; }

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string today = DateTime.Now.ToString("yyyyMMdd");
            logPath = Path.Combine(dir, today + "_tabmerge_debug.log");

            // Delete old date-named logs
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*_tabmerge_debug.log"))
                {
                    if (!string.Equals(Path.GetFileName(file), Path.GetFileName(logPath), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }

            // Cap at 10MB
            CapLogFile();
        }

        public static bool TryEnable()
        {
            Init();

            string dir = Path.GetDirectoryName(logPath);
            if (string.IsNullOrEmpty(dir)) return false;

            try
            {
                // Test writability
                string testFile = Path.Combine(dir, ".tabmerge_test");
                File.WriteAllText(testFile, "1");
                File.Delete(testFile);
            }
            catch
            {
                MessageBox.Show("当前日志路径不可用: " + dir, "TabMerge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            CapLogFile();
            Enabled = true;
            return true;
        }

        public static void Log(string point, string message)
        {
            if (!Enabled || logPath == null) return;

            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + point + "] " + message + Environment.NewLine;
                lock (syncLock)
                {
                    File.AppendAllText(logPath, line);

                    // Check size after write
                    FileInfo fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 10 * 1024 * 1024)
                    {
                        File.Delete(logPath);
                    }
                }
            }
            catch { }
        }

        private static void CapLogFile()
        {
            if (logPath == null) return;
            try
            {
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 10 * 1024 * 1024)
                {
                    fi.Delete();
                }
            }
            catch { }
        }
    }
}
