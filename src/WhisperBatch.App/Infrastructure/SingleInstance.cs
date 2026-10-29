using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace WhisperBatch.App.Infrastructure;

/// <summary>
/// アプリを 1 つだけ起動する。2 つ目の起動（エクスプローラーでファイルをアイコンにドロップした、「送る」から開いた など）は、
/// 受け取ったファイルを起動中のアプリへ渡して終了する（macOS 版の「このアプリケーションで開く」と同じ動き）。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = "WhisperBatch-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out var created);
        IsFirst = created;
    }

    /// <summary>起動中のアプリへファイルを渡す。渡せたら true。</summary>
    public static bool SendToFirst(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(paths.Count == 0 ? "<activate>" : string.Join("\n", paths));
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>ほかの起動から渡されたファイルを受け取る（received はバックグラウンドのスレッドから呼ばれる）</summary>
    public void Listen(Action<IReadOnlyList<string>> received)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);
                    var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => p != "<activate>").ToList();
                    received(paths);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
