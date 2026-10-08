using Microsoft.Win32;

namespace Aion2Dps.App.Infrastructure;

/// <summary>
/// "Start Aion2Dps with Windows": the per-user Run value <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Aion2Dps</c>
/// = <c>"&lt;exe&gt;" --autostart</c>. The registry is the source of truth (nothing is mirrored in settings.json), so the
/// installer (<c>-Autostart</c>), the uninstaller and the Settings page all agree. Windows' own switch (Task Manager →
/// Startup apps, Settings → Apps → Startup) keeps the Run value and writes
/// <c>...\Explorer\StartupApproved\Run\Aion2Dps</c> (REG_BINARY, odd first byte = disabled); that switch is honoured too.
/// Only values named <see cref="ValueName"/> are ever read or written; other apps' values are never touched. Tests pass a
/// scratch key path (the StartupApproved key then defaults to a subkey of it, never the real one).
/// </summary>
public sealed class AutostartRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultStartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "Aion2Dps";
    public const string AutostartArgument = "--autostart";
    /// <summary>File the installer writes next to the exe (install.ps1 <c>$MarkerName</c>).</summary>
    public const string InstallMarkerName = ".aion2dps-install";

    /// <param name="exePath">Full path of this executable (normally <see cref="Environment.ProcessPath"/>).</param>
    /// <param name="runKeyPath">Key below HKEY_CURRENT_USER that holds the Run values.</param>
    /// <param name="startupApprovedKeyPath">
    /// Key below HKEY_CURRENT_USER with Windows' enabled/disabled flags. Null = the real key for the real Run key, otherwise
    /// <c>&lt;runKeyPath&gt;\StartupApproved</c> (so a test Run key never reaches the real StartupApproved key).
    /// </param>
    public AutostartRegistration(string exePath, string runKeyPath = DefaultRunKeyPath, string? startupApprovedKeyPath = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("An executable path is required.", nameof(exePath));
        if (string.IsNullOrWhiteSpace(runKeyPath)) throw new ArgumentException("A registry key path is required.", nameof(runKeyPath));
        ExePath = Path.GetFullPath(exePath);
        RunKeyPath = runKeyPath;
        StartupApprovedKeyPath = startupApprovedKeyPath
            ?? (string.Equals(runKeyPath, DefaultRunKeyPath, StringComparison.OrdinalIgnoreCase)
                ? DefaultStartupApprovedKeyPath
                : runKeyPath.TrimEnd('\\') + @"\StartupApproved");
    }

    /// <summary>The registration for the running app, or null when it is not running as Aion2Dps.exe.</summary>
    public static AutostartRegistration? ForCurrentProcess() => ForProcessPath(Environment.ProcessPath);

    /// <summary>
    /// Null unless <paramref name="processPath"/> is the app's own executable (Aion2Dps.exe). Run as <c>dotnet Aion2Dps.dll</c>
    /// the process is dotnet.exe, and a Run value for it would start the dotnet host instead of the meter.
    /// </summary>
    public static AutostartRegistration? ForProcessPath(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        var appName = typeof(AutostartRegistration).Assembly.GetName().Name ?? "Aion2Dps";
        if (!string.Equals(Path.GetFileName(processPath), appName + ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new AutostartRegistration(processPath);
    }

    public string ExePath { get; }
    public string RunKeyPath { get; }
    public string StartupApprovedKeyPath { get; }

    /// <summary>The command written to the Run value.</summary>
    public string Command => $"\"{ExePath}\" {AutostartArgument}";

    /// <summary>This executable is an install made by install.ps1 (not a build output or a loose copy).</summary>
    public bool IsInstalledCopy => File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, InstallMarkerName));

    /// <summary>The raw Run value (null when absent or not a string).</summary>
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>True when the Run value starts this executable and Windows has not switched it off.</summary>
    public bool IsEnabled() => PointsAtThisExe(ReadCommand()) && !IsSwitchedOffInWindows();

    /// <summary>The Run value starts this executable, but Task Manager / Settings → Apps → Startup switched it off.</summary>
    public bool IsDisabledByWindows() => PointsAtThisExe(ReadCommand()) && IsSwitchedOffInWindows();

    /// <summary>
    /// Writes the Run value for this executable (replacing an Aion2Dps value that points elsewhere) and clears Windows'
    /// "disabled" flag for it: ticking the box is the user switching it on again (no flag = enabled for Windows).
    /// </summary>
    public void Enable()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                         ?? throw new IOException($"Could not open HKCU\\{RunKeyPath}."))
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        DeleteApprovalFlag();
    }

    /// <summary>
    /// Removes the Run value when it starts this executable (a value pointing at another Aion2Dps copy is left alone), and
    /// Windows' flag for it unless that flag belongs to such another copy.
    /// </summary>
    public void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            var command = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (command is not null && !PointsAtThisExe(command)) return;
            if (command is not null) key!.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        DeleteApprovalFlag();
    }

    public void Set(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }

    /// <summary>
    /// Start-up repair: when the Run value starts an Aion2Dps executable (same file name) that no longer exists — the install
    /// was moved or replaced by this one — it is pointed at this executable. Returns true when it was updated. A value that
    /// starts a different, existing program is never changed, nor is a missing value created. Windows' on/off flag is kept.
    /// </summary>
    public bool Reconcile()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) return false;
        if (PointsAtThisExe(command)) return false;
        var target = TryParseExePath(command);
        if (target is null) return false;
        if (!string.Equals(Path.GetFileName(target), Path.GetFileName(ExePath), StringComparison.OrdinalIgnoreCase)) return false;
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)); }
        catch { return false; }
        if (File.Exists(full)) return false;   // another, still existing Aion2Dps copy owns it
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }

    /// <summary>
    /// The one-time default for installed copies ("start with Windows" on, as asked for by the feature): creates the Run value
    /// only when there is no Aion2Dps Run value and no Windows on/off flag for it yet, so an entry the user (or Windows)
    /// already decided about is never changed. Returns null when this is not an installed copy (the caller retries on a
    /// later start), otherwise whether the value was created.
    /// </summary>
    public bool? ApplyInstallDefault()
    {
        if (!IsInstalledCopy) return null;
        if (ReadCommand() is not null || ReadApprovalFlag() is not null) return false;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new IOException($"Could not open HKCU\\{RunKeyPath}.");
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }

    private object? ReadApprovalFlag()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
        return key?.GetValue(ValueName, null);
    }

    /// <summary>Windows' flag: REG_BINARY whose first byte is odd (03, 07) = disabled; absent or even (02, 06) = enabled.</summary>
    private bool IsSwitchedOffInWindows() => ReadApprovalFlag() is byte[] { Length: > 0 } flag && (flag[0] & 1) == 1;

    private void DeleteApprovalFlag()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private bool PointsAtThisExe(string? command)
    {
        var target = TryParseExePath(command);
        if (target is null) return false;
        try { return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)), ExePath, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The executable of a Run command: the quoted first token, or the unquoted text up to ".exe".</summary>
    public static string? TryParseExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0 && (exe + 4 == s.Length || char.IsWhiteSpace(s[exe + 4]))) return s[..(exe + 4)];
        int space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }
}
