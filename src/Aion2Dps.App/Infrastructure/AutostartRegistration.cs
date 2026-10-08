using Microsoft.Win32;

namespace Aion2Dps.App.Infrastructure;

/// <summary>
/// "Start Aion2Dps with Windows": the per-user Run value <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Aion2Dps</c>
/// = <c>"&lt;exe&gt;" --autostart</c>. The registry is the source of truth (nothing is mirrored in settings.json), so the
/// installer (<c>-Autostart</c>), the uninstaller and the Settings page all agree. Only the value named
/// <see cref="ValueName"/> is ever read or written; other apps' Run values are never touched. Tests pass a scratch key path.
/// </summary>
public sealed class AutostartRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Aion2Dps";
    public const string AutostartArgument = "--autostart";

    /// <param name="exePath">Full path of this executable (normally <see cref="Environment.ProcessPath"/>).</param>
    /// <param name="runKeyPath">Key below HKEY_CURRENT_USER that holds the Run values.</param>
    public AutostartRegistration(string exePath, string runKeyPath = DefaultRunKeyPath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("An executable path is required.", nameof(exePath));
        if (string.IsNullOrWhiteSpace(runKeyPath)) throw new ArgumentException("A registry key path is required.", nameof(runKeyPath));
        ExePath = Path.GetFullPath(exePath);
        RunKeyPath = runKeyPath;
    }

    /// <summary>The registration for the running app, or null when its path is unknown.</summary>
    public static AutostartRegistration? ForCurrentProcess() =>
        Environment.ProcessPath is { Length: > 0 } p ? new AutostartRegistration(p) : null;

    public string ExePath { get; }
    public string RunKeyPath { get; }

    /// <summary>The command written to the Run value.</summary>
    public string Command => $"\"{ExePath}\" {AutostartArgument}";

    /// <summary>The raw Run value (null when absent or not a string).</summary>
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>True when the Run value exists and starts this executable.</summary>
    public bool IsEnabled() => PointsAtThisExe(ReadCommand());

    /// <summary>Writes the Run value for this executable (replacing an Aion2Dps value that points elsewhere).</summary>
    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new IOException($"Could not open HKCU\\{RunKeyPath}.");
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    /// <summary>Removes the Run value when it starts this executable (a value pointing at another Aion2Dps copy is left alone).</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;
        if (PointsAtThisExe(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public void Set(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }

    /// <summary>
    /// Start-up repair: when the Run value starts an Aion2Dps executable (same file name) that no longer exists — the install
    /// was moved or replaced by this one — it is pointed at this executable. Returns true when it was updated. A value that
    /// starts a different, existing program is never changed, nor is a missing value created.
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
