#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'dist'))
$releaseRoot = Join-Path $distRoot 'release'
$runtime = 'win-x64'

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $workspaceRoot 'Directory.Build.props') -Raw
$versionNode = $buildProperties.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) {
    throw 'Directory.Build.props must define Version.'
}
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "Unsupported release version: $version"
}

# Each run owns one new staging directory. Never remove existing publish output.
$stageName = '.staging-' + $version + '-' + [Guid]::NewGuid().ToString('N')
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $distRoot $stageName))
$appRoot = Join-Path $stageRoot 'app'
$cliRoot = Join-Path $stageRoot 'cli'
$archiveName = "Aion2Dps-v$version-$runtime.zip"
$stagedArchive = Join-Path $stageRoot $archiveName
$archivePath = Join-Path $releaseRoot $archiveName
$checksumPath = $archivePath + '.sha256'

function Assert-DistDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $distPrefix = $distRoot.TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the workspace dist directory: $fullPath"
    }

    # Reparse points could redirect a verified lexical path outside the workspace.
    $current = $fullPath
    while ($current -and $current.Length -ge $workspaceRoot.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to operate through a reparse point: $current"
            }
        }
        if ($current -eq $workspaceRoot) { break }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}

function Publish-Project {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Assert-DistDirectory -Path $Destination
    $publishArguments = @(
        'publish', (Join-Path $workspaceRoot $Project),
        '--configuration', 'Release',
        '--runtime', $runtime,
        '--self-contained', 'true',
        '--output', $Destination,
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & dotnet @publishArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit code $LASTEXITCODE)."
    }
}

Assert-DistDirectory -Path $stageRoot
Assert-DistDirectory -Path $releaseRoot
$null = Get-Command dotnet -ErrorAction Stop
$null = New-Item -ItemType Directory -Path $stageRoot

try {
    Publish-Project -Project 'src/Aion2Dps.App/Aion2Dps.App.csproj' -Destination $appRoot
    Publish-Project -Project 'src/Aion2Dps.Cli/Aion2Dps.Cli.csproj' -Destination $cliRoot

    # App owns shared files: the CLI's core-runtime facades must not replace WPF assemblies.
    $cliPrefix = $cliRoot.TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($item in Get-ChildItem -LiteralPath $cliRoot -Recurse -File -Force) {
        $relativePath = $item.FullName.Substring($cliPrefix.Length)
        $destination = Join-Path $appRoot $relativePath
        if (-not (Test-Path -LiteralPath $destination)) {
            $null = New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force
            Copy-Item -LiteralPath $item.FullName -Destination $destination
        }
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -Destination $appRoot
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'README.md') -Destination $appRoot

    foreach ($requiredFile in @('Aion2Dps.exe', 'aion2dps-cli.exe', 'PresentationFramework.dll', 'data/NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appRoot $requiredFile) -PathType Leaf)) {
            throw "Publish output is missing $requiredFile."
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $appRoot, $stagedArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false
    )
    $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedChecksum = $stagedArchive + '.sha256'
    [System.IO.File]::WriteAllText($stagedChecksum, "$hash  $archiveName`n", [System.Text.Encoding]::ASCII)

    Assert-DistDirectory -Path $releaseRoot
    $null = New-Item -ItemType Directory -Path $releaseRoot -Force
    Move-Item -LiteralPath $stagedArchive -Destination $archivePath -Force
    Move-Item -LiteralPath $stagedChecksum -Destination $checksumPath -Force

    Write-Output $archivePath
    Write-Output $checksumPath
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Assert-DistDirectory -Path $stageRoot
        $unsafeChildren = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -Force |
            Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($unsafeChildren.Count -ne 0) {
            throw "Staging cleanup refused because it contains a reparse point: $stageRoot"
        }
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
