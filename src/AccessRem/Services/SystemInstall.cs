using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AccessRem.Core;
using Microsoft.Win32;

namespace AccessRem.Services;

/// <summary>
/// Installed mode: AccessRem in Program Files, with its NVDA registered with Windows Ease of Access so that
/// Windows starts it on User Account Control and sign-in screens during a session (as an installed NVDA would).
/// Uses its own registration name so it never replaces a separately installed NVDA's registration.
/// </summary>
public static class SystemInstall
{
    public const string AtName = "accessrem_nvda";
    public const string NvdaAtName = "nvda_nvda_v1";
    private const string AccessibilityKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Accessibility";
    private const string AtKey = AccessibilityKey + @"\ATs\" + AtName;
    private const string ProductKey = @"SOFTWARE\AccessRem";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AccessRem";

    /// <summary>NVDA 2026.2's configuration schema version, used if no NVDA-written configuration is available.</summary>
    private const int FallbackSchemaVersion = 24;

    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AccessRem");

    public static string? InstalledDirectory
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProductKey);
            return key?.GetValue("InstallDir") as string;
        }
    }

    public static bool IsInstalled =>
        InstalledDirectory is { } dir && File.Exists(Path.Combine(dir, "AccessRem.exe")) && IsAtRegistered(AtName);

    /// <summary>True when this process is the installed copy.</summary>
    public static bool IsRunningInstalledCopy =>
        InstalledDirectory is { } dir && IsAtRegistered(AtName) &&
        Path.GetFullPath(AppPaths.AppDirectory).TrimEnd('\\').Equals(Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The UI Access build of NVDA in the installed copy, which must run on both desktops.</summary>
    public static string? InstalledNvdaExecutable(string nvdaDirectory)
    {
        if (!IsRunningInstalledCopy)
            return null;
        var exe = Path.Combine(nvdaDirectory, "nvda.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsAtRegistered(string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(AccessibilityKey + @"\ATs\" + name);
        return key is not null;
    }

    // Elevated operations (run as: AccessRem.exe --install-system "<source folder>")

    public static int Install(string sourceDirectory)
    {
        try
        {
            var target = DefaultInstallDirectory;
            var source = Path.GetFullPath(sourceDirectory).TrimEnd('\\');
            if (!File.Exists(Path.Combine(source, "AccessRem.exe")) || !Directory.Exists(Path.Combine(source, "nvda")))
                throw new InvalidOperationException($"{source} is not a complete AccessRem folder.");
            if (!source.Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Log($"Copying {source} to {target}");
                // When updating, the helper service runs from the installed AccessRem.exe and holds it open.
                // It is installed again below.
                SasHelper.Stop();
                CopyDirectory(source, target, skip: new[] { Path.Combine("nvda", "systemConfig") });
            }
            var nvdaDir = Path.Combine(target, "nvda");
            // Like an NVDA installation: nvda.exe is the signed UI Access build.
            File.Copy(Path.Combine(nvdaDir, "nvda_uiAccess.exe"), Path.Combine(nvdaDir, "nvda.exe"), overwrite: true);
            WriteSecureScreenConfig(nvdaDir, speakLocally: false);

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var at = hklm.CreateSubKey(AtKey, writable: true))
            {
                at.SetValue("ApplicationName", "AccessRem (NVDA)");
                at.SetValue("Description", "Screen reader used by AccessRem during remote assistance sessions");
                at.SetValue("Profile", "<HCIModel><Accommodation type=\"severe vision\"/></HCIModel>");
                at.SetValue("SimpleProfile", "screenreader");
                at.SetValue("ATExe", "nvda.exe");
                at.SetValue("StartExe", Path.Combine(nvdaDir, "nvda.exe"));
                at.SetValue("StartParams", "--ease-of-access");
                at.SetValue("TerminateOnDesktopSwitch", 0, RegistryValueKind.DWord);
            }
            using (var product = Registry.LocalMachine.CreateSubKey(ProductKey, writable: true))
                product.SetValue("InstallDir", target);

            var exe = Path.Combine(target, "AccessRem.exe");
            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "1.0.0";
            using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey, writable: true))
            {
                uninstall.SetValue("DisplayName", "AccessRem");
                uninstall.SetValue("DisplayVersion", version);
                uninstall.SetValue("Publisher", "AccessRem contributors");
                uninstall.SetValue("DisplayIcon", $"\"{exe}\",0");
                uninstall.SetValue("InstallLocation", target);
                uninstall.SetValue("UninstallString", $"\"{exe}\" --uninstall-system");
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            CreateStartMenuShortcut(exe);
            // The helper service sends Control+Alt+Delete and applies secure-screen speech settings.
            if (SasHelper.Install(exe) != 0)
                Log("The helper service could not be installed.");
            Log("Installed.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Install failed: {ex}");
            return 1;
        }
    }

    public static int Uninstall()
    {
        try
        {
            var dir = InstalledDirectory ?? DefaultInstallDirectory;
            SasHelper.Uninstall();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(AtKey, throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(ProductKey, throwOnMissingSubKey: false);
            var shortcut = StartMenuShortcutPath;
            if (File.Exists(shortcut))
                File.Delete(shortcut);
            if (Directory.Exists(dir))
            {
                // This program may be running from the folder, so remove it once we have exited.
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{dir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            Log("Uninstalled.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Uninstall failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// The configuration NVDA uses on secure screens. NVDA's Remote Access must be on there, because the copy
    /// of NVDA that Windows starts on a UAC screen joins the session through NVDA's built-in secure desktop handshake.
    /// </summary>
    public static void WriteSecureScreenConfig(string nvdaDirectory, bool speakLocally)
    {
        var dir = Path.Combine(nvdaDirectory, "systemConfig");
        Directory.CreateDirectory(dir);
        var ini = new StringBuilder();
        ini.AppendLine($"schemaVersion = {DetectSchemaVersion()}");
        ini.AppendLine("[general]");
        ini.AppendLine("\tshowWelcomeDialogAtStartup = False");
        ini.AppendLine("\taskToExit = False");
        ini.AppendLine("\tplayStartAndExitSounds = False");
        ini.AppendLine("[update]");
        ini.AppendLine("\tautoCheck = False");
        ini.AppendLine("\tstartupNotification = False");
        ini.AppendLine("\taskedAllowUsageStats = True");
        ini.AppendLine("\tallowUsageStats = False");
        ini.AppendLine("[keyboard]");
        // NVDA key: Caps Lock, numpad Insert and extended Insert.
        ini.AppendLine("\tNVDAModifierKeys = 7");
        ini.AppendLine("[speech]");
        ini.AppendLine($"\tsynth = {(speakLocally ? "auto" : "silence")}");
        ini.AppendLine("[audio]");
        ini.AppendLine("\tsoundVolumeFollowsVoice = False");
        ini.AppendLine($"\tsoundVolume = {(speakLocally ? 100 : 0)}");
        ini.AppendLine("[remote]");
        ini.AppendLine("\tenabled = True");
        File.WriteAllText(Path.Combine(dir, "nvda.ini"), ini.ToString(), new UTF8Encoding(false));
    }

    private static int DetectSchemaVersion()
    {
        // Prefer the version NVDA itself wrote for the bundled copy (correct for whichever NVDA is bundled).
        try
        {
            foreach (var userDir in Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)[..3], "Users")))
            {
                var ini = Path.Combine(userDir, "AppData", "Roaming", "AccessRem", "nvdaConfig", "nvda.ini");
                if (!File.Exists(ini))
                    continue;
                var match = Regex.Match(File.ReadAllText(ini), @"^schemaVersion\s*=\s*(\d+)", RegexOptions.Multiline);
                if (match.Success)
                    return int.Parse(match.Groups[1].Value);
            }
        }
        catch (Exception)
        {
        }
        return FallbackSchemaVersion;
    }

    private static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AccessRem.lnk");

    private static void CreateStartMenuShortcut(string exe)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(StartMenuShortcutPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "Remote assistance with NVDA Remote Access";
            link.Save();
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log($"Unable to create the Start menu shortcut: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination, string[] skip, string relative = "")
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var rel = Path.Combine(relative, Path.GetFileName(dir));
            if (skip.Contains(rel, StringComparer.OrdinalIgnoreCase) || Path.GetFileName(dir) == "__pycache__")
                continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), skip, rel);
        }
    }

    private static void Log(string message) => AppLog.Write("[install] " + message);
}

