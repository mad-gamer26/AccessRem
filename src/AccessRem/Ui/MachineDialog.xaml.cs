using System.Windows;
using AccessRem.Core;

namespace AccessRem.Ui;

public partial class MachineDialog : Window
{
    private readonly Machine _machine;
    private readonly SettingsStore _store;

    public MachineDialog(Machine machine, bool isNew, SettingsStore store)
    {
        InitializeComponent();
        AccessibleNames.Attach(this);
        _machine = machine;
        _store = store;
        Title = isNew ? "Add computer" : $"Edit {machine.DisplayName}";
        OkButton.Content = isNew ? "_Add" : "_Save";
        NameBox.Text = machine.Name;
        Fields.Initialize(store, machine.Kind == ServerKind.Relay ? machine.Host : Protocol.DefaultRelayHost, machine.Port, machine.Key, machine.Kind);
        FollowerRadio.IsChecked = machine.DefaultMode == ConnectionMode.Follower;
        LeaderRadio.IsChecked = machine.DefaultMode == ConnectionMode.Leader;
        AutoConnectBox.IsChecked = machine.AutoConnect;
        NotesBox.Text = machine.Notes;
        Loaded += (_, _) => NameBox.Focus();
    }

    public Machine Result => _machine;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Fields.TryGetValues(out var host, out var port, out var error, out var problem))
        {
            MessageBox.Show(this, error, "Check the details", MessageBoxButton.OK, MessageBoxImage.Warning);
            problem?.Focus();
            return;
        }
        _machine.Name = NameBox.Text.Trim();
        _machine.Kind = Fields.Kind;
        if (Fields.Kind == ServerKind.Relay)
            _machine.Host = host;
        _machine.Port = port;
        _machine.Key = Fields.Key;
        _machine.DefaultMode = LeaderRadio.IsChecked == true ? ConnectionMode.Leader : ConnectionMode.Follower;
        _machine.Notes = NotesBox.Text;
        if (AutoConnectBox.IsChecked == true)
        {
            // Only one computer can connect automatically.
            foreach (var other in _store.Current.Machines.Where(m => m.Id != _machine.Id))
                other.AutoConnect = false;
        }
        _machine.AutoConnect = AutoConnectBox.IsChecked == true;
        DialogResult = true;
    }
}
