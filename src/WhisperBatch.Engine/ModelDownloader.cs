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
/// モデルの取得先として接続を許可するホスト。
///
/// Hugging Face（huggingface.co）と、ファイルの実体の配信に使われる hf.co（例: us.aws.cdn.hf.co）と
/// それらのサブドメインだけを、HTTPS でのみ許可する。最初の URL だけでなく、リダイレクト先も 1 回ごとに確かめる。
/// </summary>
public static class ModelDownloadHosts
{
    public static readonly IReadOnlyList<string> AllowedDomains = ["huggingface.co", "hf.co"];

    public static bool IsAllowed(Uri? url)
    {
        if (url is not { IsAbsoluteUri: true } || !string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(url.UserInfo) || url.Port != 443) return false;
        var host = url.IdnHost.ToLowerInvariant();
        if (host.Length == 0) return false;
        return AllowedDomains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>エラー表示用のホスト名</summary>
    public static string DisplayHost(Uri? url) => url is { IsAbsoluteUri: true } && url.Host.Length > 0 ? url.Host : url?.ToString() ?? "?";
}

/// <summary>
/// Hugging Face からモデルを取得し、ファイルごとに SHA-256 を検証してから保存する。
/// 送信するのはファイルの取得要求（HTTPS GET）だけで、音声や認識結果は送らない。
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>この時間データが届かなければ通信エラーとする</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>たどるリダイレクトの最大数</summary>
    private const int MaxRedirects = 10;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // リダイレクト先を 1 回ごとに確かめるため、自動ではたどらない
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperBatch/1.1 (Windows)");
        return client;
    }

    /// <summary>
    /// 複数のファイルからなるモデルは 1 ファイルずつ取得する。取得・検証済みのファイルは取得し直さない。
    /// </summary>
    /// <param name="resume">前回の通信エラーで残ったファイルがあれば、まだ揃っていない最初のファイルを続きから取得する</param>
    /// <param name="progress">(受信済みバイト, 全体バイト)。モデル全体での値。任意のスレッドから呼ばれる。</param>
    /// <param name="verifying">ファイルの検証を始めるときに呼ばれる</param>
    /// <exception cref="ModelDownloadInterruption">キャンセル・失敗</exception>
    public static Task DownloadAsync(
        SpeechModel model,
        ModelStorage storage,
        bool resume,
        Action<long, long> progress,
        Action verifying,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DownloadCore(model, storage, resume, progress, verifying, cancellationToken), CancellationToken.None);

    private static async Task DownloadCore(SpeechModel model, ModelStorage storage, bool resume, Action<long, long> progress, Action verifying, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(storage.DirectoryFor(model));
        }
        catch (Exception e)
        {
            throw ModelDownloadInterruption.Failed(ErrorMapper.Map(e, ErrorMapper.Operation.Download)
                ?? new WhisperBatchError.DownloadFailed(L("download.detail.cannotCreateFolder", e.Message)));
        }

        var remaining = model.Files.Where(f => !storage.IsInstalled(f, model)).ToList();
        var remainingBytes = remaining.Sum(f => f.SizeBytes);
        var resumeOffset = resume && remaining.Count > 0 ? PartialLength(storage.PartialPath(remaining[0], model)) : 0;
        var required = SpeechModel.RequiredFreeSpaceFor(remainingBytes) - resumeOffset;
        if (storage.AvailableCapacity() is { } available && available < required)
        {
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.InsufficientDiskSpace(required, available), canResume: resumeOffset > 0);
        }

        var total = model.SizeBytes;
        var done = total - remainingBytes;
        for (var i = 0; i < remaining.Count; i++)
        {
            var file = remaining[i];
            var offset = done;
            await DownloadFile(file, model, storage, resume && i == 0, (received, _) => progress(offset + received, total), verifying, token)
                .ConfigureAwait(false);
            done += file.SizeBytes;
            progress(done, total);
        }
    }

    private static long PartialLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static async Task DownloadFile(ModelFile file, SpeechModel model, ModelStorage storage, bool resume,
                                           Action<long, long> progress, Action verifying, CancellationToken token)
    {
        var partial = storage.PartialPath(file, model);
        long offset = resume ? PartialLength(partial) : 0;
        if (!resume || offset >= file.SizeBytes)
        {
            FileUtil.DeleteQuietly(partial);
            offset = 0;
        }

        try
        {
            await FetchAsync(file, partial, offset, progress, token).ConfigureAwait(false);
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
        if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            FileUtil.DeleteQuietly(partial);
            throw ModelDownloadInterruption.Failed(new WhisperBatchError.ChecksumMismatch(model.DisplayName));
        }

        try
        {
            File.Move(partial, storage.FilePath(file, model), overwrite: true);
        }
        catch (Exception e)
        {
            throw ModelDownloadInterruption.Failed(ErrorMapper.Map(e, ErrorMapper.Operation.Download)
                ?? new WhisperBatchError.DownloadFailed(L("download.detail.cannotSave", e.Message)));
        }
    }

    /// <summary>通信のエラーを、利用者が対処できる案内のあるエラーにする</summary>
    private static WhisperBatchError NetworkError(Exception e) =>
        ErrorMapper.Map(e, ErrorMapper.Operation.Download) ?? new WhisperBatchError.DownloadFailed(e.Message);

    /// <summary>リダイレクトを 1 回ずつ確かめながらたどり、最終的な応答を返す</summary>
    internal static async Task<HttpResponseMessage> SendAsync(Uri url, long offset, CancellationToken token)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ModelDownloadHosts.IsAllowed(current))
            {
                throw ModelDownloadInterruption.Failed(new WhisperBatchError.UntrustedDownloadHost(ModelDownloadHosts.DisplayHost(current)));
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw ModelDownloadInterruption.Failed(NetworkError(e), canResume: offset > 0);
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw ModelDownloadInterruption.Failed(new WhisperBatchError.DownloadFailed(L("download.detail.tooManyRedirects")), canResume: offset > 0);
    }

    private static async Task FetchAsync(ModelFile file, string partial, long offset, Action<long, long> progress, CancellationToken token)
    {
        using var response = await SendAsync(file.Url, offset, token).ConfigureAwait(false);
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
        var total = response.Content.Headers.ContentLength is { } len ? len + offset : file.SizeBytes;

        FileStream output;
        try
        {
            output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ModelDownloadInterruption.Failed(ErrorMapper.Map(e, ErrorMapper.Operation.Download)
                ?? new WhisperBatchError.DownloadFailed(L("download.detail.cannotSave", e.Message)), canResume: offset > 0);
        }
        await using (output)
        {
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
                    // しばらくデータが届かない
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    throw ModelDownloadInterruption.Failed(new WhisperBatchError.NetworkInterrupted(), canResume: true);
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    throw ModelDownloadInterruption.Failed(ErrorMapper.Map(e, ErrorMapper.Operation.Download) ?? new WhisperBatchError.NetworkInterrupted(),
                        canResume: true);
                }
                if (n == 0) break;
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    // 空き容量がなくなった場合などは、次の行動が分かるエラーにする
                    throw ModelDownloadInterruption.Failed(ErrorMapper.Map(e, ErrorMapper.Operation.Download)
                        ?? new WhisperBatchError.DownloadFailed(e.Message), canResume: true);
                }
                received += n;
                progress(received, total);
            }
            if (received != file.SizeBytes)
            {
                // 途中で切れた（サーバーが接続を閉じた）
                throw ModelDownloadInterruption.Failed(new WhisperBatchError.NetworkInterrupted(), canResume: received < file.SizeBytes);
            }
        }
    }
}
