import Foundation
import Testing
@testable import WhisperBatchCore

private func cocoa(_ code: Int, path: String? = nil, underlying: (any Error)? = nil) -> NSError {
    var info: [String: Any] = [:]
    if let path { info[NSFilePathErrorKey] = path }
    if let underlying { info[NSUnderlyingErrorKey] = underlying as NSError }
    return NSError(domain: NSCocoaErrorDomain, code: code, userInfo: info)
}

@Suite("OS のエラーの置き換え")
struct ErrorMapperTests {
    init() { TestLocalization.setUp() }

    @Test func diskFullIsReportedWhateverTheOperation() {
        for op: ErrorMapper.Operation in [.reading(fileName: "a.wav"), .writing(fileName: "a.txt"), .downloading, .unspecified] {
            #expect(ErrorMapper.map(POSIXError(.ENOSPC), during: op) == .diskFull)
            #expect(ErrorMapper.map(POSIXError(.EDQUOT), during: op) == .diskFull)
            #expect(ErrorMapper.map(cocoa(NSFileWriteOutOfSpaceError), during: op) == .diskFull)
        }
    }

    @Test func permissionDependsOnWhatWasBeingDone() {
        #expect(ErrorMapper.map(POSIXError(.EACCES), during: .reading(fileName: "a.wav")) == .fileAccessDenied(name: "a.wav"))
        #expect(ErrorMapper.map(POSIXError(.EPERM), during: .writing(fileName: "a.txt")) == .writeAccessDenied(fileName: "a.txt"))
        // ダウンロード中の権限エラーは利用者が対処できないので、従来のエラーのままにする
        #expect(ErrorMapper.map(POSIXError(.EACCES), during: .downloading) == nil)
        // 何をしていたか分からなくても、Cocoa のエラーなら読み書きの別とファイル名が分かる
        #expect(ErrorMapper.map(cocoa(NSFileWriteNoPermissionError, path: "/x/out.srt")) == .writeAccessDenied(fileName: "out.srt"))
        #expect(ErrorMapper.map(cocoa(NSFileReadNoPermissionError, path: "/x/in.mp3")) == .fileAccessDenied(name: "in.mp3"))
        #expect(ErrorMapper.map(POSIXError(.EACCES)) == nil)
    }

    @Test func fileSystemProblems() {
        #expect(ErrorMapper.map(POSIXError(.EROFS), during: .writing(fileName: "a.txt")) == .readOnlyVolume)
        #expect(ErrorMapper.map(cocoa(NSFileWriteVolumeReadOnlyError)) == .readOnlyVolume)
        #expect(ErrorMapper.map(POSIXError(.EIO), during: .reading(fileName: "a.wav")) == .volumeUnavailable(name: "a.wav"))
        #expect(ErrorMapper.map(POSIXError(.ENXIO), during: .downloading) == nil)
        #expect(ErrorMapper.map(cocoa(NSFileReadNoSuchFileError), during: .reading(fileName: "a.wav")) == .fileNotFound(name: "a.wav"))
        // 保存中の「見つからない」はファイルの問題ではないので置き換えない
        #expect(ErrorMapper.map(POSIXError(.ENOENT), during: .writing(fileName: "a.txt")) == nil)
    }

    @Test func networkProblems() {
        #expect(ErrorMapper.map(URLError(.notConnectedToInternet), during: .downloading) == .networkOffline)
        #expect(ErrorMapper.map(URLError(.dataNotAllowed), during: .downloading) == .networkOffline)
        #expect(ErrorMapper.map(URLError(.networkConnectionLost), during: .downloading) == .networkInterrupted)
        #expect(ErrorMapper.map(URLError(.timedOut), during: .downloading) == .networkInterrupted)
        #expect(ErrorMapper.map(URLError(.cannotFindHost), during: .downloading) == .serverUnreachable)
        #expect(ErrorMapper.map(URLError(.secureConnectionFailed), during: .downloading) == .serverUnreachable)
        #expect(ErrorMapper.map(URLError(.badServerResponse), during: .downloading) == nil)
    }

    /// 包まれたエラー（NSUnderlyingErrorKey）の原因までたどる
    @Test func followsUnderlyingErrors() {
        let wrapped = cocoa(NSFileWriteUnknownError, underlying: POSIXError(.ENOSPC))
        #expect(ErrorMapper.map(wrapped, during: .writing(fileName: "a.txt")) == .diskFull)
        let deep = NSError(domain: "Other", code: 1, userInfo: [NSUnderlyingErrorKey: URLError(.notConnectedToInternet) as NSError])
        #expect(ErrorMapper.map(deep, during: .downloading) == .networkOffline)
    }

