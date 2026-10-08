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
        if (Environment.GetEnvironmentVariable("ASSISTBRIDGE_SNAPSHOT") is { Length: > 0 } snapshotDir)
        {
            _ = SnapshotAsync(snapshotDir);
            return;
        }
        Dispatcher.BeginInvoke(AutoConnectAsync, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Developer aid: render each window to PNG (works even with the screen off or curtained), then exit.</summary>
    private async Task SnapshotAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(1500);
        SaveSnapshot(Main, Path.Combine(directory, "main.png"));
        var windows = new (string Name, Func<Window> Create)[]
        {
            ("machine", () => new MachineDialog(new Machine(), true, Settings)),
            ("quick", () => new QuickConnectDialog(Settings, ConnectionMode.Follower, null)),
            ("settings", () => new SettingsWindow(Settings, Session)),
            ("certificate", () => new CertificateDialog(new CertificateUntrustedException("relay.example.org", 6837, new string('a', 64), System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors))),
        };
        foreach (var (name, create) in windows)
        {
            var window = create();
            window.Owner = Main;
            window.Show();
            await Task.Delay(700);
            SaveSnapshot(window, Path.Combine(directory, name + ".png"));
            window.Close();
        }
        _instance.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    private static void SaveSnapshot(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((System.Windows.Media.Brush)window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
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
