# Whisper Batch を組み立てる（Windows x64、NVIDIA GPU は CUDA で使う）。
#
#   ./scripts/build.ps1                 # リリースビルド → build\WhisperBatch\WhisperBatch.exe
#   ./scripts/build.ps1 -SelfContained  # .NET ランタイムも同梱する（.NET 10 が入っていない PC 向け）
#
# 必要なもの: .NET 10 SDK（PATH の dotnet、または %LOCALAPPDATA%\Microsoft\dotnet）
param(
    [switch]$SelfContained,
    [string]$Configuration = 'Release',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet.ps1')

# whisper.cpp（CUDA 版）の DLL を取得・検証する（取得済みなら何もしない）
& (Join-Path $PSScriptRoot 'fetch-whisper.ps1')

$out = if ($OutDir) { $OutDir } else { Join-Path $root 'build\WhisperBatch' }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "==> dotnet publish ($Configuration, win-x64, self-contained=$SelfContained)"
& $dotnet publish (Join-Path $root 'src\WhisperBatch.App\WhisperBatch.App.csproj') `
    -c $Configuration -r win-x64 --self-contained:$($SelfContained.IsPresent) `
    -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }

# ライセンス表記
Copy-Item (Join-Path $root 'App\ThirdPartyNotices.txt') $out -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt') -Force

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("==> 完了: {0}\WhisperBatch.exe（合計 {1:N0} MB）" -f $out, $size)
