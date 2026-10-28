import Foundation
@testable import WhisperBatchCore

/// テストでは、リポジトリの Localization フォルダから文字列を読み込み、日本語表示にそろえる
enum TestLocalization {
    static let directory = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("Localization")

    private static let loaded: Void = {
        Localizer.shared.loadTables(fromDirectory: directory)
        Localizer.shared.setLanguage(.japanese)
    }()

    static func setUp() { _ = loaded }
}
