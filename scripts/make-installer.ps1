# Windows インストーラー（build\installer\WhisperBatch-<版>-win-x64-setup.exe）を作る。
#
#   ./scripts/make-installer.ps1
#
# 1. アプリを .NET ランタイム同梱でビルドする（scripts/build.ps1 -SelfContained）
# 2. Visual C++ 再頒布可能パッケージ（x64）を Microsoft から取得し、署名を確認する
# 3. Inno Setup 6 でインストーラーにまとめる（winget install JRSoftware.InnoSetup で入手できる）
param(
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props に Version がありません' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 が見つかりません（winget install JRSoftware.InnoSetup）' }

# 普段使いの build\WhisperBatch とは別のフォルダに作る（起動中でも作れるように）
$source = Join-Path $root 'build\package\WhisperBatch'
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SelfContained -OutDir $source
}
if (-not (Test-Path (Join-Path $source 'WhisperBatch.exe'))) { throw "アプリのビルドがありません: $source" }

# Visual C++ 再頒布可能パッケージ（Microsoft の署名を確認する）
$redistDir = Join-Path $root 'build\redist'
$redist = Join-Path $redistDir 'vc_redist.x64.exe'
New-Item -ItemType Directory -Force $redistDir | Out-Null
if (-not (Test-Path $redist)) {
    Write-Host '==> Visual C++ 再頒布可能パッケージ（x64）を取得'
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redist -UseBasicParsing
}
$sig = Get-AuthenticodeSignature $redist
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item $redist -Force
    throw "vc_redist.x64.exe の署名を確認できません（$($sig.Status) / $($sig.SignerCertificate.Subject)）"
}

$out = Join-Path $root 'build\installer'
New-Item -ItemType Directory -Force $out | Out-Null
Write-Host "==> インストーラーを作成（$version、圧縮に数分かかります）"
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$source" "/DRedistDir=$redistDir" "/DOutputDir=$out" (Join-Path $root 'installer\WhisperBatch.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup のコンパイルに失敗しました' }

$setup = Join-Path $out "WhisperBatch-$version-win-x64-setup.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -Encoding ascii "$setup.sha256"
Write-Host ("==> 完了: {0}（{1:N0} MB）" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host "    SHA-256: $hash"
