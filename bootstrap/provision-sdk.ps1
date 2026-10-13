<#
.SYNOPSIS
    Extracts the Rockwell CSClient DLL and FtspAdapterLDSDK.exe from the
    CSClient NuGet package into the application's sdk/vendor/ directory.

.DESCRIPTION
    Locates the RockwellAutomation.LogixDesigner.CSClient .nupkg file -- either
    at the Studio 5000 Public Documents default path or via the -NupkgPath
    override -- opens it as a ZIP archive, and extracts exactly two files:

        lib/net10.0/RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll
        FtspAdapter/FtspAdapterLDSDK.exe

    Both land in <AppDir>\sdk\vendor\ because the SDK resolves
    FtspAdapterLDSDK.exe relative to the assembly directory (not
    AppContext.BaseDirectory), so co-location is required for the FTSP
    transport to initialise.

    Every failure exits non-zero with a distinct, actionable error message
    that names the exact path that was checked or the exact ZIP entry that
    was absent.  Silent partial success is explicitly forbidden: if either
    extraction fails the script exits non-zero, leaving no partially-
    populated vendor directory that would silently fail at runtime.

.PARAMETER AppDir
    Path to the published application directory produced by
    'dotnet publish' / 'make publish-win'.  Must contain
    sdk\LogixMcp.SdkAdapter.dll to be considered a valid SDK publish tree.

.PARAMETER NupkgPath
    Optional.  Full path to the RockwellAutomation.LogixDesigner.CSClient
    .nupkg file.  When omitted the script searches for a matching file under
    the default Public Documents location:

        %PUBLIC%\Documents\Studio 5000\Logix Designer SDK\dotnet\

.EXAMPLE
    # Use the default nupkg search path:
    .\provision-sdk.ps1 -AppDir "C:\LogixMcp\publish"

.EXAMPLE
    # Supply an explicit nupkg path:
    .\provision-sdk.ps1 -AppDir "C:\LogixMcp\publish" `
                        -NupkgPath "C:\packages\RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppDir,

    [Parameter(Mandatory = $false)]
    [string] $NupkgPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Constants -- kept in sync with SdkAdapterLoader.cs
# ---------------------------------------------------------------------------

# Subdirectory under AppDir where the adapter DLL lives (sdk/LogixMcp.SdkAdapter.dll).
$AdapterRelPath = 'sdk\LogixMcp.SdkAdapter.dll'

# Destination for all extracted SDK files (same dir so FtspAdapterLDSDK.exe is
# resolved relative to the CSClient assembly at runtime).
$VendorRelDir = 'sdk\vendor'

# Default directory in which Studio 5000 places the NuGet package on a developer
# workstation.  Matches SdkAdapterLoader.NuPkgLocation.
$DefaultNupkgDir = "$env:SystemDrive\Users\Public\Documents\Studio 5000\Logix Designer SDK\dotnet"

# Glob pattern for the nupkg file name.
$NupkgGlob = 'RockwellAutomation.LogixDesigner.CSClient.*.nupkg'

# SDK package version lgx-mcp is built against.  Keep in sync with the
# RockwellAutomation.LogixDesigner.CSClient PackageReference in
# src\LogixMcp.SdkAdapter\LogixMcp.SdkAdapter.csproj.
$RequiredSdkVersion = '2.2.1109'

# Paths of the two entries that must exist inside the nupkg ZIP archive.
$CsClientEntry   = 'lib/net10.0/RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll'
$FtspAdapterEntry = 'FtspAdapter/FtspAdapterLDSDK.exe'

# ---------------------------------------------------------------------------
# Step 1 -- Validate the publish tree
# ---------------------------------------------------------------------------

$adapterDll = Join-Path $AppDir $AdapterRelPath

if (-not (Test-Path -LiteralPath $adapterDll -PathType Leaf)) {
    Write-Error @"
ERROR: Target directory is not a valid SDK publish tree.
       Expected to find: $adapterDll

       -AppDir must point to the output of 'dotnet publish' (or 'make publish-win')
       with EnableLogixSdk=true so that sdk\LogixMcp.SdkAdapter.dll is present.

       Checked: $AppDir
"@
    exit 1
}

# ---------------------------------------------------------------------------
# Step 2 -- Locate the nupkg
# ---------------------------------------------------------------------------

if ($NupkgPath) {
    # Caller supplied an explicit path -- validate it.
    if (-not (Test-Path -LiteralPath $NupkgPath -PathType Leaf)) {
        Write-Error @"
ERROR: CSClient NuGet package not found at the path supplied via -NupkgPath.

       Checked: $NupkgPath

       Verify the path is correct and that the file exists.
"@
        exit 1
    }
    $resolvedNupkg = $NupkgPath
} else {
    # Search the default Public Documents directory.
    if (-not (Test-Path -LiteralPath $DefaultNupkgDir -PathType Container)) {
        Write-Error @"
ERROR: CSClient NuGet package not found -- default search directory does not exist.

       Searched: $DefaultNupkgDir

       Install Studio 5000 Logix Designer with the SDK option, or supply the
       package path explicitly via -NupkgPath.
"@
        exit 1
    }

    $candidates = @(Get-ChildItem -LiteralPath $DefaultNupkgDir `
                                  -Filter $NupkgGlob `
                                  -File `
                                  -ErrorAction SilentlyContinue)

    if ($candidates.Count -eq 0) {
        Write-Error @"
ERROR: CSClient NuGet package not found.

       Searched for: $NupkgGlob
       In directory: $DefaultNupkgDir

       No matching file was found.  Install Studio 5000 Logix Designer with the
       SDK option enabled, or supply the package path explicitly via -NupkgPath.
"@
        exit 1
    }

    # When multiple versions are present, pick the newest by file name (descending).
    $resolvedNupkg = ($candidates | Sort-Object Name -Descending | Select-Object -First 1).FullName
    Write-Host "==> Located nupkg: $resolvedNupkg"
}

