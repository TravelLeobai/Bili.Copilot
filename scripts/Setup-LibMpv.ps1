[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$mpvArchitecture = if ($Platform -eq 'x64') { 'x86_64' } else { 'aarch64' }
$targetDirectory = Join-Path $repositoryRoot "src\Desktop\BiliCopilot.UI\Assets\libmpv\$($Platform.ToLowerInvariant())"
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "BiliCopilot-libmpv-$([guid]::NewGuid().ToString('N'))"

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null

    Write-Host "Querying the latest libmpv release for $Platform..."
    $release = Invoke-RestMethod `
        -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' `
        -Headers @{ 'User-Agent' = 'Bili.Copilot dependency setup' }
    $assetPattern = "mpv-dev-$mpvArchitecture-*-git-*.7z"
    $asset = $release.assets |
        Where-Object { $_.name -like $assetPattern -and $_.name -notlike '*-v3-*' } |
        Select-Object -First 1

    if (-not $asset) {
        throw "Release '$($release.tag_name)' does not contain an asset matching '$assetPattern'."
    }

    $archivePath = Join-Path $temporaryDirectory $asset.name
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archivePath

    if ($asset.digest -like 'sha256:*') {
        $expectedHash = $asset.digest.Substring(7)
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "The libmpv archive checksum is invalid. Expected $expectedHash, received $actualHash."
        }
    }

    # Windows bsdtar cannot read some recent 7z archive properties. 7zr is the
    # official standalone extractor and does not require installing 7-Zip.
    $sevenZipPath = Join-Path $temporaryDirectory '7zr.exe'
    Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7zr.exe' -OutFile $sevenZipPath
    & $sevenZipPath e $archivePath "-o$targetDirectory" 'libmpv-2.dll' -y
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip failed with exit code $LASTEXITCODE."
    }

    $libraryPath = Join-Path $targetDirectory 'libmpv-2.dll'
    if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
        throw "The archive did not contain libmpv-2.dll."
    }

    Write-Host "libmpv is ready: $libraryPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
