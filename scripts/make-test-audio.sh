#!/bin/zsh
# 結合テスト用の音声を作る（macOS の say と ffmpeg を使用）。
#   ./scripts/make-test-audio.sh <出力フォルダ>
set -euo pipefail
out="${1:?出力フォルダを指定してください}"
mkdir -p "$out"
cd "$out"
command -v ffmpeg >/dev/null || { echo "ffmpeg が必要です（brew install ffmpeg）" >&2; exit 1; }

say -v Kyoko -o ja1.aiff "本日は晴天なり。これは音声認識のテストです。会議の議事録を自動で作成します。"
say -v Kyoko -o ja2.aiff "二つ目のファイルです。東京の天気は明日、雨の予報です。"
say -v Samantha -o en1.aiff "This is an English test file for automatic language detection."
q=(-loglevel error -y -i ja1.aiff)
ffmpeg $q ja1.wav
ffmpeg $q -c:a libmp3lame ja1.mp3
ffmpeg $q -c:a aac ja1.m4a
ffmpeg $q -c:a alac ja1_alac.m4a
ffmpeg $q -c:a aac ja1.aac
ffmpeg $q ja1.caf
ffmpeg $q ja1.flac
ffmpeg $q -c:a libopus ja1.ogg
ffmpeg $q -c:a libopus ja1.opus
ffmpeg $q -c:a libopus ja1.webm
ffmpeg $q -ac 2 -ar 48000 ja1_stereo48k.wav
ffmpeg -loglevel error -y -f lavfi -i color=c=black:s=320x240:d=8 -i ja1.aiff -shortest -c:v libx264 -c:a aac ja1.mp4
ffmpeg -loglevel error -y -f lavfi -i color=c=black:s=320x240:d=8 -i ja1.aiff -shortest -c:v libx264 -c:a aac ja1.mov
head -c 2000 /dev/urandom > broken.mp3

# 動画（音声を MP3 に変換してから文字起こしする経路のテスト用）
v=(-loglevel error -y -f lavfi -i color=c=navy:s=320x240:d=8 -i ja1.aiff -shortest)
ffmpeg $v -c:v libx264 -c:a aac ja1.m4v
ffmpeg $v -c:v libx264 -c:a aac -f 3gp ja1.3gp
ffmpeg $v -c:v libx265 -tag:v hvc1 -c:a aac ja1_hevc.mp4
ffmpeg $v -c:v libx264 -c:a alac ja1_alac.mov
ffmpeg $v -c:v libx264 -c:a pcm_s16le ja1_pcm.mov
ffmpeg $v -c:v prores_ks -c:a pcm_s16le ja1_prores.mov
ffmpeg $v -c:v libx264 -c:a libmp3lame ja1_mp3audio.mp4
ffmpeg $v -c:v libx264 -c:a aac ja1.mkv
ffmpeg $v -c:v mpeg4 -c:a libmp3lame ja1.avi
ffmpeg $v -c:v libvpx-vp9 -c:a libopus ja1_video.webm
ffmpeg $v -c:v libx264 -c:a aac -f mpegts ja1.ts
ffmpeg $v -c:v mpeg2video -c:a mp2 ja1.mpg
ffmpeg -loglevel error -y -f lavfi -i color=c=navy:s=320x240:d=8 -c:v libx264 silent.mp4
ffmpeg -loglevel error -y -f lavfi -i color=c=navy:s=320x240:d=8 -i ja1.aiff -i en1.aiff -map 0:v -map 1:a -map 2:a -shortest -c:v libx264 -c:a aac multi_audio.mov

# 「これはN番目の文で、…」を 40 文並べた約 2 分半の音声（チャンク境界の検証用）
topics=(天気 会議 予算 新製品 旅行 健康 教育 交通 料理 音楽)
text=""
for i in {1..40}; do text+="これは${i}番目の文で、話題は${topics[$(( i % 10 + 1 ))]}です。"; done
say -v Kyoko -o long.aiff "$text"
# 長い動画（キャンセルのテスト用）
ffmpeg -loglevel error -y -f lavfi -i color=c=black:s=320x240:d=160 -i long.aiff -shortest -c:v libx264 -preset ultrafast -c:a aac long.mp4
echo "作成しました: $out"
