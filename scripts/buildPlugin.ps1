<#
.SYNOPSIS
  Builds XC.VsResharperMcpServer and packs it into dist\ as a .nupkg for local Extension Manager installation.
  See docs\DEVNOTES.md for the manual install-from-local-source steps.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "src\XC.VsResharperMcpServer"
$distDir = Join-Path $repoRoot "dist"

Push-Location $projectDir
try {
    dotnet build -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    dotnet pack -c $Configuration `
        -p:NuspecFile=XC.VsResharperMcpServer.nuspec `
        -p:NuspecBasePath=. `
        -o $distDir
    if ($LASTEXITCODE -ne 0) { throw "Pack failed" }
}
finally {
    Pop-Location
}

Write-Host "Packed to $distDir"
