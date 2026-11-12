using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace ChestCounter;

/// <summary>
/// Small always-on-top counter window drawn with plain Win32/GDI (no WinForms needed).
/// Left-drag to move it, right-click to hide it (Ctrl+Shift+O or the counter's menu shows it again).
/// Visible over Fortnite when the game runs in "windowed fullscreen" (not exclusive fullscreen).
/// </summary>
/// <summary>What the overlay shows. <see cref="Current"/> null = no match in progress.</summary>
/// <param name="Detail">Optional secondary text drawn small and grey after the value.</param>
public sealed record OverlayRow(string Label, string Value, bool Treasure = false, string? Detail = null);

/// <param name="Current">Right counter (match in progress, or last match); null = "—".</param>
/// <param name="CurrentLive">Right counter is the match in progress (green).</param>
/// <param name="Badge">Header badge ("● EN DIRECT" / "● LIVE", "● PARTIE EN COURS" / "● MATCH IN PROGRESS") or null.</param>
public sealed record OverlayView(string Total, string? Current, string CurrentCaption, bool CurrentLive, string? Badge, IReadOnlyList<OverlayRow> Rows, string Treasures = "0", string Session = "0", string? Last = null);

[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static volatile OverlayView _view = new("0", null, OverlayModel.LastMatchCaption, false, null, Array.Empty<OverlayRow>());

    // Layout (computed by Relayout, used by Paint; both on the overlay thread).
    private static int _headerH, _bigH, _captionH, _rowH, _col1W, _col2W, _labelW;
    private static IntPtr _fontHeader, _fontCaption, _fontLabel;
    private static IntPtr _brushBorder, _brushLine;
    private static string Header => L.T("COFFRES", "CHESTS", "COFRES");
    private const string CaptionTotal = "total";
    private static Action<int, int>? _onMoved;
    private static Action<bool>? _onLockChanged;
    private static volatile bool _locked;
    private static bool _offscreen;
    private static int _stableW;
    private static RECT _lockRect; // lock button, client coordinates
    private static IntPtr _fontIcon;
    private static WndProcDelegate? _wndProc; // keep the delegate alive
    private static IntPtr _fontBig, _fontSmall, _brushBg;
    private static float _scale = 1f;    // display scaling × size chosen by the player
    private static float _dpiScale = 1f; // display scaling only
    private static volatile int _sizePercent = 100;
    private static volatile int _opacityPercent = 100;

    // Motion state. All reads/writes go through _animationLock because the counter updates the view from the UI
    // thread while the overlay paints on its own thread.
    private static readonly object _animationLock = new();
    private static bool _hasTotal;
    private static long _shownTotal, _targetTotal, _totalFrom;
    private static long _totalStartedMs;
    private static bool _totalAnimating;
    private static long _plusAmount;
    private static long _plusStartedMs;
    private static bool _animationTimerRunning;
    private const int AnimationTimerId = 1;
    private const int AnimationTimerMs = 33;
    private const double TotalAnimationMs = 480;
    private const double PlusAnimationMs = 900;

    /// <summary>"hud" (figures only, under the minimap, like the game's HUD) or "classic" (purple illustrated card).</summary>
    private static volatile string _style = "classic";
    private static IntPtr _fontHudBig, _fontHudSmall;
    private static int _hudBigH, _hudSmallH;

    /// <summary>Pixels of this color are fully transparent (styles without a background). Almost black, so that the
    /// anti-aliased edge of the black outline around the text blends into it without any colored fringe.</summary>
    private static readonly uint KeyColor = 0x00010001;

    public static string[] Styles => new[] { "hud", "classic" };

    /// <summary>Changes the look of the overlay at once.</summary>
    public static void SetStyle(string style)
    {
        _style = Array.IndexOf(Styles, style) >= 0 ? style : "hud";
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static bool Keyed => _style != "classic";

    private const int MinWidth = 240, Pad = 16, ColGap = 36;

    /// <summary>Size and opacity of the overlay (percent). Applied at once when the overlay is shown.</summary>
    public static void SetAppearance(int sizePercent, int opacityPercent)
    {
        _sizePercent = OverlayAppearance.ClampSize(sizePercent);
        _opacityPercent = OverlayAppearance.ClampOpacity(opacityPercent);
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static byte Alpha => (byte)Math.Round(_opacityPercent * 255 / 100.0);

    public static bool IsRunning => _hwnd != IntPtr.Zero;

    /// <summary>True when the overlay window is shown (not hidden with the shortcut or the menu).</summary>
    public static bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Closes the overlay window and its thread (overlay turned off in the settings). Start() can open it again.</summary>
    public static void Stop()
    {
        var thread = _thread;
        if (thread == null)
        {
            return;
        }
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(TimeSpan.FromSeconds(3));
        _animationTimerRunning = false;
    }

    /// <summary>True when the global Ctrl+Shift+O shortcut could be registered.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>True when the global Ctrl+Shift+L shortcut (lock/unlock) could be registered.</summary>
    public static bool LockHotkeyRegistered { get; private set; }

    /// <summary>Locked = click-through: the mouse goes through the overlay and it cannot be moved.</summary>
    public static bool IsLocked => _locked;

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged)
    {
        if (_thread != null)
        {
            return;
        }

        ResetTotalAnimation();
        _onMoved = onMoved;
        _offscreen = x <= -10000; // --overlay-preview: never brought back on screen
        _onLockChanged = onLockChanged;
        _locked = locked;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                Run(x, y, ready);
            }
            catch (Exception ex)
            {
                Console.WriteLine(L.T($"Overlay indisponible : {ex.Message}", $"Overlay unavailable: {ex.Message}", $"Overlay no disponible: {ex.Message}"));
                ready.Set();
            }
            finally
            {
                _hwnd = IntPtr.Zero;
                HotkeyRegistered = LockHotkeyRegistered = false;
                _thread = null;
            }
        })
        {
            IsBackground = true,
            Name = "Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Replaces what is shown. The window resizes itself to fit.</summary>
    public static void Update(OverlayView view)
    {
        SetTotalTarget(ParseTotal(view.Total));
        _view = view;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Saves the overlay as it is drawn to a PNG file (works off-screen). Used by --overlay-preview.</summary>
    public static bool Snapshot(string path)
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r))
        {
            return false;
        }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        var ok = PrintWindow(_hwnd, mem, 1 /* PW_CLIENTONLY: WM_PRINT -> WM_PRINTCLIENT */);
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bgra = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, bgra, ref bi, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        Png.Write(path, w, h, bgra);
        return ok;
    }

    public static void ToggleLock()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_LOCK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Toggle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Run(int? x, int? y, ManualResetEventSlim ready)
    {
        try { SetProcessDPIAware(); } catch { /* old Windows */ }

        var screenDc = GetDC(IntPtr.Zero);
        _dpiScale = GetDeviceCaps(screenDc, LOGPIXELSY) / 96f;
        ReleaseDC(IntPtr.Zero, screenDc);

        int S(int v) => (int)Math.Round(v * _scale);

        CreateFonts();
        _brushBg = CreateSolidBrush(Rgb(13, 7, 30));
        _brushBorder = CreateSolidBrush(Rgb(58, 39, 87));
        _brushLine = CreateSolidBrush(Rgb(47, 32, 77));

        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_SIZEALL),
            hbrBackground = _brushBg,
            lpszClassName = AppInfo.Name + "Overlay",
        };
        // Already registered when the overlay is turned off and on again in the same run (ERROR_CLASS_ALREADY_EXISTS).
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException("RegisterClassEx failed");
        }

        int w = S(MinWidth), h = S(90);
        var posX = x ?? S(24);
        var posY = y ?? S(24);

        _hwnd = CreateWindowExW(
            // Opaque (no WS_EX_LAYERED alpha): nothing for the compositor to blend every frame.
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, AppInfo.Name, WS_POPUP,

            posX, posY, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }

        ApplyLock(_hwnd, _locked, notify: false);
        Relayout(_hwnd);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        EnsureAnimationTimer(_hwnd);
        // The timer only runs while the HUD is visible and has something to animate. Otherwise the thread sleeps in
        // GetMessage and wakes up for counter changes, dragging, hiding or the shortcut.
        // Global shortcut, works even when the game has the focus.
        HotkeyRegistered = RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
        LockHotkeyRegistered = RegisterHotKey(_hwnd, 2, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_L);
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>(Re)creates the fonts for the current display scaling and chosen size.</summary>
    private static void CreateFonts()
    {
        foreach (var old in new[] { _fontBig, _fontSmall, _fontHeader, _fontCaption, _fontLabel, _fontIcon, _fontHudBig, _fontHudSmall })
        {
            if (old != IntPtr.Zero) DeleteObject(old);
        }
        _scale = _dpiScale * _sizePercent / 100f;
        int S(int v) => (int)Math.Round(v * _scale);
        _fontBig = CreateFontW(-S(54), 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontSmall = CreateFontW(-S(17), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontHeader = CreateFontW(-S(19), 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontCaption = CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontLabel = CreateFontW(-S(18), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontIcon = CreateFontW(-S(16), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
        // Game-like styles: condensed italic, grayscale anti-aliasing (ClearType would leave colored edges once the
        // background is made transparent).
        _fontHudBig = CreateFontW(-S(40), 0, 0, 0, 700, 1, 0, 0, 1, 0, 0, 4, 0, "Bahnschrift Condensed");
        _fontHudSmall = CreateFontW(-S(18), 0, 0, 0, 600, 1, 0, 0, 1, 0, 0, 4, 0, "Bahnschrift SemiCondensed");
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // Lock button = normal client area (clickable); everywhere else = caption (drag to move).
                // When locked the window is WS_EX_TRANSPARENT, so it never even receives this message.
                GetWindowRect(hwnd, out var wr);
                var px = (short)((long)lParam & 0xFFFF) - wr.Left;
                var py = (short)(((long)lParam >> 16) & 0xFFFF) - wr.Top;
                var onButton = px >= _lockRect.Left && px < _lockRect.Right && py >= _lockRect.Top && py < _lockRect.Bottom;
                return (IntPtr)(onButton ? HTCLIENT : HTCAPTION);
            }

            case WM_SETCURSOR:
                if (((long)lParam & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                    return (IntPtr)1;
                }
                break;

            case WM_LBUTTONUP:
                ApplyLock(hwnd, true, notify: true);
                return IntPtr.Zero;

            case WM_APP_LOCK:
                ApplyLock(hwnd, !_locked, notify: true);
                return IntPtr.Zero;

            case WM_NCRBUTTONUP:
                SetVisible(hwnd, false);
                return IntPtr.Zero;

            case WM_HOTKEY:
                if ((long)wParam == 2)
                {
                    ApplyLock(hwnd, !_locked, notify: true);
                }
                else
                {
                    SetVisible(hwnd, !IsWindowVisible(hwnd));
                }
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                if (GetWindowRect(hwnd, out var r))
                {
                    _onMoved?.Invoke(r.Left, r.Top);
                }
                return IntPtr.Zero;

            case WM_APP_STOP:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_APP_TOGGLE:
                SetVisible(hwnd, !IsWindowVisible(hwnd));
                return IntPtr.Zero;

            case WM_APP_RELAYOUT:
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_APP_APPEARANCE:
                CreateFonts();
                _stableW = 0; // the width may shrink when the overlay gets smaller
                ApplyLock(hwnd, _locked, notify: false); // also applies the opacity
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_TIMER:
                if ((long)wParam == AnimationTimerId)
                {
                    TickAnimation(hwnd);
                    return IntPtr.Zero;
                }
                break;

            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;

            case WM_PRINTCLIENT: // snapshot (--overlay-preview): draw into the given DC
                PaintTo(hwnd, wParam);
                return IntPtr.Zero;

            case WM_DESTROY:
                StopAnimationTimer(hwnd);
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Locked: click-through (WS_EX_LAYERED + WS_EX_TRANSPARENT). Unlocked: normal window. Layered also when the
    /// player made it see-through; a fully opaque unlocked overlay stays a plain window (nothing to blend).
    /// </summary>
    private static void ApplyLock(IntPtr hwnd, bool locked, bool notify)
    {
        _locked = locked;
        var alpha = Alpha;
        // Every style is layered now: the HUD has always relied on the key colour, and the classic card
        // needs it too so the chest and the bevelled corners can sit outside the plate.
        var layered = true;
        var ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        ex = layered ? ex | WS_EX_LAYERED : ex & ~(long)WS_EX_LAYERED;
        ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        if (layered)
        {
            // Required for a layered window to be drawn; the key color makes the background of the game-like styles invisible.
            SetLayeredWindowAttributes(hwnd, KeyColor, alpha, LWA_ALPHA | LWA_COLORKEY);
        }
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        InvalidateRect(hwnd, IntPtr.Zero, true);

        if (notify)
        {
            _onLockChanged?.Invoke(locked);
            Console.WriteLine(locked
                ? L.T($"Overlay verrouillé : la souris passe au travers. {(LockHotkeyRegistered ? "Ctrl+Maj+L" : "« Déverrouiller l'overlay » dans le compteur")} pour le déverrouiller.",
                      $"Overlay locked: the mouse goes through it. {(LockHotkeyRegistered ? "Ctrl+Shift+L" : "\"Unlock the overlay\" in the counter")} to unlock it.",
                      $"Overlay bloqueado: el ratón lo atraviesa. {(LockHotkeyRegistered ? "Ctrl+Mayús+L" : "«Desbloquear el overlay» en el contador")} para desbloquearlo.")
                : L.T("Overlay déverrouillé : clic gauche maintenu pour le déplacer, cadenas pour le reverrouiller.",
                      "Overlay unlocked: hold the left mouse button to move it, click the padlock to lock it again.",
                      "Overlay desbloqueado: arrástralo con el clic izquierdo; el candado lo vuelve a bloquear."));
        }
    }

    private static void SetVisible(IntPtr hwnd, bool visible)
    {
        if (visible)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            EnsureAnimationTimer(hwnd);
        }
        else
        {
            ShowWindow(hwnd, SW_HIDE);
            StopAnimationTimer(hwnd);
            Console.WriteLine(HotkeyRegistered
                ? L.T("Overlay masqué : Ctrl+Maj+O (n'importe où) pour le réafficher.", "Overlay hidden: Ctrl+Shift+O (anywhere) to show it again.", "Overlay oculto: Ctrl+Mayús+O (desde cualquier sitio) para volver a mostrarlo.")
                : L.T("Overlay masqué : « Afficher l'overlay » dans le compteur pour le réafficher.", "Overlay hidden: \"Show the overlay\" in the counter to show it again.", "Overlay oculto: «Mostrar el overlay» en el contador para volver a mostrarlo."));
        }
    }

    private static int TextWidth(IntPtr hdc, IntPtr font, string text, out int height)
    {
        SelectObject(hdc, font);
        var r = new RECT();
        DrawTextW(hdc, text.Length == 0 ? " " : text, -1, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        height = r.Bottom - r.Top;
        return r.Right - r.Left;
    }

    /// <summary>Measures the text and resizes the window (and its rounded region) to fit it.</summary>
    private static void Relayout(IntPtr hwnd)
    {
        if (Keyed)
        {
            RelayoutStyled(hwnd);
            return;
        }
        int S(int v) => (int)Math.Round(v * _scale);
        var v = _view;
        var hdc = GetDC(hwnd);
        try
        {
            // Purple card: a large chest on the left, then the title, the total, the last match and the broken row.
            var titleW = TextWidth(hdc, _fontHeader, Header, out _headerH);
            _headerH = Math.Max(_headerH, S(16));
            _col1W = Math.Max(TextWidth(hdc, _fontBig, v.Total, out _bigH), TextWidth(hdc, _fontCaption, CaptionTotal, out _captionH));
            _col2W = Math.Max(TextWidth(hdc, _fontBig, v.Current ?? "—", out _), Math.Max(TextWidth(hdc, _fontCaption, v.CurrentCaption, out _), TextWidth(hdc, _fontCaption, OverlayModel.LastMatchCaption, out _)));

            _labelW = 0;
            var valueW = 0;
            _rowH = 0;
            foreach (var row in v.Rows)
            {
                _labelW = Math.Max(_labelW, TextWidth(hdc, _fontLabel, row.Label, out var lh));
                valueW = Math.Max(valueW, TextWidth(hdc, _fontSmall, row.Value, out var vh)
                    + (row.Detail != null ? S(6) + TextWidth(hdc, _fontCaption, row.Detail, out _) : 0));
                _rowH = Math.Max(_rowH, Math.Max(lh, vh));
            }
            var rowsW = v.Rows.Count > 0 ? S(26) + _labelW + S(12) + valueW : 0;
            var countersW = _col1W + S(44) + _col2W;
            var headerW = S(32) + titleW + S(14) + (v.Badge != null ? TextWidth(hdc, _fontCaption, v.Badge, out _) : 0);

            // Never shrinks: the card keeps the same size between a match in progress and the end of the match.
            var w = Math.Max(_stableW, Math.Max(S(558), S(193) + Math.Max(Math.Max(countersW, rowsW), headerW) + S(28)));
            _stableW = w;
            var rowsTop = S(52) + _bigH + _captionH + S(12);
            var h = Math.Max(S(196), rowsTop + (v.Rows.Count > 0 ? v.Rows.Count * (_rowH + S(4)) + S(12) : 0));
            _lockRect = new RECT { Left = w - S(42), Top = S(16), Right = w - S(18), Bottom = S(40) };

            // Keep it fully on its screen: on the right half of the screen it grows to the left, and it is
            // pushed back inside when it would overflow (a wider overlay used to be cut by the screen edge).
            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // anchored on the right edge
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            // The classic card uses a color key for its transparent corners and the chest that overhangs
            // the left edge; no window region (which would clip the chest) is needed.
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            if (nx != cur.Left || ny != cur.Top)
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd); // paint now; the optional motion timer is started when the HUD needs it
        EnsureAnimationTimer(hwnd);
    }

    private static void Text(IntPtr hdc, IntPtr font, uint color, string text, int left, int top, int right, int bottom, uint align = DT_LEFT)
    {
        SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        DrawTextW(hdc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX | align);
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        try
        {
            PaintTo(hwnd, hdc);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void PaintTo(IntPtr hwnd, IntPtr hdc)
    {
        if (Keyed)
        {
            PaintStyled(hwnd, hdc);
            return;
        }
        PaintJewelNew(hwnd, hdc);
    }

    // ---- Photo-like classic card ----------------------------------------------------------------

    private static void PaintJewelNew(IntPtr hwnd, IntPtr hdc)
    {
        GetClientRect(hwnd, out var rc);
        var keyBrush = CreateSolidBrush(KeyColor);
        FillRect(hdc, ref rc, keyBrush);
        DeleteObject(keyBrush);
        SetBkMode(hdc, TRANSPARENT);
        int S(int n) => (int)Math.Round(n * _scale);
        var v = _view;

        var plateX = S(28);
        var plateW = rc.Right - plateX;
        using (var g = Drawing.Graphics.FromHdc(hdc))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            var state = g.Save();
            g.TranslateTransform(plateX, 0);
            PaintJewelPlate(g, plateW, rc.Bottom);
            g.Restore(state);
            JewelChestNew(g, S(10), S(22), S(166), S(178));
            MiniChest(g, plateX + S(140), S(21), S(26), S(19));
        }

        var contentX = plateX + S(150);
        var right = rc.Right - S(20);
        var top = S(20);
        var headerTextX = contentX + S(26);
        Text(hdc, _fontHeader, Rgb(250, 245, 255), Header, headerTextX, top, _lockRect.Left - S(8), top + _headerH);
        if (v.Badge != null)
        {
            Text(hdc, _fontCaption, Rgb(95, 235, 151), v.Badge, contentX, top, _lockRect.Left - S(8), top + _headerH, DT_RIGHT);
        }

        // Locked is the normal in-game state: the padlock is hidden to keep the reference's clean header.
        if (!_locked)
        {
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, Rgb(211, 190, 255));
            var lr = _lockRect;
            DrawTextW(hdc, "\uE785", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
        }

        var bigY = S(52);
        var col2X = contentX + _col1W + S(90);
        Text(hdc, _fontBig, Rgb(255, 201, 67), v.Total, contentX - S(1), bigY, contentX + _col1W + S(5), bigY + _bigH);
        Text(hdc, _fontBig, v.Current == null ? Rgb(123, 105, 151) : Rgb(249, 245, 255),
            v.Current ?? "—", col2X - S(1), bigY, right + S(4), bigY + _bigH);

        var separatorX = col2X - S(20);
        var sep = new RECT { Left = separatorX, Top = bigY + S(10), Right = separatorX + Math.Max(1, Sc(1.5)), Bottom = bigY + _bigH + S(5) };
        var sepBrush = CreateSolidBrush(Rgb(226, 214, 243));
        FillRect(hdc, ref sep, sepBrush);
        DeleteObject(sepBrush);

        var captionY = bigY + _bigH - S(1);
        Text(hdc, _fontCaption, Rgb(241, 235, 252), CaptionTotal, contentX, captionY, contentX + _col1W + S(4), captionY + _captionH);
        Text(hdc, _fontCaption, Rgb(241, 235, 252), v.CurrentCaption, col2X, captionY, right + S(4), captionY + _captionH);

        var rowsY = bigY + _bigH + _captionH + S(12);
        using (var g = Drawing.Graphics.FromHdc(hdc))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            foreach (var row in v.Rows)
            {
                ToolIconNew(g, contentX - S(16), rowsY + Math.Max(0, (_rowH - S(20)) / 2), S(20));
                var labelX = contentX + S(14);
                Text(hdc, _fontLabel, Rgb(244, 238, 255), row.Label, labelX, rowsY, labelX + _labelW, rowsY + _rowH);
                var valueX = labelX + _labelW + S(12);
                Text(hdc, _fontSmall, row.Treasure ? Rgb(196, 181, 253) : Rgb(255, 201, 67), row.Value, valueX, rowsY, right + S(4), rowsY + _rowH);
                if (row.Detail != null)
                {
                    var detailX = valueX + TextWidth(hdc, _fontSmall, row.Value, out _) + S(6);
                    Text(hdc, _fontCaption, Rgb(216, 202, 238), row.Detail, detailX, rowsY, right + S(4), rowsY + _rowH - S(2), DT_BOTTOM);
                }
                rowsY += _rowH + S(4);
            }
        }
    }

    private static Drawing2D.GraphicsPath CardPath(int w, int h, int inset = 0)
    {
        var x0 = inset;
        var y0 = inset;
        var x1 = w - inset;
        var y1 = h - inset;
        var cx = Math.Max(5, Sc(30) - inset);
        var cy = Math.Max(4, Sc(22) - inset);
        var path = new Drawing2D.GraphicsPath();
        path.AddPolygon(new[]
        {
            new Drawing.PointF(x0 + cx, y0),
            new Drawing.PointF(x1 - cx, y0),
            new Drawing.PointF(x1, y0 + cy),
            new Drawing.PointF(x1, y1 - cy),
            new Drawing.PointF(x1 - cx, y1),
            new Drawing.PointF(x0 + cx, y1),
            new Drawing.PointF(x0, y1 - cy),
            new Drawing.PointF(x0, y0 + cy),
        });
        path.CloseFigure();
        return path;
    }

    /// <summary>The bevelled dark-violet plate of the reference, with a soft light behind the chest.</summary>
    private static void PaintJewelPlate(Drawing.Graphics g, int w, int h)
    {
        using var path = CardPath(w, h);
        using (var bg = new Drawing2D.LinearGradientBrush(
            new Drawing.RectangleF(0, 0, w, h),
            Drawing.Color.FromArgb(255, 34, 14, 66),
            Drawing.Color.FromArgb(255, 8, 3, 18), 90f))
        {
            g.FillPath(bg, path);
        }

        var state = g.Save();
        g.SetClip(path);
        using (var glow = new Drawing2D.GraphicsPath())
        {
            glow.AddEllipse(Sc(-30), Sc(12), Sc(235), Sc(225));
            using var brush = new Drawing2D.PathGradientBrush(glow)
            {
                CenterColor = Drawing.Color.FromArgb(105, 104, 43, 190),
                SurroundColors = new[] { Drawing.Color.FromArgb(0, 104, 43, 190) },
            };
            g.FillPath(brush, glow);
        }
        g.Restore(state);

        using (var top = new Drawing.Pen(Drawing.Color.FromArgb(190, 174, 126, 255), Math.Max(1, Sc(1.6))))
        {
            g.DrawLine(top, Sc(36), Sc(2), w - Sc(36), Sc(2));
        }

        using (var outer = new Drawing.Pen(Drawing.Color.FromArgb(255, 5, 2, 12), Math.Max(2, Sc(8))) { LineJoin = Drawing2D.LineJoin.Miter })
        {
            g.DrawPath(outer, path);
        }
        using (var bevelPath = CardPath(w, h, Sc(4)))
        using (var bevel = new Drawing.Pen(Drawing.Color.FromArgb(255, 109, 40, 217), Math.Max(2, Sc(3))) { LineJoin = Drawing2D.LineJoin.Miter })
        {
            g.DrawPath(bevel, bevelPath);
        }
        using (var innerPath = CardPath(w, h, Sc(8)))
        using (var inner = new Drawing.Pen(Drawing.Color.FromArgb(190, 139, 92, 246), Math.Max(1, Sc(1.2))))
        {
            g.DrawPath(inner, innerPath);
        }
    }

    /// <summary>A small gold-and-purple chest beside the title.</summary>
    private static void MiniChest(Drawing.Graphics g, int x, int y, int w, int h)
    {
        var outline = Drawing.Color.FromArgb(255, 20, 7, 35);
        var gold = Drawing.Color.FromArgb(255, 245, 183, 50);
        var goldDark = Drawing.Color.FromArgb(255, 171, 105, 17);
        var purple = Drawing.Color.FromArgb(255, 92, 35, 169);
        var purpleDark = Drawing.Color.FromArgb(255, 43, 15, 94);
        using var body = RoundRect(x, y + h * .36f, w, h * .58f, Math.Max(2, Sc(3)));
        using (var fill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(x, y + h * .36f, w, h * .6f), gold, goldDark, 90f))
        {
            g.FillPath(fill, body);
        }
        using (var pen = new Drawing.Pen(outline, Math.Max(1, Sc(1.5)))) g.DrawPath(pen, body);
        using var panel = RoundRect(x + w * .16f, y + h * .47f, w * .68f, h * .36f, Math.Max(1, Sc(2)));
        using (var fill = new Drawing.SolidBrush(purple)) g.FillPath(fill, panel);
        using var lid = new Drawing2D.GraphicsPath();
        lid.AddBezier(x + w * .05f, y + h * .39f, x + w * .12f, y + h * .02f, x + w * .88f, y + h * .02f, x + w * .95f, y + h * .39f);
        lid.AddLine(x + w * .95f, y + h * .39f, x + w * .05f, y + h * .39f);
        lid.CloseFigure();
        using (var fill = new Drawing.SolidBrush(purpleDark)) g.FillPath(fill, lid);
        using (var pen = new Drawing.Pen(gold, Math.Max(1, Sc(2)))) g.DrawPath(pen, lid);
        using (var band = new Drawing.SolidBrush(gold)) g.FillRectangle(band, x + w * .45f, y + h * .35f, Math.Max(2, w * .12f), h * .38f);
        using (var key = new Drawing.SolidBrush(outline)) g.FillEllipse(key, x + w * .48f, y + h * .46f, Math.Max(2, w * .08f), Math.Max(2, h * .12f));
    }

    /// <summary>A detailed purple/gold chest in the spirit of the reference illustration, drawn with GDI+.</summary>
    private static void JewelChestNew(Drawing.Graphics g, int x, int y, int w, int h)
    {
        float X(float t) => x + t * w;
        float Y(float t) => y + t * h;
        float U(float t) => t * w;
        Drawing.PointF P(float px, float py) => new(X(px), Y(py));
        var outline = Drawing.Color.FromArgb(255, 10, 3, 22);
        var gold = Drawing.Color.FromArgb(255, 247, 188, 55);
        var goldLight = Drawing.Color.FromArgb(255, 255, 226, 139);
        var goldDark = Drawing.Color.FromArgb(255, 161, 91, 14);
        var purpleLight = Drawing.Color.FromArgb(255, 139, 76, 224);
        var purple = Drawing.Color.FromArgb(255, 77, 29, 151);
        var purpleDark = Drawing.Color.FromArgb(255, 38, 13, 81);
        var bandWidth = Math.Max(2, Sc(3));

        using (var shadow = new Drawing.SolidBrush(Drawing.Color.FromArgb(145, 3, 1, 8)))
        {
            g.FillEllipse(shadow, X(-.02f), Y(.84f), U(1.04f), h * .16f);
        }

        // Dark side and back faces give the chest its three-quarter volume.
        using (var side = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 29, 9, 62)))
        {
            g.FillPolygon(side, new[] { P(.10f, .42f), P(.04f, .88f), P(-.02f, .82f), P(.02f, .47f) });
        }
        using (var sidePen = new Drawing.Pen(outline, bandWidth)) g.DrawPolygon(sidePen, new[] { P(.10f, .42f), P(.04f, .88f), P(-.02f, .82f), P(.02f, .47f) });

        var body = new[] { P(.10f, .42f), P(.90f, .42f), P(.96f, .88f), P(.04f, .88f) };
        using (var bodyFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.04f), Y(.40f), U(.92f), h * .5f), purpleLight, purpleDark, 90f))
        {
            g.FillPolygon(bodyFill, body);
        }
        using (var bodyPen = new Drawing.Pen(outline, Math.Max(2, Sc(3)))) g.DrawPolygon(bodyPen, body);
        using (var bodyGold = new Drawing.Pen(goldDark, Math.Max(2, Sc(4)))) g.DrawPolygon(bodyGold, body);

        // Purple front panel with an inset gold frame.
        var panel = new[] { P(.15f, .47f), P(.85f, .47f), P(.90f, .83f), P(.10f, .83f) };
        using (var panelFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.10f), Y(.46f), U(.8f), h * .38f), purple, purpleDark, 90f))
        {
            g.FillPolygon(panelFill, panel);
        }
        using (var panelPen = new Drawing.Pen(gold, Math.Max(1, Sc(2)))) g.DrawPolygon(panelPen, panel);

        // Domed lid, gold rim, then a recessed purple top.
        using var lid = new Drawing2D.GraphicsPath();
        lid.AddBezier(X(.03f), Y(.43f), X(.10f), Y(.02f), X(.90f), Y(.02f), X(.97f), Y(.43f));
        lid.AddLine(X(.97f), Y(.43f), X(.03f), Y(.43f));
        lid.CloseFigure();
        using (var lidFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.03f), Y(.02f), U(.94f), h * .43f), purpleLight, purpleDark, 90f))
        {
            g.FillPath(lidFill, lid);
        }
        using (var lidPen = new Drawing.Pen(goldDark, Math.Max(2, Sc(8))) { LineJoin = Drawing2D.LineJoin.Miter }) g.DrawPath(lidPen, lid);
        using (var lidGold = new Drawing.Pen(gold, Math.Max(1, Sc(4))) { LineJoin = Drawing2D.LineJoin.Miter }) g.DrawPath(lidGold, lid);

        using var lidInner = new Drawing2D.GraphicsPath();
        lidInner.AddBezier(X(.11f), Y(.39f), X(.16f), Y(.10f), X(.84f), Y(.10f), X(.89f), Y(.39f));
        lidInner.AddLine(X(.89f), Y(.39f), X(.11f), Y(.39f));
        lidInner.CloseFigure();
        using (var lidInnerFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.11f), Y(.10f), U(.78f), h * .3f), purple, purpleDark, 90f))
        {
            g.FillPath(lidInnerFill, lidInner);
        }
        using (var lidInnerPen = new Drawing.Pen(Drawing.Color.FromArgb(180, 185, 135, 250), Math.Max(1, Sc(1.5)))) g.DrawPath(lidInnerPen, lidInner);

        // Gold straps and the two horizontal metal bands.
        foreach (var t in new[] { .27f, .73f })
        {
            var strap = new[] { P(t - .035f, .10f), P(t + .035f, .10f), P(t + .055f, .87f), P(t - .055f, .87f) };
            using (var strapFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(t - .06f), Y(.10f), U(.12f), h * .78f), goldLight, goldDark, 90f))
            {
                g.FillPolygon(strapFill, strap);
            }
            using (var strapPen = new Drawing.Pen(outline, Math.Max(1, Sc(1.5)))) g.DrawPolygon(strapPen, strap);
        }
        using (var band = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.04f), Y(.402f), U(.92f), h * .06f), goldLight, goldDark, 90f))
        {
            g.FillPolygon(band, new[] { P(.04f, .402f), P(.96f, .402f), P(.96f, .447f), P(.04f, .447f) });
        }
        using (var bandPen = new Drawing.Pen(outline, Math.Max(1, Sc(1.5)))) g.DrawPolygon(bandPen, new[] { P(.04f, .402f), P(.96f, .402f), P(.96f, .447f), P(.04f, .447f) });
        using (var band2 = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.04f), Y(.845f), U(.92f), h * .05f), goldLight, goldDark, 90f))
        {
            g.FillPolygon(band2, new[] { P(.04f, .845f), P(.96f, .845f), P(.96f, .885f), P(.04f, .885f) });
        }

        // Gold lock plate, keyhole and feet.
        using (var lockPath = RoundRect(X(.405f), Y(.47f), U(.19f), h * .21f, Math.Max(2, Sc(5))))
        {
            using (var lockFill = new Drawing2D.LinearGradientBrush(new Drawing.RectangleF(X(.40f), Y(.47f), U(.2f), h * .21f), goldLight, goldDark, 90f))
            {
                g.FillPath(lockFill, lockPath);
            }
            using (var lockPen = new Drawing.Pen(outline, Math.Max(2, Sc(2.5)))) g.DrawPath(lockPen, lockPath);
        }
        using (var keyhole = new Drawing.SolidBrush(outline))
        {
            g.FillEllipse(keyhole, X(.475f), Y(.525f), U(.05f), h * .055f);
            g.FillRectangle(keyhole, X(.49f), Y(.56f), U(.02f), h * .06f);
        }
        foreach (var fx in new[] { .17f, .77f })
        {
            using (var foot = new Drawing.SolidBrush(goldDark))
            {
                g.FillRectangle(foot, X(fx), Y(.86f), U(.07f), h * .08f);
            }
            using (var footPen = new Drawing.Pen(outline, Math.Max(1, Sc(1.5))))
            {
                g.DrawRectangle(footPen, X(fx), Y(.86f), U(.07f), h * .08f);
            }
        }

        // Crisp highlights keep the illustration readable at the small overlay sizes.
        using (var shine = new Drawing.Pen(Drawing.Color.FromArgb(170, 218, 188, 255), Math.Max(1, Sc(2))))
        {
            g.DrawBezier(shine, X(.16f), Y(.25f), X(.30f), Y(.15f), X(.48f), Y(.13f), X(.62f), Y(.16f));
            g.DrawLine(shine, X(.17f), Y(.53f), X(.17f), Y(.78f));
        }
        using (var goldShine = new Drawing.Pen(goldLight, Math.Max(1, Sc(1.5))))
        {
            g.DrawLine(goldShine, X(.30f), Y(.46f), X(.30f), Y(.83f));
            g.DrawLine(goldShine, X(.76f), Y(.46f), X(.76f), Y(.83f));
        }
    }

    private static Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var path = new Drawing2D.GraphicsPath();
        r = Math.Max(1, Math.Min(r, Math.Min(w, h) / 2));
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void ToolIconNew(Drawing.Graphics g, int x, int y, int size)
    {
        var width = Math.Max(2, Sc(2.4));
        using var pen = new Drawing.Pen(Drawing.Color.FromArgb(255, 239, 231, 255), width)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
        };
        g.DrawLine(pen, x + Sc(3), y + Sc(15), x + Sc(15), y + Sc(3));
        g.DrawLine(pen, x + Sc(3), y + Sc(3), x + Sc(15), y + Sc(15));
        using var gold = new Drawing.Pen(Drawing.Color.FromArgb(255, 247, 188, 55), Math.Max(1, Sc(1.4)));
        g.DrawLine(gold, x + Sc(5), y + Sc(13), x + Sc(8), y + Sc(16));
        g.DrawLine(gold, x + Sc(5), y + Sc(5), x + Sc(8), y + Sc(2));
    }

    private static uint Blend(uint a, uint b, double t)
    {
        var ar = (int)(a & 0xFF); var br = (int)(b & 0xFF);
        var ag = (int)((a >> 8) & 0xFF); var bg = (int)((b >> 8) & 0xFF);
        var ab = (int)((a >> 16) & 0xFF); var bb = (int)((b >> 16) & 0xFF);
        return Rgb(
            (byte)Math.Round(ar + (br - ar) * t),
            (byte)Math.Round(ag + (bg - ag) * t),
            (byte)Math.Round(ab + (bb - ab) * t));
    }

    // ---- "hud" style: figures only, outlined like the game's own HUD, right-aligned under the minimap -------------
    //
    //   [chest] 3 934   Dernière • 14   dont cassés • 17
    // One horizontal row, like the game's own HUD. The last part becomes "● En partie" or "● En direct • 12".

    private sealed record HudRow(string Label, string Value, bool Dot);

    private static HudRow StateRow(OverlayView v)
    {
        if (v.CurrentLive && v.Current is { } cur && cur != "…")
        {
            return new HudRow(L.T("En direct", "Live", "En directo"), cur, true);
        }
        if (v.CurrentLive)
        {
            return new HudRow(L.T("En partie", "In a match", "En partida"), "", true); // figures readable only at the end
        }
        return new HudRow(L.T("Dernière", "Last", "Última"), v.Last ?? "—", false);
    }

    private static int Sc(double v) => (int)Math.Round(v * _scale);

    private const string Sep = "  •  ";

    private static void RelayoutStyled(IntPtr hwnd)
    {
        var v = _view;
        var hdc = GetDC(hwnd);
        int w, h;
        try
        {
            var pad = Sc(8);
            var lockW = Sc(22);
            var bigW = TextWidth(hdc, _fontHudBig, v.Total, out _hudBigH);
            var state = StateRow(v);
            var stateText = state.Label + (state.Value.Length > 0 ? Sep + state.Value : "");
            var stateTextW = TextWidth(hdc, _fontHudSmall, stateText, out _hudSmallH);
            var stateW = stateTextW + (state.Dot ? Sc(18) : 0);
            var rowsW = v.Rows.Sum(r => Sc(18) + TextWidth(hdc, _fontHudSmall, r.Label + Sep + r.Value, out _));
            var lineH = Math.Max(_hudBigH, _hudSmallH);
            // One row: chest + total, then the last/live counter and the destroyed chests.
            w = lockW + pad + Sc(26) + Sc(8) + bigW + Sc(18) + stateW + rowsW + pad + Sc(14);
            h = pad + lineH + pad;
            w = Math.Max(_stableW, w);
            _stableW = w;
            _lockRect = new RECT { Left = Sc(2), Top = 0, Right = lockW, Bottom = h };

            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // anchored on the right edge (under the minimap)
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, IntPtr.Zero, true); // plain rectangle: the corners are made invisible by the key color
            if (nx != cur.Left || ny != cur.Top)
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd);
        EnsureAnimationTimer(hwnd);
    }

    /// <summary>Text outlined in near black, like the game's own HUD figures (readable on any background).</summary>
    private static void Outlined(IntPtr hdc, IntPtr font, uint color, string text, int left, int top, int right, int bottom, uint align, bool thin = false)
    {
        SelectObject(hdc, font);
        var o = thin ? 1 : Math.Max(1, Sc(1.6));
        SetTextColor(hdc, Rgb(11, 15, 24));
        for (var dx = -o; dx <= o; dx++)
        {
            for (var dy = -o; dy <= o + 1; dy++)
            {
                if (Math.Abs(dx) != o && Math.Abs(dy) < o) continue; // only the ring (and one pixel of drop shadow)
                var r = new RECT { Left = left + dx, Top = top + dy, Right = right + dx, Bottom = bottom + dy };
                DrawTextW(hdc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP | align);
            }
        }
        SetTextColor(hdc, color);
        var rc = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        DrawTextW(hdc, text, -1, ref rc, DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP | align); // italic glyphs lean past the box
    }

    /// <summary>A treasure chest made of three shapes: body, rounded lid, latch.</summary>
    private static void Chest(IntPtr hdc, int x, int y, int w, int h, uint body, uint lid, uint latch, uint outline)
    {
        var pen = CreatePen(0, Math.Max(1, Sc(1.5)), outline);
        var oldPen = SelectObject(hdc, pen);
        var lidH = (int)(h * 0.42);
        var bLid = CreateSolidBrush(lid);
        var old = SelectObject(hdc, bLid);
        Chord(hdc, x, y, x + w, y + 2 * lidH, x + w, y + lidH, x, y + lidH);
        var bBody = CreateSolidBrush(body);
        SelectObject(hdc, bBody);
        RoundRect(hdc, x, y + lidH, x + w, y + h, Sc(4), Sc(4));
        var bLatch = CreateSolidBrush(latch);
        SelectObject(hdc, bLatch);
        var lw = Math.Max(3, w / 5);
        Rectangle(hdc, x + (w - lw) / 2, y + lidH - Sc(2), x + (w + lw) / 2, y + lidH + Math.Max(4, h / 4));
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(bLid);
        DeleteObject(bBody);
        DeleteObject(bLatch);
        DeleteObject(pen);
    }

    /// <summary>A five-pointed star (treasures), outlined like the text.</summary>
    private static void Star(IntPtr hdc, int cx, int cy, int r, uint fill, uint outline)
    {
        var pts = new POINT[10];
        for (var i = 0; i < 10; i++)
        {
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var rr = i % 2 == 0 ? r : r * 0.45;
            pts[i] = new POINT { X = cx + (int)Math.Round(rr * Math.Cos(a)), Y = cy + (int)Math.Round(rr * Math.Sin(a)) };
        }
        var pen = CreatePen(0, Math.Max(1, Sc(1.5)), outline);
        var brush = CreateSolidBrush(fill);
        var oldPen = SelectObject(hdc, pen);
        var old = SelectObject(hdc, brush);
        Polygon(hdc, pts, pts.Length);
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    private static void Disc(IntPtr hdc, int x, int y, int d, uint fill, uint outline, int penW)
    {
        var pen = CreatePen(0, penW, outline);
        var brush = CreateSolidBrush(fill);
        var oldPen = SelectObject(hdc, pen);
        var old = SelectObject(hdc, brush);
        Ellipse(hdc, x, y, x + d, y + d);
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    /// <summary>"Label  •  value" right-aligned: pale label, white value.</summary>
    private static int LabelValue(IntPtr hdc, string label, string value, int left, int right, int top, int height, uint valueColor)
    {
        if (value.Length == 0)
        {
            Outlined(hdc, _fontHudSmall, Rgb(255, 233, 166), label, left, top, right, top + height, DT_RIGHT, thin: true);
            return right - TextWidth(hdc, _fontHudSmall, label, out _);
        }
        var valueW = TextWidth(hdc, _fontHudSmall, value, out _);
        Outlined(hdc, _fontHudSmall, valueColor, value, left, top, right, top + height, DT_RIGHT, thin: true);
        var labelRight = right - valueW;
        Outlined(hdc, _fontHudSmall, Rgb(255, 233, 166), label + Sep, left, top, labelRight, top + height, DT_RIGHT, thin: true);
        return labelRight - TextWidth(hdc, _fontHudSmall, label + Sep, out _);
    }

    private static void PaintStyled(IntPtr hwnd, IntPtr hdc)
    {
        GetClientRect(hwnd, out var rc);
        var key = CreateSolidBrush(KeyColor);
        FillRect(hdc, ref rc, key);
        DeleteObject(key);
        SetBkMode(hdc, TRANSPARENT);
        var v = _view;
        var pad = Sc(8);
        var right = rc.Right - pad - Sc(12); // italic glyphs and their outline lean past the measured width
        var white = Rgb(255, 255, 255);
        var gold = Rgb(255, 210, 63);
        var ink = Rgb(11, 15, 24);

        // Unlocked: a dark backdrop to grab it, and the padlock (locked, only the figures remain, mouse goes through).
        if (!_locked)
        {
            var pen = CreatePen(0, 1, Rgb(58, 66, 86));
            var brush = CreateSolidBrush(Rgb(28, 34, 48));
            var oldPen = SelectObject(hdc, pen);
            var old = SelectObject(hdc, brush);
            RoundRect(hdc, 0, 0, rc.Right, rc.Bottom, Sc(12), Sc(12));
            SelectObject(hdc, old);
            SelectObject(hdc, oldPen);
            DeleteObject(brush);
            DeleteObject(pen);
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, Rgb(200, 200, 208));
            var lr = _lockRect;
            DrawTextW(hdc, "", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
        }

        var left = _lockRect.Right;
        var state = StateRow(v);
        var live = v.CurrentLive && state.Value.Length > 0;
        var totalText = L.N((int)Math.Clamp(GetShownTotal(), 0, int.MaxValue));
        // Reserve the width of the destination number, not the intermediate one: the live caption and rows must
        // stay perfectly still while the total counts up.
        var bigW = TextWidth(hdc, _fontHudBig, v.Total, out _);
        var iw = Sc(26);
        var ih = Sc(22);
        var stateText = state.Label + (state.Value.Length > 0 ? Sep + state.Value : "");
        var stateTextW = TextWidth(hdc, _fontHudSmall, stateText, out _);
        var rowParts = v.Rows.Select(r => new { Row = r, W = TextWidth(hdc, _fontHudSmall, r.Label + Sep + r.Value, out _) }).ToList();
        var contentW = iw + Sc(8) + bigW + Sc(30) + (state.Dot ? Sc(18) : 0) + stateTextW
            + rowParts.Sum(r => Sc(18) + r.W);
        var lineH = Math.Max(_hudBigH, _hudSmallH);
        var x = Math.Max(left, right - contentW);
        var smallTop = pad + Math.Max(0, (lineH - _hudSmallH) / 2);

        // One horizontal row, like the game's own HUD.
        Chest(hdc, x, pad + Math.Max(0, (lineH - ih) / 2), iw, ih, gold, Rgb(224, 165, 38), ink, ink);
        var totalX = x + iw + Sc(8);
        Outlined(hdc, _fontHudBig, live ? gold : white, totalText, totalX, pad, totalX + bigW + Sc(4), pad + lineH, DT_LEFT);

        // A short gold "+1" rises out of the total when the counter gains chests. It fades by moving away from the
        // baseline; the gap is reserved in RelayoutStyled so the live caption never moves while it is visible.
        var plus = GetPlusInfo();
        if (plus.Amount > 0)
        {
            var plusText = "+" + plus.Amount;
            var plusW = TextWidth(hdc, _fontHudSmall, plusText, out _);
            var rise = Sc(5) * (1 - plus.Progress);
            var plusTop = Math.Max(0, smallTop - (int)Math.Round(rise));
            Outlined(hdc, _fontHudSmall, Blend(gold, white, 0.35 * plus.Progress), plusText,
                totalX + bigW + Sc(2), plusTop, totalX + bigW + Sc(2) + plusW + Sc(2), plusTop + _hudSmallH, DT_LEFT, thin: true);
        }

        var stateX = totalX + bigW + Sc(30);
        if (state.Dot)
        {
            // A slow breath on the live dot, never a flashing effect.
            var pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1000.0 * Math.PI * 2 / 2.2);
            var dotD = Sc(9) + (int)Math.Round(Sc(2) * pulse);
            Disc(hdc, stateX + (Sc(18) - dotD) / 2, pad + lineH / 2 - dotD / 2, dotD,
                Blend(Rgb(255, 59, 48), Rgb(255, 143, 111), pulse * 0.65), ink, 1);
            stateX += Sc(18);
        }
        LabelValue(hdc, state.Label, state.Value, stateX, stateX + stateTextW, smallTop, _hudSmallH, live ? gold : white);

        var rowX = stateX + stateTextW + Sc(18);
        foreach (var row in rowParts)
        {
            LabelValue(hdc, row.Row.Label, row.Row.Value, rowX, rowX + row.W, smallTop, _hudSmallH, white);
            rowX += row.W + Sc(18);
        }
    }

    // ---- Motion ------------------------------------------------------------------------------

    private static long ParseTotal(string? text)
    {
        long value = 0;
        var found = false;
        foreach (var ch in text ?? "")
        {
            if (!char.IsDigit(ch)) continue;
            found = true;
            value = value * 10 + (ch - '0');
            if (value > 9_999_999_999L) return 9_999_999_999L;
        }
        return found ? value : 0;
    }

    private static void ResetTotalAnimation()
    {
        lock (_animationLock)
        {
            _hasTotal = false;
            _shownTotal = _targetTotal = _totalFrom = 0;
            _totalAnimating = false;
            _plusAmount = 0;
            _plusStartedMs = 0;
        }
    }

    private static void SetTotalTarget(long target)
    {
        target = Math.Max(0, target);
        lock (_animationLock)
        {
            if (!_hasTotal)
            {
                _hasTotal = true;
                _shownTotal = _targetTotal = target;
                _totalAnimating = false;
                _plusAmount = 0;
                return;
            }
            if (target == _targetTotal) return;

            var now = Environment.TickCount64;
            var current = AdvanceTotalLocked(now);
            if (target <= current)
            {
                _shownTotal = _targetTotal = target;
                _totalAnimating = false;
                _plusAmount = 0;
                return;
            }

            _totalFrom = current;
            _targetTotal = target;
            _totalStartedMs = now;
            _totalAnimating = true;
            _plusAmount = target - current;
            _plusStartedMs = now;
        }
    }

    private static long AdvanceTotalLocked(long now)
    {
        if (!_totalAnimating)
        {
            return _shownTotal;
        }
        var elapsed = now - _totalStartedMs;
        if (elapsed >= TotalAnimationMs)
        {
            _shownTotal = _targetTotal;
            _totalAnimating = false;
            return _shownTotal;
        }
        var progress = Math.Clamp(elapsed / TotalAnimationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _shownTotal = _totalFrom + (long)Math.Round((_targetTotal - _totalFrom) * eased);
        return _shownTotal;
    }

    private static long GetShownTotal()
    {
        lock (_animationLock)
        {
            return AdvanceTotalLocked(Environment.TickCount64);
        }
    }

    private static (long Amount, double Progress) GetPlusInfo()
    {
        lock (_animationLock)
        {
            if (_plusAmount <= 0) return (0, 1);
            var elapsed = Environment.TickCount64 - _plusStartedMs;
            if (elapsed >= PlusAnimationMs)
            {
                _plusAmount = 0;
                return (0, 1);
            }
            return (_plusAmount, Math.Clamp(elapsed / PlusAnimationMs, 0, 1));
        }
    }

    private static bool AnyMotion()
    {
        lock (_animationLock)
        {
            return _totalAnimating
                || (_plusAmount > 0 && Environment.TickCount64 - _plusStartedMs < PlusAnimationMs);
        }
    }

    private static void EnsureAnimationTimer(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || !Keyed)
        {
            StopAnimationTimer(hwnd);
            return;
        }
        var needed = _view.CurrentLive || _view.Badge != null || AnyMotion();
        if (!needed)
        {
            StopAnimationTimer(hwnd);
            return;
        }
        if (!_animationTimerRunning)
        {
            _animationTimerRunning = SetTimer(hwnd, (UIntPtr)AnimationTimerId, AnimationTimerMs, IntPtr.Zero) != UIntPtr.Zero;
        }
    }

    private static void StopAnimationTimer(IntPtr hwnd)
    {
        if (!_animationTimerRunning) return;
        _animationTimerRunning = false;
        if (hwnd != IntPtr.Zero)
        {
            KillTimer(hwnd, (UIntPtr)AnimationTimerId);
        }
    }

    private static void TickAnimation(IntPtr hwnd)
    {
        lock (_animationLock)
        {
            var now = Environment.TickCount64;
            AdvanceTotalLocked(now);
            if (_plusAmount > 0 && now - _plusStartedMs >= PlusAnimationMs)
            {
                _plusAmount = 0;
            }
        }
        InvalidateRect(hwnd, IntPtr.Zero, false);
        UpdateWindow(hwnd);
        EnsureAnimationTimer(hwnd);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20, HTCLIENT = 1;
    private const uint WM_SETCURSOR = 0x0020, WM_LBUTTONUP = 0x0202, WM_APP_LOCK = 0x8003, VK_L = 0x4C, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr IDC_HAND = new(32649);
    private const uint LWA_ALPHA = 0x2, LWA_COLORKEY = 0x1;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_NCHITTEST = 0x0084, WM_NCRBUTTONUP = 0x00A5,
        WM_TIMER = 0x0113, WM_EXITSIZEMOVE = 0x0232, WM_APP_TOGGLE = 0x8001, WM_PRINTCLIENT = 0x0318, WM_APP_RELAYOUT = 0x8002, WM_HOTKEY = 0x0312, WM_APP_APPEARANCE = 0x8004, WM_APP_STOP = 0x8005;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F;
    private const int HTCAPTION = 2;
    private const int TRANSPARENT = 1, LOGPIXELSY = 90;
    private const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_BOTTOM = 0x8, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_NOCLIP = 0x100;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr IDC_SIZEALL = new(32646);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool FrameRgn(IntPtr hdc, IntPtr rgn, IntPtr brush, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool Polygon(IntPtr hdc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool Chord(IntPtr hdc, int l, int t, int r, int b, int x1, int y1, int x2, int y2);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr hdc, ref RECT rect, IntPtr brush);
}
