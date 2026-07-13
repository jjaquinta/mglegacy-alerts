// Program.cs
//
// Entry point. Sequence:
//   1. Clean up any leftover .exe.old from a prior in-place update.
//   2. If we're not at the expected install location AND we're not a dev
//      build, try to bootstrap (download, install to expected location,
//      launch, exit). On failure, fall through to running in place.
//   3. Acquire the single-instance mutex with a 5s WaitOne — the timeout
//      matters during self-update because the outgoing instance may still
//      briefly hold the mutex when the new one starts.
//   4. Initialize WinForms + WPF and run the tray context.
//
// Startup progression and all unhandled exceptions are logged to
// %APPDATA%\MartianGamesAlerts\update-check.log so silent process deaths
// are diagnosable after the fact.

using System.Windows.Forms;

namespace MartianGamesAlerts;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        HookUnhandledExceptions();
        UpdateLog.Line("STARTUP", $"Main() enter, exe={Environment.ProcessPath ?? "(null)"}");

        try
        {
            SelfUpdater.CleanupStaleFiles();
            UpdateLog.Line("STARTUP", "CleanupStaleFiles done");

            var settings = Settings.LoadOrCreate();
            UpdateLog.Line("STARTUP", "Settings loaded");

            if (!SelfUpdater.IsRunningFromExpectedLocation() && !SelfUpdater.IsDevBuild())
            {
                UpdateLog.Line("STARTUP", "Not at expected location; attempting bootstrap");
                try
                {
                    var bootstrapped = SelfUpdater.TryBootstrapAsync(settings.ManifestUrl)
                        .GetAwaiter().GetResult();
                    if (bootstrapped)
                    {
                        UpdateLog.Line("STARTUP", "Bootstrap succeeded; exiting.");
                        return;
                    }
                    UpdateLog.Line("STARTUP", "Bootstrap returned false; falling through to run in place.");
                }
                catch (Exception ex)
                {
                    UpdateLog.Line("STARTUP", $"Bootstrap threw ({ex.GetType().Name}: {ex.Message}); falling through.");
                }
            }

            using var singleInstance = new Mutex(
                initiallyOwned: false,
                name: "Global\\MartianGamesAlerts.SingleInstance");
            UpdateLog.Line("STARTUP", "Mutex created; waiting");

            if (!singleInstance.WaitOne(TimeSpan.FromSeconds(5), exitContext: false))
            {
                UpdateLog.Line("STARTUP", "Mutex WaitOne timed out (another instance is running?); exiting.");
                return;
            }
            UpdateLog.Line("STARTUP", "Mutex acquired");

            try
            {
                ApplicationConfiguration.Initialize();
                UpdateLog.Line("STARTUP", "WinForms initialized");

                _ = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
                UpdateLog.Line("STARTUP", "WPF Application constructed");

                using var ctx = new TrayAppContext(settings);
                UpdateLog.Line("STARTUP", "TrayAppContext constructed; entering Application.Run");

                Application.Run(ctx);
                UpdateLog.Line("STARTUP", "Application.Run returned normally");
            }
            finally
            {
                try { singleInstance.ReleaseMutex(); } catch { }
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Line("STARTUP", $"Uncaught in Main: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    private static void HookUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            UpdateLog.Line("CRASH", $"AppDomain unhandled ({(e.IsTerminating ? "terminating" : "not terminating")}): "
                + (ex != null ? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
                              : e.ExceptionObject?.ToString() ?? "(null)"));
        };

        Application.ThreadException += (_, e) =>
        {
            var ex = e.Exception;
            UpdateLog.Line("CRASH", $"WinForms ThreadException: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var ex = e.Exception;
            UpdateLog.Line("CRASH", $"Unobserved task exception: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            e.SetObserved();
        };
    }
}