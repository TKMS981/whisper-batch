// swift-tools-version: 6.2
import PackageDescription

// LAME（MP3 エンコーダー、LGPL-2.0-or-later）を動的ライブラリとしてビルドする。
// ソースはリポジトリに含めず、scripts/fetch-lame.sh が公式配布物を取得・検証して
// Sources/CLAME に展開する。動的ライブラリにしているのは、LGPL の条件に沿って
// 利用者がライブラリを差し替えられるようにするため。
let package = Package(
    name: "LAME",
    platforms: [.macOS("27.0")],
    products: [
        .library(name: "LAME", type: .dynamic, targets: ["CLAME"]),
    ],
    targets: [
        .target(
            name: "CLAME",
            path: "Sources/CLAME",
            exclude: ["COPYING", "README"],
            publicHeadersPath: "include",
            cSettings: [
                .define("HAVE_CONFIG_H"),
                .headerSearchPath("."),
            ]
        ),
    ]
)
