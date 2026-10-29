# Whisper Batch（Windows 版）

複数の音声・動画ファイルを Whisper でまとめて文字起こしし、結果を 1 つのファイルに統合して保存できる Windows アプリです。
文字起こしと動画の変換はすべてこの PC の中で行い、音声や結果を外部へ送信しません。

macOS（Apple Silicon）向けの SwiftUI 版を、Windows（x64 + NVIDIA GPU）向けに書き直したものです。機能・画面構成・出力形式は macOS 版と同じです（違いは「[macOS 版との違い](#macos-版との違い)」）。

- C# / .NET 10 / WPF のネイティブ Windows アプリ（Windows 11 の Fluent テーマ、ライト・ダークはシステムに追従）
- 認識エンジン: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) v1.9.5（macOS 版と同じビルド b5454 の公式 Windows CUDA 版）。**NVIDIA GPU（CUDA）で処理**し、GPU がなければ CPU で処理
- 音声・動画の読み込み: Windows 標準の Media Foundation（Ogg・CAF・AIFF は C# のデコーダーで補完）
- 動画: 音声を取り出して MP3 に変換してから文字起こし（MP3 エンコードは Windows 標準の MP3 エンコーダー）
- 表示言語: 日本語・英語（Windows の表示言語から自動選択、設定で切り替え可）
- 既定モデル: Large v3 Turbo（量子化 Q5_0、574 MB）／文字起こしの初期言語: 日本語

## 動作環境

| 項目 | 内容 |
| --- | --- |
| OS | Windows 10 / 11（x64） |
| GPU | NVIDIA GPU（CUDA 12 対応ドライバー）。ない場合は CPU で処理（大幅に遅くなる） |
| 必要なランタイム | .NET 10 Desktop Runtime（`-SelfContained` でビルドした場合は不要）、Visual C++ 再頒布可能パッケージ（x64） |
| 確認済みの環境 | Windows 11 Home、AMD Ryzen 7 7800X3D、NVIDIA GeForce RTX 4080 SUPER（ドライバー 616.92）、ダークモード |

処理速度の目安（RTX 4080 SUPER、Large v3 Turbo Q5_0、約 2 分 53 秒の日本語音声）: **GPU で約 4 秒**（CPU だけでは約 98 秒）。

## ビルドと実行

```powershell
./scripts/build.ps1                  # リリースビルド → build\WhisperBatch\WhisperBatch.exe
./scripts/build.ps1 -SelfContained   # .NET ランタイムも同梱する（配布用）
.\build\WhisperBatch\WhisperBatch.exe
```

