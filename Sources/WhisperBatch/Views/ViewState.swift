import SwiftUI

/// SwiftUI の `State` プロパティラッパー。
///
/// macOS 27 SDK では `@State` が Xcode に同梱されたマクロ（SwiftUIMacros）として解決されるため、
/// Command Line Tools だけの環境ではビルドできない。型エイリアス経由で従来のプロパティラッパーとして使う。
typealias ViewState = SwiftUI.State
