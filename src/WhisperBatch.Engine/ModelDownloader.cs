using System.Net;
using System.Net.Http.Headers;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Engine;

/// <summary>ダウンロードが完了しなかったときのエラー。通信エラーの場合は途中から再開できることがある。</summary>
public sealed class ModelDownloadInterruption : Exception
{
    /// <summary>null ならキャンセル</summary>
    public WhisperBatchError? Failure { get; }

    /// <summary>途中まで取得したファイルが残っていて、続きから再開できる</summary>
    public bool CanResume { get; }

    public bool IsCancelled => Failure is null;

    private ModelDownloadInterruption(WhisperBatchError? failure, bool canResume)
    {
        Failure = failure;
        CanResume = canResume;
    }

    public static ModelDownloadInterruption Cancelled() => new(null, false);
    public static ModelDownloadInterruption Failed(WhisperBatchError error, bool canResume = false) => new(error, canResume);

    public override string Message => Failure?.Description ?? L("download.cancelled");
}

/// <summary>
/// Hugging Face からモデルを取得し、SHA-256 を検証してから保存する。
/// 送信するのはファイルの取得要求（HTTPS GET）だけで、音声や認識結果は送らない。
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>この時間データが届かなければ通信エラーとする</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperBatch/1.0 (Windows)");
        return client;
    }

    /// <param name="resume">前回の通信エラーで残ったファイルがあれば続きから取得する</param>
    /// <param name="progress">(受信済みバイト, 全体バイト)。任意のスレッドから呼ばれる。</param>
    /// <param name="verifying">ダウンロード後の検証を始めるときに呼ばれる</param>
    /// <exception cref="ModelDownloadInterruption">キャンセル・失敗</exception>
    public static Task DownloadAsync(
        WhisperModel model,
        ModelStorage storage,
        bool resume,
        Action<long, long> progress,
        Action verifying,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DownloadCore(model, storage, resume, progress, verifying, cancellationToken), CancellationToken.None);

    private static async Task DownloadCore(WhisperModel model, ModelStorage storage, bool resume, Action<long, long> progress, Action verifying, CancellationToken token)
    {
        try
        {
            storage.EnsureDirectory();
        }
        catch (Exception e)
        {
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.cannotCreateFolder", e.Message)));
        }

        var partial = storage.PartialPath(model);
        long offset = 0;
        if (resume && File.Exists(partial)) offset = new FileInfo(partial).Length;
        if (!resume || offset >= model.SizeBytes)
        {
            FileUtil.DeleteQuietly(partial);
            offset = 0;
        }

        if (storage.AvailableCapacity() is { } available && available < model.RequiredFreeSpace - offset)
        {
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.InsufficientDiskSpace(model.RequiredFreeSpace - offset, available), canResume: offset > 0);
        }

        try
        {
            await FetchAsync(model, partial, offset, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            FileUtil.DeleteQuietly(partial);
            throw ModelDownloadInterruption.Cancelled();
        }

        // 内容の検証
        verifying();
        string digest;
        try
        {
            digest = await ModelStorage.Sha256Async(partial, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FileUtil.DeleteQuietly(partial);
            throw ModelDownloadInterruption.Cancelled();
        }
        catch (Exception)
        {
            FileUtil.DeleteQuietly(partial);
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.cannotVerify")));
        }
        if (!string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            FileUtil.DeleteQuietly(partial);
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.ChecksumMismatch(model.DisplayName));
        }

        try
        {
            File.Move(partial, storage.FilePath(model), overwrite: true);
        }
        catch (Exception e)
        {
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.cannotSave", e.Message)));
        }
    }

    private static async Task FetchAsync(WhisperModel model, string partial, long offset, Action<long, long> progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(e.Message), canResume: offset > 0);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 途中のファイルが壊れている可能性がある。最初から取り直してもらう
                FileUtil.DeleteQuietly(partial);
                throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadHttpError((int)response.StatusCode));
            }
            if (!response.IsSuccessStatusCode)
            {
                throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadHttpError((int)response.StatusCode), canResume: offset > 0);
            }

            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) offset = 0;
            var total = response.Content.Headers.ContentLength is { } len ? len + offset : model.SizeBytes;

            await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[1 << 20];
            var received = offset;
            progress(received, total);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                int n;
                try
                {
                    n = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.timeout")), canResume: true);
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(e.Message), canResume: true);
                }
                if (n == 0) break;
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                }
                catch (IOException e) when ((e.HResult & 0xFFFF) is 0x70 or 0x27)
                {
                    throw ModelDownloadInterruption.Failed(new WhisperBatchError.InsufficientDiskSpace(model.RequiredFreeSpace, 0), canResume: true);
                }
                received += n;
                progress(received, total);
            }
            if (received != model.SizeBytes)
            {
                // 途中で切れた（サーバーが接続を閉じた）
                throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.incomplete")), canResume: received < model.SizeBytes);
            }
        }
    }
}
