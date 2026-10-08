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
                CancelPendingDeletes(target);
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
                RemoveInUseTolerant(dir);
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
        // Laptop layout, with Caps Lock, numpad Insert and extended Insert as the NVDA key.
        ini.AppendLine("\tkeyboardLayout = laptop");
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

    /// <summary>
    /// The installed AccessRem.exe that this copy hands over to as it starts: the copy installed for all users
    /// does everything any other copy does, and also works on secure screens, so a second copy (for example one
    /// installed just for this user) only runs while it is newer than the installed one. Set
    /// ACCESSREM_NO_HANDOFF=1 to run another copy anyway, for example a development build.
    /// </summary>
    public static string? HandOffTarget()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("ACCESSREM_NO_HANDOFF") is { Length: > 0 } || !IsInstalled || IsRunningInstalledCopy)
                return null;
            var installed = Path.Combine(InstalledDirectory!, "AccessRem.exe");
            var theirs = Version.Parse(FileVersionInfo.GetVersionInfo(installed).FileVersion ?? "");
            var ours = Version.Parse(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion ?? "");
            return theirs >= ours ? installed : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to compare with the installed copy: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// After installing for all users from another copy: that copy's own Start menu shortcut would duplicate
    /// the installed one, so remove it, and point its desktop shortcut at the installed copy.
    /// </summary>
    public static void RetireUserShortcuts(string fromExe, string toExe)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AccessRem.lnk");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AccessRem.lnk");
            foreach (var path in new[] { startMenu, desktop })
            {
                if (!File.Exists(path))
                    continue;
                dynamic link = shell.CreateShortcut(path);
                if (string.Equals((string)link.TargetPath, fromExe, StringComparison.OrdinalIgnoreCase))
                {
                    if (path == startMenu)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        link.TargetPath = toExe;
                        link.WorkingDirectory = Path.GetDirectoryName(toExe);
                        link.Save();
                    }
                }
                Marshal.FinalReleaseComObject(link);
            }
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log($"Unable to update this user's shortcuts: {ex.Message}");
        }
    }

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
            CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var rel = Path.Combine(relative, Path.GetFileName(dir));
            if (skip.Contains(rel, StringComparer.OrdinalIgnoreCase) || Path.GetFileName(dir) == "__pycache__")
                continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), skip, rel);
        }
    }

    // NVDA loads its IAccessible2 proxy DLLs (nvda\lib\<version>) into every program it reads, and they stay
    // loaded until those programs exit, Explorer included. Such a file cannot be replaced or deleted, but it can
    // be renamed, so it is moved aside and deleted when Windows restarts, as NVDA's own installer does.

    private static void CopyFile(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            var replacement = new FileInfo(source);
            // The same file (zip extraction and copying keep the time): nothing to replace.
            if (existing.Length == replacement.Length && existing.LastWriteTimeUtc == replacement.LastWriteTimeUtc)
                return;
            MoveAside(destination);
            File.Copy(source, destination);
        }
    }

    private static void MoveAside(string path)
    {
        var aside = $"{path}.{Guid.NewGuid():N}.delete";
        File.Move(path, aside);
        if (!MoveFileEx(aside, null, MoveFileDelayUntilReboot))
            Log($"Unable to schedule {aside} for deletion: error {Marshal.GetLastWin32Error()}");
        Log($"{path} is in use; replaced it, and the old copy is deleted when Windows restarts.");
    }

    /// <summary>
    /// Deletes what it can in <paramref name="dir"/>. Files in use are deleted when Windows restarts, and so are
    /// folders that are not empty yet. The running program is left for the caller.
    /// </summary>
    private static void RemoveInUseTolerant(string dir)
    {
        var self = Environment.ProcessPath;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MoveFileEx(file, null, MoveFileDelayUntilReboot);
                Log($"{file} is in use; it is deleted when Windows restarts.");
            }
        }
        // Deepest first, so that each folder is empty (or scheduled after its contents) when its turn comes.
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(sub);
            }
            catch (IOException)
            {
                MoveFileEx(sub, null, MoveFileDelayUntilReboot);
            }
        }
        if (Directory.EnumerateFileSystemEntries(dir).Any(e => !string.Equals(e, self, StringComparison.OrdinalIgnoreCase)))
            MoveFileEx(dir, null, MoveFileDelayUntilReboot);
    }

    /// <summary>
    /// An uninstall since Windows last restarted may have scheduled files in <paramref name="dir"/> for deletion
    /// at restart. Those deletions go by path, so they would remove the files being installed again: cancel them,
    /// except for old copies moved aside (*.delete).
    /// </summary>
    private static void CancelPendingDeletes(string dir)
    {
        const string Value = "PendingFileRenameOperations";
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", writable: true);
        if (key?.GetValue(Value) is not string[] operations)
            return;
        // Pairs of (path, new path); an empty new path means delete. Paths look like \??\C:\Program Files\...,
        // and Windows 11 puts flags in front ("*1\??\C:\...").
        var folder = @"\??\" + Path.GetFullPath(dir).TrimEnd('\\');
        var kept = new List<string>();
        for (var i = 0; i + 1 < operations.Length; i += 2)
        {
            var at = operations[i].IndexOf(@"\??\", StringComparison.Ordinal);
            var path = at >= 0 ? operations[i][at..] : operations[i];
            var inFolder = path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);
            if (inFolder && operations[i + 1].Length == 0 && !path.EndsWith(".delete", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(operations[i]);
            kept.Add(operations[i + 1]);
        }
        if (kept.Count == operations.Length)
            return;
        if (kept.Count == 0)
            key.DeleteValue(Value);
        else
            key.SetValue(Value, kept.ToArray(), RegistryValueKind.MultiString);
        Log($"Cancelled {(operations.Length - kept.Count) / 2} deletions at restart that an earlier uninstall scheduled.");
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

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