必要なもの: .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`。`%LOCALAPPDATA%\Microsoft\dotnet` にユーザー単位で入れたものも使えます）。Visual Studio や CUDA Toolkit は不要です。

ビルド時に次のものをネットワークから取得し、内容をハッシュで検証します。

| 取得するもの | 取得元 | 検証 |
| --- | --- | --- |
| whisper.cpp の Windows CUDA 版（約 685 MB の zip から必要な DLL だけを展開） | GitHub（ggml-org/whisper.cpp のリリース b5454 の `whisper-bin-win-cuda-12.4.0-x64.zip`） | `scripts/fetch-whisper.ps1` の SHA-256 |
| NuGet パッケージ（NAudio、NVorbis、Concentus） | nuget.org | NuGet の署名・ハッシュ |

`scripts/fetch-whisper.ps1` は zip の中から、アプリが読み込む DLL（`whisper.dll`、`ggml*.dll`、CUDA ランタイムの `cudart64_12.dll`・`cublas64_12.dll`・`cublasLt64_12.dll`）だけを `native/win-x64/` に展開します（Git の追跡対象外）。配布物に含まれる実行ファイルは使いません。依存関係は各 DLL のインポート表で確認しています。`dotnet build` を直接使う場合は先に `./scripts/fetch-whisper.ps1` を実行してください。

出力フォルダは約 1.1 GB です（大半は CUDA ランタイムと `ggml-cuda.dll`）。

## テスト

```powershell
./scripts/test.ps1      # ロジックのテスト（キュー、状態遷移、出力形式、チャンク境界、表示文字列など）と DLL の確認
```

実際の音声・動画とモデルを使う結合テストは、環境変数を指定したときだけ実行されます。テスト素材はリポジトリに含めず、スクリプトで生成します。

```powershell
./scripts/make-test-audio.ps1 $env:TEMP\wb-audio     # Windows の音声合成（Haruka / Zira）と ffmpeg で作成
$env:WHISPER_BATCH_TEST_AUDIO_DIR = "$env:TEMP\wb-audio"
$env:WHISPER_BATCH_TEST_MODEL = "$env:LOCALAPPDATA\WhisperBatch\Models\ggml-large-v3-turbo-q5_0.bin"
$env:WHISPER_BATCH_TEST_DOWNLOAD_DIR = "$env:TEMP\wb-models"
./scripts/test.ps1
```

- DLL のテスト: C# 側の構造体が whisper.dll と同じ並びか（DLL が返す既定値で確認）、CUDA のバックエンドが読み込まれ GPU が見えているか
- 表示文字列のテスト: 日英のキーが一致しているか、書式指定が両言語で一致しているか、ソース（C# と XAML）で使っているキーがすべて定義されているか、英語に日本語が混ざっていないか、macOS 固有の言葉（Finder、⌘ など）が残っていないか
- 読み込み・変換のテスト: 対応形式が 16 kHz モノラルに変換できて長さが一致すること、動画の MP3 変換（出力が MP3 として読めて長さとビットレートが合い、元ファイルが変わらないこと）、非対応形式・音声のない動画・存在しないファイルのエラー、キャンセル時に書きかけの MP3 が残らないこと、5.1ch のダウンミックス、日本語・空白・`#` を含むファイル名
- 認識のテスト（GPU）: 日本語の認識、英語の自動判定、チャンク分割で文が欠けたり重複したりしないこと、前のファイルの内容が次に混ざらないこと、キャンセルがすぐ効くこと、動画 → MP3 → 文字起こし
- ダウンロードのテスト: キャンセル、取得と SHA-256 の検証、途中からの再開（HTTP の Range 要求）
- 容量不足のテストは、小さいボリュームを用意して `WB_SMALL_VOLUME` にそのパスを指定したときだけ実行されます

画面の動作（追加・処理・キャンセル・統合保存・個別書き出し・上書き時の「両方残す」・MP3 保存・表示言語の切り替え・処理中に閉じたときの確認と後片付け・2 つ目の起動からのファイルの受け渡し）は、UI オートメーションで実際のアプリを操作して確認しました。

## 使い方

1. 音声・動画ファイルを追加します。
   - **ウィンドウ内のどこにでも**ドラッグ＆ドロップ（キュー、結果表示、設定パネル、下部の操作バーのどこでも可。複数ファイル・フォルダ可）
   - 「ファイルを追加…」（Ctrl+O）、「フォルダを追加…」（Ctrl+Shift+O）
   - エクスプローラーで `WhisperBatch.exe`（またはそのショートカット）にファイルをドロップ。アプリが起動中なら、そのウィンドウのキューに追加されます
2. モデルが未取得なら「ダウンロード…」を押し、確認ダイアログで取得元・サイズ・保存先を確認してからダウンロードします。
3. 文字起こしの言語を選び、「文字起こしを開始」（Ctrl+R）を押します。キューの上から順に処理し、「キャンセル」（Ctrl+.）で中止できます。
4. キューの項目を選ぶと結果を確認できます（タイムスタンプ表示、コピー、Ctrl+F で検索）。
5. 保存します。
   - **統合して保存…**（Ctrl+Shift+S）: 含める結果と順番を決めて（Alt+↑↓ で並べ替え）、1 つのテキストファイルにまとめます。
   - **個別に書き出す…**: 完了した結果をフォルダへそれぞれ書き出します（TXT / SRT / VTT）。
   - 各結果の「このファイルを書き出す」: 1 件だけ書き出します（TXT / SRT / VTT）。
   - 動画の項目では「**音声を MP3 で保存…**」で、動画の音声を MP3 として好きな場所に保存できます（このときだけ MP3 を残します）。

