# Whisper Batch

複数の音声・動画ファイルを Whisper でまとめて文字起こしし、結果を 1 つのファイルに統合して保存できる macOS アプリです。
文字起こしと動画の変換はすべてこの Mac の中で行い、音声や結果を外部へ送信しません。

- SwiftUI のネイティブ macOS アプリ（Apple Silicon / arm64 専用、App Sandbox 有効）
- 認識エンジン: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) v1.9.5（Metal による GPU 処理）
- 動画: 音声を取り出して MP3 に変換してから文字起こし（MP3 エンコードに [LAME](https://lame.sourceforge.io/) 4.0）
- 表示言語: 日本語・英語（macOS の優先言語から自動選択、設定で切り替え可）
- 既定モデル: Large v3 Turbo（量子化 Q5_0、574 MB）／文字起こしの初期言語: 日本語

## 動作環境と、この環境での制約

| 項目 | 内容 |
| --- | --- |
| 対象 OS | macOS 27 以降（`LSMinimumSystemVersion` / デプロイメントターゲットとも 27.0） |
| CPU | Apple Silicon のみ（arm64 でビルド。Intel / Rosetta は対象外） |
| ビルド環境（確認済み） | macOS 27.0.1、Command Line Tools（Swift 6.4、macOS 27.0 SDK）、Apple M5 |

開発した Mac には **Xcode がなく、Command Line Tools だけ**でした。そのため次のようにしています。

- Xcode プロジェクトではなく **Swift Package** で構成し、`scripts/build-app.sh` で `.app` バンドルを組み立てます。
- 署名は **アドホック署名**（ローカル実行用）です。他の Mac に配布するには Developer ID 署名と公証（notarization）が必要です。
- macOS 27 SDK では SwiftUI の `@State` が Xcode 同梱のマクロとして解決され、Command Line Tools ではビルドできません。`State` を型エイリアス `ViewState` 経由で使っています（`Sources/WhisperBatch/Views/ViewState.swift`）。
- swift-testing のマクロプラグインが自動で読み込まれないため、`scripts/test.sh` が `-load-plugin-library` を付けて実行します。
- String Catalog（`.xcstrings`）は Xcode の `xcstringstool` がないとコンパイルできないため、表示文字列は従来の `.strings` 形式で管理しています（後述）。
- Bundle ID は仮の `com.example.WhisperBatch` です（`App/Info.plist`）。公開・配布する場合は自分のドメインに変更してください。

## ビルドと実行

```sh
./scripts/build-app.sh          # リリースビルド → build/Whisper Batch.app
open "build/Whisper Batch.app"
```

ビルド時に次のものをネットワークから取得し、どちらも内容をハッシュで検証します。

| 取得するもの | 取得元 | 検証 |
| --- | --- | --- |
| whisper.cpp の XCFramework（約 66 MB） | GitHub（ggml-org/whisper.cpp のリリース b5454） | `Package.swift` のチェックサム（SwiftPM が検証） |
| LAME 4.0 のソース | SourceForge（公式配布物 lame-4.0.tar.gz） | `scripts/fetch-lame.sh` の SHA-256 |

LAME は `scripts/fetch-lame.sh` が取得し、必要なソースとヘッダーだけを `ThirdParty/LAME/Sources/CLAME/` に展開します（このフォルダは Git の追跡対象外）。配布物に含まれる `configure` などのスクリプトは実行せず、macOS 用の設定ヘッダー `ThirdParty/LAME/config.h` を使って SwiftPM でコンパイルします。`build-app.sh` と `test.sh` は自動で取得します。`swift build` を直接使う場合は先に `./scripts/fetch-lame.sh` を実行してください。

生成物の `build/Whisper Batch.app` には、`whisper.framework` と `libLAME.dylib`（どちらも arm64）、日本語・英語の文字列、ライセンス表記が入ります。

ビルドスクリプトは署名の前に、実行ファイルと `libLAME.dylib` からデバッグ用の参照情報を取り除きます（`strip -S -x`）。この情報にはソースやオブジェクトファイルの絶対パス（ビルドした Mac のユーザー名を含む）が入るためです。クラッシュ時のシンボル解決に使う dSYM は `.build/` にだけ残り、アプリには含めません。`.build/` の中身はローカルパスを含むので公開・配布しないでください。

## テスト

```sh
./scripts/test.sh                        # ロジックのテスト（キュー、状態遷移、出力形式、チャンク境界、表示文字列など）
```

実際の音声・動画とモデルを使う結合テストは、環境変数を指定したときだけ実行されます。テスト素材はリポジトリに含めず、スクリプトで生成します。

```sh
./scripts/make-test-audio.sh /tmp/wb-audio          # say と ffmpeg（Homebrew）でテスト用の音声・動画を作成
WHISPER_BATCH_TEST_AUDIO_DIR=/tmp/wb-audio \
WHISPER_BATCH_TEST_MODEL=/path/to/ggml-large-v3-turbo-q5_0.bin \
WHISPER_BATCH_TEST_DOWNLOAD_DIR=/tmp/wb-models \
./scripts/test.sh
```

- 表示文字列のテスト: 日英のキーが一致しているか、書式指定（`%@`・`%ld` など）が両言語で一致しているか、ソースで使っているキーがすべて定義されているか、英語に日本語が混ざっていないか
- 動画のテスト: 対応形式の変換（出力が MP3 として読めて長さが一致し、元ファイルが変わらないこと）、非対応形式・音声のない動画・存在しないファイルのエラー、キャンセル時に書きかけの MP3 が残らないこと、動画 → MP3 → Whisper での文字起こし
- 容量不足のテストは、小さいボリュームを用意して `WB_SMALL_VOLUME` にそのパスを指定したときだけ実行されます

## 使い方

1. 音声・動画ファイルを追加します。
   - **ウィンドウ内のどこにでも**ドラッグ＆ドロップ（キュー、結果表示、設定パネル、下部の操作バーのどこでも可。複数ファイル・フォルダ可）
   - 「ファイルを追加…」（⌘O）、Finder でアプリのアイコンにドロップ、または「このアプリケーションで開く」
2. モデルが未取得なら「ダウンロード…」を押し、確認ダイアログで取得元・サイズ・保存先を確認してからダウンロードします。
3. 文字起こしの言語を選び、「文字起こしを開始」（⌘R）を押します。キューの上から順に処理し、「キャンセル」（⌘.）で中止できます。
4. キューの項目を選ぶと結果を確認できます（タイムスタンプ表示、コピー、⌘F で検索）。
5. 保存します。
   - **統合して保存…**（⇧⌘S）: 含める結果と順番を決めて、1 つのテキストファイルにまとめます。
   - **個別に書き出す…**: 完了した結果をフォルダへそれぞれ書き出します（TXT / SRT / VTT）。
   - 各結果の「このファイルを書き出す」: 1 件だけ書き出します（TXT / SRT / VTT）。
   - 動画の項目では「**音声を MP3 で保存…**」で、動画の音声を MP3 として好きな場所に保存できます（このときだけ MP3 を残します）。

### キューの扱い

- **状態表示**: 音声は「待機中 → 文字起こし中 → 完了」、動画は「変換待ち → MP3 に変換中 → 文字起こし中 → 完了」と表示し、それぞれ進捗（%）を出します。失敗・キャンセル済みも項目ごとに表示します。
- **重複**: 同じファイル（シンボリックリンクや `..` を解決した同じパス）は追加しません。スキップしたことを表示し、既にある項目を選択します。
- **対象外のファイル**: 拡張子が音声・動画でないファイル（.txt など）は追加せず、理由を表示します。読み込めない形式は追加直後の確認で失敗として表示します。
- **失敗**: 1 件が失敗しても残りの処理は続けます。
- **削除・再試行**: 選択項目の削除（⌫）、待機中の一括削除、処理中以外の一括削除、失敗・キャンセルした項目の再試行。
- キューと結果はアプリを終了すると消えます。処理中・変換中に終了しようとすると確認します。

## 動画の文字起こし（変換方式）

動画を追加すると、通常の一括処理の中で次の順に処理します。利用者が変換操作をする必要はありません。

1. AVFoundation で動画を開き、映像トラックの有無と音声トラックを確認する
2. 最初の音声トラックを AVFoundation でデコードし（44.1 kHz、ステレオ以上は 2ch・モノラルは 1ch）、**LAME で MP3（128 kbps 固定）に変換**して一時ファイルに書く
3. その MP3 を Whisper で文字起こしする（音声ファイルと同じ処理）
4. 一時 MP3 を削除する（成功・失敗・キャンセルのどの場合も）

### 方式を選んだ理由

- **macOS には MP3 エンコーダーがありません**。macOS 27 の AudioToolbox でエンコードできる形式を実機で調べた結果、AAC・ALAC・FLAC・Opus・PCM などはありましたが MP3 はありませんでした。そのため、動画からの音声の取り出しとデコードは標準の AVFoundation で行い、MP3 化だけに外部ライブラリを使っています。
- **LAME を選んだ理由**: MP3 エンコーダーとして定番で、C のソースだけでできていて arm64 向けに SwiftPM で直接ビルドでき、アプリのプロセス内で動くので App Sandbox でも問題なく使えます。ライブラリは約 0.26 MB です。
- **FFmpeg を採用しなかった理由**: Homebrew の FFmpeg（9.0.2）は GPL-3.0-or-later でビルドされており、ライブラリのフォルダだけで約 40 MB（ほかに依存パッケージが 11 個）と配布サイズが大きく、別プロセスとして実行するにはサンドボックス用の署名・権限設定が必要です。必要なのは MP3 エンコードだけなので過剰と判断しました。
- 認識に使う音声を MP3 にすると、元の音声を直接読むより音質はわずかに落ちます（不可逆圧縮のため）。128 kbps では文字起こしの結果に差は見られませんでした（テスト音声での確認）。

### 一時ファイル

- 置き場所: アプリの一時フォルダ（サンドボックスのコンテナ内）の `WhisperBatch-Conversions/`
- 処理が終わる・失敗する・キャンセルされると、その都度削除します。アプリの起動時・終了時にも、残っていれば削除します（異常終了した場合の残りも次回起動時に消えます）。
- 変換前に、出力先の空き容量が足りるか確認します（目安: 1 分あたり約 1 MB）。足りない場合は変換せずにエラーを表示します。
- 元の動画は読み込むだけで、変更・移動・削除はしません。

### 動画の対応形式（macOS 27.0.1 で確認）

| 結果 | 形式 |
| --- | --- |
| 変換・文字起こしできる | MP4（H.264 / HEVC、AAC 音声）、MOV（AAC / ALAC / リニア PCM 音声、ProRes 映像を含む）、M4V、3GP、MPEG-2 プログラムストリーム（.mpg）、MPEG-2 トランスポートストリーム（.ts） |
| 複数の音声トラックがある動画 | 最初の音声トラックを使います |
| 音声のない動画 | 「macOS で読み込める音声が見つかりません」と表示 |
| MP4 に MP3 音声を入れたもの | AVFoundation が音声トラックとして認識しないため、上と同じエラーになります |
| 読み込めない | MKV（追加時に対象外）、WebM、AVI（追加直後に「対応していない形式」と表示） |

上記以外（WMV、FLV、.mts / .m2ts など）は確認していません。追加はできますが、macOS で読めない場合はエラーになります。

## 音声の対応形式

WAV、MP3、M4A（AAC / ALAC）、AAC（ADTS）、AIFF、CAF、FLAC、Ogg Vorbis/Opus（.ogg / .opus）を実ファイルで確認しました。ステレオ・48 kHz なども 16 kHz モノラルに変換して処理します。WebM 音声は読み込めません。

## 表示言語（日本語・英語）

- **初期設定**: macOS の優先言語（システム設定 > 一般 > 言語と地域）の並びで、日本語と英語のうち先にある方を使います。どちらもなければ英語です。
- **切り替え方**: メニューの「Whisper Batch > 設定…」（⌘,）で「システムに合わせる / 日本語 / English」を選びます。画面の文字はすぐに切り替わり、選択は次回起動後も保持されます。
- ファイル・編集・ウインドウなど macOS 標準のメニュー項目や、保存ダイアログのボタンは、次回起動時に切り替わります（このアプリ用の `AppleLanguages` 設定を書き換えています）。
- **文字起こしの言語は別の設定**です。表示を英語にしても、認識する言語（日本語・自動判定・英語など）は変わりません。
- 文字列は `Localization/ja.lproj/Localizable.strings` と `Localization/en.lproj/Localizable.strings` にまとめてあり、Swift からは `L("キー")` で参照します。テキストエディタで編集でき、ビルド時に `.app` の Resources にコピーされます。`swift run` などで直接実行する場合は、環境変数 `WHISPER_BATCH_LOCALIZATION_DIR` に `Localization` フォルダを指定してください。
- OS が返すエラーの説明（ファイルが破損している場合など）の一部は、macOS の言語で表示されることがあります。

## アニメーション

操作の分かりやすさのための短い動き（0.12〜0.22 秒）だけを使い、点滅や大きな拡大縮小はしません。設定は `Sources/WhisperBatch/Views/Motion.swift` にまとめています。

| 操作 | 動き | 「視差効果を減らす」が有効なとき |
| --- | --- | --- |
| ファイルの追加・削除 | キューの行が短くフェードし、少しスライドして出入りする | フェードのみ |
| 状態の変化 | 状態アイコンが入れ替わり、完了・失敗時に 1 回だけ小さく弾む | 即座に切り替え、弾まない |
| 進捗の数字 | ％の数字が滑らかに切り替わる | 即座に切り替え |
| 変換 → 文字起こし | 処理中の表示のラベルがクロスフェードで入れ替わる | 即座に切り替え |
| ドラッグ中 | ウィンドウの強調表示と案内がフェードで現れる・消える | 即座に表示 |
| 結果表示・下部のバナー | 状態が変わると結果表示がクロスフェード、下部のバナーは下からフェードで入れ替わる | 透明度の変化のみ |

- 「視差効果を減らす」は macOS のシステム設定（アクセシビリティ > 表示）に従います。
- 開発時は、システム設定を変えずに `WHISPER_BATCH_REDUCE_MOTION=1 "build/Whisper Batch.app/Contents/MacOS/WhisperBatch"` で起動すると、「視差効果を減らす」の表示を確認できます。
- 画面録画をコマ送りして、通常時の動き（行のフェード、アイコンの弾み、ラベルのクロスフェード、強調表示のフェード、バナー）と、「視差効果を減らす」の表示（アイコンが弾まない）を確認しました。VoiceOver などの支援技術との組み合わせは試していません。

## Whisper の実装方式

**whisper.cpp の公式 XCFramework** を Swift Package の `binaryTarget` として使い、C API を Swift から直接呼んでいます。完全にローカルで動き、Metal で GPU を使え、MIT License です。OpenAI の Whisper API（音声の外部送信が必要）、WhisperKit（依存が多く Core ML 用モデルが別途必要）、Homebrew の whisper-cpp（アプリ単体で完結しない）は採用していません。

長時間の音声は 16 kHz モノラルで少しずつ読み出し、10 分ごとのチャンクで認識します。チャンクの境界は末尾付近の最も静かな位置に合わせ、境界で切れた区間は次のチャンクで読み直すので、文の途中で切れたり重複したりしにくくなっています。

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

- 保存先: `~/Library/Containers/com.example.WhisperBatch/Data/Library/Application Support/WhisperBatch/Models/`（「モデル管理…」の「Finder で表示」で開けます）
- 切り替え・削除: メイン画面のモデル選択、または「モデル管理…」
- 通信エラーで失敗した場合は「再開」で途中から、それ以外は「再試行」で最初から取得し直します。

## 出力形式

| 形式 | 内容 |
| --- | --- |
| テキスト (.txt) | 認識区間ごとに 1 行。オプションで各行にタイムスタンプ |
| SRT (.srt) / WebVTT (.vtt) | 字幕形式（WebVTT の本文中の `-->` は `→` に置き換え） |
| 統合ファイル (.txt) | 各結果を `[番号/件数] ファイル名` の見出しで区切り、指定した順に並べる（統合はテキストのみ） |
| MP3（動画の音声） | 「音声を MP3 で保存…」を選んだときだけ。128 kbps、44.1 kHz |

UTF-8（BOM なし）・LF で、一時ファイル経由で置き換えるため書き込みに失敗しても既存ファイルは壊れません。保存先は必ずダイアログで選び、既存ファイルの上書きは確認します。音声なしの場合の本文（「音声が検出されませんでした」）と統合ファイルの既定名は、表示言語に従います。

## プライバシー

- 文字起こしと動画の変換はすべてこの Mac 上で行います。音声・動画・認識結果・利用状況を外部へ送信しません。
- 通信するのはモデルのダウンロード時だけで、送るのはファイルの取得要求（`huggingface.co` への HTTPS GET）だけです。ダウンロード前に確認ダイアログを表示します。
- App Sandbox を有効にしています。読み書きできるのは、利用者が選択・ドロップしたファイルと、保存先として選んだ場所、アプリ自身のコンテナだけです。選択・ドロップしたファイルには読み取り専用のセキュリティスコープ付きブックマークを作り、処理している間だけアクセスを開始して、終わったら解放します。
- 変換用の一時 MP3 は処理後に削除し、利用者が「音声を MP3 で保存…」を選んだときだけ、選んだ場所に保存します。
- whisper.cpp のログは出力しません（開発時の調査用に `WHISPER_BATCH_VERBOSE=1` で標準エラーに出せます）。API キーなどの秘密情報は使っていません。

## ライセンスと配布上の注意

| コンポーネント | ライセンス | 扱い |
| --- | --- | --- |
| Whisper Batch（このリポジトリのソース） | MIT | [LICENSE](LICENSE) |
| whisper.cpp / ggml | MIT（© The ggml authors） | `whisper.framework` として同梱。全文を `App/ThirdPartyNotices.txt`（アプリ内 `Contents/Resources/ThirdPartyNotices.txt`）に収録 |
| LAME 4.0 | LGPL-2.0-or-later | `libLAME.dylib` として**動的リンク**で同梱。利用者が差し替えられます。全文をアプリ内 `Contents/Resources/LAME-COPYING.txt` に収録。ソースは改変していません |
| Whisper モデル | MIT（OpenAI）。ggml 変換版（Hugging Face `ggerganov/whisper.cpp`）も MIT と表示されています | 同梱せず、利用者がダウンロード |

- LGPL のライブラリを含むアプリを配布する場合は、ライセンス文の同梱、ライブラリを差し替えられる形での提供（このアプリは動的ライブラリとして同梱）、LAME のソースの入手方法の案内が必要です。正確な条件は LGPL の本文を確認してください。
- `libLAME.dylib` を差し替えると、アプリの署名が無効になります。現在のアドホック署名のビルドでは、差し替えたライブラリとアプリを `codesign --force --sign - Contents/Frameworks/libLAME.dylib`、`codesign --force --sign - --entitlements App/WhisperBatch.entitlements "Whisper Batch.app"` の順に再署名すれば使えます（別ビルドの libLAME.dylib に差し替えて、動画の変換・文字起こしができることを確認済み）。

### Hardened Runtime での動作（確認結果）

`scripts/build-app.sh` の既定の署名は、Hardened Runtime を有効にしていません。Hardened Runtime を有効にした署名について、このアプリのコピーで次のことを確認しました（この Mac にはコード署名用の証明書がないため、すべてアドホック署名での確認です）。

| 署名の組み合わせ | 結果 |
| --- | --- |
| アドホック署名 + Hardened Runtime | **起動しません**。ライブラリ検証により `libLAME.dylib` の読み込みが「プロセスと Team ID が異なる」として拒否されます（アドホック署名には Team ID がないため） |
| アドホック署名 + Hardened Runtime + `com.apple.security.cs.disable-library-validation` | 起動し、`whisper.framework` と `libLAME.dylib` が読み込まれ、動画と音声の文字起こしができました |
| Developer ID 署名（アプリとライブラリを同じ Team ID で署名）+ Hardened Runtime | **未確認**（証明書がないため）。Apple の仕様上は同じ Team ID のライブラリは読み込まれますが、その場合、利用者が差し替えたライブラリは読み込まれません |

Developer ID で配布する場合は、LGPL の「利用者がライブラリを差し替えられること」をどう満たすか（`com.apple.security.cs.disable-library-validation` を付けるか、差し替え後に利用者自身が再署名する手順を案内するか）を決めてから、実際の証明書で動作を確認してください。`disable-library-validation` を付けると、別の署名のライブラリも読み込めるようになる点に注意してください（このアプリは App Sandbox を有効にしています）。

- MP3 の基本特許は期限が切れていると一般に説明されていますが、配布する地域での扱いは各自で確認してください。

## リポジトリと配布の状態

- このリポジトリのソースコードは [MIT License](LICENSE) です。依存ライブラリのライセンスは上の表と `App/ThirdPartyNotices.txt` のとおりです。
- コミットの作者には GitHub のユーザー名と GitHub の noreply メールアドレスを使っています。
- `.gitignore` で、ビルド成果物（`build/`、`.build/`）、取得した LAME のソース、モデル、音声・動画、字幕・統合ファイル、ログなどを除外しています。

配布の前に残っている作業:

1. **Bundle ID を独自のものに変える**（現在は仮の `com.example.WhisperBatch`）。変えるとモデルの保存先（サンドボックスのコンテナ）も変わります。
2. **他の人にアプリを配布する場合**は、Developer ID による署名と公証（notarization）が必要です。公証には Hardened Runtime が必要なので、上記「Hardened Runtime での動作」を踏まえて署名の設定を決め、実際の証明書で動作を確認してください。
3. アプリを配布する場合は、必ず `scripts/build-app.sh` で作ったものを使ってください（デバッグ情報を取り除いているため）。`swift build` の出力や `.build/` 内のファイルにはローカルパスが含まれます。なお、同梱の `whisper.framework`（whisper.cpp 公式のビルド済みバイナリ）には、エラー表示用の文字列として whisper.cpp の CI 環境（GitHub Actions）のパスが含まれています。これは第三者のビルド環境の情報で、このアプリの開発者や利用者の情報ではありません。

## 既知の制約・未対応

- アドホック署名のため、他の Mac ではそのままでは開けません（Developer ID 署名と公証が必要）。
- 動作確認は macOS 27.0.1 / Apple M5 / ダークモードの 1 台だけです。アクセシビリティのラベルは Accessibility API で確認しましたが、VoiceOver での読み上げは試していません。
- 認識・変換の検証には、`say` で作った合成音声とそれを ffmpeg で動画にしたものを使いました。実際の録音・撮影データや複数話者での精度は確認していません。
- ウィンドウ上端のタイトルバー（ツールバー部分）へのドロップは受け付けません（macOS が管理する領域のため）。それ以外のウィンドウ内の領域はドロップを受け付けます。
- 表示言語の切り替え中に作成された一部の詳細メッセージ（ダウンロード失敗の詳細など）は、作成時の言語のまま残ることがあります。
- 話者分離、翻訳、音声区間検出（VAD）、単語単位のタイムスタンプには対応していません。統合保存はテキスト形式のみです。

## ソースの構成

```
Package.swift                     whisper.cpp XCFramework（binaryTarget）、LAME（ローカルパッケージ）、3 つのターゲット
Localization/<言語>.lproj/        表示文字列（日本語・英語）
Sources/WhisperBatchCore/         UI に依存しないロジック（キュー・状態遷移・出力形式・チャンク境界・表示言語）
Sources/WhisperBatchEngine/       AVFoundation・whisper.cpp・MP3 変換・モデル取得
  MediaInspector.swift              音声か動画か、音声トラックの有無
  MP3Converter.swift                動画の音声 → MP3（AVFoundation + LAME）
Sources/WhisperBatch/             SwiftUI アプリ（Views/Motion.swift にアニメーションの設定）
ThirdParty/LAME/                  LAME をビルドする Package.swift と macOS 用 config.h（ソースは取得スクリプトで展開）
Tests/                            swift-testing のテスト
App/                              Info.plist、Entitlements、アイコン、サードパーティのライセンス表記
scripts/                          build-app.sh、test.sh、fetch-lame.sh、make-test-audio.sh、make-icon.swift
```
