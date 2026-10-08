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
                case "--install-system" when args.Length > 1:
                    return SystemInstall.Install(args[1]);
                case "--uninstall-system":
                    return UninstallSystem(args.Contains("--confirmed"));
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

    /// <summary>Started from Settings or from Windows' installed apps list.</summary>
    private static int UninstallSystem(bool confirmed)
    {
        if (!confirmed)
        {
            var answer = System.Windows.MessageBox.Show(
                "Remove AssistBridge for all users? Your saved computers and settings are kept.",
                "Uninstall AssistBridge", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return 1;
        }
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            return SasHelper.RunElevated("--uninstall-system --confirmed") ? 0 : 1;
        return SystemInstall.Uninstall();
    }
}
