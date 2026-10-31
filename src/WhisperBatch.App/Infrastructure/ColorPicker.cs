using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WhisperBatch.App.Infrastructure;

/// <summary>Windows 標準の「色の設定」ダイアログ（WPF には色の選択画面がないため）</summary>
public static class ColorPicker
{
    private static readonly int[] CustomColors = new int[16];

    /// <summary>色を選ばせる。キャンセルなら null。</summary>
    public static Color? Choose(Window owner, Color initial)
    {
        var custom = Marshal.AllocCoTaskMem(sizeof(int) * 16);
        try
        {
            Marshal.Copy(CustomColors, 0, custom, 16);
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = new WindowInteropHelper(owner).Handle,
                rgbResult = initial.R | (initial.G << 8) | (initial.B << 16),
                lpCustColors = custom,
                Flags = CC_RGBINIT | CC_FULLOPEN,
            };
            if (!ChooseColorW(ref cc)) return null;
            Marshal.Copy(custom, CustomColors, 0, 16);
            return Color.FromRgb((byte)(cc.rgbResult & 0xFF), (byte)((cc.rgbResult >> 8) & 0xFF), (byte)((cc.rgbResult >> 16) & 0xFF));
        }
        finally
        {
            Marshal.FreeCoTaskMem(custom);
        }
    }

    private const int CC_RGBINIT = 0x1;
    private const int CC_FULLOPEN = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorW(ref CHOOSECOLOR cc);
}
