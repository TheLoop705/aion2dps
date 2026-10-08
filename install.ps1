# Aion2Dps installer: installs Npcap (if missing) and the latest Aion2Dps release for the current user.
#
#   Install / update:  irm https://raw.githubusercontent.com/TheLoop705/aion2dps/main/install.ps1 | iex
#   With options:      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/TheLoop705/aion2dps/main/install.ps1))) -Uninstall
#
# Options (or the matching environment variable, for the one-liner):
#   -Version 0.2.0        AION2DPS_VERSION        install a specific release instead of the latest
#   -InstallDir <path>    AION2DPS_INSTALL_DIR    default %LOCALAPPDATA%\Programs\Aion2Dps (an existing install is reused)
#   -SkipNpcap            AION2DPS_SKIP_NPCAP=1   don't check / install Npcap
#   -NoShortcut           AION2DPS_NO_SHORTCUT=1  no Start menu or desktop shortcut
#   -NoDesktopShortcut    AION2DPS_NO_DESKTOP_SHORTCUT=1
#   -NoLaunch             AION2DPS_NO_LAUNCH=1    don't start the meter afterwards
#   -Autostart            AION2DPS_AUTOSTART=1    start Aion2Dps with Windows now (in the tray; the overlay appears with AION 2),
#                                                 even if it was switched off before. Without it the app switches this on
#                                                 once, the first time it runs, and leaves it to Settings > Startup after.
#   -Uninstall [-Purge]   AION2DPS_UNINSTALL=1    remove Aion2Dps (-Purge also deletes settings and fight history)
#   -ZipPath <zip>        AION2DPS_ZIP            install from a local release ZIP (offline / testing)
#
# Npcap is the packet-capture driver Wireshark uses. Its free license allows personal use on up to 5 PCs but does not
# allow silent installs or redistribution, so this script downloads the official installer from npcap.com, verifies
# its Nmap Software LLC signature and opens it with the right options preselected. You confirm it yourself (UAC).
param(
    [Alias('Version')][string]$A2dVersion,
    [Alias('InstallDir')][string]$A2dInstallDir,
    [Alias('SkipNpcap')][switch]$A2dSkipNpcap,
    [Alias('NoShortcut')][switch]$A2dNoShortcut,
    [Alias('NoDesktopShortcut')][switch]$A2dNoDesktopShortcut,
    [Alias('NoLaunch')][switch]$A2dNoLaunch,
    [Alias('Uninstall')][switch]$A2dUninstall,
    [Alias('Purge')][switch]$A2dPurge,
    [Alias('ZipPath')][string]$A2dZipPath,
    [Alias('Autostart')][switch]$A2dAutostart
)

