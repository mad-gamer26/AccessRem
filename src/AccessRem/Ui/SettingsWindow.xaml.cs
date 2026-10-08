using System.Windows;
using AccessRem.Backend;
using AccessRem.Core;
using AccessRem.Services;

namespace AccessRem.Ui;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly SessionController _session;

    public sealed record SynthChoice(string Name, string Description);

    // Shown when no session is running (the bundled NVDA reports the full list during a session).
    private static readonly SynthChoice[] KnownSynths =
    {
        new("auto", "Automatic (Windows OneCore voices when available)"),
        new("oneCore", "Windows OneCore voices"),
        new("sapi5", "Microsoft Speech API version 5"),
        new("espeak", "eSpeak NG"),
    };

    public SettingsWindow(SettingsStore store, SessionController session)
    {
        InitializeComponent();
        AccessibleNames.Attach(this);
        _store = store;
        _session = session;
        var s = store.Current;
        StartWithWindows.IsChecked = s.StartWithWindows;
        StartMinimized.IsChecked = s.StartMinimized;
        MinimizeToTray.IsChecked = s.MinimizeToTray;
        CloseToTray.IsChecked = s.CloseToTray;
        PlaySounds.IsChecked = s.PlaySounds;
        ShowNotifications.IsChecked = s.ShowNotifications;
        HandleLinks.IsChecked = s.HandleNvdaRemoteLinks;
        KeepTranscript.IsChecked = s.KeepTranscript;
        ConfirmDisconnect.IsChecked = s.ConfirmDisconnectAsFollower;

        SpeakLocally.IsChecked = s.SpeakLocallyWhenControlled;
        LocalSounds.IsChecked = s.LocalSoundsWhenControlled;
        AllowSas.IsChecked = s.AllowCtrlAltDel;
        EstimatedRate.Value = s.EstimatedRemoteRate;
        UpdateSecureScreenStatus();
        UpdateSasStatus();

        ReadLocalScreen.IsChecked = s.ReadLocalScreen;
        MuteOnLocalControl.IsChecked = s.MuteOnLocalControl;
        ControlOnConnect.IsChecked = s.ControlRemoteOnConnect;
        var synths = session.AvailableSynths.Count > 0
            ? new[] { new SynthChoice("auto", "Automatic") }.Concat(session.AvailableSynths.Select(x => new SynthChoice(x.Name, x.Description)))
                .Where(x => x.Name != "accessRemSilent").ToList()
            : KnownSynths.ToList();
        if (synths.All(x => x.Name != s.Synth))
            synths.Add(new SynthChoice(s.Synth, s.Synth));
        SynthBox.ItemsSource = synths;
        SynthBox.SelectedValue = s.Synth;
        CustomRate.IsChecked = s.SpeechRate is not null;
        RateSlider.Value = s.SpeechRate ?? 50;
        CustomVolume.IsChecked = s.SpeechVolume is not null;
        VolumeSlider.Value = s.SpeechVolume ?? 100;
        var active = session.IsActive;
        NvdaSpeechButton.IsEnabled = NvdaBrailleButton.IsEnabled = active;

        ToggleGestures.Text = string.Join(", ", s.ToggleControlGestures);
        ClipboardGestures.Text = string.Join(", ", s.PushClipboardGestures);
        SasGestures.Text = string.Join(", ", s.SendSasGestures);
        MuteGestures.Text = string.Join(", ", s.ToggleMuteGestures);

        CertificateList.ItemsSource = s.TrustedCertificates.Select(kv => $"{kv.Key} — {kv.Value}").OrderBy(x => x).ToList();

        ReplaceWithoutAsking.IsChecked = s.ReplaceRunningNvdaWithoutAsking;
        RestoreNvda.IsChecked = s.RestoreReplacedNvda;
        NvdaPath.Text = s.NvdaPathOverride ?? "";
        NvdaPathStatus.Text = NvdaBackend.LocateBundledNvda(s.NvdaPathOverride) is { } dir ? $"Using NVDA in {dir}." : "NVDA was not found. Sessions cannot start until it is available.";
        TranscriptLimit.Text = s.TranscriptLimit.ToString();
    }

    private void UpdateSecureScreenStatus()
    {
        if (SystemInstall.IsRunningInstalledCopy)
        {
            SecureScreenStatus.Text = "AccessRem is installed for all users. When Windows shows a User Account Control or sign-in screen " +
                                      "during a session, your helper can read and use it.";
            InstallSystemButton.Visibility = Visibility.Collapsed;
            UninstallSystemButton.Visibility = Visibility.Visible;
        }
        else if (SystemInstall.IsInstalled)
        {
            SecureScreenStatus.Text = $"AccessRem is installed in {SystemInstall.InstalledDirectory}, but you are using a different copy. " +
                                      "Start AccessRem from the Start menu so your helper can use User Account Control and sign-in screens.";
            InstallSystemButton.Content = "Update the _installed copy…";
            UninstallSystemButton.Visibility = Visibility.Visible;
        }
        else
        {
            SecureScreenStatus.Text = "Your helper cannot read User Account Control or sign-in screens yet. Installing AccessRem for all users " +
                                      "(in Program Files, with administrator permission) lets Windows start AccessRem's NVDA on those screens during a session. " +
                                      "It also installs the helper service for Control+Alt+Delete.";
            UninstallSystemButton.Visibility = Visibility.Collapsed;
        }
    }

    private void InstallSystem_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsActive)
        {
            System.Windows.MessageBox.Show(this, "Disconnect before installing.", "Install for all users", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SasHelper.RunElevated($"--install-system \"{AppPaths.AppDirectory.TrimEnd('\\')}\""))
        {
            System.Windows.MessageBox.Show(this, $"AccessRem was not installed. Details are in {AppLog.FilePath}.", "Install for all users",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateSecureScreenStatus();
            UpdateSasStatus();
            return;
        }
        UpdateSecureScreenStatus();
        UpdateSasStatus();
        if (!SystemInstall.IsRunningInstalledCopy && SystemInstall.InstalledDirectory is { } installedDir)
            SystemInstall.RetireUserShortcuts(Environment.ProcessPath!, Path.Combine(installedDir, "AccessRem.exe"));
        if (!SystemInstall.IsRunningInstalledCopy && SystemInstall.InstalledDirectory is { } dir &&
            System.Windows.MessageBox.Show(this, $"AccessRem is installed in {dir} and has a Start menu shortcut. Switch to the installed copy now?",
                "Install for all users", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
        {
            DialogResult = false;
            App.Current.RelaunchFrom(Path.Combine(dir, "AccessRem.exe"));
        }
    }

    private void UninstallSystem_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsActive)
        {
            System.Windows.MessageBox.Show(this, "Disconnect before uninstalling.", "Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var runningInstalled = SystemInstall.IsRunningInstalledCopy;
        if (System.Windows.MessageBox.Show(this, "Remove AccessRem for all users, including secure screen support and the helper service? " +
                "Your saved computers and settings are kept.", "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SasHelper.RunElevated("--uninstall-system --confirmed");
        if (runningInstalled)
        {
            // The program folder is removed once this copy exits.
            _ = App.Current.ExitAsync();
            return;
        }
        UpdateSecureScreenStatus();
        UpdateSasStatus();
    }

    private void UpdateSasStatus()
    {
        var installed = SasHelper.IsInstalled();
        SasStatus.Text = installed
            ? "The Control+Alt+Delete helper service is installed, so a helper can open the security screen (for example to sign in or lock)."
            : "Windows only lets a system service send Control+Alt+Delete. Install the small helper service to allow it (administrator permission is required).";
        InstallSasButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        RemoveSasButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallSas_Click(object sender, RoutedEventArgs e)
    {
        if (!SasHelper.RunElevated("--install-sas-helper"))
            System.Windows.MessageBox.Show(this, "The helper service was not installed.", "Control+Alt+Delete helper", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateSasStatus();
    }

    private void RemoveSas_Click(object sender, RoutedEventArgs e)
    {
        SasHelper.RunElevated("--uninstall-sas-helper");
        UpdateSasStatus();
    }

    private async void NvdaSpeech_Click(object sender, RoutedEventArgs e) => await _session.OpenNvdaSettingsAsync("speech");

    private async void NvdaBraille_Click(object sender, RoutedEventArgs e) => await _session.OpenNvdaSettingsAsync("braille");

    private void ForgetCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (CertificateList.SelectedItem is not string item)
            return;
        var address = item.Split(" — ")[0];
        _store.Current.TrustedCertificates.Remove(address);
        _store.Save();
        CertificateList.ItemsSource = _store.Current.TrustedCertificates.Select(kv => $"{kv.Key} — {kv.Value}").OrderBy(x => x).ToList();
        CertificateList.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder containing NVDA" };
        if (dialog.ShowDialog(this) == true)
            NvdaPath.Text = dialog.FolderName;
    }

    private static List<string> ParseGestures(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.Contains(':') ? g : "kb:" + g)
            .ToList();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TranscriptLimit.Text, out var limit) || limit < 50)
        {
            System.Windows.MessageBox.Show(this, "The transcript length must be a number of at least 50.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            Tabs.SelectedIndex = 5;
            TranscriptLimit.Focus();
            return;
        }
        var toggles = ParseGestures(ToggleGestures.Text);
        if (toggles.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "At least one key is needed to switch the keyboard back to this computer.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            Tabs.SelectedIndex = 3;
            ToggleGestures.Focus();
            return;
        }
        var path = NvdaPath.Text.Trim();
        if (path.Length > 0 && NvdaBackend.LocateBundledNvda(path) != path)
        {
            System.Windows.MessageBox.Show(this, "That folder does not contain NVDA (nvda.exe).", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            Tabs.SelectedIndex = 5;
            NvdaPath.Focus();
            return;
        }

        var s = _store.Current;
        s.StartWithWindows = StartWithWindows.IsChecked == true;
        s.StartMinimized = StartMinimized.IsChecked == true;
        s.MinimizeToTray = MinimizeToTray.IsChecked == true;
        s.CloseToTray = CloseToTray.IsChecked == true;
        s.PlaySounds = PlaySounds.IsChecked == true;
        s.ShowNotifications = ShowNotifications.IsChecked == true;
        s.HandleNvdaRemoteLinks = HandleLinks.IsChecked == true;
        s.KeepTranscript = KeepTranscript.IsChecked == true;
        s.ConfirmDisconnectAsFollower = ConfirmDisconnect.IsChecked == true;
        s.SpeakLocallyWhenControlled = SpeakLocally.IsChecked == true;
        s.LocalSoundsWhenControlled = LocalSounds.IsChecked == true;
        s.AllowCtrlAltDel = AllowSas.IsChecked == true;
        s.EstimatedRemoteRate = (int)EstimatedRate.Value;
        s.ReadLocalScreen = ReadLocalScreen.IsChecked == true;
        s.MuteOnLocalControl = MuteOnLocalControl.IsChecked == true;
        s.ControlRemoteOnConnect = ControlOnConnect.IsChecked == true;
        s.Synth = SynthBox.SelectedValue as string ?? "auto";
        s.SpeechRate = CustomRate.IsChecked == true ? (int)RateSlider.Value : null;
        s.SpeechVolume = CustomVolume.IsChecked == true ? (int)VolumeSlider.Value : null;
        s.ToggleControlGestures = toggles;
        s.PushClipboardGestures = ParseGestures(ClipboardGestures.Text);
        s.SendSasGestures = ParseGestures(SasGestures.Text);
        s.ToggleMuteGestures = ParseGestures(MuteGestures.Text);
        s.ReplaceRunningNvdaWithoutAsking = ReplaceWithoutAsking.IsChecked == true;
        s.RestoreReplacedNvda = RestoreNvda.IsChecked == true;
        s.NvdaPathOverride = path.Length > 0 ? path : null;
        s.TranscriptLimit = limit;
        _store.Save();

        try
        {
            StartupRegistration.Apply(s.StartWithWindows);
            if (s.HandleNvdaRemoteLinks)
                UrlProtocol.Register();
            else
                UrlProtocol.Unregister();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to update system registrations: {ex.Message}");
        }
        DialogResult = true;
    }
}
