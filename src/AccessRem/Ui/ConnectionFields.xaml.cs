using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AccessRem.Core;

namespace AccessRem.Ui;

/// <summary>Server type, host, port and key, with key generation and port checking (as in NVDA's connect dialog).</summary>
public partial class ConnectionFields : UserControl
{
    private SettingsStore? _store;

    public ConnectionFields()
    {
        InitializeComponent();
    }

    public event Action? KindChanged;

    public ServerKind Kind
    {
        get => HostRadio.IsChecked == true ? ServerKind.HostLocally : ServerKind.Relay;
        set
        {
            RelayRadio.IsChecked = value == ServerKind.Relay;
            HostRadio.IsChecked = value == ServerKind.HostLocally;
            UpdateKind();
        }
    }

    public string Host { get => HostBox.Text.Trim(); set => HostBox.Text = value; }
    public string Key { get => KeyBox.Text.Trim(); set => KeyBox.Text = value; }

    public void Initialize(SettingsStore store, string host, int port, string key, ServerKind kind)
    {
        _store = store;
        var hosts = new List<string> { Protocol.DefaultRelayHost };
        hosts.AddRange(store.Current.Machines.Where(m => m.Kind == ServerKind.Relay).Select(m => m.Host));
        foreach (var h in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
            HostBox.Items.Add(h);
        HostBox.Text = host;
        PortBox.Text = port.ToString();
        KeyBox.Text = key;
        Kind = kind;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e) => UpdateKind();

    private void UpdateKind()
    {
        if (HostPanel is null)
            return;
        var hosting = HostRadio.IsChecked == true;
        HostPanel.IsEnabled = !hosting;
        HostPanel.Opacity = hosting ? 0.5 : 1;
        HostedPanel.Visibility = hosting ? Visibility.Visible : Visibility.Collapsed;
        KindChanged?.Invoke();
    }

    /// <summary>Validate the fields, moving focus to the first problem. Returns the parsed host and port.</summary>
    public bool TryGetValues(out string host, out int port, out string? error, out Control? problem)
    {
        host = Host;
        port = Protocol.DefaultPort;
        error = null;
        problem = null;
        if (Kind == ServerKind.Relay)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                (error, problem) = ("Enter the host: a relay server such as nvdaremote.com, or the address of the computer hosting the session.", HostBox);
                return false;
            }
            if (host.Contains("://") && !host.StartsWith(Protocol.UrlPrefix, StringComparison.OrdinalIgnoreCase))
            {
                (error, problem) = ("Enter just the server name or address, without http:// or other prefixes.", HostBox);
                return false;
            }
            // Accept "host:port" in the host field, as NVDA does.
            var (parsedHost, parsedPort) = Protocol.AddressToHostPort(host);
            host = parsedHost;
            if (parsedPort != Protocol.DefaultPort || host != Host)
            {
                PortBox.Text = parsedPort.ToString();
                HostBox.Text = parsedHost;
            }
        }
        if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535)
        {
            (error, problem) = ("The port must be a number from 1 to 65535. The usual port is 6837.", PortBox);
            return false;
        }
        if (string.IsNullOrWhiteSpace(Key))
        {
            (error, problem) = ("Enter a key, or choose Generate key. Both computers must use the same key.", KeyBox);
            return false;
        }
        return true;
    }

    private async void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (Kind == ServerKind.HostLocally)
        {
            KeyBox.Text = Protocol.GenerateLocalKey();
            KeyBox.Focus();
            KeyBox.SelectAll();
            Report($"Generated key {KeyBox.Text}.");
            return;
        }
        if (string.IsNullOrWhiteSpace(Host))
        {
            MessageBox.Show(Window.GetWindow(this), "Host must be set.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            HostBox.Focus();
            return;
        }
        var (host, port) = Protocol.AddressToHostPort(Host);
        if (int.TryParse(PortBox.Text, out var p) && p != Protocol.DefaultPort && !Host.Contains(':'))
            port = p;
        await GenerateRelayKeyAsync(host, port, CertificatePolicy.Verify);
    }

    private async Task GenerateRelayKeyAsync(string host, int port, CertificatePolicy policy)
    {
        GenerateKeyButton.IsEnabled = false;
        Report("Generating key…");
        try
        {
            var key = await RelayTools.GenerateKeyAsync(host, port, policy, _store!, CancellationToken.None);
            KeyBox.Text = key;
            Report($"The server generated key {key}.");
            KeyBox.Focus();
            KeyBox.SelectAll();
        }
        catch (CertificateUntrustedException ex)
        {
            Report("");
            var dialog = new CertificateDialog(ex) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            if (dialog.Decision == CertificateDecision.TrustAlways && ex.Fingerprint is not null)
            {
                _store!.Trust(ex.Address, ex.Fingerprint);
                await GenerateRelayKeyAsync(host, port, CertificatePolicy.Verify);
            }
            else if (dialog.Decision == CertificateDecision.ConnectOnce && ex.Fingerprint is not null)
            {
                await GenerateRelayKeyAsync(host, port, new CertificatePolicy(true, ex.Fingerprint));
            }
        }
        catch (Exception ex)
        {
            Report("");
            MessageBox.Show(Window.GetWindow(this),
                $"Unable to connect to {Protocol.HostPortToAddress(host, port)}. Check that you have internet access, and that there are no mistakes in the host field.\n\n{NetworkSession.Describe(ex)}",
                "Host connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateKeyButton.IsEnabled = true;
        }
    }

    private async void CheckPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(Window.GetWindow(this), "Enter a valid port first.", "Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }
        CheckPortButton.IsEnabled = false;
        Report("Getting external address…");
        try
        {
            var result = await RelayTools.CheckPortAsync(port, CancellationToken.None);
            ExternalAddress = result.Host;
            PortCheckResult.Text = result.Open
                ? $"Your external address is {result.Host}. Port {port} is open."
                : $"Your external address is {result.Host}, but port {port} is most likely not currently forwarded. " +
                  "Start hosting first, then check again; if it is still closed, forward the port on your router.";
            PortCheckResult.Visibility = Visibility.Visible;
            Report(PortCheckResult.Text);
        }
        catch (Exception ex)
        {
            Report("");
            MessageBox.Show(Window.GetWindow(this), $"Unable to contact the port check service, please find your address manually.\n\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CheckPortButton.IsEnabled = true;
        }
    }

    public string? ExternalAddress { get; private set; }

    private void Report(string text)
    {
        ProgressText.Text = text;
        ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(text) && UIElementAutomationPeer.CreatePeerForElement(ProgressText) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void FocusFirstField()
    {
        if (Kind == ServerKind.Relay)
            HostBox.Focus();
        else
            PortBox.Focus();
    }
}
