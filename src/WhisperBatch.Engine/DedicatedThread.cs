namespace WhisperBatch.Engine;

/// <summary>
/// 数秒〜数分ブロックするネイティブの処理（モデルの読み込み・認識）を、スレッドプールではなく専用のスレッドで実行する。
/// </summary>
internal static class DedicatedThread
{
    public static Task<T> Run<T>(Func<T> work, string name, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(cancellationToken); }
            catch (Exception e) { tcs.SetException(e); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return tcs.Task;
    }
}
