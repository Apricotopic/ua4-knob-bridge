namespace Ua4KnobBridge;

internal static class Program
{
    private const string MutexName = @"Local\Ua4KnobBridge.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            SingleInstanceCoordinator.NotifyExistingInstance(args.Any(arg => string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase)) ? "exit" : "activate");
            return;
        }

        // Use Windows' native per-monitor DPI and ClearType text path. This is
        // CPU-only and has no animation or GPU rendering cost.
        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool startedFromWindows = args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        using var coordinator = new SingleInstanceCoordinator();
        using var form = new MainForm(startedFromWindows, coordinator);
        Application.Run(form);
    }
}
