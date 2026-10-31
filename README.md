# Whisper Batch（Windows 版）

複数の音声・動画ファイルをまとめて文字起こしし、結果を 1 つのファイルに統合して保存できる Windows アプリです。文字起こしの方式は Whisper・ReazonSpeech から選べます。
文字起こしと動画の変換はすべてこの PC の中で行い、音声や結果を外部へ送信しません。

macOS（Apple Silicon）向けの SwiftUI 版を、Windows（x64 + NVIDIA GPU）向けに書き直したものです。機能・画面構成・出力形式は macOS 版（1.1.1）と同じです（違いは「[macOS 版との違い](#macos-版との違い)」）。

- C# / .NET 10 / WPF のネイティブ Windows アプリ（Windows 11 の Fluent テーマ）
- 認識エンジン（方式）:
  - **Whisper**: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) v1.9.5（macOS 版と同じビルド b5454 の公式 Windows CUDA 版）。**NVIDIA GPU（CUDA）で処理**し、GPU がなければ CPU で処理
  - **ReazonSpeech**: 日本語専用の [reazonspeech-k2-v2](https://huggingface.co/reazon-research/reazonspeech-k2-v2) を [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) v1.13.8（macOS 版と同じ版）で実行。CPU で処理
- 音声・動画の読み込み: Windows 標準の Media Foundation（Ogg・CAF・AIFF は C# のデコーダーで補完）
- 動画: 音声を取り出して MP3 に変換してから文字起こし（MP3 エンコードは Windows 標準の MP3 エンコーダー）
- 表示言語: 日本語・英語（Windows の表示言語から自動選択、設定で切り替え可）
- 書体・文字の大きさ、背景（色・画像）とライト／ダークを設定で変更可
- 既定の方式・モデル: Whisper の Large v3 Turbo（量子化 Q5_0、574 MB）／文字起こしの初期言語: 日本語

## 動作環境

| 項目 | 内容 |
| --- | --- |
| OS | Windows 10 / 11（x64） |
| GPU | NVIDIA GPU（CUDA 12 対応ドライバー）。ない場合は CPU で処理（大幅に遅くなる） |
| 必要なランタイム | .NET 10 Desktop Runtime（`-SelfContained` でビルドした場合は不要）、Visual C++ 再頒布可能パッケージ（x64） |
| 確認済みの環境 | Windows 11 Home、AMD Ryzen 7 7800X3D、NVIDIA GeForce RTX 4080 SUPER（ドライバー 616.92）、ダークモード |

処理速度の目安（約 2 分 53 秒の日本語音声）: Whisper（RTX 4080 SUPER、Large v3 Turbo Q5_0）は **GPU で約 4 秒**（CPU だけでは約 98 秒）。ReazonSpeech（軽量 int8、Ryzen 7 7800X3D の CPU）は約 10 秒。

## インストール

[Releases](https://github.com/TKMS981/whisper-batch/releases) から `WhisperBatch-<版>-win-x64-setup.exe` をダウンロードして実行します。

- .NET ランタイムを同梱しているので、別途インストールは不要です。Visual C++ ランタイム（x64）がない PC では、インストール中に自動で入れます（このときだけ管理者の確認が出ます）。
- 既定では「自分だけ」にインストールします（管理者権限不要、`%LOCALAPPDATA%\Programs\Whisper Batch`）。最初の画面で「すべてのユーザー」も選べます。
- オプション: デスクトップのショートカット、エクスプローラーの「送る」メニュー、「プログラムから開く」への登録（既定でオン。既定のアプリにはしません）。
- アンインストールは「設定 > アプリ」から。ダウンロードしたモデルを消すかどうかを確認します（既定は残す）。
- コード署名をしていないため、初回は SmartScreen の「Windows によって PC が保護されました」が出ることがあります（「詳細情報」→「実行」）。
- ダウンロードしたファイルは、リリースに添付した `.sha256` の値と照合できます（`Get-FileHash <ファイル>`）。

インストーラーの作成: `./scripts/make-installer.ps1`（Inno Setup 6 が必要。`winget install JRSoftware.InnoSetup`）→ `build\installer\`

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
| NuGet パッケージ（NAudio、NVorbis、Concentus、sherpa-onnx） | nuget.org | NuGet の署名・ハッシュ |

`scripts/fetch-whisper.ps1` は zip の中から、アプリが読み込む DLL（`whisper.dll`、`ggml*.dll`、CUDA ランタイムの `cudart64_12.dll`・`cublas64_12.dll`・`cublasLt64_12.dll`）だけを `native/win-x64/` に展開します（Git の追跡対象外）。配布物に含まれる実行ファイルは使いません。依存関係は各 DLL のインポート表で確認しています。`dotnet build` を直接使う場合は先に `./scripts/fetch-whisper.ps1` を実行してください。

sherpa-onnx は NuGet パッケージ `org.k2fsa.sherpa.onnx` 1.13.8 を使います。C# の構造体定義と、win-x64 用の `sherpa-onnx-c-api.dll`・`onnxruntime.dll`（ONNX Runtime 1.28.2）が出力フォルダに入ります（このパッケージは他の OS 用のランタイムパッケージにも依存しているため、初回の復元ではそれらも NuGet のキャッシュに取得されます）。

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
$env:WHISPER_BATCH_TEST_REAZON_DIR = "<ReazonSpeech の 4 ファイルを置いたフォルダ>"   # 任意
./scripts/test.ps1
```

- DLL のテスト: C# 側の構造体が whisper.dll と同じ並びか（DLL が返す既定値で確認）、CUDA のバックエンドが読み込まれ GPU が見えているか
- 表示文字列のテスト: 日英のキーが一致しているか、書式指定が両言語で一致しているか、ソース（C# と XAML）で使っているキーがすべて定義されているか、英語に日本語が混ざっていないか、macOS 固有の言葉（Finder、⌘ など）が残っていないか
- 読み込み・変換のテスト: 対応形式が 16 kHz モノラルに変換できて長さが一致すること、動画の MP3 変換（出力が MP3 として読めて長さとビットレートが合い、元ファイルが変わらないこと）、非対応形式・音声のない動画・存在しないファイルのエラー、キャンセル時に書きかけの MP3 が残らないこと、5.1ch のダウンミックス、日本語・空白・`#` を含むファイル名
- 認識のテスト（GPU）: 日本語の認識、英語の自動判定、チャンク分割で文が欠けたり重複したりしないこと、前のファイルの内容が次に混ざらないこと、キャンセルがすぐ効くこと、動画 → MP3 → 文字起こし
- ダウンロードのテスト: キャンセル、取得と SHA-256 の検証、途中からの再開（HTTP の Range 要求）
- 容量不足のテストは、小さいボリュームを用意して `WB_SMALL_VOLUME` にそのパスを指定したときだけ実行されます
- ReazonSpeech のテスト（`tests/WhisperBatch.Engine.Tests/ReazonSpeechTests.cs`）: `WHISPER_BATCH_TEST_REAZON_DIR`（encoder / decoder / joiner / tokens.txt を置いたフォルダ、モデルは既定で軽量 int8）を指定したときだけ実行されます。日本語の認識、30 秒を超える音声の区切り、キャンセル、ファイルが揃っていないときのエラー。日本語を含むフォルダのパスでも読み込めることを確認しています
- 環境変数なしで動くテスト: OS のエラーの置き換え（`ErrorMapper`）、設定の保存（以前の版の設定ファイルの読み込みを含む）、発話ごとの区切り、音声の読み込み窓、一括処理（偽の文字起こしを使った成功・失敗・キャンセル・探索方式の受け渡し）、ダウンロード先の制限、空き容量の確認、読み込みの中止、「完了した項目をすべて削除」

画面の動作（追加・処理・キャンセル・統合保存・個別書き出し・上書き時の「両方残す」・MP3 保存・表示言語の切り替え・処理中に閉じたときの確認と後片付け・2 つ目の起動からのファイルの受け渡し）は、UI オートメーションで実際のアプリを操作して確認しました。

## 使い方

1. 音声・動画ファイルを追加します。
   - **ウィンドウ内のどこにでも**ドラッグ＆ドロップ（キュー、結果表示、設定パネル、下部の操作バーのどこでも可。複数ファイル・フォルダ可）
   - 「ファイルを追加…」（Ctrl+O）、「フォルダを追加…」（Ctrl+Shift+O）
   - エクスプローラーで `WhisperBatch.exe`（またはそのショートカット）にファイルをドロップ。アプリが起動中なら、そのウィンドウのキューに追加されます
2. 「方式」で Whisper / ReazonSpeech を選び、「モデル」でその方式のモデルを選びます。モデルが未取得なら「ダウンロード…」を押し、確認ダイアログで取得元・サイズ・保存先を確認してからダウンロードします。
3. 文字起こしの言語を選び（ReazonSpeech は日本語に固定）、「文字起こしを開始」（Ctrl+R）を押します。キューの上から順に処理し、「キャンセル」（Ctrl+.）で中止できます。
4. キューの項目を選ぶと結果を確認できます（タイムスタンプ表示、コピー、Ctrl+F で検索）。
5. 保存します。
   - **統合して保存…**（Ctrl+Shift+S）: 含める結果と順番を決めて（Alt+↑↓ で並べ替え）、1 つのテキストファイルにまとめます。
   - **個別に書き出す…**: 完了した結果をフォルダへそれぞれ書き出します（TXT / SRT / VTT）。
   - 各結果の「このファイルを書き出す」: 1 件だけ書き出します（TXT / SRT / VTT）。
   - 動画の項目では「**音声を MP3 で保存…**」で、動画の音声を MP3 として好きな場所に保存できます（このときだけ MP3 を残します）。保存先の空き容量も変換の前に確かめます。
6. 保存し終えたら、キューの下部にある「**完了 N件を消去**」で完了した項目をまとめて片付けられます（完了した項目があるときだけ表示。「…」メニューの「完了した項目をすべて削除」と同じ）。文字起こし結果も消えるので、先に保存してください。

設定パネルの右下に、認識に使う装置（例: 「処理: NVIDIA GeForce RTX 4080 SUPER」。ReazonSpeech では「処理: CPU」）を表示します。

### キューの扱い

- **状態表示**: 音声は「待機中 → 文字起こし中 → 完了」、動画は「変換待ち → MP3 に変換中 → 文字起こし中 → 完了」と表示し、それぞれ進捗（%）を出します。失敗・キャンセル済みも項目ごとに表示します。
- **重複**: 同じファイル（`..` やシンボリックリンクを解決し、大文字小文字を区別しない同じパス）は追加しません。スキップしたことを表示し、既にある項目を選択します。
- **対象外のファイル**: 拡張子が音声・動画でないファイル（.txt やプレイリストなど）は追加せず、理由を表示します。読み込めない形式は追加直後の確認で失敗として表示します。
- **失敗**: 1 件が失敗しても残りの処理は続けます。
- **削除・再試行**: 選択項目の削除（Delete）、待機中の一括削除、処理中以外の一括削除、失敗・キャンセルした項目の再試行（右クリックメニュー・下部の「…」）。
- キューと結果はアプリを終了すると消えます。処理中・変換中・ダウンロード中に閉じようとすると確認します。
- **エラーの案内**: 権限がない、ディスクがいっぱい、読み取り専用、外付けディスクやネットワーク上の共有フォルダが外れた、オフライン、通信が切れた、サーバーにつながらない、などの OS のエラーは、次に何をすればよいかを含む文で表示します（`src/WhisperBatch.Core/ErrorMapper.cs`。Win32 のエラーコードとソケット・HTTP のエラーで分類）。

## 設定（Ctrl+,）

- **一般**
  - 表示言語（下の「表示言語」を参照）
  - **自動開始**: 「ファイルを追加したらすぐに文字起こしを開始する」をオンにすると、ドロップや「ファイルを追加…」、エクスプローラーからの受け渡しの時点で処理を始めます（既定はオフ）。処理中に追加したファイルは今の処理の続きで扱います。モデルが未取得などで開始できないときは、その理由をキューの上に表示します。
  - **Whisper の認識**: 「精度優先（Beam Search）」（既定。ビーム幅 5）と「速度優先（Greedy）」から選びます。Whisper を使うときだけ反映します。
- **フォント**: 書体（インストール済みの書体から選択、既定はシステム標準）と文字の大きさ（小・標準・大・特大）。画面の文字（ボタンなどの部品を含む）と文字起こし結果の表示に使います。ファイルを開く・保存するダイアログなど Windows 標準の画面は変わりません。保存するファイルには影響しません。選んだ書体にない文字（日本語など）は Yu Gothic UI で表示します。
- **テーマ**
  - **外観**: システムに合わせる / ライト / ダーク
  - **背景**: 標準 / 色 / 画像。色は Windows 標準の「色の設定」ダイアログで選びます。外観が「システムに合わせる」なら背景色の明るさに合わせて文字の色（ライト・ダーク）を自動で選びます。
  - 画像の場合は**不透明度**（5〜100%、既定 30%）を指定できます。選んだ画像は `%LOCALAPPDATA%\WhisperBatch\Background` に複製するため、元のファイルを移動・削除しても背景は残ります。「削除」で複製も消えます。

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

## 文字起こしの方式

メイン画面の「方式」で Whisper / ReazonSpeech を切り替え、「モデル」でその方式のモデル（サイズ）を選びます。「モデル管理…」も方式ごとに一覧を切り替えられます。方式を切り替えて戻ると、前にその方式で選んでいたモデルに戻ります。

| 方式 | 言語 | モデルの選択肢 | 取得元 | 処理 | 特徴 |
| --- | --- | --- | --- | --- | --- |
| Whisper | 多言語（自動判定あり） | Tiny 〜 Large v3 の 7 種類 | Hugging Face（ggerganov/whisper.cpp） | NVIDIA GPU（なければ CPU） | 精度が高い。大きいモデルほど遅い |
| ReazonSpeech | 日本語のみ | 軽量（int8、160 MB）／高精度（615 MB） | Hugging Face（reazon-research/reazonspeech-k2-v2） | CPU | 日本語に特化。速い（約 3 分の音声を約 10 秒） |

### ReazonSpeech

株式会社レアゾン・ホールディングスが公開している日本語の音声認識モデル（Zipformer RNN-T、Apache License 2.0）を、k2-fsa の公式 C# パッケージ（`org.k2fsa.sherpa.onnx` 1.13.8、ONNX Runtime 1.28.2 を同梱）で動かします。モデルは公式リポジトリの ONNX ファイルを、公開時のコミットに固定して取得し、ファイルごとに SHA-256 を検証します（コードを含みうる pickle 形式のファイルは使いません）。

このモデルは 1 つの発話を前提にしているため、複数の文をまとめて渡すと一部の文を出力しないことがあります（macOS 版の試験で確認）。そのため、音声を文の間の無音（0.25 秒以上）で区切り、1 発話ずつ（最長 10 秒）認識します。各発話の前後には公式ライブラリと同じく 0.9 秒の無音を足します。出力に句読点は付かず、1 発話が 1 行（1 区間）になります。

- sherpa-onnx はモデルのパスを UTF-8 で受け取りますが、C# パッケージの構造体は文字列を ANSI（日本語版 Windows では CP932）で渡すため、そのままでは日本語を含むパス（ユーザー名など）のモデルを読み込めません。構造体をネイティブのメモリに書き出してから、パスの欄だけ UTF-8 の文字列に差し替えて渡しています（`src/WhisperBatch.Engine/ReazonSpeechContext.cs`）。
- Windows 11 の `System32` には別の版の `onnxruntime.dll` があるため、同梱の `onnxruntime.dll` をフルパスで先に読み込んでから sherpa-onnx を読み込みます。

## Whisper の実装方式

whisper.cpp の**公式 Windows CUDA ビルドの DLL**を、C# から P/Invoke で直接呼んでいます（macOS 版が XCFramework の C API を Swift から呼んでいたのと同じ構成）。C# 側の構造体（`whisper_full_params` など）は `whisper.h` と同じ並びで定義し、起動時に DLL が返す既定値と照合して、ずれていれば読み込みを止めます。ggml の各バックエンド（CPU 用の最適化版と CUDA）は実行ファイルのフォルダから明示的に読み込みます。

認識の設定（ビームサーチ 5 または Greedy（設定で選択）、Flash Attention、呼び出しごとに文脈をリセット、非音声トークンの抑制）、10 分ごとのチャンク分割、チャンク境界を末尾付近の最も静かな位置に合わせる処理は macOS 版と同じです。長時間の音声は「読み込み窓」（`AudioStreamBuffer`）で少しずつ読み出し、時刻は読み捨てたサンプル数から計算します。

Windows 版では、Whisper が 30 秒ごとの区切りで直前の文を**隙間なくそのまま繰り返す**誤認識だけを取り除く処理を加えています（同じ文が 6 文字以上で、直前の区間の終わりと 0.05 秒未満でつながっている場合のみ。実際に繰り返して話した場合は間が入るので残ります）。CUDA の計算結果では、テスト音声でこの繰り返しが 1 か所起きたためです（CPU・Flash Attention なしでは別の小さな誤りが出ており、モデルの数値的な揺れによるものと判断しました）。

## モデルの取得と保存

モデルはアプリに同梱していません。利用者が確認ダイアログで同意したときだけ、Hugging Face からダウンロードし、ファイルごとに SHA-256 を検証してから保存します。

**Whisper**（`huggingface.co/ggerganov/whisper.cpp`、コミット `5359861` に固定）

| モデル | ファイル | サイズ | 必要な空き容量の目安 |
| --- | --- | --- | --- |
| Tiny | ggml-tiny.bin | 77.7 MB | 約 128 MB |
| Base | ggml-base.bin | 148 MB | 約 198 MB |
| Small | ggml-small.bin | 488 MB | 約 538 MB |
| Medium | ggml-medium.bin | 1.53 GB | 約 1.69 GB |
| **Large v3 Turbo（Q5_0）** | ggml-large-v3-turbo-q5_0.bin | 574 MB | 約 631 MB（**既定**） |
| Large v3 Turbo | ggml-large-v3-turbo.bin | 1.62 GB | 約 1.79 GB |
| Large v3 | ggml-large-v3.bin | 3.10 GB | 約 3.40 GB |

**ReazonSpeech**（`huggingface.co/reazon-research/reazonspeech-k2-v2`、コミット `291488c` に固定）

| モデル | ファイル | サイズ | 必要な空き容量の目安 |
| --- | --- | --- | --- |
| **軽量（int8）** | encoder / decoder / joiner の int8 版 + tokens.txt | 160 MB | 約 210 MB（**ReazonSpeech の既定**） |
| 高精度 | encoder / decoder / joiner + tokens.txt | 615 MB | 約 676 MB |

- 保存先: `%LOCALAPPDATA%\WhisperBatch\Models\`（「モデル管理…」の「エクスプローラーで表示」で開けます）。Whisper はこの直下（以前の版と同じ場所）、ReazonSpeech はモデルごとのフォルダに置きます。
- 複数ファイルのモデルは 1 ファイルずつ取得・検証し、途中で失敗しても検証済みのファイルは取得し直しません。
- main ブランチではなくコミットを固定して取得するので、配布元が更新されても SHA-256 の検証が通らなくなることはありません。
- 通信エラーで失敗した場合は「再開」で途中から（HTTP の Range 要求）、それ以外は「再試行」で最初から取得し直します。60 秒間データが届かない場合は通信エラーとします。
- 設定は `%APPDATA%\WhisperBatch\settings.json`（文字起こしの言語、モデル（方式ごとの最後の選択も）、表示言語、タイムスタンプ表示、自動開始、Whisper の探索方式、書体・文字の大きさ・テーマ、ウィンドウの位置）に保存します。以前の版の設定ファイルもそのまま読めます。

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
- 接続先は `huggingface.co` と `hf.co`（ファイルの実体の配信元。例: `cas-bridge.xethub.hf.co`）とそのサブドメインに、HTTPS（ポート 443）でのみ限っています。リダイレクトは自動ではたどらず、転送先を 1 回ずつ確かめ、それ以外への接続は中止します（公衆 Wi-Fi のログインページへの転送などで起こります）。途中からの再開も同じ確認をします。
- whisper.cpp のログは出力しません（開発時の調査用に `WHISPER_BATCH_VERBOSE=1` で標準エラーに出せます）。
- 予期しないエラーが起きた場合だけ、調査用にエラーの内容（スタックトレース）を `%LOCALAPPDATA%\WhisperBatch\error.log` に書きます。音声や結果の内容は含みません。

## ライセンスと配布上の注意

| コンポーネント | ライセンス | 扱い |
| --- | --- | --- |
| Whisper Batch（このリポジトリのソース） | MIT | [LICENSE](LICENSE) |
| whisper.cpp / ggml | MIT（© The ggml authors） | DLL として同梱 |
| NVIDIA CUDA ランタイム（cudart / cuBLAS） | NVIDIA CUDA EULA（再頒布可能ファイル） | whisper.cpp の公式ビルドに含まれるものを同梱 |
| sherpa-onnx v1.13.8 | Apache-2.0（k2-fsa） | `sherpa-onnx-c-api.dll`・`sherpa-onnx.dll` として同梱。全文は `Licenses\sherpa-onnx-LICENSE.txt` |
| ONNX Runtime 1.28.2 | MIT（Microsoft） | `onnxruntime.dll` として同梱。ONNX Runtime が使うライブラリの表記は `Licenses\onnxruntime-ThirdPartyNotices.txt` |
| NAudio | MIT | Media Foundation の呼び出しに使用 |
| NVorbis | MIT | Ogg Vorbis のデコード |
| Concentus / Concentus.OggFile | BSD（Opus のライセンス）/ MIT | Ogg Opus のデコード |
| Whisper モデル | MIT（OpenAI）。ggml 変換版も MIT | 同梱せず、利用者がダウンロード |
| ReazonSpeech モデル（k2 v2） | Apache-2.0（レアゾン・ホールディングス） | 同梱せず、利用者がダウンロード |

全文は `App/ThirdPartyNotices.txt`（アプリのフォルダの `ThirdPartyNotices.txt`、メニュー「ヘルプ > サードパーティのライセンス」）にあります。

## macOS 版との違い

| 項目 | macOS 版 | Windows 版 |
| --- | --- | --- |
| UI | SwiftUI | WPF（Fluent テーマ） |
| 文字起こしの方式 | Whisper・ReazonSpeech・Apple（macOS 標準の音声認識） | Whisper・ReazonSpeech（Apple の音声認識は macOS の機能のため、Windows 版にはありません） |
| ReazonSpeech の実行 | sherpa-onnx の XCFramework | sherpa-onnx の NuGet パッケージ（同じ v1.13.8） |
| 背景色の選択 | macOS の色パネル | Windows 標準の「色の設定」ダイアログ |
| Developer ID 署名・公証、DMG | あり（環境変数で指定） | 該当なし（Inno Setup のインストーラー） |
| GPU | Metal（Apple Silicon） | CUDA（NVIDIA）。なければ CPU |
| 読み込み | AVFoundation | Media Foundation ＋ C# のデコーダー |
| MP3 エンコード | LAME（LGPL、動的ライブラリで同梱） | Windows 標準の MP3 エンコーダー |
| 読める形式 | MKV・WebM・AVI は不可 | MKV・WebM・AVI・WMV・WMA も可 |
| ファイルの受け取り | Finder の「このアプリケーションで開く」 | 「プログラムから開く」「送る」・exe へのドロップ（起動中のアプリへ受け渡し） |
| フォルダの追加 | ファイル選択と同じダイアログ | 「フォルダを追加…」（Windows のファイル選択ダイアログはファイルとフォルダを同時に選べないため） |
| サンドボックス | App Sandbox・セキュリティスコープ付きブックマーク | なし（Windows デスクトップアプリの通常の権限） |
| 区切りでの繰り返し | なし | 隙間なく続く同じ文を取り除く（上記） |

## 既知の制約・未確認の点

- コード署名をしていないため、初回起動時に SmartScreen の警告が出ることがあります。
- GPU 処理は NVIDIA（CUDA）のみです。AMD・Intel の GPU では CPU で処理します。
- 動作確認は Windows 11 Home / RTX 4080 SUPER / ダークモードの 1 台だけです。ライトテーマ、高 DPI の複数モニター、ナレーターでの読み上げは確認していません（UI オートメーションの名前は確認済み）。
- **ドラッグ＆ドロップ**は実装していますが、自動テストでは確認できていません（操作中の PC でマウスを動かす必要があるため）。ドロップ後の追加処理はファイル選択・exe へのドロップと共通で、そちらは確認済みです。
- キューの行の削除時のアニメーション（macOS 版のフェードアウト）はありません。追加時のみです。
- 認識・変換の検証には、Windows の音声合成で作った音声と、それを ffmpeg で動画にしたものを使いました。実際の録音・撮影データや複数話者での精度は確認していません。
- ReazonSpeech は文の間の無音で区切って認識するため、間を置かずに話し続ける音声では、10 秒ごとに最も静かな位置で区切ります（語の途中で切れることがあります）。出力に句読点は付きません。GPU は使いません。
- 「フォント」の書体と文字の大きさ、「テーマ」の外観（ライト・ダーク）は、このアプリの画面（メイン画面・設定・モデル管理・統合保存・確認ダイアログ）に反映します。背景（色・画像）はメイン画面だけです。ファイルを開く・保存するダイアログや「色の設定」ダイアログなど Windows 標準の画面には反映しません。
- 話者分離、翻訳、音声区間検出（VAD）、単語単位のタイムスタンプには対応していません。統合保存はテキスト形式のみです。

## ソースの構成

```
WhisperBatch.slnx                  ソリューション
Directory.Build.props              共通のビルド設定（x64、.NET 10）
Localization/<言語>.lproj/          表示文字列（日本語・英語。macOS 版と同じ形式）
src/WhisperBatch.Core/             UI に依存しないロジック（キュー・状態遷移・出力形式・チャンク境界・表示言語・モデル一覧・
                                   設定の保存・OS のエラーの置き換え・発話ごとの区切り）
src/WhisperBatch.Engine/           whisper.cpp・sherpa-onnx・Media Foundation・MP3 変換・モデル取得
  Native/                            whisper.cpp / ggml の P/Invoke と初期化（DLL の読み込み、構造体の照合、GPU の確認）
  Media/                             音声・動画の読み込み（Media Foundation、Ogg、CAF、AIFF）、16 kHz への変換、読み込み窓
  TranscriptionEngine.cs             方式の共通の窓口（ITranscriber）と Whisper のチャンク処理
  ReazonSpeechContext.cs             ReazonSpeech（発話ごとに区切って認識）
  TranscriptionService.cs            キューと一括処理
  Mp3Converter.cs                    動画の音声 → MP3
  DiskSpace.cs                       書き込み前の空き容量の確認
src/WhisperBatch.App/              WPF アプリ（Views/ 画面、ViewModels/ 状態、Infrastructure/ アニメーション・書体とテーマ（Appearance.cs）など）
src/Native.targets                 whisper.cpp の DLL と表示文字列を出力フォルダに置く
tests/                             xUnit のテスト
App/                               アイコン（AppIcon.ico）、サードパーティのライセンス表記（Licenses/ に全文）
installer/                         Inno Setup のインストーラー定義（WhisperBatch.iss）
scripts/                           build.ps1、make-installer.ps1、test.ps1、fetch-whisper.ps1、make-test-audio.ps1、make-icon.ps1
```