設定パネルの右下に、認識に使う装置（例: 「処理: NVIDIA GeForce RTX 4080 SUPER」）を表示します。

### キューの扱い

- **状態表示**: 音声は「待機中 → 文字起こし中 → 完了」、動画は「変換待ち → MP3 に変換中 → 文字起こし中 → 完了」と表示し、それぞれ進捗（%）を出します。失敗・キャンセル済みも項目ごとに表示します。
- **重複**: 同じファイル（`..` やシンボリックリンクを解決し、大文字小文字を区別しない同じパス）は追加しません。スキップしたことを表示し、既にある項目を選択します。
- **対象外のファイル**: 拡張子が音声・動画でないファイル（.txt やプレイリストなど）は追加せず、理由を表示します。読み込めない形式は追加直後の確認で失敗として表示します。
- **失敗**: 1 件が失敗しても残りの処理は続けます。
- **削除・再試行**: 選択項目の削除（Delete）、待機中の一括削除、処理中以外の一括削除、失敗・キャンセルした項目の再試行（右クリックメニュー・下部の「…」）。
- キューと結果はアプリを終了すると消えます。処理中・変換中・ダウンロード中に閉じようとすると確認します。

## 動画の文字起こし（変換方式）

1. Media Foundation で動画を開き、映像トラックの有無と音声トラックを確認する
2. 最初の音声トラックをデコードし（44.1 kHz、ステレオ以上は 2ch・モノラルは 1ch）、**Windows 標準の MP3 エンコーダーで MP3（128 kbps 固定）に変換**して一時ファイルに書く
3. その MP3 を Whisper で文字起こしする（音声ファイルと同じ処理）
4. 一時 MP3 を削除する（成功・失敗・キャンセルのどの場合も）

macOS には MP3 エンコーダーがないため macOS 版は LAME を同梱していましたが、Windows には Media Foundation の MP3 エンコーダーが標準で入っているため、外部ライブラリは使いません（LGPL のライブラリの同梱も不要になりました）。

