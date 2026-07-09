// Program.cs
//
// Entry point. Enforces single-instance via a named Mutex and initializes
// both UI frameworks (WinForms for the tray, WPF for the launcher window).

using System.Windows.Forms;

namespace MartianGamesAlerts;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "Global\\MartianGamesAlerts.SingleInstance",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            // Another copy already running. Exit silently.
            return;
        }

        ApplicationConfiguration.Initialize();

        // Instantiate a WPF Application so WPF Window instances shown from
        // the tray have proper theming and resource-dictionary support.
        // No need to Run() it — the WinForms message loop drives both.
        //
        // ShutdownMode must be OnExplicitShutdown; the default of
        // OnLastWindowClose would tear down the WPF app subsystem the
        // moment the launcher window closes, and any subsequent attempt
        // to open it would fail during InitializeComponent with
        // "The Application object is being shut down."
        _ = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };

        var settings = Settings.LoadOrCreate();
        using var ctx = new TrayAppContext(settings);
        Application.Run(ctx);
    }
}