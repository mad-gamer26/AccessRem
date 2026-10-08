using AssistBridge.Core;
using AssistBridge.Services;

namespace AssistBridge;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Service and elevated maintenance modes never show UI.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--sas-service":
                    SasHelper.RunService();
                    return 0;
                case "--install-sas-helper":
                    return SasHelper.Install();
                case "--uninstall-sas-helper":
                    return SasHelper.Uninstall();
            }
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running: hand over any link and bring the window forward.
            SingleInstance.SendToRunningInstance(args);
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"Unhandled exception: {e.ExceptionObject}");
        var app = new App(instance, args);
        app.InitializeComponent();
        return app.Run();
    }
}
