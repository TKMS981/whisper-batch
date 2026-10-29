# 結合テスト用の音声・動画を作る（Windows の音声合成 SAPI と ffmpeg を使用）。
#   ./scripts/make-test-audio.ps1 <出力フォルダ>
# 日本語の音声（Microsoft Haruka）と英語の音声（Microsoft Zira）が必要（Windows の言語設定で追加できる）。
param(
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw 'ffmpeg が必要です（例: winget install Gyan.FFmpeg）'
}
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Add-Type -AssemblyName System.Speech
function Say([string]$voiceCulture, [string]$path, [string]$text) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $voice = $synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq $voiceCulture } | Select-Object -First 1
        if (-not $voice) { throw "音声合成の声がありません: $voiceCulture" }
        $synth.SelectVoice($voice.VoiceInfo.Name)
        $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $synth.SetOutputToWaveFile((Join-Path $Out $path), $format)
        $synth.Speak($text)
    } finally {
        $synth.Dispose()
    }
}

# ffmpeg は直接呼ぶ（関数経由だと PowerShell が -c:a などの引数を書き換えるため）
$q = @('-loglevel', 'error', '-y')
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "ffmpeg に失敗しました: $what" } }

Push-Location $Out
try {
    Say 'ja-JP' 'ja1.wav' '本日は晴天なり。これは音声認識のテストです。会議の議事録を自動で作成します。'
    Say 'ja-JP' 'ja2.wav' '二つ目のファイルです。東京の天気は明日、雨の予報です。'
    Say 'en-US' 'en1.wav' 'This is an English test file for automatic language detection.'

    # 音声の各形式
    ffmpeg @q -i ja1.wav -c:a libmp3lame ja1.mp3; Check ja1.mp3
    ffmpeg @q -i ja1.wav -c:a aac ja1.m4a; Check ja1.m4a
    ffmpeg @q -i ja1.wav -c:a alac ja1_alac.m4a; Check ja1_alac.m4a
    ffmpeg @q -i ja1.wav -c:a aac ja1.aac; Check ja1.aac
    ffmpeg @q -i ja1.wav ja1.aiff; Check ja1.aiff
    ffmpeg @q -i ja1.wav ja1.caf; Check ja1.caf
    ffmpeg @q -i ja1.wav ja1.flac; Check ja1.flac
    ffmpeg @q -i ja1.wav -c:a libvorbis ja1_vorbis.ogg; Check ja1_vorbis.ogg
    ffmpeg @q -i ja1.wav -c:a libopus ja1.ogg; Check ja1.ogg
    ffmpeg @q -i ja1.wav -c:a libopus ja1.opus; Check ja1.opus
    ffmpeg @q -i ja1.wav -c:a libopus ja1.webm; Check ja1.webm
    ffmpeg @q -i ja1.wav -c:a wmav2 ja1.wma; Check ja1.wma
    ffmpeg @q -i ja1.wav -ac 2 -ar 48000 ja1_stereo48k.wav; Check ja1_stereo48k.wav
    ffmpeg @q -i ja1.wav -c:a libmp3lame '日本語 ファイル #1.mp3'; Check #1.mp3'
    ffmpeg @q -f lavfi -i color=c=black:s=320x240:d=8 -i ja1.wav -shortest -c:v libx264 -c:a aac ja1.mp4; Check ja1.mp4
    ffmpeg @q -f lavfi -i color=c=black:s=320x240:d=8 -i ja1.wav -shortest -c:v libx264 -c:a aac ja1.mov; Check ja1.mov
    $bytes = New-Object byte[] 2000
    (New-Object System.Random 42).NextBytes($bytes)
    [System.IO.File]::WriteAllBytes((Join-Path $Out 'broken.mp3'), $bytes)

    # 動画（音声を MP3 に変換してから文字起こしする経路のテスト用）
    $v = @('-f', 'lavfi', '-i', 'color=c=navy:s=320x240:d=8', '-i', 'ja1.wav', '-shortest')
    ffmpeg @q @v -c:v libx264 -c:a aac ja1.m4v; Check ja1.m4v
    ffmpeg @q @v -c:v libx264 -c:a aac -f 3gp ja1.3gp; Check ja1.3gp
    ffmpeg @q @v -c:v libx265 -tag:v hvc1 -c:a aac ja1_hevc.mp4; Check ja1_hevc.mp4
    ffmpeg @q @v -c:v libx264 -c:a alac ja1_alac.mov; Check ja1_alac.mov
    ffmpeg @q @v -c:v libx264 -c:a pcm_s16le ja1_pcm.mov; Check ja1_pcm.mov
    ffmpeg @q @v -c:v prores_ks -c:a pcm_s16le ja1_prores.mov; Check ja1_prores.mov
    ffmpeg @q @v -c:v libx264 -c:a libmp3lame ja1_mp3audio.mp4; Check ja1_mp3audio.mp4
    ffmpeg @q @v -c:v libx264 -c:a aac ja1.mkv; Check ja1.mkv
    ffmpeg @q @v -c:v mpeg4 -c:a libmp3lame ja1.avi; Check ja1.avi
    ffmpeg @q @v -c:v libvpx-vp9 -c:a libopus ja1_video.webm; Check ja1_video.webm
    ffmpeg @q @v -c:v libx264 -c:a aac -f mpegts ja1.ts; Check ja1.ts
    ffmpeg @q @v -c:v mpeg2video -c:a mp2 ja1.mpg; Check ja1.mpg
    ffmpeg @q @v -c:v wmv2 -c:a wmav2 ja1.wmv; Check ja1.wmv
    # 5.1ch の音声を持つ動画（ステレオへのダウンミックスの確認）
    ffmpeg @q @v -filter_complex '[1:a]pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0[a]' -map 0:v -map '[a]' -c:v libx264 -c:a aac -ac 6 ja1_51.mp4; Check ja1_51.mp4
    ffmpeg @q -f lavfi -i color=c=navy:s=320x240:d=8 -c:v libx264 silent.mp4; Check silent.mp4
    ffmpeg @q -f lavfi -i color=c=navy:s=320x240:d=8 -i ja1.wav -i en1.wav -map 0:v -map 1:a -map 2:a -shortest -c:v libx264 -c:a aac multi_audio.mov; Check multi_audio.mov

    # 「これはN番目の文で、…」を 40 文並べた約 2 分半の音声（チャンク境界の検証用）
    $topics = @('天気', '会議', '予算', '新製品', '旅行', '健康', '教育', '交通', '料理', '音楽')
    $text = ''
    for ($i = 1; $i -le 40; $i++) { $text += "これは${i}番目の文で、話題は$($topics[$i % 10])です。" }
    Say 'ja-JP' 'long.wav' $text
    # 長い動画（キャンセルのテスト用）
    ffmpeg @q -f lavfi -i color=c=black:s=320x240:d=600 -stream_loop 3 -i long.wav -shortest -c:v libx264 -preset ultrafast -c:a aac long.mp4; Check long.mp4
    Write-Host "作成しました: $Out"
} finally {
    Pop-Location
}