# Keep installer preferences and helper functions out of the caller's session.
& {
    param($Opt)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far faster without the progress bar

    $Repo = 'TheLoop705/aion2dps'
    $NpcapFallbackUrl = 'https://npcap.com/dist/npcap-1.89.exe'
    $UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Aion2Dps'
    # "Start with Windows": the value 'Aion2Dps' below HKCU\<RunKey> (the app reads the registry; nothing in settings.json).
    $RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $RunValue = 'Aion2Dps'
    # Windows' own on/off switch for it (Task Manager > Startup apps): REG_BINARY 'Aion2Dps' below HKCU\<ApprovedKey>.
    $ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    # Test hook (undocumented): AION2DPS_TEST_RUN_KEY redirects the Run key to another key below HKCU\Software (and the
    # StartupApproved key to its subkey 'StartupApproved', as the app does), so installer tests never modify the real keys.
    # The generated uninstall.ps1 honours it too.
    if ($env:AION2DPS_TEST_RUN_KEY -match '^Software\\[^\\]') {
        $RunKey = $env:AION2DPS_TEST_RUN_KEY.TrimEnd('\')
        $ApprovedKey = "$RunKey\StartupApproved"
    }
    $MarkerName = '.aion2dps-install'
    $ManifestName = 'install-manifest.txt'
    $DefaultDir = Join-Path $env:LOCALAPPDATA 'Programs\Aion2Dps'

    function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
    function Write-Info([string]$text) { Write-Host "    $text" }
    function Write-Good([string]$text) { Write-Host "    $text" -ForegroundColor Green }
    function Write-Warn2([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

    function Test-Flag([string]$name) {
        $v = [Environment]::GetEnvironmentVariable($name)
        return [bool]($v -and ($v -notin @('0', 'false', 'no')))
    }
    function Resolve-UserPath([string]$path) {
        # Relative paths resolve against the current PowerShell location (not the process working directory).
        return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath([Environment]::ExpandEnvironmentVariables($path))
    }

    # ── options ──
    if (-not $Opt.Version) { $Opt.Version = $env:AION2DPS_VERSION }
    if (-not $Opt.ZipPath) { $Opt.ZipPath = $env:AION2DPS_ZIP }
    if (-not $Opt.InstallDir) { $Opt.InstallDir = $env:AION2DPS_INSTALL_DIR }
    $explicitDir = [bool]$Opt.InstallDir
    if (-not $explicitDir) {
        # Reuse the location of an existing install so updates and uninstalls find it.
        $previous = $null
        try { $previous = (Get-ItemProperty -Path $UninstallKey -ErrorAction Stop).InstallLocation } catch { }
        $Opt.InstallDir = if ($previous) { $previous } else { $DefaultDir }
    }
    foreach ($pair in @(@('SkipNpcap', 'AION2DPS_SKIP_NPCAP'), @('NoShortcut', 'AION2DPS_NO_SHORTCUT'),
                        @('NoDesktopShortcut', 'AION2DPS_NO_DESKTOP_SHORTCUT'), @('NoLaunch', 'AION2DPS_NO_LAUNCH'),
                        @('Uninstall', 'AION2DPS_UNINSTALL'), @('Purge', 'AION2DPS_PURGE'), @('Autostart', 'AION2DPS_AUTOSTART'))) {
        if (Test-Flag $pair[1]) { $Opt[$pair[0]] = $true }
    }

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Aion2Dps requires Windows.' }
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Aion2Dps requires 64-bit Windows 10 or 11.' }
    if ($Opt.Version -and $Opt.Version -notmatch '^v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw 'Version must be a release version such as v0.2.0 or 0.2.0.'
    }

    # Normalize, but never trim a root ("C:\" -> "C:" would mean "current folder on drive C").
    $Destination = [IO.Path]::GetFullPath((Resolve-UserPath $Opt.InstallDir))
    if ($Destination.Length -gt [IO.Path]::GetPathRoot($Destination).Length) { $Destination = $Destination.TrimEnd('\', '/') }
    $appPath = Join-Path $Destination 'Aion2Dps.exe'
    $cliPath = Join-Path $Destination 'aion2dps-cli.exe'
    $markerPath = Join-Path $Destination $MarkerName
    $manifestPath = Join-Path $Destination $ManifestName

    function Get-LinkPath([Environment+SpecialFolder]$folder) {
        $dir = [Environment]::GetFolderPath($folder)   # '' when the folder does not exist (e.g. unlinked OneDrive)
        if ($dir -and (Test-Path -LiteralPath $dir -PathType Container)) { return (Join-Path $dir 'Aion2Dps.lnk') }
        return $null
    }
    $startMenuLink = Get-LinkPath ([Environment+SpecialFolder]::Programs)
    $desktopLink = Get-LinkPath ([Environment+SpecialFolder]::DesktopDirectory)

    function Assert-SafeDestination {
        $full = $Destination
        if ($full.TrimEnd('\', '/') -ieq ([IO.Path]::GetPathRoot($full)).TrimEnd('\', '/')) {
            throw "Refusing to install into a drive root ($full). Choose a folder such as $DefaultDir."
        }
        $protected = @(
            [Environment]::GetFolderPath('UserProfile'), [Environment]::GetFolderPath('Desktop'),
            [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('MyDocuments'),
            [Environment]::GetFolderPath('LocalApplicationData'), [Environment]::GetFolderPath('ApplicationData'),
            [Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86'),
            [Environment]::GetFolderPath('Windows'), (Join-Path $env:LOCALAPPDATA 'Programs')
        ) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
        if ($protected -contains $full) { throw "Refusing to install directly into $full. Use a dedicated folder, e.g. $(Join-Path $full 'Aion2Dps')." }
        if ((Test-Path -LiteralPath $full -PathType Container) -and
            -not (Test-Path -LiteralPath $markerPath) -and -not (Test-Path -LiteralPath $appPath) -and
            @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) {
            throw "$full already contains other files. Choose an empty folder (e.g. $(Join-Path $full 'Aion2Dps'))."
        }
    }

    function Test-Interactive {
        try {
            if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) { return $false }
            if (@([Environment]::GetCommandLineArgs() | Where-Object { $_ -match '^-noni' }).Count -gt 0) { return $false }
            return $true
        } catch { return $false }
    }

    function Confirm-Yes([string]$question) {
        if (-not (Test-Interactive)) { return $true }
        try { $answer = Read-Host "    $question [Y/n]" } catch { return $true }
        return ($answer -eq '' -or $answer -match '^(y|yes|j|ja)$')
    }

    function Test-IsElevated {
        try { return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        catch { return $false }
    }

    function Stop-InstalledApp {
        foreach ($process in @(Get-Process -Name 'Aion2Dps', 'aion2dps-cli' -ErrorAction SilentlyContinue)) {
            $processPath = $null
            try { $processPath = $process.Path } catch { }
            if ($processPath -and (($processPath -ieq $appPath) -or ($processPath -ieq $cliPath))) {
                Write-Info "Closing the running $($process.ProcessName) ..."
                try { & taskkill.exe /PID $process.Id 2>$null | Out-Null } catch { }   # polite close first
                if (-not $process.WaitForExit(4000)) {
                    try { $process | Stop-Process -Force; $process.WaitForExit(5000) | Out-Null } catch { }
                }
            }
        }
        # A copy running as administrator is invisible to Stop-Process: detect locked files instead of half-copying.
        foreach ($file in @($appPath, $cliPath)) {
            if (Test-Path -LiteralPath $file) {
                try { [IO.File]::Open($file, 'Open', 'ReadWrite', 'None').Dispose() }
                catch { throw 'Aion2Dps is still running (maybe as administrator). Right-click its tray icon > Quit, then run the installer again.' }
            }
        }
    }

    # ─────────────────────────── Npcap ───────────────────────────

    function Get-NpcapState {
        $dll = Join-Path $env:SystemRoot 'System32\Npcap\wpcap.dll'
        $service = Get-Service -Name 'npcap' -ErrorAction SilentlyContinue
        $options = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Npcap', 'HKLM:\SOFTWARE\Npcap' -ErrorAction SilentlyContinue | Select-Object -First 1
        $installed = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst',
                                      'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst' -ErrorAction SilentlyContinue | Select-Object -First 1
        $version = $null
        if ($installed) { $version = $installed.DisplayVersion }
        # Test hook (with AION2DPS_NPCAP_DRYRUN): pretend Npcap is missing to exercise download + signature check.
        $forceMissing = Test-Flag 'AION2DPS_TEST_NO_NPCAP'
        [pscustomobject]@{
            Installed = (-not $forceMissing) -and (Test-Path -LiteralPath $dll) -and ($null -ne $service)
            Running   = ($null -ne $service) -and ($service.Status -eq 'Running')
            Version   = $version
            AdminOnly = [bool]($options -and $options.AdminOnly -eq 1)
        }
    }

    function Get-NpcapInstallerUrl {
        try {
            $html = (Invoke-WebRequest -UseBasicParsing -Uri 'https://npcap.com/' -TimeoutSec 20).Content
            $versions = @([regex]::Matches($html, 'dist/npcap-(\d+(?:\.\d+)+)\.exe') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
            if ($versions.Count -gt 0) {
                $latest = $versions | Sort-Object { [version]$_ } | Select-Object -Last 1
                return "https://npcap.com/dist/npcap-$latest.exe"
            }
        } catch { Write-Info "Could not read npcap.com ($($_.Exception.Message)); using a known version." }
        return $NpcapFallbackUrl
    }

    function Test-NpcapSignature([string]$path) {
        $signature = Get-AuthenticodeSignature -FilePath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or -not $signature.SignerCertificate) { return $false }
        $cert = $signature.SignerCertificate
        $commonName = $cert.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        return ($commonName -ceq 'Nmap Software LLC') -and ($cert.Subject -match '(^|,\s*)O=Nmap Software LLC(,|$)')
    }

    function Save-NpcapInstaller([string]$workDir) {
        $url = Get-NpcapInstallerUrl
        foreach ($candidate in @($url, $NpcapFallbackUrl) | Select-Object -Unique) {
            $file = Join-Path $workDir ([IO.Path]::GetFileName($candidate))
            try {
                Write-Info "Downloading $candidate ..."
                Invoke-WebRequest -UseBasicParsing -Uri $candidate -OutFile $file -TimeoutSec 300
                return $file
            } catch { Write-Warn2 "Download failed ($($_.Exception.Message))." }
        }
        return $null
    }

    # Returns $true when Npcap is installed and running afterwards.
    function Install-Npcap([string]$workDir) {
        $state = Get-NpcapState
        if ($state.Installed) {
            Write-Good "Npcap $($state.Version) is installed."
            if ($state.AdminOnly) {
                Write-Warn2 'Npcap is restricted to administrators, so Aion2Dps would have to run as administrator.'
                Write-Warn2 "Fix: reinstall Npcap from https://npcap.com and untick 'Restrict Npcap driver's access to Administrators only'."
            }
            if (-not $state.Running) { Write-Warn2 'The Npcap driver is installed but not running. Restart Windows or reinstall Npcap.'; return $false }
            return $true
        }

        Write-Info 'Npcap (the capture driver Wireshark uses) is required for live capture and is not installed.'
        Write-Info 'The official installer opens after a UAC prompt. Keep "Install Npcap in WinPcap API-compatible Mode"'
        Write-Info 'ticked and "Restrict Npcap driver''s access to Administrators only" unticked (both are preselected).'
        if (-not (Confirm-Yes 'Download and run the official Npcap installer now?')) { Write-Warn2 'Skipped.'; return $false }

        try {
            $installer = Save-NpcapInstaller $workDir
            if (-not $installer) { Write-Warn2 'Could not download the Npcap installer.'; return $false }
            # Hold the file open (read-only sharing) from verification until the installer exits, so it can't be swapped.
            $lock = [IO.File]::Open($installer, 'Open', 'Read', 'Read')
            try {
                if (-not (Test-NpcapSignature $installer)) {
                    Write-Warn2 'The downloaded Npcap installer is not validly signed by Nmap Software LLC; it was not run.'
                    return $false
                }
                Write-Good 'Npcap installer signature verified (Nmap Software LLC).'
                if (Test-Flag 'AION2DPS_NPCAP_DRYRUN') { Write-Warn2 'Dry run: not starting the Npcap installer.'; return $false }

                Write-Info 'Starting the Npcap installer; confirm the UAC prompt and click through it ...'
                try {
                    $process = Start-Process -FilePath $installer -ArgumentList '/winpcap_mode=yes', '/admin_only=no' -Verb RunAs -Wait -PassThru
                    $exitCode = $process.ExitCode
                } catch {
                    Write-Warn2 "The Npcap installer did not start ($($_.Exception.Message))."
                    return $false
                }
            } finally { $lock.Dispose() }
        } catch {
            Write-Warn2 "Npcap could not be installed: $($_.Exception.Message)"
            return $false
        }

        $state = Get-NpcapState
        if (-not $state.Installed) { Write-Warn2 "Npcap does not appear to be installed (installer exit code $exitCode)."; return $false }
        Write-Good "Npcap $($state.Version) installed."
        if ($state.AdminOnly) { Write-Warn2 'Npcap was installed in administrators-only mode: run Aion2Dps as administrator or reinstall Npcap without that option.' }
        if (-not $state.Running) { Write-Warn2 'The Npcap driver is not running yet; restart Windows if live capture does not start.'; return $false }
        return $true
    }

    # ─────────────────────────── Aion2Dps ───────────────────────────

    function Get-RedirectTarget([string]$url) {
        $request = [Net.HttpWebRequest]::Create($url)
        $request.AllowAutoRedirect = $false
        $request.Method = 'HEAD'
        $request.UserAgent = 'Aion2Dps-Installer'
        $request.Timeout = 30000
        $response = $null
        try {
            try { $response = $request.GetResponse() } catch [Net.WebException] { $response = $_.Exception.Response; if (-not $response) { throw } }
            return @{ Status = [int]$response.StatusCode; Location = $response.Headers['Location'] }
        } finally { if ($response) { $response.Close() } }
    }

    function Invoke-Download([string]$url, [string]$outFile, [int]$timeoutSec) {
        try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $outFile -Headers @{ 'User-Agent' = 'Aion2Dps-Installer' } -TimeoutSec $timeoutSec }
        catch {
            $status = $null
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
            if ($status -eq 404) { throw "Not found on GitHub: $url" }
            if ($status -in 403, 429) { throw 'GitHub is temporarily limiting downloads from your network. Wait a while and try again, or download the ZIP from https://github.com/TheLoop705/aion2dps/releases.' }
            throw "Could not download $url ($($_.Exception.Message))."
        }
    }

    function Get-Package([string]$workDir) {
        if ($Opt.ZipPath) {
            $zip = Resolve-UserPath $Opt.ZipPath
            if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "ZIP not found: $zip" }
            Write-Info "Using local package $zip"
            return [pscustomobject]@{ Path = $zip; Tag = 'local' }
        }

        if ($Opt.Version) {
            $tag = if ($Opt.Version.StartsWith('v')) { $Opt.Version } else { 'v' + $Opt.Version }
        } else {
            # Resolve "latest" through the github.com redirect (not subject to the REST API rate limit).
            Write-Info 'Finding the latest release ...'
            try { $redirect = Get-RedirectTarget "https://github.com/$Repo/releases/latest" }
            catch { throw "Could not reach GitHub ($($_.Exception.Message)). Check your internet connection." }
            if ($redirect.Location -notmatch '/releases/tag/(?<tag>[^/?#]+)') { throw 'Could not determine the latest Aion2Dps release on GitHub.' }
            $tag = [Uri]::UnescapeDataString($Matches['tag'])
        }
        if ($tag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Unexpected release tag '$tag'." }

        $assetName = "Aion2Dps-$tag-win-x64.zip"
        $base = "https://github.com/$Repo/releases/download/$tag/"
        $archivePath = Join-Path $workDir $assetName
        Write-Info "Downloading $assetName ..."
        Invoke-Download ($base + $assetName) $archivePath 900

        # Checksum: "<zip>.sha256" (current releases) or a SHA256SUMS.txt list.
        $expected = $null
        foreach ($sumName in @("$assetName.sha256", 'SHA256SUMS.txt')) {
            $sumPath = Join-Path $workDir $sumName
            try { Invoke-Download ($base + $sumName) $sumPath 60 } catch { continue }
            foreach ($line in Get-Content -LiteralPath $sumPath) {
                if ($line.Trim() -match ('^(?<hash>[0-9a-fA-F]{64})\s+\*?' + [Regex]::Escape($assetName) + '$')) { $expected = $Matches['hash'] }
            }
            if ($expected) { break }
        }
        if (-not $expected) { throw "Release $tag has no SHA-256 checksum for $assetName. Installation was cancelled." }
        $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($actual -ine $expected) { throw 'The ZIP failed SHA-256 verification. Installation was cancelled; existing files were not changed.' }
        Write-Good "Release $tag downloaded; SHA-256 checksum verified."
        return [pscustomobject]@{ Path = $archivePath; Tag = $tag }
    }

    function New-Shortcut([string]$path) {
        if (-not $path) { return }
        $shell = New-Object -ComObject WScript.Shell
        try {
            $shortcut = $shell.CreateShortcut($path)
            $shortcut.TargetPath = $appPath
            $shortcut.WorkingDirectory = $Destination
            $shortcut.IconLocation = $appPath + ',0'
            $shortcut.Description = 'Aion2Dps - passive DPS meter for AION 2'
            $shortcut.Save()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }

    # Deletes Windows' on/off flag for the 'Aion2Dps' Run value (absent = enabled); other apps' flags stay untouched.
    function Remove-AutostartApproval {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($ApprovedKey, $true)
        if ($key) { try { $key.DeleteValue($RunValue, $false) } finally { $key.Dispose() } }
    }

    # Writes the Run value for the installed exe (the same command the app's Settings checkbox writes) and clears a
    # "switched off" flag Windows may hold for it: -Autostart is an explicit request to start with Windows.
    function Set-Autostart {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RunKey)   # opens an existing key; other values stay untouched
        if (-not $key) { throw "Could not open HKCU\$RunKey." }
        try { $key.SetValue($RunValue, "`"$appPath`" --autostart", [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
        Remove-AutostartApproval
    }

    # Removes the Run value (and Windows' on/off flag for it) only when it starts a program inside $dir (never another app's
    # entry or another install's). Works whether or not $dir still exists.
    function Remove-AutostartEntry([string]$dir) {
        $dir = $dir.TrimEnd('\')
        if (-not $dir -or $dir.Length -le ([IO.Path]::GetPathRoot($dir + '\')).TrimEnd('\').Length) { return }   # never a drive root
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RunKey, $true)
        if (-not $key) { return }
        try {
            $cmd = [string]$key.GetValue($RunValue, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($cmd -and ($cmd -match '^\s*"(?<exe>[^"]+)"' -or $cmd -match '^\s*(?<exe>.+?\.exe)(\s|$)')) {
                $exe = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Matches['exe']))
                if ($exe.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    $key.DeleteValue($RunValue, $false)
                    Remove-AutostartApproval
                    Write-Info 'Removed the start-with-Windows entry.'
                }
            }
        } catch { Write-Warn2 "Could not check the start-with-Windows entry: $($_.Exception.Message)" }
        finally { $key.Dispose() }
    }

    # The generated uninstaller contains no paths (it locates itself), so user names in any script are safe.
    $UninstallScript = @'
# Generated by the Aion2Dps installer. Removes the files it installed; settings and fight history in
# %APPDATA%\Aion2Dps are kept. Run from "Settings > Apps > Aion2Dps > Uninstall".
$ErrorActionPreference = 'SilentlyContinue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path -LiteralPath (Join-Path $dir '.aion2dps-install'))) { Write-Host "$dir is not an Aion2Dps install; nothing was removed."; exit 1 }
foreach ($p in @(Get-Process -Name 'Aion2Dps', 'aion2dps-cli')) {
    try { if ($p.Path -and $p.Path.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)) { & taskkill.exe /PID $p.Id 2>$null | Out-Null; if (-not $p.WaitForExit(4000)) { $p | Stop-Process -Force; $p.WaitForExit(5000) | Out-Null } } } catch { }
}
foreach ($folder in 'Programs', 'DesktopDirectory') {
    $root = [Environment]::GetFolderPath($folder)
    if (-not $root) { continue }
    $link = Join-Path $root 'Aion2Dps.lnk'
    if (Test-Path -LiteralPath $link) {
        $shell = New-Object -ComObject WScript.Shell
        try { if ($shell.CreateShortcut($link).TargetPath -like ($dir + '\*')) { Remove-Item -LiteralPath $link -Force } } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Aion2Dps'
if ((Get-ItemProperty -Path $key).InstallLocation -ieq $dir) { Remove-Item -LiteralPath $key -Recurse -Force }
# Start with Windows: remove the Run value 'Aion2Dps' only when it starts a program in this folder.
# Windows' on/off flag for it (StartupApproved) goes with it.
# Test hook: AION2DPS_TEST_RUN_KEY (a key below HKCU\Software) replaces the Run key (and its subkey 'StartupApproved' the
# StartupApproved key) so tests never touch the real ones.
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
if ($env:AION2DPS_TEST_RUN_KEY -match '^Software\\[^\\]') { $runKey = $env:AION2DPS_TEST_RUN_KEY.TrimEnd('\'); $approvedKey = "$runKey\StartupApproved" }
$run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey, $true)
if ($run) {
    try {
        $cmd = [string]$run.GetValue('Aion2Dps', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($cmd -and ($cmd -match '^\s*"(?<exe>[^"]+)"' -or $cmd -match '^\s*(?<exe>.+?\.exe)(\s|$)')) {
            $exe = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Matches['exe']))
            if ($exe.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $run.DeleteValue('Aion2Dps', $false)
                $approved = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($approvedKey, $true)
                if ($approved) { try { $approved.DeleteValue('Aion2Dps', $false) } finally { $approved.Dispose() } }
            }
        }
    } catch { } finally { $run.Dispose() }
}
$manifest = Join-Path $dir 'install-manifest.txt'
if (Test-Path -LiteralPath $manifest) {
    foreach ($rel in Get-Content -LiteralPath $manifest -Encoding UTF8) {
        if ($rel -and -not [IO.Path]::IsPathRooted($rel) -and $rel -notmatch '(^|[\\/])\.\.([\\/]|$)') { Remove-Item -LiteralPath (Join-Path $dir $rel) -Force }
    }
}
Remove-Item -LiteralPath $manifest, (Join-Path $dir '.aion2dps-install'), $MyInvocation.MyCommand.Path -Force
Get-ChildItem -LiteralPath $dir -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending |
    Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
Set-Location -LiteralPath $env:TEMP
if (@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) { Remove-Item -LiteralPath $dir -Force }
Write-Host 'Aion2Dps was removed. Npcap is still installed (remove it under Settings > Apps if you no longer need it).'
'@

    function Write-InstallRecord([string[]]$newFiles, [string]$displayVersion) {
        $all = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        if (Test-Path -LiteralPath $manifestPath) { foreach ($l in Get-Content -LiteralPath $manifestPath -Encoding UTF8) { if ($l) { [void]$all.Add($l) } } }
        foreach ($f in $newFiles) { [void]$all.Add($f) }
        $utf8Bom = New-Object Text.UTF8Encoding $true
        [IO.File]::WriteAllLines($manifestPath, [string[]]@($all | Sort-Object), $utf8Bom)
        [IO.File]::WriteAllText($markerPath, $displayVersion, $utf8Bom)
        [IO.File]::WriteAllText((Join-Path $Destination 'uninstall.ps1'), $UninstallScript, $utf8Bom)

        $null = New-Item -Path $UninstallKey -Force
        $sizeKb = [int]((Get-ChildItem -LiteralPath $Destination -Recurse -File -Force | Measure-Object Length -Sum).Sum / 1KB)
        $values = @{
            DisplayName = 'Aion2Dps'; DisplayVersion = $displayVersion; Publisher = 'TheLoop705'
            InstallLocation = $Destination; DisplayIcon = $appPath; URLInfoAbout = "https://github.com/$Repo"
            UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Destination 'uninstall.ps1')`""
        }
        foreach ($name in $values.Keys) { $null = New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force }
        foreach ($name in 'NoModify', 'NoRepair') { $null = New-ItemProperty -Path $UninstallKey -Name $name -Value 1 -PropertyType DWord -Force }
        $null = New-ItemProperty -Path $UninstallKey -Name 'EstimatedSize' -Value $sizeKb -PropertyType DWord -Force
    }

    function Install-App([string]$workDir) {
        Assert-SafeDestination
        $package = Get-Package $workDir

        $unpackDir = Join-Path $workDir 'package'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($package.Path, $unpackDir)
        foreach ($required in @('Aion2Dps.exe', 'aion2dps-cli.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path $unpackDir $required) -PathType Leaf)) {
                throw "The release ZIP is missing $required. Installation was cancelled."
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $unpackDir 'data') -PathType Container)) {
            throw 'The release ZIP is missing its data folder. Installation was cancelled.'
        }
        $unpackFull = [IO.Path]::GetFullPath($unpackDir).TrimEnd('\') + '\'
        $files = @(Get-ChildItem -LiteralPath $unpackDir -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($unpackFull.Length) })

        Stop-InstalledApp
        [void][IO.Directory]::CreateDirectory($Destination)
        Write-Info "Installing to $Destination ..."
        try {
            # Merge package files; never remove the destination or the user's settings/history.
            foreach ($item in Get-ChildItem -LiteralPath $unpackDir -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
            }
        } catch {
            throw "Copying files failed ($($_.Exception.Message)). Close Aion2Dps and run the installer again to repair the install."
        }

        $displayVersion = $package.Tag.TrimStart('v')
        if ($package.Tag -eq 'local') {
            $productVersion = (Get-Item -LiteralPath $appPath).VersionInfo.ProductVersion
            $displayVersion = if ($productVersion) { ($productVersion -split '\+')[0] } else { 'local' }
        }
        Write-InstallRecord $files $displayVersion

        if (-not $Opt.NoShortcut) {
            New-Shortcut $startMenuLink
            if (-not $Opt.NoDesktopShortcut) { New-Shortcut $desktopLink }
        }
        Write-Good "Aion2Dps $displayVersion installed. Settings and history in %APPDATA%\Aion2Dps are preserved."
    }

    function Uninstall-App {
        Write-Step 'Uninstalling Aion2Dps'
        if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
            Remove-AutostartEntry $Destination   # the folder was deleted by hand: still drop its start-with-Windows entry
            Write-Info "Aion2Dps is not installed at $Destination."
            return
        }
        $script = Join-Path $Destination 'uninstall.ps1'
        if ((Test-Path -LiteralPath $markerPath) -and (Test-Path -LiteralPath $script)) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script
            Remove-AutostartEntry $Destination   # also covers an uninstall.ps1 written by an older installer
        } elseif ((Test-Path -LiteralPath $appPath) -and ([IO.Path]::GetFullPath($Destination).TrimEnd('\') -ieq [IO.Path]::GetFullPath($DefaultDir).TrimEnd('\'))) {
            # Install made by an older installer (no manifest) in the default location: it only ever held Aion2Dps files.
            Stop-InstalledApp
            foreach ($link in @($startMenuLink, $desktopLink)) { if ($link) { Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue } }
            Remove-Item -LiteralPath $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
            Remove-AutostartEntry $Destination
            Remove-Item -LiteralPath $Destination -Recurse -Force
            Write-Good "Removed $Destination and the shortcuts."
        } else {
            throw "$Destination has no Aion2Dps install record; nothing was removed. Delete the Aion2Dps files there manually."
        }
        $data = Join-Path $env:APPDATA 'Aion2Dps'
        if ($Opt.Purge -and (Test-Path -LiteralPath $data)) {
            Remove-Item -LiteralPath $data -Recurse -Force
            Write-Good "Deleted settings, logs and fight history ($data)."
        } elseif (Test-Path -LiteralPath $data) {
            Write-Info "Kept settings and fight history in $data (use -Purge to delete them)."
        }
    }

    # ─────────────────────────── main ───────────────────────────

    if ($Opt.Uninstall) { Uninstall-App; return }

    $oldSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
    [Net.ServicePointManager]::SecurityProtocol = $oldSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $tempDir = Join-Path $tempRoot ('Aion2Dps-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void][IO.Directory]::CreateDirectory($tempDir)
        Write-Host ''
        Write-Host '  Aion2Dps installer - passive DPS meter for AION 2 (reads network traffic only, never touches the game)'
        Write-Host ''
        $npcapOk = $true
        if (-not $Opt.SkipNpcap) { Write-Step 'Checking Npcap'; $npcapOk = Install-Npcap $tempDir }
        Write-Step 'Installing Aion2Dps'
        Install-App $tempDir
        if ($Opt.Autostart) {
            try {
                Set-Autostart
                Write-Good 'Aion2Dps starts with Windows (in the tray); the overlay appears while AION 2 is running.'
            } catch { Write-Warn2 "Could not set up the start with Windows ($($_.Exception.Message)). Use Settings > Startup in the dashboard." }
        } else {
            Write-Info 'The first time it runs, Aion2Dps switches on "Start with Windows" (in the tray), so it is ready when you'
            Write-Info 'start AION 2 from Steam. Change it under Settings > Startup in the dashboard.'
        }
        if (-not $Opt.NoLaunch) {
            Write-Step 'Starting Aion2Dps'
            if (Test-IsElevated) {
                # Don't hand admin rights to the meter: start it through Explorer as the normal user.
                Write-Info 'This PowerShell runs as administrator; that is not needed. Starting the meter as your normal user.'
                Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$appPath`""
            } else {
                Start-Process -FilePath $appPath -WorkingDirectory $Destination
            }
            Write-Info 'The overlay appears while AION 2 is running; the tray icon has the menu (Ctrl+Alt+O shows/hides the overlay).'
        }
        Write-Host ''
        if ($npcapOk) {
            Write-Host '  Done. Run the same command again to update; uninstall from Settings > Apps.' -ForegroundColor Green
        } else {
            Write-Host '  Aion2Dps is installed, but Npcap is NOT ready: live capture will not work until it is installed' -ForegroundColor Yellow
            Write-Host '  and running. Run this installer again, or install it from https://npcap.com/#download.' -ForegroundColor Yellow
            $global:LASTEXITCODE = 2
        }
    }
    finally {
        [Net.ServicePointManager]::SecurityProtocol = $oldSecurityProtocol
        # Delete only the unique temporary directory created by this installer.
        $resolvedTemp = [IO.Path]::GetFullPath($tempDir)
        $tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar + 'Aion2Dps-install-'
        if ($resolvedTemp.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetDirectoryName($resolvedTemp) -ieq $tempRoot) -and
            (Test-Path -LiteralPath $resolvedTemp -PathType Container)) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} @{
    Version = $A2dVersion; InstallDir = $A2dInstallDir; SkipNpcap = [bool]$A2dSkipNpcap; NoShortcut = [bool]$A2dNoShortcut
    NoDesktopShortcut = [bool]$A2dNoDesktopShortcut; NoLaunch = [bool]$A2dNoLaunch; Uninstall = [bool]$A2dUninstall
    Purge = [bool]$A2dPurge; ZipPath = $A2dZipPath; Autostart = [bool]$A2dAutostart
}
