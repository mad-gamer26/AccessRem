using System.Windows;
using System.Windows.Threading;
using AssistBridge.Core;
using AssistBridge.Services;
using AssistBridge.Ui;
using Microsoft.Win32;

namespace AssistBridge;

public partial class App : Application
{
    private readonly SingleInstance _instance;
    private readonly string[] _startupArgs;
    private TrayIcon? _tray;

    public App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _startupArgs = args;
    }

    public static new App Current => (App)Application.Current;
    public SettingsStore Settings { get; private set; } = null!;
    public SessionController Session { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ApplyTheme();
        ApplyTextScale();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
                ApplyTheme();
        };
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(ApplyTextScale);
        };

        Settings = new SettingsStore();
        Main = new MainWindow();
        Session = new SessionController(Settings, Main, SynchronizationContext.Current!);
        Main.Attach(Settings, Session);
        _tray = new TrayIcon(this);
        Session.Announced += text => _tray.Notify(text, Main.IsActive);

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => HandleArguments(args, fromAnotherInstance: true));
        _instance.Listen();

        SyncSystemRegistrations();

        var startHidden = _startupArgs.Contains("--startup") || Settings.Current.StartMinimized;
        if (startHidden && Settings.Current.MinimizeToTray)
            _tray.Notify("AssistBridge is running in the notification area.", false);
        else
        {
            Main.Show();
            if (startHidden)
                Main.WindowState = WindowState.Minimized;
        }
        HandleArguments(_startupArgs, fromAnotherInstance: false);
        Dispatcher.BeginInvoke(AutoConnectAsync, DispatcherPriority.ApplicationIdle);
    }

    private void SyncSystemRegistrations()
    {
        try
        {
            StartupRegistration.Apply(Settings.Current.StartWithWindows);
            if (Settings.Current.HandleNvdaRemoteLinks)
                UrlProtocol.Register();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to update system registrations: {ex.Message}");
        }
    }

    private async void AutoConnectAsync()
    {
        if (Session.IsActive)
            return;
        var machine = Settings.Current.Machines.FirstOrDefault(m => m.AutoConnect);
        if (machine is null)
            return;
        Session.Log($"Connecting automatically to {machine.DisplayName}.");
        await Main.ConnectMachineAsync(machine, machine.DefaultMode, confirm: false);
    }

    public void HandleArguments(string[] args, bool fromAnotherInstance)
    {
        var link = args.FirstOrDefault(a => a.StartsWith(Protocol.UrlPrefix, StringComparison.OrdinalIgnoreCase));
        if (link is not null)
        {
            ShowMainWindow();
            _ = Main.OpenLinkAsync(link, confirm: true);
            return;
        }
        if (fromAnotherInstance)
            ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (!Main.IsVisible)
            Main.Show();
        if (Main.WindowState == WindowState.Minimized)
            Main.WindowState = WindowState.Normal;
        Main.Activate();
        Main.Topmost = true;
        Main.Topmost = false;
        Main.Focus();
    }

    public async Task ExitAsync()
    {
        if (Session.IsActive && !await Main.ConfirmDisconnectAsync(exiting: true))
            return;
        await Session.DisposeAsync();
        _tray?.Dispose();
        _instance.Dispose();
        Shutdown();
    }

    /// <summary>Use Windows' contrast theme colours whenever high contrast is on.</summary>
    private void ApplyTheme()
    {
        var source = SystemParameters.HighContrast ? "Ui/Theme.HighContrast.xaml" : "Ui/Theme.Light.xaml";
        var dictionaries = Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
    }

    /// <summary>Follow Settings › Accessibility › Text size, which WPF does not apply by itself.</summary>
    private void ApplyTextScale()
    {
        var factor = 1.0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225)
                factor = percent / 100.0;
        }
        catch (Exception)
        {
        }
        Resources["BaseFontSize"] = 14.0 * factor;
        Resources["HeadingFontSize"] = 17.0 * factor;
        Resources["TitleFontSize"] = 22.0 * factor;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write($"Unhandled UI exception: {e.Exception}");
        MessageBox.Show(Main?.IsVisible == true ? Main : null!,
            $"Something went wrong: {e.Exception.Message}\n\nDetails were written to {AppLog.FilePath}.",
            "AssistBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
