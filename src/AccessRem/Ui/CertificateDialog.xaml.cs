using System.Windows;
using AccessRem.Core;

namespace AccessRem.Ui;

/// <summary>Asks whether to trust a server whose certificate cannot be verified (NVDA's CertificateUnauthorizedDialog).</summary>
public partial class CertificateDialog : Window
{
    public CertificateDialog(CertificateUntrustedException error)
    {
        InitializeComponent();
        AccessibleNames.Attach(this);
        DetailsBox.Text =
            $"The certificate presented by {error.Address} could not be verified. This connection may be compromised: " +
            "it is possible that someone is trying to overhear your communication.\n\n" +
            "Servers that people host themselves usually use a certificate like this. If you trust this server, " +
            "compare the fingerprint below with the one the server's owner gives you before continuing.";
        FingerprintBox.Text = FormatFingerprint(error.Fingerprint);
        Loaded += (_, _) => CancelButton.Focus();
    }

    public CertificateDecision Decision { get; private set; } = CertificateDecision.Cancel;

    private static string FormatFingerprint(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return "Unavailable";
        // Group in pairs so it can be compared aloud.
        return string.Join(":", Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2))).ToUpperInvariant();
    }

    private void TrustAlways_Click(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.TrustAlways;
        DialogResult = true;
    }

    private void Once_Click(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.ConnectOnce;
        DialogResult = true;
    }
}
