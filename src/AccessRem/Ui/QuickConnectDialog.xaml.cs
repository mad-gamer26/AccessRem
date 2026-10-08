using System.Windows;
using AccessRem.Core;

namespace AccessRem.Ui;

/// <summary>Connect once from a link or typed details, optionally saving them (NVDA's "Connect" dialog).</summary>
public partial class QuickConnectDialog : Window
{
    public QuickConnectDialog(SettingsStore store, ConnectionMode? mode, ConnectionInfo? prefill)
    {
        InitializeComponent();
        AccessibleNames.Attach(this);
        Fields.Initialize(store, prefill?.Host ?? Protocol.DefaultRelayHost, prefill?.Port ?? Protocol.DefaultPort, prefill?.Key ?? "", ServerKind.Relay);
        var m = prefill?.Mode ?? mode ?? ConnectionMode.Follower;
        FollowerRadio.IsChecked = m == ConnectionMode.Follower;
        LeaderRadio.IsChecked = m == ConnectionMode.Leader;
        Title = mode switch
        {
            ConnectionMode.Follower => "Get help",
            ConnectionMode.Leader => "Give help",
            _ => "Connect",
        };
        Loaded += (_, _) =>
        {
            var clip = SafeClipboard.GetText()?.Trim();
            if (clip is not null && clip.StartsWith(Protocol.UrlPrefix, StringComparison.OrdinalIgnoreCase))
                LinkBox.Text = clip;
            LinkBox.Focus();
        };
    }

    public ConnectionInfo? Result { get; private set; }
    public bool HostLocally { get; private set; }
    public Machine? SaveAs { get; private set; }

    private void UseLink_Click(object sender, RoutedEventArgs e) => ApplyLink(focusConnect: true);

    private bool ApplyLink(bool focusConnect)
    {
        var text = LinkBox.Text.Trim();
        if (text.Length == 0)
            return true;
        try
        {
            var info = ConnectionInfo.FromUrl(text);
            Fields.Kind = ServerKind.Relay;
            Fields.Host = info.Host;
            Fields.PortBox.Text = info.Port.ToString();
            Fields.Key = info.Key;
            FollowerRadio.IsChecked = info.Mode == ConnectionMode.Follower;
            LeaderRadio.IsChecked = info.Mode == ConnectionMode.Leader;
            _insecure = info.Insecure;
            if (focusConnect)
                ConnectButton.Focus();
            return true;
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, $"This link could not be used: {ex.Message}", "Invalid link", MessageBoxButton.OK, MessageBoxImage.Warning);
            LinkBox.Focus();
            return false;
        }
    }

    private bool _insecure;

    private void SaveBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveNamePanel.Visibility = SaveBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SaveBox.IsChecked == true)
            NameBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyLink(focusConnect: false))
            return;
        if (!Fields.TryGetValues(out var host, out var port, out var error, out var problem))
        {
            MessageBox.Show(this, error, "Check the details", MessageBoxButton.OK, MessageBoxImage.Warning);
            problem?.Focus();
            return;
        }
        var mode = LeaderRadio.IsChecked == true ? ConnectionMode.Leader : ConnectionMode.Follower;
        HostLocally = Fields.Kind == ServerKind.HostLocally;
        Result = HostLocally
            ? new ConnectionInfo("localhost", port, Fields.Key, mode, Insecure: true)
            : new ConnectionInfo(host, port, Fields.Key, mode, _insecure);
        if (SaveBox.IsChecked == true)
        {
            SaveAs = new Machine
            {
                Name = NameBox.Text.Trim(),
                Kind = Fields.Kind,
                Host = HostLocally ? Protocol.DefaultRelayHost : host,
                Port = port,
                Key = Fields.Key,
                DefaultMode = mode,
            };
        }
        DialogResult = true;
    }
}