- 一時ファイルの置き場所: `%TEMP%\WhisperBatch-Conversions\`。処理後・失敗時・キャンセル時に削除し、起動時・終了時にも残っていれば削除します。
- 変換前に出力先の空き容量を確認します（目安: 1 分あたり約 1 MB）。
- 元の動画は読み込むだけで、変更・移動・削除はしません。

## 対応形式（Windows 11 で実ファイルにより確認）

| 結果 | 形式 |
| --- | --- |
| 音声 | WAV、MP3、M4A（AAC / ALAC）、AAC（ADTS）、WMA、FLAC、AIFF、CAF（非圧縮 PCM）、Ogg Vorbis、Ogg Opus（.ogg / .opus）、WebM（Opus 音声のみ） |
| 動画（音声を MP3 に変換して処理） | MP4（H.264 / HEVC、AAC / MP3 音声）、MOV（AAC / ALAC / リニア PCM、ProRes 映像を含む）、M4V、3GP、MKV、WebM（VP9 + Opus）、AVI、WMV、MPEG-2 プログラムストリーム（.mpg）、MPEG-2 トランスポートストリーム（.ts） |
| 5.1ch などの音声 | ステレオにまとめてから処理 |
| 複数の音声トラックがある動画 | 最初の音声トラックを使います |
| 音声のない動画 | 「Windows で読み込める音声が見つかりません」と表示 |
| 壊れたファイル | 「対応していない形式です」と表示 |

- 映像トラックのコーデック（HEVC、ProRes、VP9 など）は使わないので、映像を再生できない動画でも音声が読めれば処理できます。
- FLAC と AAC（ADTS）は、ファイルの情報から得られる長さが推定値のため、キューに表示する長さが実際と 1 秒未満ずれることがあります（文字起こしには影響しません）。
- Ogg・CAF・AIFF は Media Foundation が対応していないため、C# のデコーダー（NVorbis、Concentus、自前の CAF / NAudio の AIFF 読み込み）で読みます。CAF は中身が非圧縮 PCM のものだけに対応しています。
- 上記以外（FLV、.mts / .m2ts など）は確認していません。追加はできますが、Windows で読めない場合はエラーになります。

## 表示言語（日本語・英語）

- **初期設定**: Windows の表示言語の優先順（設定 > 時刻と言語 > 言語と地域）で、日本語と英語のうち先にある方を使います。どちらもなければ英語です。
- **切り替え方**: 「ファイル > 設定…」（Ctrl+,）で「システムに合わせる / 日本語 / English」を選びます。画面の文字はすぐに切り替わり、選択は次回起動後も保持されます。
- ファイルを開く・保存するダイアログなど Windows 標準の画面は、Windows の表示言語で表示されます。
- **文字起こしの言語は別の設定**です。表示を英語にしても、認識する言語は変わりません。
- 文字列は macOS 版と同じ `Localization/ja.lproj/Localizable.strings` と `Localization/en.lproj/Localizable.strings` にあり、C# からは `L("キー")`、XAML からは `{l:L キー}` で参照します。

## アニメーション

操作の分かりやすさのための短い動き（0.12〜0.22 秒）だけを使います。設定は `src/WhisperBatch.App/Infrastructure/Motion.cs` にまとめています。

| 操作 | 動き | 「アニメーション効果」がオフのとき |
| --- | --- | --- |
| ファイルの追加 | キューの行が短くフェードし、少し上からスライドして入る | フェードのみ |
| 状態の変化 | 完了・失敗時に状態アイコンが 1 回だけ小さく弾む | 弾まない |
| 変換 → 文字起こし | 処理中の表示のラベルがフェードで入れ替わる | 即座に切り替え |
| ドラッグ中 | ウィンドウの強調表示と案内がフェードで現れる・消える | 即座に表示 |
| 結果表示・下部のバナー | 状態が変わると結果表示がフェード、下部のバナーは下からフェードで入れ替わる | 透明度の変化のみ |

- Windows の「設定 > アクセシビリティ > 視覚効果 > アニメーション効果」に従います。開発時は `WHISPER_BATCH_REDUCE_MOTION=1` で、設定を変えずに確認できます。

## Whisper の実装方式

whisper.cpp の**公式 Windows CUDA ビルドの DLL**を、C# から P/Invoke で直接呼んでいます（macOS 版が XCFramework の C API を Swift から呼んでいたのと同じ構成）。C# 側の構造体（`whisper_full_params` など）は `whisper.h` と同じ並びで定義し、起動時に DLL が返す既定値と照合して、ずれていれば読み込みを止めます。ggml の各バックエンド（CPU 用の最適化版と CUDA）は実行ファイルのフォルダから明示的に読み込みます。

認識の設定（ビームサーチ 5、Flash Attention、呼び出しごとに文脈をリセット、非音声トークンの抑制）、10 分ごとのチャンク分割、チャンク境界を末尾付近の最も静かな位置に合わせる処理は macOS 版と同じです。

Windows 版では、Whisper が 30 秒ごとの区切りで直前の文を**隙間なくそのまま繰り返す**誤認識だけを取り除く処理を加えています（同じ文が 6 文字以上で、直前の区間の終わりと 0.05 秒未満でつながっている場合のみ。実際に繰り返して話した場合は間が入るので残ります）。CUDA の計算結果では、テスト音声でこの繰り返しが 1 か所起きたためです（CPU・Flash Attention なしでは別の小さな誤りが出ており、モデルの数値的な揺れによるものと判断しました）。

## モデルの取得と保存

モデルはアプリに同梱していません。利用者が確認ダイアログで同意したときだけ、Hugging Face（`huggingface.co/ggerganov/whisper.cpp`）からダウンロードし、SHA-256 を検証してから保存します。

| モデル | ファイル | サイズ | 必要な空き容量の目安 |
| --- | --- | --- | --- |
| Tiny | ggml-tiny.bin | 77.7 MB | 約 128 MB |
| Base | ggml-base.bin | 148 MB | 約 198 MB |
| Small | ggml-small.bin | 488 MB | 約 538 MB |
| Medium | ggml-medium.bin | 1.53 GB | 約 1.69 GB |
| **Large v3 Turbo（Q5_0）** | ggml-large-v3-turbo-q5_0.bin | 574 MB | 約 631 MB（**既定**） |
| Large v3 Turbo | ggml-large-v3-turbo.bin | 1.62 GB | 約 1.79 GB |
| Large v3 | ggml-large-v3.bin | 3.10 GB | 約 3.40 GB |

- 保存先: `%LOCALAPPDATA%\WhisperBatch\Models\`（「モデル管理…」の「エクスプローラーで表示」で開けます）
- 通信エラーで失敗した場合は「再開」で途中から（HTTP の Range 要求）、それ以外は「再試行」で最初から取得し直します。60 秒間データが届かない場合は通信エラーとします。
- 設定は `%APPDATA%\WhisperBatch\settings.json`（文字起こしの言語、モデル、表示言語、タイムスタンプ表示、ウィンドウの位置）に保存します。

## 出力形式

| 形式 | 内容 |
| --- | --- |
| テキスト (.txt) | 認識区間ごとに 1 行。オプションで各行にタイムスタンプ |
| SRT (.srt) / WebVTT (.vtt) | 字幕形式（WebVTT の本文中の `-->` は `→` に置き換え） |
| 統合ファイル (.txt) | 各結果を `[番号/件数] ファイル名` の見出しで区切り、指定した順に並べる（統合はテキストのみ） |
| MP3（動画の音声） | 「音声を MP3 で保存…」を選んだときだけ。128 kbps、44.1 kHz |

UTF-8（BOM なし）・LF で、一時ファイル経由で置き換えるため書き込みに失敗しても既存ファイルは壊れません（macOS 版と同じ。Windows 10 以降のメモ帳でもそのまま開けます）。保存先は必ずダイアログで選び、既存ファイルの上書きは確認します。個別書き出しで同じ名前のファイルがある場合は「両方残す（連番を付ける）／置き換える」を選べます。

## プライバシー

- 文字起こしと動画の変換はすべてこの PC 上で行います。音声・動画・認識結果・利用状況を外部へ送信しません。
- 通信するのはモデルのダウンロード時だけで、送るのはファイルの取得要求（`huggingface.co` への HTTPS GET）だけです。ダウンロード前に確認ダイアログを表示します。
- whisper.cpp のログは出力しません（開発時の調査用に `WHISPER_BATCH_VERBOSE=1` で標準エラーに出せます）。
- 予期しないエラーが起きた場合だけ、調査用にエラーの内容（スタックトレース）を `%LOCALAPPDATA%\WhisperBatch\error.log` に書きます。音声や結果の内容は含みません。

## ライセンスと配布上の注意

| コンポーネント | ライセンス | 扱い |
| --- | --- | --- |
| Whisper Batch（このリポジトリのソース） | MIT | [LICENSE](LICENSE) |
| whisper.cpp / ggml | MIT（© The ggml authors） | DLL として同梱 |
| NVIDIA CUDA ランタイム（cudart / cuBLAS） | NVIDIA CUDA EULA（再頒布可能ファイル） | whisper.cpp の公式ビルドに含まれるものを同梱 |
| NAudio | MIT | Media Foundation の呼び出しに使用 |
| NVorbis | MIT | Ogg Vorbis のデコード |
| Concentus / Concentus.OggFile | BSD（Opus のライセンス）/ MIT | Ogg Opus のデコード |
| Whisper モデル | MIT（OpenAI）。ggml 変換版も MIT | 同梱せず、利用者がダウンロード |

全文は `App/ThirdPartyNotices.txt`（アプリのフォルダの `ThirdPartyNotices.txt`、メニュー「ヘルプ > サードパーティのライセンス」）にあります。

## macOS 版との違い

| 項目 | macOS 版 | Windows 版 |
| --- | --- | --- |
| UI | SwiftUI | WPF（Fluent テーマ） |
| GPU | Metal（Apple Silicon） | CUDA（NVIDIA）。なければ CPU |
| 読み込み | AVFoundation | Media Foundation ＋ C# のデコーダー |
| MP3 エンコード | LAME（LGPL、動的ライブラリで同梱） | Windows 標準の MP3 エンコーダー |
| 読める形式 | MKV・WebM・AVI は不可 | MKV・WebM・AVI・WMV・WMA も可 |
| ファイルの受け取り | Finder の「このアプリケーションで開く」 | exe へのドロップ・2 つ目の起動から起動中のアプリへ受け渡し |
| フォルダの追加 | ファイル選択と同じダイアログ | 「フォルダを追加…」（Windows のファイル選択ダイアログはファイルとフォルダを同時に選べないため） |
| サンドボックス | App Sandbox・セキュリティスコープ付きブックマーク | なし（Windows デスクトップアプリの通常の権限） |
| 区切りでの繰り返し | なし | 隙間なく続く同じ文を取り除く（上記） |

## 既知の制約・未確認の点

- コード署名をしていないため、初回起動時に SmartScreen の警告が出ることがあります。インストーラーはありません（フォルダごとコピーして使います）。
- エクスプローラーの「プログラムから開く」への登録はしていません（exe やショートカットへのドロップは使えます）。
- GPU 処理は NVIDIA（CUDA）のみです。AMD・Intel の GPU では CPU で処理します。
- 動作確認は Windows 11 Home / RTX 4080 SUPER / ダークモードの 1 台だけです。ライトテーマ、高 DPI の複数モニター、ナレーターでの読み上げは確認していません（UI オートメーションの名前は確認済み）。
- **ドラッグ＆ドロップ**は実装していますが、自動テストでは確認できていません（操作中の PC でマウスを動かす必要があるため）。ドロップ後の追加処理はファイル選択・exe へのドロップと共通で、そちらは確認済みです。
- キューの行の削除時のアニメーション（macOS 版のフェードアウト）はありません。追加時のみです。
- 認識・変換の検証には、Windows の音声合成で作った音声と、それを ffmpeg で動画にしたものを使いました。実際の録音・撮影データや複数話者での精度は確認していません。
- 話者分離、翻訳、音声区間検出（VAD）、単語単位のタイムスタンプには対応していません。統合保存はテキスト形式のみです。

## ソースの構成

```
WhisperBatch.slnx                  ソリューション
Directory.Build.props              共通のビルド設定（x64、.NET 10）
Localization/<言語>.lproj/          表示文字列（日本語・英語。macOS 版と同じ形式）
src/WhisperBatch.Core/             UI に依存しないロジック（キュー・状態遷移・出力形式・チャンク境界・表示言語）
src/WhisperBatch.Engine/           whisper.cpp・Media Foundation・MP3 変換・モデル取得
  Native/                            whisper.cpp / ggml の P/Invoke と初期化（DLL の読み込み、構造体の照合、GPU の確認）
  Media/                             音声・動画の読み込み（Media Foundation、Ogg、CAF、AIFF）と 16 kHz への変換
  Mp3Converter.cs                    動画の音声 → MP3
src/WhisperBatch.App/              WPF アプリ（Views/ 画面、ViewModels/ 状態、Infrastructure/ アニメーション・設定など）
src/Native.targets                 whisper.cpp の DLL と表示文字列を出力フォルダに置く
tests/                             xUnit のテスト
App/                               アイコン（AppIcon.ico）、サードパーティのライセンス表記
scripts/                           build.ps1、test.ps1、fetch-whisper.ps1、make-test-audio.ps1、make-icon.ps1
```
