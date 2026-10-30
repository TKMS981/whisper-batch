// swift-tools-version: 6.2
import PackageDescription

// whisper.cpp 公式リリースの XCFramework（v1.9.5 と同じビルド b5454）。
// チェックサムで内容を固定しているので、別のファイルに差し替わるとビルドが失敗します。
let whisperFramework = Target.binaryTarget(
    name: "whisper",
    url: "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-b5454-xcframework.zip",
    checksum: "e57f8c48933000acabc13bb913fbe82805d483a2deff692cc6738836bd92393b"
)

// sherpa-onnx 公式リリースの macOS 用 XCFramework（v1.13.8、onnxruntime を静的に含む動的ライブラリ）。
// ReazonSpeech モデルの実行に使う。チェックサムは GitHub のリリース情報（digest）と同じ値。
let sherpaOnnxFramework = Target.binaryTarget(
    name: "SherpaOnnxC",
    url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/xcframework/sherpa-onnx-v1.13.8-macos-shared-onnxruntime-static.xcframework.zip",
    checksum: "82d5cb3af38c3cb4871c3532a6c7dc86d6b7db1077bcc9d6a1047100976f199b"
)

let package = Package(
    name: "WhisperBatch",
    defaultLocalization: "ja",
    platforms: [.macOS("27.0")],
    products: [
        .executable(name: "WhisperBatch", targets: ["WhisperBatch"]),
    ],
    dependencies: [
        // MP3 エンコーダー（LGPL）。ソースは scripts/fetch-lame.sh で取得する。
        .package(path: "ThirdParty/LAME"),
    ],
    targets: [
        // UI に依存しないロジック（キュー、状態遷移、重複判定、出力フォーマット、チャンク分割）
        .target(name: "WhisperBatchCore"),
        // whisper.cpp / sherpa-onnx（ReazonSpeech）/ Speech（Apple の音声認識）/ AVFoundation / 動画の MP3 変換 / モデル取得
        .target(
            name: "WhisperBatchEngine",
            dependencies: ["WhisperBatchCore", "whisper", "SherpaOnnxC", .product(name: "LAME", package: "LAME")]
        ),
        // SwiftUI アプリ本体
        .executableTarget(
            name: "WhisperBatch",
            dependencies: ["WhisperBatchCore", "WhisperBatchEngine"]
        ),
        whisperFramework,
        sherpaOnnxFramework,
        .testTarget(name: "WhisperBatchCoreTests", dependencies: ["WhisperBatchCore"]),
        .testTarget(name: "WhisperBatchEngineTests", dependencies: ["WhisperBatchEngine"]),
    ]
)
