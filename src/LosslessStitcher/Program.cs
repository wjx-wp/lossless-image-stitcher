using System;
using System.Threading;
using System.Windows.Forms;

namespace LosslessStitcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception exception)
            {
                ShowUnexpectedError(exception);
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ShowUnexpectedError(e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            ShowUnexpectedError(exception);
        }

        private static void ShowUnexpectedError(Exception exception)
        {
            string details = exception == null ? "未知错误。" : exception.Message;

            try
            {
                MessageBox.Show(
                    "程序遇到未处理的错误：\r\n\r\n" + details +
                    "\r\n\r\n请先保存当前工作，并重新启动程序。",
                    "无损拼图 - 错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // The desktop environment itself may be unavailable while the process exits.
            }
        }
    }
}
