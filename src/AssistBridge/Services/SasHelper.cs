using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using AssistBridge.Core;
using Microsoft.Win32;

namespace AssistBridge.Services;

/// <summary>
/// Control+Alt+Delete support. Windows only lets a service (or a signed UI Access app) simulate the
/// Secure Attention Sequence, so AssistBridge can install a tiny optional helper service, as RIM does.
/// </summary>
public static class SasHelper
{
    public const string ServiceName = "AssistBridgeSas";
    public const string DisplayName = "AssistBridge Control+Alt+Delete Helper";
    private const string PipeName = "AssistBridgeSas";
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "SoftwareSASGeneration";

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Ask the helper service to send control+alt+delete. Returns an error message, or null on success.</summary>
    public static async Task<string?> TrySendAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            var request = Encoding.UTF8.GetBytes("sas\n");
            await pipe.WriteAsync(request, cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await pipe.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var reply = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return reply == "ok" ? null : reply;
        }
        catch (Exception ex) when (ex is System.TimeoutException or OperationCanceledException or IOException)
        {
            return "The Control+Alt+Delete helper service is not running. Install it from Settings › Advanced.";
        }
    }

    /// <summary>Install (or remove) the helper by relaunching AssistBridge elevated.</summary>
    public static bool RunElevated(string argument)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the elevation prompt.
            return false;
        }
    }

    /// <summary>Elevated: register the service and allow services to simulate the SAS.</summary>
    public static int Install()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Uninstall(quiet: true);
            RunSc($"create {ServiceName} binPath= \"\\\"{exe}\\\" --sas-service\" start= auto DisplayName= \"{DisplayName}\"");
            RunSc($"description {ServiceName} \"Lets AssistBridge send Control+Alt+Delete when a remote helper requests it.\"");
            using (var key = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true))
            {
                var current = key.GetValue(PolicyValue) as int? ?? 0;
                // 1 = services, 2 = Ease of Access apps, 3 = both. Keep whatever already allows services.
                if (current is 0 or 2)
                    key.SetValue(PolicyValue, 3, RegistryValueKind.DWord);
            }
            RunSc($"start {ServiceName}");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Installing the SAS helper failed: {ex}");
            return 1;
        }
    }

    public static int Uninstall(bool quiet = false)
    {
        try
        {
            RunSc($"stop {ServiceName}", ignoreErrors: true);
            RunSc($"delete {ServiceName}", ignoreErrors: true);
            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
                AppLog.Write($"Removing the SAS helper failed: {ex}");
            return 1;
        }
    }

    private static void RunSc(string arguments, bool ignoreErrors = false)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreErrors)
            throw new InvalidOperationException($"sc {arguments} failed: {p.StandardOutput.ReadToEnd()}");
    }

    /// <summary>Entry point when running as the Windows service.</summary>
    public static void RunService() => ServiceBase.Run(new SasService());

    private sealed class SasService : ServiceBase
    {
        private CancellationTokenSource? _stop;
        private Task? _loop;

        public SasService()
        {
            ServiceName = SasHelper.ServiceName;
            CanStop = true;
        }

        protected override void OnStart(string[] args)
        {
            _stop = new CancellationTokenSource();
            _loop = Task.Run(() => ServeAsync(_stop.Token));
        }

        protected override void OnStop()
        {
            _stop?.Cancel();
            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }
        }

        private static async Task ServeAsync(CancellationToken ct)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!ct.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, security);
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[64];
                    var read = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
                    var request = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    string reply;
                    if (request == "sas")
                    {
                        try
                        {
                            SendSAS(false);
                            reply = "ok";
                        }
                        catch (Exception ex)
                        {
                            reply = "Windows refused to send Control+Alt+Delete: " + ex.Message;
                        }
                    }
                    else
                    {
                        reply = "unknown request";
                    }
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), ct).ConfigureAwait(false);
                    await pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // Client went away; serve the next one.
                }
            }
        }
    }
}
