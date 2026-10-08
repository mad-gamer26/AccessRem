using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using AssistBridge.Backend;
using AssistBridge.Core;

namespace AssistBridge.Ui;

public partial class MainWindow : Window, ISessionUi
{
    private SettingsStore _store = null!;
    private SessionController _session = null!;
    private readonly ObservableCollection<Machine> _machines = new();
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        AccessibleNames.Attach(this);
        GetHelpCommand = new RelayCommand(() => QuickStartAsync(ConnectionMode.Follower), () => _session?.IsIdle == true);
        GiveHelpCommand = new RelayCommand(() => QuickStartAsync(ConnectionMode.Leader), () => _session?.IsIdle == true);
        QuickConnectCommand = new RelayCommand(() => ShowQuickConnectAsync(null, null), () => _session?.IsIdle == true);
        ToggleControlCommand = new RelayCommand(() => _session.ToggleControlAsync(), () => _session?.CanControl == true || _session?.SendingKeys == true);
        ToggleMuteCommand = new RelayCommand(() => _session.ToggleMuteAsync(), () => _session?.IsLeader == true && _session.IsConnected);
        PushClipboardCommand = new RelayCommand(() => _session.PushClipboardAsync(), () => _session?.IsConnected == true);
        CopyLinkCommand = new RelayCommand(CopyLink, () => _session?.IsActive == true);
        CopyInvitationCommand = new RelayCommand(CopyInvitation, () => _session?.IsActive == true);
        SendSasCommand = new RelayCommand(() => _session.SendSasAsync(), () => _session?.IsLeader == true && _session.IsConnected);
        DisconnectCommand = new RelayCommand(DisconnectWithConfirmationAsync, () => _session?.IsActive == true);
        ExitCommand = new RelayCommand(() => App.Current.ExitAsync());
        AddMachineCommand = new RelayCommand(AddMachine);
        EditMachineCommand = new RelayCommand(EditSelectedMachine, () => SelectedMachine is not null);
        DuplicateMachineCommand = new RelayCommand(DuplicateSelectedMachine, () => SelectedMachine is not null);
        RemoveMachineCommand = new RelayCommand(RemoveSelectedMachine, () => SelectedMachine is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => MachineList.SelectedIndex > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => MachineList.SelectedIndex >= 0 && MachineList.SelectedIndex < _machines.Count - 1);
        SettingsCommand = new RelayCommand(ShowSettings);
        NvdaSpeechSettingsCommand = new RelayCommand(() => _session.OpenNvdaSettingsAsync("speech"), () => _session?.IsActive == true);
        NvdaBrailleSettingsCommand = new RelayCommand(() => _session.OpenNvdaSettingsAsync("braille"), () => _session?.IsActive == true);
        ClearTranscriptCommand = new RelayCommand(() => _session.Transcript.Clear());
        CopyTranscriptCommand = new RelayCommand(CopyTranscript, () => _session?.Transcript.Count > 0);
        OpenLogsCommand = new RelayCommand(() => OpenFolder(AppPaths.LogDirectory));
        HelpCommand = new RelayCommand(() => InfoWindow.ShowHelp(this));
        ShortcutsCommand = new RelayCommand(() => InfoWindow.ShowShortcuts(this, _store.Current));
        AboutCommand = new RelayCommand(() => InfoWindow.ShowAbout(this, _session.BackendVersion));

