using System;
using System.Threading;
using System.Windows.Forms;

namespace CodexProxyMask
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\CodexProxyMask-SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Codex Proxy Mask 已经在运行。", "Codex Proxy Mask",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Application.Run(new MainForm());
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show("程序发生未处理异常:\r\n\r\n" + e.Exception.Message,
                "Codex Proxy Mask", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (exception != null)
            {
                MessageBox.Show("程序发生未处理异常:\r\n\r\n" + exception.Message,
                    "Codex Proxy Mask", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