    @Test func leavesCancellationAndOwnErrorsAlone() {
        #expect(ErrorMapper.map(CancellationError()) == nil)
        #expect(ErrorMapper.map(URLError(.cancelled), during: .downloading) == nil)
        #expect(ErrorMapper.map(WhisperBatchError.nothingToExport) == .nothingToExport)
        #expect(ErrorMapper.map(NSError(domain: "Other", code: 42)) == nil)
    }

    /// 分類できたエラーは、表示言語を切り替えても追従し、次の行動が書かれている
    @Test func itemFailureUsesMappedError() {
        let failure = ItemFailure(POSIXError(.ENOSPC))
        #expect(failure.error == .diskFull)
        #expect(failure.message.contains("空きを増やして"))
        let read = ItemFailure(POSIXError(.EACCES), during: .reading(fileName: "a.wav"))
        #expect(read.error == .fileAccessDenied(name: "a.wav"))
        #expect(read.message.contains("追加し直して"))
        let unknown = ItemFailure(NSError(domain: "Other", code: 42, userInfo: [NSLocalizedDescriptionKey: "something"]))
        #expect(unknown.error == nil)
        #expect(unknown.message == "something")
    }

    @Test func osLevelMessagesTellWhatToDo() {
        let errors: [WhisperBatchError] = [
            .diskFull, .readOnlyVolume, .writeAccessDenied(fileName: "a.txt"), .volumeUnavailable(name: "a.wav"),
            .networkOffline, .networkInterrupted, .serverUnreachable,
            .fileAccessDenied(name: "a.wav"), .fileNotFound(name: "a.wav"),
            .insufficientDiskSpace(required: 2_000_000, available: 1_000_000),
        ]
        for error in errors {
            let message = error.errorDescription ?? ""
            #expect(message.hasSuffix("てください。"), "\(error): \(message)")
        }
        #expect(userFacingMessage(for: URLError(.notConnectedToInternet), during: .downloading)
            == WhisperBatchError.networkOffline.errorDescription)
    }
}

@Suite("設定の保存")
@MainActor
struct SettingsStoreTests {
    private let suiteName = "WhisperBatchTests.settings.\(UUID().uuidString)"
    private let defaults: UserDefaults

    init() {
        TestLocalization.setUp()
        defaults = UserDefaults(suiteName: suiteName)!
    }

    private func cleanUp() { defaults.removePersistentDomain(forName: suiteName) }

    @Test func defaultsWhenNothingSaved() {
        defer { cleanUp() }
        let store = SettingsStore(defaults: defaults)
        #expect(store.language == LanguageOption(storageValue: nil))
        #expect(store.selectedModelID == ModelCatalog.defaultModelID)
        #expect(store.languagePreference == .system)
        #expect(store.autoStartOnAdd == false)
    }

    @Test func valuesSurviveRelaunch() {
        defer { cleanUp() }
        let store = SettingsStore(defaults: defaults)
        let reazon = ModelCatalog.defaultModel(for: .reazonSpeech)
        store.language = .english
        store.selectedModelID = reazon.id
        store.autoStartOnAdd = true
        store.languagePreference = .english

        let reloaded = SettingsStore(defaults: defaults)
        #expect(reloaded.language == .english)
        #expect(reloaded.selectedModelID == reazon.id)
        #expect(reloaded.selectedModel == reazon)
        #expect(reloaded.autoStartOnAdd)
        #expect(reloaded.languagePreference == .english)
    }

    /// 表示言語を指定すると AppKit 用の言語指定も保存し、「システムに合わせる」に戻すと消す
    @Test func languagePreferenceWritesAppleLanguages() {
        defer { cleanUp() }
        let store = SettingsStore(defaults: defaults)
        store.languagePreference = .japanese
        #expect(defaults.stringArray(forKey: "AppleLanguages") == ["ja"])
        store.languagePreference = .system
        // object(forKey:) は OS 全体の値も返すので、このスイートに保存された値だけを見る
        #expect(defaults.persistentDomain(forName: suiteName)?["AppleLanguages"] == nil)
    }

    @Test func remembersModelPerEngine() {
        defer { cleanUp() }
        let store = SettingsStore(defaults: defaults)
        let whisper = ModelCatalog.models(for: .whisper).last!
        let reazon = ModelCatalog.defaultModel(for: .reazonSpeech)
        store.selectedModelID = whisper.id
        store.selectedModelID = reazon.id
        #expect(store.lastSelectedModel(for: .whisper) == whisper)
        #expect(store.lastSelectedModel(for: .reazonSpeech) == reazon)
        #expect(store.lastSelectedModel(for: .apple) == nil)
    }

    @Test func unknownSavedModelFallsBackToDefault() {
        defer { cleanUp() }
        defaults.set("no-such-model", forKey: "modelID")
        #expect(SettingsStore(defaults: defaults).selectedModelID == ModelCatalog.defaultModelID)
    }
}