        InputBindings.Add(new KeyBinding(GetHelpCommand, Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(GiveHelpCommand, Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(QuickConnectCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ToggleControlCommand, Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ToggleMuteCommand, Key.M, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(PushClipboardCommand, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(CopyLinkCommand, Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(CopyInvitationCommand, Key.L, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(DisconnectCommand, Key.D, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(AddMachineCommand, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(SettingsCommand, Key.OemComma, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(HelpCommand, Key.F1, ModifierKeys.None));
    }

    public ICommand GetHelpCommand { get; }
    public ICommand GiveHelpCommand { get; }
    public ICommand QuickConnectCommand { get; }
    public ICommand ToggleControlCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand PushClipboardCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand CopyInvitationCommand { get; }
    public ICommand SendSasCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand AddMachineCommand { get; }
    public ICommand EditMachineCommand { get; }
    public ICommand DuplicateMachineCommand { get; }
    public ICommand RemoveMachineCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand NvdaSpeechSettingsCommand { get; }
    public ICommand NvdaBrailleSettingsCommand { get; }
    public ICommand ClearTranscriptCommand { get; }
    public ICommand CopyTranscriptCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ShortcutsCommand { get; }
    public ICommand AboutCommand { get; }

    private Machine? SelectedMachine => MachineList.SelectedItem as Machine;

    public void Attach(SettingsStore store, SessionController session)
    {
        _store = store;
        _session = session;
        DataContext = session;
        foreach (var m in store.Current.Machines)
            _machines.Add(m);
        MachineList.ItemsSource = _machines;
        _machines.CollectionChanged += (_, _) => UpdateEmptyState();
        MachineList.SelectionChanged += (_, _) => UpdateSelectionText();
        var last = _machines.FirstOrDefault(m => m.Id == store.Current.LastMachineId) ?? _machines.FirstOrDefault();
        if (last is not null)
            MachineList.SelectedItem = last;
        UpdateEmptyState();
        UpdateSelectionText();

        session.PropertyChanged += OnSessionPropertyChanged;
        session.Announced += Announce;
        session.Transcript.CollectionChanged += OnTranscriptChanged;
        session.Events.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && EventList.Items.Count > 0)
                EventList.ScrollIntoView(EventList.Items[^1]);
        };
        UpdateSessionText();

        Loaded += (_, _) =>
        {
            if (MachineList.Items.Count > 0)
                FocusSelectedMachine();
            else
                GetHelpButton.Focus();
        };
    }

    // Session presentation

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateSessionText();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateSessionText()
    {
        var s = _session;
        var gestures = string.Join(" or ", _store.Current.ToggleControlGestures.Select(InfoWindow.DescribeGesture));
        if (s.IsLeader)
        {
            SessionTitle.Text = s.Machine is { } m ? $"Controlling {m.DisplayName}" : "Controlling another computer";
            RolePill.Text = s.SendingKeys ? "Keyboard: remote" : "Keyboard: this computer";
            ToggleControlButton.Content = s.SendingKeys ? "Return keyboard to this computer (Ctrl+T)" : "Control the remote computer (Ctrl+T)";
            ToggleControlButton.ToolTip = $"While controlling the remote computer, press {gestures} to return.";
            TranscriptHint.Text = "Everything the remote computer's screen reader says appears here and is spoken aloud on this computer.";
        }
        else if (s.IsFollower)
        {
            SessionTitle.Text = "Getting help";
            RolePill.Text = s.ControllingPeers > 0 ? "Helper connected" : "Waiting for helper";
            TranscriptHint.Text = "What your helper hears: the speech sent from this computer.";
        }
        else
        {
            TranscriptHint.Text = "Speech appears here during a session.";
        }
        ControlBannerText.Text = $"Your keyboard is controlling the remote computer. Press {gestures} to return to this computer.";
        Title = s.IsActive ? $"AssistBridge — {s.Status}" : "AssistBridge";

        if (s.Hosted && s.Info is { } info)
        {
            var address = s.ExternalAddress ?? SessionController.GuessLocalAddress() ?? "this computer's address";
            HostedInfoText.Text = $"Hosting on this computer. The other person connects to {address}, port {info.Port}, key {info.Key}. " +
                                  $"Port {info.Port} must be reachable from their network.";
            HostedInfoText.Visibility = Visibility.Visible;
        }
        else
        {
            HostedInfoText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || TranscriptList.Items.Count == 0)
            return;
        // Keep the newest speech in view unless the user is reviewing older entries.
        if (TranscriptList.IsKeyboardFocusWithin && TranscriptList.SelectedIndex >= 0 && TranscriptList.SelectedIndex < TranscriptList.Items.Count - 2)
            return;
        TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
    }

    /// <summary>Report a message through a UI Automation live region and the status bar.</summary>
    public void Announce(string text)
    {
        StatusBarText.Text = text;
        LiveRegion.Text = text;
        AutomationProperties.SetName(LiveRegion, text);
        if (UIElementAutomationPeer.FromElement(LiveRegion) is { } peer || (peer = UIElementAutomationPeer.CreatePeerForElement(LiveRegion)) is not null)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // Machines

    private void UpdateEmptyState() => EmptyMachinesText.Visibility = _machines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateSelectionText()
    {
        SelectedMachineText.Text = SelectedMachine is { } m
            ? $"Using {m.DisplayName} ({m.Summary}). Choose what you would like to do."
            : "Choose what you would like to do. You will be asked for a server and key.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void FocusSelectedMachine()
    {
        MachineList.UpdateLayout();
        if (MachineList.ItemContainerGenerator.ContainerFromItem(MachineList.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            MachineList.Focus();
    }

    private void SaveMachines()
    {
        _store.Current.Machines = _machines.ToList();
        _store.Save();
    }

    private void AddMachine()
    {
        var dialog = new MachineDialog(new Machine(), isNew: true, _store) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _machines.Add(dialog.Result);
        SaveMachines();
        MachineList.SelectedItem = dialog.Result;
        FocusSelectedMachine();
        Announce($"Added {dialog.Result.DisplayName}");
    }

    private void EditSelectedMachine()
    {
        if (SelectedMachine is not { } machine)
            return;
        var dialog = new MachineDialog(machine.Clone(), isNew: false, _store) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var index = _machines.IndexOf(machine);
        _machines[index] = dialog.Result;
        SaveMachines();
        MachineList.SelectedIndex = index;
        FocusSelectedMachine();
    }

    private void DuplicateSelectedMachine()
    {
        if (SelectedMachine is not { } machine)
            return;
        var copy = machine.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = $"{machine.DisplayName} (copy)";
        copy.AutoConnect = false;
        _machines.Insert(_machines.IndexOf(machine) + 1, copy);
        SaveMachines();
        MachineList.SelectedItem = copy;
        FocusSelectedMachine();
    }

    private void RemoveSelectedMachine()
    {
        if (SelectedMachine is not { } machine)
            return;
        if (MessageBox.Show(this, $"Remove {machine.DisplayName}? Its key will be deleted from this computer.", "Remove computer",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var index = _machines.IndexOf(machine);
        _machines.Remove(machine);
        SaveMachines();
        if (_machines.Count > 0)
        {
            MachineList.SelectedIndex = Math.Min(index, _machines.Count - 1);
            FocusSelectedMachine();
        }
        Announce($"Removed {machine.DisplayName}");
    }

    private void MoveSelected(int delta)
    {
        var index = MachineList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _machines.Count)
            return;
        _machines.Move(index, target);
        SaveMachines();
        MachineList.SelectedIndex = target;
        FocusSelectedMachine();
    }

    private async void MachineList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedMachine is { } m && _session.IsIdle && e.OriginalSource is FrameworkElement { DataContext: Machine })
            await ConnectMachineAsync(m, m.DefaultMode, confirm: false);
    }

    private async void MachineList_KeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedMachine is not { } m)
            return;
        if (e.Key == Key.Enter && _session.IsIdle)
        {
            e.Handled = true;
            await ConnectMachineAsync(m, m.DefaultMode, confirm: false);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RemoveSelectedMachine();
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            EditSelectedMachine();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            e.Handled = true;
            MoveSelected(e.SystemKey == Key.Up ? -1 : 1);
        }
    }

    // Connecting

    public async Task QuickStartAsync(ConnectionMode mode)
    {
        if (!_session.IsIdle)
        {
            App.Current.ShowMainWindow();
            return;
        }
        if (SelectedMachine is { } machine)
            await ConnectMachineAsync(machine, mode, confirm: false);
        else
            await ShowQuickConnectAsync(mode, null);
    }

    public async Task ConnectMachineAsync(Machine machine, ConnectionMode mode, bool confirm)
    {
        if (string.IsNullOrWhiteSpace(machine.Key) || (machine.Kind == ServerKind.Relay && string.IsNullOrWhiteSpace(machine.Host)))
        {
            MessageBox.Show(this, $"{machine.DisplayName} needs a host and key before connecting.", "Missing details", MessageBoxButton.OK, MessageBoxImage.Information);
            MachineList.SelectedItem = machine;
            EditSelectedMachine();
            return;
        }
        var info = machine.ToConnectionInfo(mode);
        if (confirm && !ConfirmConnection(info))
            return;
        await _session.ConnectAsync(info, machine, machine.Kind == ServerKind.HostLocally);
    }

    private async Task ShowQuickConnectAsync(ConnectionMode? mode, ConnectionInfo? prefill)
    {
        var dialog = new QuickConnectDialog(_store, mode, prefill) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } info)
            return;
        Machine? machine = null;
        if (dialog.SaveAs is { } saved)
        {
            _machines.Add(saved);
            SaveMachines();
            MachineList.SelectedItem = saved;
            machine = saved;
        }
        await _session.ConnectAsync(info, machine, dialog.HostLocally);
    }

    /// <summary>Handle an nvdaremote:// link, confirming first as NVDA does.</summary>
    public async Task OpenLinkAsync(string url, bool confirm)
    {
        ConnectionInfo info;
        try
        {
            info = ConnectionInfo.FromUrl(url);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, $"This link could not be used: {ex.Message}", "Invalid link", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_session.IsActive)
        {
            MessageBox.Show(this, "A Remote Access session is already in progress. Disconnect before starting a new session.", "Already connected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (confirm && !ConfirmConnection(info))
            return;
        await _session.ConnectAsync(info, null, hostLocally: false);
    }

    private bool ConfirmConnection(ConnectionInfo info)
    {
        var question = info.Mode == ConnectionMode.Leader
            ? $"Do you wish to control the computer on server {info.Address} with key {info.Key}?"
            : $"Do you wish to allow this computer to be controlled on server {info.Address} with key {info.Key}?";
        return MessageBox.Show(this, question, "Remote Access connection request", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }

    public async Task<bool> ConfirmDisconnectAsync(bool exiting)
    {
        if (!_session.IsActive)
            return true;
        if (_session.IsFollower && _store.Current.ConfirmDisconnectAsFollower)
        {
            App.Current.ShowMainWindow();
            var message = exiting
                ? "Exiting will end the Remote Access session. Are you sure?"
                : "Are you sure you want to disconnect from the Remote Access session?";
            if (MessageBox.Show(this, message, "Confirm disconnection", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return false;
        }
        await _session.DisconnectAsync();
        return true;
    }

    public async Task DisconnectWithConfirmationAsync()
    {
        if (await ConfirmDisconnectAsync(exiting: false))
            FocusAfterDisconnect();
    }

    private void FocusAfterDisconnect()
    {
        if (SelectedMachine is not null)
            FocusSelectedMachine();
        else
            GetHelpButton.Focus();
    }

    public void CopyLink()
    {
        if (_session.GetShareLink() is { } link && SafeClipboard.SetText(link))
            Announce("Copied link");
    }

    private void CopyInvitation()
    {
        if (_session.GetInvitationText() is { } text && SafeClipboard.SetText(text))
            Announce("Copied invitation. Paste it into an email or chat message.");
    }

    private void CopyTranscript()
    {
        var text = string.Join(Environment.NewLine, _session.Transcript.Select(t => t.ToString()));
        if (SafeClipboard.SetText(text))
            Announce("Copied transcript");
    }

    private void ShowSettings()
    {
        var dialog = new SettingsWindow(_store, _session) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = _session.ApplySettingsAsync();
            UpdateSessionText();
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // Window behaviour

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _store?.Current.MinimizeToTray == true)
            Hide();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting)
            return;
        e.Cancel = true;
        if (_store.Current.CloseToTray)
        {
            Hide();
            return;
        }
        _exiting = true;
        await App.Current.ExitAsync();
        _exiting = false;
    }

    // ISessionUi

    public Task<CertificateDecision> AskTrustCertificateAsync(CertificateUntrustedException error)
    {
        App.Current.ShowMainWindow();
        var dialog = new CertificateDialog(error) { Owner = this };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Decision);
    }

    public Task<bool> AskReplaceRunningNvdaAsync(RunningNvda running)
    {
        App.Current.ShowMainWindow();
        var restore = _store.Current.RestoreReplacedNvda ? " It will be started again when the session ends." : "";
        var result = MessageBox.Show(this,
            "NVDA is already running on this computer. AssistBridge needs to use its own copy of NVDA during the session, " +
            $"so the running NVDA will be closed while you are connected.{restore}\n\nContinue?",
            "NVDA is running", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public void ShowMotd(string server, string message) =>
        MessageBox.Show(this, message, $"Message from Remote Access server {server}", MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
    {
        if (!IsVisible)
            App.Current.ShowMainWindow();
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public string? GetClipboardText() => SafeClipboard.GetText();

    public void SetClipboardText(string text) => SafeClipboard.SetText(text);
}
