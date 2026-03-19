using System.Runtime.InteropServices;
using SkiaSharp;

namespace Noctis.Services;

/// <summary>
/// Adds Previous / Play-Pause / Forward thumbnail toolbar buttons to the
/// Windows taskbar preview (the popup when you hover over the app icon).
/// Uses the ITaskbarList3 COM interface (Windows 7+).
/// Icons rendered via SkiaSharp using the same SVG paths as the playback island.
/// </summary>
public sealed class TaskbarIntegrationService : IDisposable
{
    // ── COM interface ────────────────────────────────────────────

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    // ── P/Invoke ─────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc,
        UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr id, IntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    // ── Constants ────────────────────────────────────────────────

    private const uint THB_ICON = 0x0002, THB_TOOLTIP = 0x0004, THB_FLAGS = 0x0008;
    private const uint THBF_ENABLED = 0x0000;
    private const uint WM_COMMAND = 0x0111, THBN_CLICKED = 0x1800;
    private const uint ID_PREV = 0, ID_PLAY = 1, ID_NEXT = 2;
    private const int IconSize = 20;

    // ── SVG path data (same as Icons.axaml, viewBox 0 0 24 24) ──

    private const string PathPrevious =
        "M3 3.75a.75.75 0 0 1 1.5 0v16.5a.75.75 0 0 1-1.5 0z" +
        "m18 1.003c0-1.408-1.578-2.24-2.74-1.444L7.763 10.503a1.75 1.75 0 0 0-.01 2.88l10.499 7.302c1.16.807 2.749-.024 2.749-1.437z";

    private const string PathPlay =
        "M5 5.274c0-1.707 1.826-2.792 3.325-1.977l12.362 6.727c1.566.852 1.566 3.1 0 3.952L8.325 20.702C6.826 21.518 5 20.432 5 18.726z";

    private const string PathPause =
        "M5.746 3a1.75 1.75 0 0 0-1.75 1.75v14.5c0 .966.784 1.75 1.75 1.75h3.5a1.75 1.75 0 0 0 1.75-1.75V4.75A1.75 1.75 0 0 0 9.246 3z" +
        "m9 0a1.75 1.75 0 0 0-1.75 1.75v14.5c0 .966.784 1.75 1.75 1.75h3.5a1.75 1.75 0 0 0 1.75-1.75V4.75A1.75 1.75 0 0 0 18.246 3z";

    private const string PathNext =
        "M3 4.753c0-1.408 1.578-2.24 2.74-1.444l10.498 7.194a1.75 1.75 0 0 1 .01 2.88L5.749 20.685C4.59 21.492 3 20.66 3 19.248z" +
        "M21 3.75a.75.75 0 0 0-1.5 0v16.5a.75.75 0 0 0 1.5 0z";

    // ── Fields ───────────────────────────────────────────────────

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private SubclassProc? _wndProc; // prevent GC
    private IntPtr _icoPrev, _icoPlay, _icoPause, _icoNext;
    private bool _ready;

    public event Action? PreviousClicked;
    public event Action? PlayPauseClicked;
    public event Action? NextClicked;

    // ── Public API ───────────────────────────────────────────────

    public void Initialize(IntPtr hwnd)
    {
        if (_ready) return;
        _hwnd = hwnd;

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
            if (type == null) return;

            _taskbar = (ITaskbarList3?)Activator.CreateInstance(type);
            if (_taskbar == null) return;
            _taskbar.HrInit();

            _icoPrev = MakeIcon(PathPrevious);
            _icoPlay = MakeIcon(PathPlay);
            _icoPause = MakeIcon(PathPause);
            _icoNext = MakeIcon(PathNext);

            var buttons = new[]
            {
                Btn(ID_PREV, _icoPrev, "Previous"),
                Btn(ID_PLAY, _icoPlay, "Play"),
                Btn(ID_NEXT, _icoNext, "Forward"),
            };
            _taskbar.ThumbBarAddButtons(_hwnd, 3, buttons);

            _wndProc = WndProc;
            SetWindowSubclass(_hwnd, _wndProc, (UIntPtr)1, IntPtr.Zero);

            _ready = true;
        }
        catch
        {
            // Taskbar integration not available
        }
    }

    public void UpdatePlayPauseState(bool isPlaying)
    {
        if (!_ready || _taskbar == null) return;

        _taskbar.ThumbBarUpdateButtons(_hwnd, 1, new[]
        {
            Btn(ID_PLAY, isPlaying ? _icoPause : _icoPlay, isPlaying ? "Pause" : "Play")
        });
    }

    // ── WndProc hook ─────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr id, IntPtr refData)
    {
        if (msg == WM_COMMAND)
        {
            uint hi = ((uint)(int)wParam >> 16) & 0xFFFF;
            uint lo = (uint)(int)wParam & 0xFFFF;

            if (hi == THBN_CLICKED)
            {
                switch (lo)
                {
                    case ID_PREV: PreviousClicked?.Invoke(); break;
                    case ID_PLAY: PlayPauseClicked?.Invoke(); break;
                    case ID_NEXT: NextClicked?.Invoke(); break;
                }
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static THUMBBUTTON Btn(uint id, IntPtr icon, string tip) => new()
    {
        dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
        iId = id,
        hIcon = icon,
        szTip = tip,
        dwFlags = THBF_ENABLED,
    };

    // ── Icon creation via SkiaSharp (SVG path rendering) ─────────

    private static IntPtr MakeIcon(string svgPathData)
    {
        const float viewBox = 24f;
        const float padding = 2f;

        using var path = SKPath.ParseSvgPathData(svgPathData);
        if (path == null) return IntPtr.Zero;

        using var bitmap = new SKBitmap(IconSize, IconSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        // Scale from 24x24 viewBox to icon size with padding for crispness
        float scale = (IconSize - padding * 2) / viewBox;
        canvas.Translate(padding, padding);
        canvas.Scale(scale, scale);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        canvas.DrawPath(path, paint);
        canvas.Flush();

        // Create Win32 HICON from the SkiaSharp bitmap pixel data
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = IconSize,
            biHeight = -IconSize, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };

        var hColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (hColor == IntPtr.Zero) return IntPtr.Zero;

        Marshal.Copy(bitmap.Bytes, 0, bits, bitmap.Bytes.Length);

        var hMask = CreateBitmap(IconSize, IconSize, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = true, hbmColor = hColor, hbmMask = hMask };
        var hIcon = CreateIconIndirect(ref info);

        DeleteObject(hColor);
        DeleteObject(hMask);
        return hIcon;
    }

    // ── Dispose ──────────────────────────────────────────────────

    public void Dispose()
    {
        if (_ready && _hwnd != IntPtr.Zero && _wndProc != null)
            RemoveWindowSubclass(_hwnd, _wndProc, (UIntPtr)1);

        if (_icoPrev != IntPtr.Zero) DestroyIcon(_icoPrev);
        if (_icoPlay != IntPtr.Zero) DestroyIcon(_icoPlay);
        if (_icoPause != IntPtr.Zero) DestroyIcon(_icoPause);
        if (_icoNext != IntPtr.Zero) DestroyIcon(_icoNext);

        _ready = false;
    }
}
