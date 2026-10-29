using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace WhisperBatch.App.Infrastructure;

/// <summary>設定（%APPDATA%\WhisperBatch\settings.json に保存）</summary>
public sealed class AppSettings
{
    /// <summary>文字起こしの言語（"ja", "auto" など）</summary>
    public string? Language { get; set; }
    public string? ModelId { get; set; }
    /// <summary>表示言語: "system" / "ja" / "en"</summary>
    public string? AppLanguage { get; set; }
    public bool ShowTimestamps { get; set; }
    public WindowPlacement? Window { get; set; }

    public sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public double SidebarWidth { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhisperBatch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception)
        {
            // 壊れた設定は無視して既定値で始める
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存できなくても動作は続ける
        }
    }
}

public static class SystemLanguages
{
    /// <summary>Windows の表示言語の優先順（「設定 > 時刻と言語 > 言語と地域」の並び）</summary>
    public static IReadOnlyList<string> Preferred
    {
        get
        {
            try
            {
                uint count = 0, size = 0;
                if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, ref count, null, ref size) && size > 0)
                {
                    var buffer = new char[size];
                    if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, ref count, buffer, ref size))
                    {
                        var list = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        if (list.Length > 0) return list;
                    }
                }
            }
            catch (Exception)
            {
                // 取得できなければ現在の UI 言語を使う
            }
            return [CultureInfo.InstalledUICulture.Name, CultureInfo.CurrentUICulture.Name];
        }
    }

    private const uint MUI_LANGUAGE_NAME = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, ref uint numLanguages, [Out] char[]? buffer, ref uint bufferSize);
}
