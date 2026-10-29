# whisper.cpp 公式リリース（v1.9.5 と同じビルド b5454）の Windows CUDA 版を取得し、
# SHA-256 を検証して、アプリに必要な DLL だけを native/win-x64 に展開する。
# 展開済みなら何もしない。配布物内の実行ファイルは使わない。
#
#   ./scripts/fetch-whisper.ps1                    # GitHub から取得
#   ./scripts/fetch-whisper.ps1 -ZipPath <zip>     # 手元の zip を使う（ハッシュは同じく検証）
param(
    [string]$ZipPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$build = 'b5454'
$asset = 'whisper-bin-win-cuda-12.4.0-x64.zip'
$url = "https://github.com/ggml-org/whisper.cpp/releases/download/$build/$asset"
$sha256 = 'afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a'
$dest = Join-Path $root 'native\win-x64'
$stamp = Join-Path $dest ".whisper-$build-cuda12.4"

# アプリが読み込む DLL（依存関係は PE のインポート表で確認済み）
#   whisper.dll → ggml.dll, ggml-base.dll（→ VCOMP140.DLL: Visual C++ 再頒布可能パッケージ）
#   ggml-cpu-*.dll: CPU ごとに最適なものを ggml が実行時に選ぶ
#   ggml-cuda.dll → cudart64_12.dll, cublas64_12.dll（→ cublasLt64_12.dll）, nvcuda.dll（NVIDIA ドライバー）
$files = @(
    'whisper.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cuda.dll',
    'ggml-cpu-x64.dll', 'ggml-cpu-sse42.dll', 'ggml-cpu-sandybridge.dll', 'ggml-cpu-haswell.dll',
    'ggml-cpu-skylakex.dll', 'ggml-cpu-cannonlake.dll', 'ggml-cpu-cascadelake.dll', 'ggml-cpu-icelake.dll',
    'ggml-cpu-alderlake.dll',
    'cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll'
)

if (Test-Path $stamp) { exit 0 }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("wb-fetch-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $work | Out-Null
try {
    if (-not $ZipPath) {
        $ZipPath = Join-Path $work $asset
        Write-Host "==> whisper.cpp $build (CUDA 12.4, x64) を取得（約 685 MB）"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $ZipPath -UseBasicParsing
    }
    $actual = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "whisper.cpp のチェックサムが一致しません（期待: $sha256 / 実際: $actual）"
    }
    Write-Host "==> 展開"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
        New-Item -ItemType Directory -Force $dest | Out-Null
        foreach ($name in $files) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq "Release/$name" }
            if (-not $entry) { throw "zip に $name がありません" }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest $name), $true)
        }
    } finally {
        $zip.Dispose()
    }
    New-Item -ItemType File $stamp | Out-Null
    Write-Host "==> 展開しました: $dest"
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
