# Install the latest Windows x64 release:
# irm https://raw.githubusercontent.com/TheLoop705/aion2dps/main/install.ps1 | iex
[CmdletBinding()]
param(
    [string]$Version,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Aion2Dps'),
    [switch]$NoLaunch,
    [switch]$NoShortcut
)

# Keep installer preferences and helper functions out of the caller's session.
& {
    param($RequestedVersion, $Destination, $SkipLaunch, $SkipShortcut)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Aion2Dps requires Windows.'
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'This release requires 64-bit Windows.'
    }
    if ($RequestedVersion -and $RequestedVersion -notmatch '^v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw 'Version must be a release version such as v0.2.0 or 0.2.0.'
    }

    $Destination = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Destination))
    $appPath = Join-Path $Destination 'Aion2Dps.exe'
    $cliPath = Join-Path $Destination 'aion2dps-cli.exe'

    function Assert-AppClosed {
        foreach ($process in @(Get-Process -Name 'Aion2Dps', 'aion2dps-cli' -ErrorAction SilentlyContinue)) {
            $processPath = $null
            try { $processPath = $process.Path } catch { }
            if ($processPath -and (($processPath -ieq $appPath) -or ($processPath -ieq $cliPath))) {
                throw 'Close the installed Aion2Dps app (including its tray icon) and CLI, then run the installer again.'
            }
        }
    }

    Assert-AppClosed
    $oldSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
    [Net.ServicePointManager]::SecurityProtocol = $oldSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $tempDir = Join-Path $tempRoot ('Aion2Dps-install-' + [Guid]::NewGuid().ToString('N'))

    try {
        [void][IO.Directory]::CreateDirectory($tempDir)
        $releaseApi = 'https://api.github.com/repos/TheLoop705/aion2dps/releases/latest'
        if ($RequestedVersion) {
            $tag = $RequestedVersion
            if (-not $tag.StartsWith('v')) { $tag = 'v' + $tag }
            $releaseApi = 'https://api.github.com/repos/TheLoop705/aion2dps/releases/tags/' + [Uri]::EscapeDataString($tag)
        }
        $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'Aion2Dps-Installer' }
        Write-Host 'Finding Aion2Dps release...'
        $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
        $assetName = 'Aion2Dps-' + $release.tag_name + '-win-x64.zip'
        $archiveAsset = @($release.assets | Where-Object { $_.name -ceq $assetName })
        $checksumAsset = @($release.assets | Where-Object { $_.name -ceq ($assetName + '.sha256') })
        if (($archiveAsset.Count -ne 1) -or ($checksumAsset.Count -ne 1)) {
            throw "Release $($release.tag_name) does not contain the Windows x64 ZIP and its SHA-256 checksum."
        }

        $archivePath = Join-Path $tempDir $assetName
        $checksumPath = $archivePath + '.sha256'
        Write-Host "Downloading $assetName..."
        Invoke-WebRequest -UseBasicParsing -Uri $archiveAsset[0].browser_download_url -Headers $headers -OutFile $archivePath
        Invoke-WebRequest -UseBasicParsing -Uri $checksumAsset[0].browser_download_url -Headers $headers -OutFile $checksumPath
        $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        $checksumPattern = '^(?<hash>[0-9a-fA-F]{64})\s+\*?' + [Regex]::Escape($assetName) + '$'
        if ($checksumText -notmatch $checksumPattern) {
            throw 'The release checksum file is invalid. Installation was cancelled.'
        }
        $expectedHash = $Matches['hash']
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($actualHash -ine $expectedHash) {
            throw 'The ZIP failed SHA-256 verification. Installation was cancelled; existing files were not changed.'
        }

        $unpackDir = Join-Path $tempDir 'package'
        Expand-Archive -LiteralPath $archivePath -DestinationPath $unpackDir
        foreach ($requiredFile in @('Aion2Dps.exe', 'aion2dps-cli.exe', 'coreclr.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $unpackDir $requiredFile) -PathType Leaf)) {
                throw "The release ZIP is missing $requiredFile. Installation was cancelled."
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $unpackDir 'data') -PathType Container)) {
            throw 'The release ZIP is missing its data folder. Installation was cancelled.'
        }

        Assert-AppClosed
        [void][IO.Directory]::CreateDirectory($Destination)
        Write-Host "Installing $($release.tag_name) to $Destination..."
        # Merge package files; never remove the destination or the user's settings/history.
        foreach ($item in Get-ChildItem -LiteralPath $unpackDir -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
        }
        if (-not $SkipShortcut) {
            $startMenu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
            $shell = New-Object -ComObject WScript.Shell
            try {
                $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'Aion2Dps.lnk'))
                $shortcut.TargetPath = $appPath
                $shortcut.WorkingDirectory = $Destination
                $shortcut.IconLocation = $appPath + ',0'
                $shortcut.Save()
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }

        Write-Host "Installed Aion2Dps $($release.tag_name). Settings and history in %APPDATA%\Aion2Dps are preserved."
        Write-Host 'Live capture requires Npcap. Install it separately from https://npcap.com/#download if needed.'
        if (-not $SkipLaunch) {
            Start-Process -FilePath $appPath -WorkingDirectory $Destination
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
} $Version $InstallDir $NoLaunch $NoShortcut
