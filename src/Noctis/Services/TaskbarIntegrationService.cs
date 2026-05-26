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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int value, int size);

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
    private const uint ID_SHUFFLE = 0, ID_PREV = 1, ID_PLAY = 2, ID_NEXT = 3, ID_FAVORITE = 4;
    private const int IconSize = 20;
    private const uint DWMWA_DISALLOW_PEEK = 11;

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

    private const string PathShuffle =
        "M19.28 4.72a.75.75 0 1 0-1.06 1.06L19.44 7h-.19c-3.918 0-6.423 2.302-8.692 4.388l-.066.06C8.154 13.597 6.044 15.5 2.75 15.5a.75.75 0 0 0 0 1.5c3.918 0 6.423-2.302 8.692-4.388l.066-.06C13.846 10.403 15.956 8.5 19.25 8.5h.19l-1.22 1.22a.75.75 0 1 0 1.06 1.06l2.5-2.5a.75.75 0 0 0 0-1.06z" +
        "M2.75 7c3.248 0 5.525 1.582 7.501 3.311l-.303.279l-.132.121q-.347.32-.68.62C7.283 9.732 5.4 8.5 2.75 8.5a.75.75 0 1 1 0-1.5" +
        "m16.5 10c-3.248 0-5.525-1.582-7.501-3.312l.302-.277l.133-.122q.347-.32.68-.62c1.853 1.6 3.736 2.83 6.386 2.83h.19l-1.22-1.219a.75.75 0 0 1 1.06-1.06l2.5 2.5a.75.75 0 0 1 0 1.06l-2.5 2.5a.75.75 0 1 1-1.06-1.06L19.44 17z";

    private const string PathHeartOutline =
        "m12.82 5.58l-.82.822l-.824-.824a5.375 5.375 0 1 0-7.601 7.602l7.895 7.895a.75.75 0 0 0 1.06 0l7.902-7.897a5.376 5.376 0 0 0-.001-7.599a5.38 5.38 0 0 0-7.611 0" +
        "m6.548 6.54L12 19.485L4.635 12.12a3.875 3.875 0 1 1 5.48-5.48l1.358 1.357a.75.75 0 0 0 1.073-.012L13.88 6.64a3.88 3.88 0 0 1 5.487 5.48";

    private const string PathHeartFilled =
        "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z";

    // ── Fields ───────────────────────────────────────────────────

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private SubclassProc? _wndProc; // prevent GC
    private IntPtr _icoPrev, _icoPlay, _icoPause, _icoNext;
    private IntPtr _icoShuffleOff, _icoShuffleOn, _icoHeart, _icoHeartFilled;
    private bool _ready;

    public event Action? PreviousClicked;
    public event Action? PlayPauseClicked;
    public event Action? NextClicked;
    public event Action? ShuffleClicked;
    public event Action? FavoriteClicked;

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

            // Stop Aero Peek from turning every other window (including an auto-hide
            // taskbar) transparent when the user hovers our taskbar thumbnail.
            int disallowPeek = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_DISALLOW_PEEK, ref disallowPeek, sizeof(int));

            _icoPrev = MakeIcon(PathPrevious);
            _icoPlay = MakeIcon(PathPlay);
            _icoPause = MakeIcon(PathPause);
            _icoNext = MakeIcon(PathNext);
            // Shuffle + outline heart are thin-outline glyphs that read lighter than
            // the solid play/prev/next icons. Add a bold stroke pass so their visual
            // weight matches at 20×20.
            _icoShuffleOff = MakeIcon(PathShuffle, boldenOutline: true);
            _icoShuffleOn = MakeIcon(PathShuffle, boldenOutline: true);
            _icoHeart = MakeIcon(PathHeartOutline, boldenOutline: true);
            _icoHeartFilled = MakeIcon(PathHeartFilled);

            var buttons = new[]
            {
                Btn(ID_SHUFFLE, _icoShuffleOff, "Shuffle"),
                Btn(ID_PREV, _icoPrev, "Previous"),
                Btn(ID_PLAY, _icoPlay, "Play"),
                Btn(ID_NEXT, _icoNext, "Forward"),
                Btn(ID_FAVORITE, _icoHeart, "Favorite"),
            };
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);

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

    public void UpdateShuffleState(bool isOn)
    {
        if (!_ready || _taskbar == null) return;

        _taskbar.ThumbBarUpdateButtons(_hwnd, 1, new[]
        {
            Btn(ID_SHUFFLE, isOn ? _icoShuffleOn : _icoShuffleOff, isOn ? "Shuffle (on)" : "Shuffle"),
        });
    }

    public void UpdateFavoriteState(bool isFavorite)
    {
        if (!_ready || _taskbar == null) return;

        _taskbar.ThumbBarUpdateButtons(_hwnd, 1, new[]
        {
            Btn(ID_FAVORITE, isFavorite ? _icoHeartFilled : _icoHeart, isFavorite ? "Unfavorite" : "Favorite"),
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
                    case ID_SHUFFLE: ShuffleClicked?.Invoke(); break;
                    case ID_FAVORITE: FavoriteClicked?.Invoke(); break;
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

    private static IntPtr MakeIcon(string svgPathData, byte alpha = 0xFF, bool boldenOutline = false)
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
            Color = new SKColor(0xFF, 0xFF, 0xFF, alpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        canvas.DrawPath(path, paint);

        // Outline-style glyphs (shuffle, outline heart) read lighter than the solid
        // play/prev/next icons. Stroke the same path on top to thicken the visible
        // edges so all five taskbar icons share the same weight.
        if (boldenOutline)
        {
            using var stroke = new SKPaint
            {
                Color = paint.Color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f, // in 24-unit viewBox space
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.DrawPath(path, stroke);
        }

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
        if (_icoShuffleOff != IntPtr.Zero) DestroyIcon(_icoShuffleOff);
        if (_icoShuffleOn != IntPtr.Zero) DestroyIcon(_icoShuffleOn);
        if (_icoHeart != IntPtr.Zero) DestroyIcon(_icoHeart);
        if (_icoHeartFilled != IntPtr.Zero) DestroyIcon(_icoHeartFilled);

        _ready = false;
    }
}