/// <summary>
/// Tells the Windows Ease of Access broker which screen readers are running, which decides what Windows starts
/// on User Account Control and sign-in screens. Mirrors NVDA's easeOfAccess.notify (3 = running, 2 = stopped).
/// </summary>
public static class AtBroker
{
    private const string TempKey = @"Software\Microsoft\Windows NT\CurrentVersion\AccessibilityTemp";

    public static void Notify(IReadOnlyDictionary<string, int> states)
    {
        if (states.Count == 0)
            return;
        using (var key = Registry.CurrentUser.CreateSubKey(TempKey))
        {
            foreach (var (name, state) in states)
                key.SetValue(name, state, RegistryValueKind.DWord);
        }
        // The broker re-reads these values when Windows+U is pressed, exactly as NVDA signals it.
        SendWindowsU();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static void SendWindowsU()
    {
        const uint KEYUP = 2;
        var keys = new List<(ushort Vk, bool Desired)>();
        // Release modifiers the user might be holding, then press Windows+U.
        foreach (var vk in new ushort[] { 0x10, 0x11, 0x12 })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                keys.Add((vk, false));
        keys.Add((0x5B, true));
        keys.Add((0x55, true));
        var inputs = new List<INPUT>();
        foreach (var (vk, desired) in keys)
            inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = desired ? 0 : KEYUP } });
        foreach (var (vk, desired) in Enumerable.Reverse(keys))
            inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = desired ? KEYUP : 0 } });
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
