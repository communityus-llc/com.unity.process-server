[CmdletBinding()]
Param(
    [switch]
    $Trace = $false
)

Set-StrictMode -Version Latest
if ($Trace) {
    Set-PSDebug -Trace 1
}

. $PSScriptRoot\helpers.ps1 | out-null

$packagesDir = Join-Path $rootDirectory 'build\packages'
$upmDir = Join-Path $rootDirectory 'build\upm'
$srcDir = Join-Path $rootDirectory 'src'

New-Item -itemtype Directory -Path $upmDir -Force -ErrorAction SilentlyContinue

Get-ChildItem -Directory $srcDir | % {
    Write-Output "Packing $($_.Name)"

    $src = Join-Path $packagesDir $_.Name
    $target = $upmDir
    Copy-Item $src $target -Recurse -Force -ErrorAction SilentlyContinue

    $packageDir = Join-Path $srcDir $_.Name
    $src = Join-Path $packageDir "Tests"
    $target = Join-Path $upmDir $_.Name
    Copy-Item $src $target -Recurse -Force -ErrorAction SilentlyContinue
    $src = Join-Path $packageDir "Tests.meta"
    $target = Join-Path $upmDir $_.Name
    Copy-Item $src $target -Force -ErrorAction SilentlyContinue
    $packageDir = Join-Path $upmDir $_.Name
    Invoke-Command -Fatal { & upm-ci package pack --package-path $packageDir }
}
