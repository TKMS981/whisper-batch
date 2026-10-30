import Foundation
import WhisperBatchCore

/// 読み込んだモデル（またはOSの音声認識）で 1 ファイルずつ文字起こしする
public protocol Transcriber: AnyObject, Sendable {
    /// - Parameter progress: 0...1。任意のスレッドから呼ばれる。
    func transcribe(fileURL: URL, options: TranscriptionOptions,
                    progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript
}

extension WhisperContext: Transcriber {
    public func transcribe(fileURL: URL, options: TranscriptionOptions,
                           progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript {
        try await TranscriptionEngine.transcribe(fileURL: fileURL, context: self, options: options, progress: progress)
    }
}

public enum TranscriberLoader {
    /// モデルを読み込む。Apple の音声認識は言語ごとに用意されているかを確かめる。
    public static func load(_ model: SpeechModel, storage: ModelStorage?, language: LanguageOption) async throws -> any Transcriber {
        switch model.engine {
        case .whisper:
            guard let storage, let file = model.files.first, storage.isInstalled(model) else {
                throw WhisperBatchError.modelNotInstalled(modelName: model.displayName)
            }
            return try await WhisperContext.load(model: model, fileURL: storage.fileURL(for: file, of: model))
        case .reazonSpeech:
            guard let storage, storage.isInstalled(model) else {
                throw WhisperBatchError.modelNotInstalled(modelName: model.displayName)
            }
            return try await ReazonSpeechContext.load(model: model, directory: storage.directory(for: model))
        case .apple:
            return try await AppleSpeechTranscriber.make(model: model, language: language)
        }
    }
}
