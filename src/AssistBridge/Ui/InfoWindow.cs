using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AssistBridge.Core;

namespace AssistBridge.Ui;

/// <summary>Help, keyboard shortcut and about text, shown in a read-only text field that screen readers can review freely.</summary>
public sealed class InfoWindow : Window
{
    private InfoWindow(Window owner, string title, string text)
    {
        Owner = owner;
        Title = title;
        Width = 700;
        Height = 600;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "AppWindow");
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(10),
            IsReadOnlyCaretVisible = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(box);
        Content = panel;
        Loaded += (_, _) => box.Focus();
    }

    public static void ShowHelp(Window owner) => new InfoWindow(owner, "How AssistBridge works", HelpText).ShowDialog();

    public static void ShowShortcuts(Window owner, AppSettings settings) =>
        new InfoWindow(owner, "Keyboard shortcuts", ShortcutsText(settings)).ShowDialog();

    public static void ShowAbout(Window owner, string? nvdaVersion)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var text =
            $"AssistBridge {version}\n\n" +
            "Remote assistance that works with NVDA Remote Access (NVDA 2025.1 and later) and the NVDA Remote add-on.\n\n" +
            $"Speech engine: {(nvdaVersion is null ? "the bundled copy of NVDA (starts with each session)" : $"NVDA {nvdaVersion}")}\n" +
            $"Settings and logs: {AppPaths.DataDirectory}\n\n" +
            "AssistBridge is free software under the GNU General Public License, version 2 or later. " +
            "It includes NVDA, copyright NV Access Limited and contributors, distributed under the GNU General Public License version 2 " +
            "with additional permissions. NVDA's source code is available from https://github.com/nvaccess/nvda.\n\n" +
            "AssistBridge is an independent project and is not made or endorsed by NV Access.";
        new InfoWindow(owner, "About AssistBridge", text).ShowDialog();
    }

    public static string DescribeGesture(string gesture)
    {
        var g = gesture.StartsWith("kb:", StringComparison.OrdinalIgnoreCase) ? gesture[3..] : gesture;
        return string.Join("+", g.Split('+').Select(part => part.ToLowerInvariant() switch
        {
            "nvda" => "NVDA (Insert)",
            "control" or "ctrl" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            "windows" => "Windows",
            _ => part.Length <= 3 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..],
        }));
    }

    private static string ShortcutsText(AppSettings s)
    {
        string List(IEnumerable<string> gestures) => gestures.Any() ? string.Join(" or ", gestures.Select(DescribeGesture)) : "(none set)";
        return
            "In the AssistBridge window\n" +
            "  Ctrl+1  Get help (let a helper control this computer)\n" +
            "  Ctrl+2  Give help (control another computer)\n" +
            "  Ctrl+O  Connect with a link or one-time details\n" +
            "  Enter  Connect to the selected computer using its usual action\n" +
            "  Ctrl+N  Add a computer\n" +
            "  F2  Edit the selected computer\n" +
            "  Delete  Remove the selected computer\n" +
            "  Alt+Up / Alt+Down  Reorder computers\n" +
            "  Ctrl+T  Switch the keyboard between this and the remote computer\n" +
            "  Ctrl+M  Mute or unmute the remote computer\n" +
            "  Ctrl+Shift+C  Send the clipboard\n" +
            "  Ctrl+L  Copy a link to this session\n" +
            "  Ctrl+Shift+L  Copy an invitation you can paste into an email or chat\n" +
            "  Ctrl+D  Disconnect\n" +
            "  Ctrl+Comma  Settings\n" +
            "  F1  Help\n\n" +
            "Anywhere, during a session (handled by AssistBridge's copy of NVDA)\n" +
            $"  {List(s.ToggleControlGestures)}  Switch the keyboard between this and the remote computer\n" +
            $"  {List(s.PushClipboardGestures)}  Send the clipboard\n" +
            $"  {List(s.SendSasGestures)}  Send Control+Alt+Delete to the remote computer\n" +
            $"  {List(s.ToggleMuteGestures)}  Mute or unmute the remote computer\n\n" +
            "While the keyboard controls the remote computer, every other key goes to that computer. " +
            "NVDA commands are carried out by the remote computer's NVDA, using its keyboard layout.";
    }

    private const string HelpText =
        "AssistBridge connects two computers using NVDA Remote Access, the remote support built into NVDA 2025.1 and later " +
        "(and compatible with the NVDA Remote add-on). One computer controls; the other is controlled.\n\n" +
        "GETTING HELP\n" +
        "Choose Get help. A helper who uses NVDA can then control this computer from anywhere. AssistBridge starts its own copy of NVDA, " +
        "which reads this computer to your helper — exactly what they would hear on their own computer. You do not hear it unless you turn that on in Settings. " +
        "You stay in charge: choose Disconnect at any time.\n\n" +
        "GIVING HELP\n" +
        "Choose Give help to control a computer that is running NVDA (or AssistBridge). Its screen reader is spoken on this computer, " +
        "the Speech tab shows everything that is said, and a connected braille display shows its braille. " +
        "Press Ctrl+T or the Control button to send your keyboard to the remote computer; press Control+Alt+Shift+F11 (or NVDA+Alt+Tab) to bring it back.\n\n" +
        "COMPUTERS, SERVERS AND KEYS\n" +
        "Both computers connect to the same relay server (nvdaremote.com is a free public server) with the same key, which works like a password for the session. " +
        "Save the details as a computer so you can connect again with one key press. Choose Generate key to get a fresh key from the server.\n\n" +
        "DIRECT CONNECTIONS\n" +
        "Instead of a relay server, one computer can host the session: choose 'Host the connection on this computer'. " +
        "The other person then connects to this computer's address with the same port and key. The port must be reachable, which usually means " +
        "forwarding it on your router. Use 'Get external address and check port' to test.\n\n" +
        "LINKS\n" +
        "Copy link creates an nvdaremote:// link that opens the session in the opposite role on the other computer. " +
        "Copy invitation creates a short message with the link and the manual details. AssistBridge can also open nvdaremote:// links (see Settings).\n\n" +
        "CLIPBOARD AND CONTROL+ALT+DELETE\n" +
        "Send clipboard copies your clipboard text to the other computer. A helper can send Control+Alt+Delete to this computer once the optional helper service is installed (Settings › Getting help).\n\n" +
        "SECURITY\n" +
        "Connections are encrypted. If a server's certificate cannot be verified, you are shown its fingerprint and asked before connecting. " +
        "Keys are stored encrypted for your Windows account.\n\n" +
        "LIMITS\n" +
        "Like a portable copy of NVDA, the copy inside AssistBridge cannot read or operate the secure screens Windows shows for User Account Control and sign-in, " +
        "or programs running as administrator, unless AssistBridge is run as administrator.";
}
