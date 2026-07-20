using System;
using System.Windows.Forms;
using Tower_Unite_Instrument_Autoplayer.GUI;

namespace Tower_Unite_Instrument_Autoplayer
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            //Catches exceptions that happen once the WinForms message loop is running.
            Application.ThreadException += (s, e) => ShowFatalError(e.Exception);
            //Catches anything else - including exceptions during static type initialization,
            //which can happen before Application.Run's own message loop (and so before
            //ThreadException above) even starts, and would otherwise just silently kill the
            //process with no explanation at all - which is exactly what "doesn't launch" with
            //no error looks like from the outside.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatalError(e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new GraphicalUserInterface());
            }
            catch (Exception ex)
            {
                ShowFatalError(ex);
            }
        }

        private static void ShowFatalError(Exception ex)
        {
            string message = ex != null
                ? $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}"
                : "Неизвестная ошибка (исключение отсутствует).";
            MessageBox.Show(
                "Приложение столкнулось с ошибкой при запуске:\n\n" + message,
                "Tower Unite Instrument Autoplayer - ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
