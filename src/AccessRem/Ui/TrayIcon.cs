using System.ComponentModel;
using System.Drawing;
using AccessRem.Core;
using Forms = System.Windows.Forms;

namespace AccessRem.Ui;

/// <summary>Notification area icon with quick session actions and status notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _getHelp;
    private readonly Forms.ToolStripMenuItem _giveHelp;
    private readonly Forms.ToolStripMenuItem _toggleControl;
    private readonly Forms.ToolStripMenuItem _mute;
    private readonly Forms.ToolStripMenuItem _pushClipboard;
    private readonly Forms.ToolStripMenuItem _copyLink;
    private readonly Forms.ToolStripMenuItem _disconnect;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new Forms.ContextMenuStrip();
        _status = new Forms.ToolStripMenuItem("Not connected") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("&Open AccessRem", null, (_, _) => _app.ShowMainWindow());
        _getHelp = new Forms.ToolStripMenuItem("&Get help (let this computer be controlled)", null, async (_, _) => await _app.Main.QuickStartAsync(ConnectionMode.Follower));
        _giveHelp = new Forms.ToolStripMenuItem("Gi&ve help (control another computer)", null, async (_, _) => await _app.Main.QuickStartAsync(ConnectionMode.Leader));
        _toggleControl = new Forms.ToolStripMenuItem("&Control the remote computer", null, async (_, _) => await _app.Session.ToggleControlAsync());
        _mute = new Forms.ToolStripMenuItem("&Mute remote", null, async (_, _) => await _app.Session.ToggleMuteAsync());
        _pushClipboard = new Forms.ToolStripMenuItem("&Send clipboard", null, async (_, _) => await _app.Session.PushClipboardAsync());
        _copyLink = new Forms.ToolStripMenuItem("Copy &link", null, (_, _) => _app.Main.CopyLink());
        _disconnect = new Forms.ToolStripMenuItem("&Disconnect", null, async (_, _) => await _app.Main.DisconnectWithConfirmationAsync());
        menu.Items.AddRange(new Forms.ToolStripItem[] { _getHelp, _giveHelp, _toggleControl, _mute, _pushClipboard, _copyLink, _disconnect });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("E&xit", null, async (_, _) => await _app.ExitAsync());
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AccessRem",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _app.ShowMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) => _app.ShowMainWindow();
        _app.Session.PropertyChanged += OnSessionChanged;
        Refresh();
    }

    private static Icon LoadIcon()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))?.Stream;
        return stream is null ? SystemIcons.Application : new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var s = _app.Session;
        var status = s.IsActive ? $"{s.Status}{(string.IsNullOrEmpty(s.PeerSummary) ? "" : " · " + s.PeerSummary)}" : "Not connected";
        _status.Text = status;
        var tip = $"AccessRem — {status}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        _getHelp.Visible = _giveHelp.Visible = !s.IsActive;
        _toggleControl.Visible = _mute.Visible = s.IsLeader;
        _toggleControl.Enabled = s.CanControl || s.SendingKeys;
        _toggleControl.Text = s.SendingKeys ? "&Control this computer again" : "&Control the remote computer";
        _mute.Checked = s.Muted;
        _pushClipboard.Visible = _copyLink.Visible = _disconnect.Visible = s.IsActive;
        _pushClipboard.Enabled = s.IsConnected;
    }

    /// <summary>Show a notification unless the main window is already in front of the user.</summary>
    public void Notify(string text, bool windowActive)
    {
        if (windowActive || !_app.Settings.Current.ShowNotifications)
            return;
        _icon.ShowBalloonTip(4000, "AccessRem", text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _app.Session.PropertyChanged -= OnSessionChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
