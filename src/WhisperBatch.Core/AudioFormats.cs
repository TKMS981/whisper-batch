using Microsoft.Win32;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>
/// 追加できるファイルの判定。
///
/// 判定は 2 段階:
/// 1. 追加時: 拡張子が音声・動画か（ここ。既知の拡張子と、Windows に登録された種類 PerceivedType / Content Type で判定）
/// 2. 読み込み時: Media Foundation で実際に読めるか、動画か音声か（エンジン側）
/// </summary>
public static class AudioFormats
{
    /// <summary>音声として扱う拡張子</summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "wave", "mp3", "m4a", "m4b", "aac", "adts", "aif", "aiff", "aifc", "flac",
        "ogg", "oga", "opus", "wma", "mp2", "ac3", "ec3", "amr", "3ga", "caf",
    };

    /// <summary>動画として扱う拡張子（音声を取り出して文字起こしする）</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "qt", "3gp", "3g2", "mpg", "mpeg", "m2v", "ts", "mts", "m2ts",
        "avi", "wmv", "asf", "mkv", "webm", "flv",
    };

    /// <summary>Windows では音声扱いだが中身はメディアではないもの（プレイリストなど）</summary>
    private static readonly HashSet<string> NotMediaDespiteRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        "m3u", "m3u8", "pls", "wpl", "zpl", "asx", "wax", "wvx", "wmx", "cda", "cue", "xspf",
    };

    /// <summary>対象外とした理由</summary>
    public abstract record RejectionReason
    {
        public sealed record NoExtension : RejectionReason;
        public sealed record UnknownType(string Ext) : RejectionReason;
        public sealed record NotMedia(string Ext) : RejectionReason;

        public string Message => this switch
        {
            NoExtension => L("format.reject.noExtension"),
            UnknownType u => L("format.reject.unknownType", u.Ext),
            NotMedia n => L("format.reject.notMedia", n.Ext),
            _ => "",
        };
    }

    /// <summary>判定結果。null なら追加できる。</summary>
    public static RejectionReason? Check(string path)
    {
        var ext = Extension(path);
        if (ext.Length == 0) return new RejectionReason.NoExtension();
        if (AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext)) return null;
        if (NotMediaDespiteRegistry.Contains(ext)) return new RejectionReason.NotMedia(ext);
        return SystemLookup(ext) switch
        {
            SystemKind.Media => null,
            SystemKind.Other => new RejectionReason.NotMedia(ext),
            _ => new RejectionReason.UnknownType(ext),
        };
    }

    public static bool IsAccepted(string path) => Check(path) is null;

    /// <summary>ファイル名の拡張子（小文字、ドットなし）。".hidden" のようにドットで始まる名前は拡張子なしとみなす。</summary>
    public static string Extension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return "";
        return name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>ファイルを開くダイアログのフィルター（例: "*.wav;*.mp3;…"）</summary>
    public static string DialogPattern =>
        string.Join(";", AudioExtensions.Concat(VideoExtensions).Order(StringComparer.Ordinal).Select(e => "*." + e));

    /// <summary>UI に表示する対応形式の説明（README の「対応形式」と同じく、実ファイルで読み込みを確認した形式）</summary>
    public static string Summary => L("format.summary");

    private enum SystemKind { Unknown, Media, Other }

    /// <summary>Windows に登録されている拡張子の種類（HKEY_CLASSES_ROOT\.ext）</summary>
    private static SystemKind SystemLookup(string ext)
    {
        if (!OperatingSystem.IsWindows()) return SystemKind.Unknown;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("." + ext);
            if (key is null) return SystemKind.Unknown;
            var perceived = (key.GetValue("PerceivedType") as string)?.ToLowerInvariant();
            var contentType = (key.GetValue("Content Type") as string)?.ToLowerInvariant();
            if (perceived is "audio" or "video") return SystemKind.Media;
            if (contentType is not null && (contentType.StartsWith("audio/", StringComparison.Ordinal) || contentType.StartsWith("video/", StringComparison.Ordinal)))
                return SystemKind.Media;
            return SystemKind.Other;
        }
        catch (Exception)
        {
            return SystemKind.Unknown;
        }
    }
}

/// <summary>ファイルの中身の種類（読み込んで確認した結果）</summary>
public enum MediaKind
{
    Audio,
    /// <summary>映像トラックを持つファイル。音声を取り出して MP3 に変換してから文字起こしする。</summary>
    Video,
}
