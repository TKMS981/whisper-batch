using System.Globalization;
using System.Runtime.InteropServices;

namespace WhisperBatch.App.Infrastructure;

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