# ---------------------------------------------------------------------------
# Step 3 -- Prepare the destination directory
# ---------------------------------------------------------------------------

$vendorDir = Join-Path $AppDir $VendorRelDir
if (-not (Test-Path -LiteralPath $vendorDir -PathType Container)) {
    Write-Host "==> Creating vendor directory: $vendorDir"
    New-Item -ItemType Directory -Path $vendorDir -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Step 4 -- Open the nupkg as a ZIP and extract both required entries
# ---------------------------------------------------------------------------

Add-Type -AssemblyName 'System.IO.Compression'
Add-Type -AssemblyName 'System.IO.Compression.FileSystem'

$zipStream = $null
$zip       = $null

try {
    $zipStream = [System.IO.File]::OpenRead($resolvedNupkg)
    $zip       = [System.IO.Compression.ZipArchive]::new($zipStream,
                     [System.IO.Compression.ZipArchiveMode]::Read)

    # Pre-check: the package must carry the lib/net10.0 folder and the adapter
    # executable.  Name whichever is missing (or both) before extracting anything.
    $missingParts = @()
    $hasLibNet10 = @($zip.Entries | Where-Object { $_.FullName.StartsWith('lib/net10.0/', [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if (-not $hasLibNet10) {
        $foundLibs = @($zip.Entries | Where-Object { $_.FullName -like 'lib/*/*' } |
            ForEach-Object { ($_.FullName -split '/')[1] } | Sort-Object -Unique)
        $foundText = if ($foundLibs.Count -gt 0) { $foundLibs -join ', ' } else { '(no lib/ folder)' }
        $missingParts += "       Missing: lib/net10.0 (the package has lib/ folders: $foundText)"
    }
    if ($null -eq $zip.GetEntry($FtspAdapterEntry)) {
        $missingParts += "       Missing: $([System.IO.Path]::GetFileName($FtspAdapterEntry)) (expected at $FtspAdapterEntry)"
    }
    if ($missingParts.Count -gt 0) {
        $missingText = $missingParts -join [Environment]::NewLine
        Write-Error @"
ERROR: The NuGet package does not have the files lgx-mcp needs.

$missingText
       Package: $resolvedNupkg

       This is usually a different Logix Designer SDK version than the one
       lgx-mcp is built against.  Use Logix Designer SDK $RequiredSdkVersion or the
       version in Directory.Build.props.
"@
        exit 1
    }

    # Helper: extract a single named entry or exit non-zero with a clear message.
    function Invoke-ExtractEntry {
        param(
            [System.IO.Compression.ZipArchive] $Archive,
            [string] $EntryPath,
            [string] $DestinationDir,
            [string] $NupkgFile
        )

        # ZipArchiveEntry.FullName uses forward slashes on all platforms.
        $entry = $Archive.GetEntry($EntryPath)

        if ($null -eq $entry) {
            Write-Error @"
ERROR: Required entry not found inside the NuGet package.

       Entry searched : $EntryPath
       Package        : $NupkgFile

       The package may be the wrong version or may be corrupt.
       Verify the file and re-download it from the Studio 5000 SDK installer
       if necessary.
"@
            exit 1
        }

        $destFileName = [System.IO.Path]::GetFileName($entry.FullName)
        $destPath     = Join-Path $DestinationDir $destFileName

        Write-Host "==> Extracting: $EntryPath"
        Write-Host "    -> $destPath"

        $entryStream = $null
        $destStream  = $null
        try {
            $entryStream = $entry.Open()
            $destStream  = [System.IO.File]::Create($destPath)
            $entryStream.CopyTo($destStream)
        } finally {
            if ($destStream)  { $destStream.Dispose()  }
            if ($entryStream) { $entryStream.Dispose() }
        }
    }

    Invoke-ExtractEntry -Archive $zip `
                        -EntryPath $CsClientEntry `
                        -DestinationDir $vendorDir `
                        -NupkgFile $resolvedNupkg

    Invoke-ExtractEntry -Archive $zip `
                        -EntryPath $FtspAdapterEntry `
                        -DestinationDir $vendorDir `
                        -NupkgFile $resolvedNupkg

} finally {
    if ($zip)       { $zip.Dispose()       }
    if ($zipStream) { $zipStream.Dispose() }
}

# ---------------------------------------------------------------------------
# Step 5 -- Report success
# ---------------------------------------------------------------------------

$csClientDest    = Join-Path $vendorDir ([System.IO.Path]::GetFileName($CsClientEntry))
$ftspAdapterDest = Join-Path $vendorDir ([System.IO.Path]::GetFileName($FtspAdapterEntry))

Write-Host ""
Write-Host "==> provision-sdk OK"
Write-Host "    $csClientDest"
Write-Host "    $ftspAdapterDest"
Write-Host ""
Write-Host "    Both files are in sdk\vendor\ so FtspAdapterLDSDK.exe is resolved"
Write-Host "    by the SDK relative to the CSClient assembly at runtime."
