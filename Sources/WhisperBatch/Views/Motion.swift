import Foundation
import SwiftUI

/// 画面の動き（アニメーション）の方針を 1 か所にまとめる。
///
/// - 操作の分かりやすさのための短い動きだけを使う（0.12〜0.22 秒、点滅や大きな拡大縮小はしない）
/// - 「視差効果を減らす」（Reduce Motion）が有効なときは、位置の移動や弾む動きをやめ、
///   必要なものだけ透明度の変化にするか、即座に切り替える
struct Motion: Sendable {
    let reduce: Bool

    // MARK: 1. キュー行の追加・削除

    /// 行の出入り。動きを減らす場合はフェードだけ。
    var rowTransition: AnyTransition {
        reduce ? .opacity : .opacity.combined(with: .move(edge: .top))
    }

    var listChange: Animation {
        reduce ? .easeInOut(duration: 0.15) : .snappy(duration: 0.22)
    }

    // MARK: 2. 状態アイコン

    /// アイコンの入れ替え。動きを減らす場合は即座に切り替える。
    var symbolChange: ContentTransition {
        reduce ? .identity : .symbolEffect(.replace)
    }

    var symbolChangeAnimation: Animation? {
        reduce ? nil : .snappy(duration: 0.2)
    }

    /// 完了・失敗時に 1 回だけ弾ませるか
    var bouncesStatusIcon: Bool { !reduce }

    // MARK: 3. 進捗の数字

    var numberChange: ContentTransition {
        reduce ? .identity : .numericText()
    }

    var numberAnimation: Animation? {
        reduce ? nil : .snappy(duration: 0.2)
    }

    // MARK: 4. 変換 → 文字起こしのラベル

    var phaseAnimation: Animation? {
        reduce ? nil : .easeInOut(duration: 0.15)
    }

    // MARK: 5. ドラッグ中の強調表示

    var dropHighlightAnimation: Animation? {
        reduce ? nil : .easeOut(duration: 0.12)
    }

    // MARK: 6. 結果表示・完了／失敗バナー

    /// 結果表示の切り替え。透明度の変化だけなので、動きを減らす場合も同じ。
    var resultAnimation: Animation { .easeInOut(duration: 0.15) }

    /// 下部のバナー。動きを減らす場合はフェードだけ。
    var bannerTransition: AnyTransition {
        reduce ? .opacity : .opacity.combined(with: .move(edge: .bottom))
    }

    var bannerAnimation: Animation { .easeInOut(duration: reduce ? 0.15 : 0.2) }

    /// 開発時の確認用: 環境変数 WHISPER_BATCH_REDUCE_MOTION=1 で、システム設定を変えずに「動きを減らす」表示にする
    static let forcedReduce = ProcessInfo.processInfo.environment["WHISPER_BATCH_REDUCE_MOTION"] == "1"
}

private struct MotionKey: EnvironmentKey {
    static let defaultValue = Motion(reduce: false)
}

extension EnvironmentValues {
    var motion: Motion {
        get { self[MotionKey.self] }
        set { self[MotionKey.self] = newValue }
    }
}

/// システムの「視差効果を減らす」の設定から動きの方針を決めて、画面全体に渡す
struct MotionProvider: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    func body(content: Content) -> some View {
        content.environment(\.motion, Motion(reduce: systemReduceMotion || Motion.forcedReduce))
    }
}
