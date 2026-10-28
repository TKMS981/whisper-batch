import Dispatch

/// 呼び出しスレッドをブロックする処理（AVAssetReader の読み出しなど）を、
/// Swift の協調スレッドプールではなく専用の GCD キューで実行するための TaskExecutor。
/// 協調スレッドプールはコア数分しかスレッドがないため、ブロックする処理を直接載せると
/// 複数ファイルを同時に扱ったときに全スレッドが埋まり、処理が止まることがある。
final class BlockingWorkExecutor: TaskExecutor {
    static let shared = BlockingWorkExecutor()

    private let queue = DispatchQueue(label: "WhisperBatch.blocking-work", qos: .userInitiated, attributes: .concurrent)

    func enqueue(_ job: consuming ExecutorJob) {
        let job = UnownedJob(job)
        queue.async {
            job.runSynchronously(on: self.asUnownedTaskExecutor())
        }
    }
}
