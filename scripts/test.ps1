# テストを実行する。
#
#   ./scripts/test.ps1     # ロジックのテスト（キュー、状態遷移、出力形式、チャンク境界、表示文字列）と DLL の確認
#
# 実際の音声・動画とモデルを使う結合テストは、環境変数を指定したときだけ実行される。
#   ./scripts/make-test-audio.ps1 $env:TEMP\wb-audio
#   $env:WHISPER_BATCH_TEST_AUDIO_DIR = "$env:TEMP\wb-audio"
#   $env:WHISPER_BATCH_TEST_MODEL = "$env:LOCALAPPDATA\WhisperBatch\Models\ggml-large-v3-turbo-q5_0.bin"
#   $env:WHISPER_BATCH_TEST_DOWNLOAD_DIR = "$env:TEMP\wb-models"
#   ./scripts/test.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet.ps1')
& (Join-Path $PSScriptRoot 'fetch-whisper.ps1')
& $dotnet test (Join-Path $root 'WhisperBatch.slnx') @args
exit $LASTEXITCODE
