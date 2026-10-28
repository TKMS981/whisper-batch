import Foundation
import Synchronization
import WhisperBatchCore
import whisper

/// キャンセル要求を whisper.cpp のコールバックへ伝えるためのフラグ
final class CancellationFlag: Sendable {
    private let state = Atomic<Bool>(false)
    func cancel() { state.store(true, ordering: .relaxed) }
    var isCancelled: Bool { state.load(ordering: .relaxed) }
}

/// whisper.cpp のコールバックに渡す箱（C 関数ポインタはキャプチャできないため user_data 経由で渡す）
private final class CallbackBox {
    let flag: CancellationFlag
    let progress: @Sendable (Double) -> Void
    init(flag: CancellationFlag, progress: @escaping @Sendable (Double) -> Void) {
        self.flag = flag
        self.progress = progress
    }
}

struct ChunkResult: Sendable {
    var segments: [TranscriptSegment]
    /// whisper が使った（自動判定時は判定した）言語コード
    var language: String?
}

/// whisper.cpp のコンテキスト（読み込んだモデル）を 1 つ保持する。
///
/// whisper_context は同時に 1 スレッドからしか使えないので、専用のシリアルキューで処理する。
/// 認識は数秒〜数分ブロックするため、Swift の協調スレッドプールではなくこのキューで実行する。
public final class WhisperContext: @unchecked Sendable {
    public let model: WhisperModel
    private let ctx: OpaquePointer
    private let queue: DispatchQueue

    private init(model: WhisperModel, ctx: OpaquePointer, queue: DispatchQueue) {
        self.model = model
        self.ctx = ctx
        self.queue = queue
    }

    deinit {
        whisper_free(ctx)
    }

    /// モデルファイルを読み込む。Metal（GPU）が使えない場合は CPU で読み込み直す。
    public static func load(model: WhisperModel, fileURL: URL) async throws -> WhisperContext {
        WhisperLogging.configure()
        let queue = DispatchQueue(label: "WhisperBatch.whisper", qos: .userInitiated)
        let path = fileURL.path
        let ctx: OpaquePointer? = await withCheckedContinuation { continuation in
            queue.async {
                var params = whisper_context_default_params()
                params.use_gpu = true
                params.flash_attn = true
                var ctx = whisper_init_from_file_with_params(path, params)
                if ctx == nil {
                    params.use_gpu = false
                    params.flash_attn = false
                    ctx = whisper_init_from_file_with_params(path, params)
                }
                continuation.resume(returning: ctx)
            }
        }
        guard let ctx else { throw WhisperBatchError.modelLoadFailed(modelName: model.displayName) }
        return WhisperContext(model: model, ctx: ctx, queue: queue)
    }

    /// 16kHz モノラル PCM を認識する。時刻は samples 先頭からの相対秒。
    func transcribe(
        samples: [Float],
        language: String?,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> ChunkResult {
        let flag = CancellationFlag()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<ChunkResult, any Error>) in
                // self を保持して、処理中にコンテキストが解放されないようにする
                queue.async { [self] in
                    if flag.isCancelled {
                        continuation.resume(throwing: CancellationError())
                        return
                    }
                    let result = Self.runFull(ctx: self.ctx, samples: samples, language: language,
                                              flag: flag, progress: progress)
                    continuation.resume(with: result)
                }
            }
        } onCancel: {
            flag.cancel()
        }
    }

    private static func runFull(
        ctx: OpaquePointer,
        samples: [Float],
        language: String?,
        flag: CancellationFlag,
        progress: @escaping @Sendable (Double) -> Void
    ) -> Result<ChunkResult, any Error> {
        var params = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH)
        params.n_threads = Int32(recommendedThreadCount)
        params.translate = false
        // 呼び出しごとに文脈（前回の認識文）をリセットする。false にすると前のファイルやチャンクの
        // 文章が次の認識に引き継がれ、別ファイルの言い回しが混ざったり繰り返しが起きたりする。
        // （1 回の呼び出しの中の 30 秒ごとの区切りでは、これまでどおり直前の文脈が使われる）
        params.no_context = true
        params.no_timestamps = false
        params.single_segment = false
        params.print_special = false
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        params.suppress_blank = true
        params.suppress_nst = true
        params.beam_search.beam_size = 5
        params.greedy.best_of = 5

        // C 文字列は whisper_full が終わるまで生かしておく
        let languageC = strdup(language ?? "auto")
        defer { free(languageC) }
        params.language = UnsafePointer(languageC)
        params.detect_language = false

        let box = CallbackBox(flag: flag, progress: progress)
        let boxPtr = Unmanaged.passRetained(box).toOpaque()
        defer { Unmanaged<CallbackBox>.fromOpaque(boxPtr).release() }

        params.progress_callback = { _, _, value, userData in
            guard let userData else { return }
            let box = Unmanaged<CallbackBox>.fromOpaque(userData).takeUnretainedValue()
            box.progress(Double(value) / 100)
        }
        params.progress_callback_user_data = boxPtr
        params.abort_callback = { userData in
            guard let userData else { return false }
            return Unmanaged<CallbackBox>.fromOpaque(userData).takeUnretainedValue().flag.isCancelled
        }
        params.abort_callback_user_data = boxPtr
        params.encoder_begin_callback = { _, _, userData in
            guard let userData else { return true }
            // false を返すと処理を中止する
            return !Unmanaged<CallbackBox>.fromOpaque(userData).takeUnretainedValue().flag.isCancelled
        }
        params.encoder_begin_callback_user_data = boxPtr

        let code = samples.withUnsafeBufferPointer { buf in
            whisper_full(ctx, params, buf.baseAddress, Int32(buf.count))
        }
        if flag.isCancelled { return .failure(CancellationError()) }
        guard code == 0 else { return .failure(WhisperBatchError.transcriptionFailed(code: code)) }

        var segments: [TranscriptSegment] = []
        let n = whisper_full_n_segments(ctx)
        segments.reserveCapacity(Int(n))
        for i in 0 ..< n {
            guard let cText = whisper_full_get_segment_text(ctx, i) else { continue }
            let text = String(cString: cText).trimmingCharacters(in: .whitespacesAndNewlines)
            // t0/t1 は 10ms 単位
            let t0 = Double(whisper_full_get_segment_t0(ctx, i)) / 100
            let t1 = Double(whisper_full_get_segment_t1(ctx, i)) / 100
            segments.append(TranscriptSegment(start: t0, end: max(t1, t0), text: text))
        }
        let langID = whisper_full_lang_id(ctx)
        let lang = langID >= 0 ? whisper_lang_str(langID).map { String(cString: $0) } : nil
        return .success(ChunkResult(segments: segments, language: lang))
    }

    /// 性能コアを中心に使い、UI 用に余裕を残す
    static var recommendedThreadCount: Int {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        return max(2, min(8, cores - 2))
    }
}

/// whisper.cpp / ggml のログはアプリの出力に混ぜない。
/// 環境変数 WHISPER_BATCH_VERBOSE=1 のときだけ標準エラーへ出す（開発時の調査用）。
enum WhisperLogging {
    private static let once: Void = {
        let verbose = ProcessInfo.processInfo.environment["WHISPER_BATCH_VERBOSE"] == "1"
        let callback: ggml_log_callback = verbose
            ? { _, text, _ in if let text { fputs(text, stderr) } }
            : { _, _, _ in }
        whisper_log_set(callback, nil)
        ggml_log_set(callback, nil)
    }()

    static func configure() { _ = once }
}
