/*
 * G3World launcher (fix-152, Docs/game-start.md "Start-up splash"). Shipped as G3World.exe in the build folder's root; the
 * Unity player (G3WorldPlayer.exe + G3WorldPlayer_Data, GameData, Scenarios, splash.bmp, ...) is in its bin\ subfolder
 * (fix-167, Docs/build.md "Folder layout"). A build of the old flat layout (player next to the launcher) still starts.
 *
 * Gothic 3 (Engine.dll eCApplication::CreateSplashScreen -> eCSplash("splash.bmp", 0x808080)) shows <game>/splash.bmp
 * (300x300) in a borderless popup (WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW, layered with the colour key 0x808080)
 * centred on the primary screen: the grey vanishes and only the Gothic 3 logo floats on the desktop, unscaled, until the
 * game window is up. Unity has no window of its own before its player window, so this launcher does exactly that:
 *
 *   1. splash.bmp next to the player (else next to this exe) -> that window, at once (no file: no splash, the game still
 *      starts). logo-1: splash_hd.bmp (made at packaging from splash.bmp, Tools/RemasterLogo/logo_matte.py: the logo 4x
 *      upscaled with a soft, anti-aliased alpha matte instead of the colour key's hard grey-fringed edge; 32-bit BGRA,
 *      straight alpha) wins when it is there: premultiplied, area-scaled to G3's 300 px times the primary monitor's
 *      DPI scale and shown with per-pixel alpha (UpdateLayeredWindow, ULW_ALPHA);
 *   2. starts bin\G3WorldPlayer.exe (else G3WorldPlayer.exe next to this exe) in the player's folder as the working
 *      directory, with this exe's command line arguments passed through verbatim, with the environment variable
 *      G3WORLD_SPLASH_EVENT naming a manual-reset event;
 *   3. the player sets the event after its first frame (Myrtana.Unity.LauncherSplash); the splash closes then, when the
 *      player exits, or after SPLASH_TIMEOUT_MS at the latest;
 *   4. waits for the player and returns its exit code.
 *
 * Graphics API (fix-211, Docs/controls-options.md "Graphics API"; Myrtana.Session.LaunchConfig is the same rule in C#):
 * <LocalLow>\G3World\G3World\launch.cfg (the player's Application.persistentDataPath) holds "api=dx11" (default, also
 * with no file: -force-d3d11 is put before the forwarded arguments) or "api=dx12" (dx12-4: -force-d3d12 -force-gfx-mt,
 * D3D12 with graphics jobs off) or "api=dx12jobs" (experimental: -force-d3d12 with the build's graphics jobs, Split; it
 * crashed inside D3D12Core in build 26b and hung the GPU in 25b). Before a dx12 start the launcher writes launch.dx12 and removes it when the player exits with code 0;
 * a dx12 session ending with another code, or a marker left from the last start (the launcher did not survive it,
 * e.g. a reset after a GPU hang), rewrites launch.cfg to api=dx11 (the fallback).
 * dx12-3's "api=dx12mt" (D3D12 with graphics jobs off) is now the same as dx12; dx12jobs has dx12's marker and fallback.
 * GPU diagnostics (dx12-3, Docs/performance.md "D3D12 GPU hang"): -g3gpudiag on the launcher's command line or
 * "gpudiag=1" in launch.cfg adds -force-d3d12-debug (Unity's D3D12 debug layer + DRED: the hang names the command) on a
 * D3D12 start and passes -g3gpudiag to the player (per-frame GPU step log, gpudiag.log); -g3gpudiag-gbv / "gpudiag=gbv"
 * also -force-d3d12-debug-gbv (GPU-based validation, very slow). Needs the Windows "Graphics Tools" optional feature.
 *
 * Pure Win32 + static CRT: no runtime to install. Build: Tools/Launcher/build.cmd (MSVC via vswhere, else MinGW gcc).
 * G3LAUNCHER_TARGET (environment) replaces the player path; only for testing the launcher with a dummy program.
 * Paths are absolute from this exe's own path (GetModuleFileName): the launcher works from any working directory.
 */
#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <shlobj.h>
#include <knownfolders.h>

#define PLAYER_EXE L"G3WorldPlayer.exe"
#define BIN_DIR L"bin\\"
#define SPLASH_BMP L"splash.bmp"
#define SPLASH_HD L"splash_hd.bmp" /* logo-1: 32-bit, alpha matte, SPLASH_HD_SCALE x G3's size */
#ifndef SPLASH_HD_SCALE
#define SPLASH_HD_SCALE 4
#endif
#define EVENT_ENV L"G3WORLD_SPLASH_EVENT"
#define TARGET_ENV L"G3LAUNCHER_TARGET"
#define SPLASH_KEY RGB(0x80, 0x80, 0x80)
#define SPLASH_TIMEOUT_MS 60000u
#define DATA_DIR L"\\G3World\\G3World\\" /* companyName, productName (ProjectSettings) under LocalLow */
#define CONFIG_FILE L"launch.cfg"
#define MARKER_FILE L"launch.dx12"
#define FORCE_DX11 L"-force-d3d11"
#define FORCE_DX12 L"-force-d3d12 -force-gfx-mt" /* dx12-4: graphics jobs off */
#define FORCE_DX12_JOBS L"-force-d3d12"          /* dx12-4: the build's graphics jobs (Split), experimental */
#define GPU_DIAG L"-g3gpudiag"
#define GPU_DIAG_GBV L"-g3gpudiag-gbv"
#define D3D12_DEBUG L"-force-d3d12-debug"
#define D3D12_GBV L"-force-d3d12-debug-gbv"
#define API_DX11 0
#define API_DX12 1
#define API_DX12_JOBS 2

static HBITMAP g_bitmap;
static int g_width, g_height;

static LRESULT CALLBACK SplashProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);
        if (g_bitmap) /* the colour-key splash; the alpha splash is drawn by UpdateLayeredWindow, never painted */
        {
            HDC mem = CreateCompatibleDC(dc);
            HGDIOBJ old = SelectObject(mem, g_bitmap);
            BitBlt(dc, 0, 0, g_width, g_height, mem, 0, 0, SRCCOPY);
            SelectObject(mem, old);
            DeleteDC(mem);
        }
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_ERASEBKGND:
        return 1;
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE; /* a click on the logo must not take the focus from the starting game */
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

/* Physical pixels: the bitmap is shown 1:1 on a scaled desktop too (no DPI virtualisation stretch). */
static void BeDpiAware(void)
{
    typedef BOOL(WINAPI * SetContextFn)(HANDLE);
    HMODULE user = GetModuleHandleW(L"user32.dll");
    SetContextFn setContext = user ? (SetContextFn)(void *)GetProcAddress(user, "SetProcessDpiAwarenessContext") : NULL;
    if (setContext && setContext((HANDLE)-4 /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */)) return;
    SetProcessDPIAware();
}

/* The primary monitor's DPI (Shcore GetDpiForMonitor when there, else the screen DC's); 96 = 100 %. */
static int PrimaryDpi(void)
{
    typedef HRESULT(WINAPI * DpiFn)(HMONITOR, int, UINT *, UINT *);
    HMODULE shcore = LoadLibraryW(L"shcore.dll");
    DpiFn getDpi = shcore ? (DpiFn)(void *)GetProcAddress(shcore, "GetDpiForMonitor") : NULL;
    UINT dx = 0, dy = 0;
    int dpi = 0;
    POINT origin = {0, 0};
    if (getDpi && SUCCEEDED(getDpi(MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY), 0 /* MDT_EFFECTIVE_DPI */, &dx, &dy)))
        dpi = (int)dx;
    if (shcore) FreeLibrary(shcore);
    if (dpi <= 0)
    {
        HDC screen = GetDC(NULL);
        dpi = screen ? GetDeviceCaps(screen, LOGPIXELSX) : 96;
        if (screen) ReleaseDC(NULL, screen);
    }
    return dpi < 96 ? 96 : dpi;
}

/* Box (area) filter: destination pixel d of n covers source [d*len/n, (d+1)*len/n); its pixels i0..i1-1, the first and
   the last only partly (weights first / last), the others fully. */
static void Span(int d, int n, int len, int *i0, int *i1, float *first, float *last)
{
    double a = (double)d * len / n, b = (double)(d + 1) * len / n;
    *i0 = (int)a;
    *i1 = (int)b;
    if (*i1 < len && (double)*i1 < b) (*i1)++;
    if (*i1 > len) *i1 = len;
    if (*i1 <= *i0) *i1 = *i0 + 1;
    *first = (float)(*i0 + 1 - a);
    *last = (float)(b - (*i1 - 1));
    if (*i1 - *i0 == 1) *first = *last = (float)(b - a);
}

/* logo-1: splash_hd.bmp (32-bit BGRA, straight alpha, bottom-up or top-down) -> a premultiplied top-down DIB, area-scaled
   to G3's size (the source / SPLASH_HD_SCALE) times the primary monitor's DPI scale, never above the source. */
static HBITMAP LoadAlphaSplash(const wchar_t *path, int *outW, int *outH)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    LARGE_INTEGER size;
    BYTE *file = NULL, *bits = NULL;
    float *rows = NULL;
    HBITMAP dib = NULL;
    DWORD got = 0, off, compression;
    int sw, sh, topDown, dw, dh, dpi, x, y, c, i, i0, i1;
    float first, last;
    BITMAPINFO bi;
    if (h == INVALID_HANDLE_VALUE) return NULL;
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 54 || size.QuadPart > 64 * 1024 * 1024)
    {
        CloseHandle(h);
        return NULL;
    }
    file = (BYTE *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)size.QuadPart);
    if (file && !ReadFile(h, file, (DWORD)size.QuadPart, &got, NULL)) got = 0;
    CloseHandle(h);
    if (!file || got != (DWORD)size.QuadPart || file[0] != 'B' || file[1] != 'M') goto done;
    memcpy(&off, file + 10, 4);
    memcpy(&sw, file + 18, 4);
    memcpy(&sh, file + 22, 4);
    memcpy(&compression, file + 30, 4);
    topDown = sh < 0;
    if (topDown) sh = -sh;
    if (file[28] != 32 || file[29] != 0 || (compression != BI_RGB && compression != BI_BITFIELDS)) goto done;
    if (sw <= 0 || sh <= 0 || sw > 8192 || sh > 8192 || (ULONGLONG)off + (ULONGLONG)sw * (ULONGLONG)sh * 4 > (ULONGLONG)got) goto done;

    dpi = PrimaryDpi();
    dw = MulDiv(sw / SPLASH_HD_SCALE, dpi, 96);
    dh = MulDiv(sh / SPLASH_HD_SCALE, dpi, 96);
    if (dw > sw || dh > sh)
    {
        dw = sw;
        dh = sh;
    }
    if (dw < 1 || dh < 1) goto done;

    /* horizontal pass: premultiplied float rows, sh x dw */
    rows = (float *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)sh * (SIZE_T)dw * 4 * sizeof(float));
    if (!rows) goto done;
    for (y = 0; y < sh; y++)
    {
        const BYTE *src = file + off + (SIZE_T)(topDown ? y : sh - 1 - y) * (SIZE_T)sw * 4;
        float *dst = rows + (SIZE_T)y * (SIZE_T)dw * 4;
        for (x = 0; x < dw; x++)
        {
            float acc[4] = {0, 0, 0, 0}, wsum = 0;
            Span(x, dw, sw, &i0, &i1, &first, &last);
            for (i = i0; i < i1; i++)
            {
                const BYTE *p = src + (SIZE_T)i * 4;
                float wgt = i == i0 ? first : i == i1 - 1 ? last : 1.0f, a = p[3] * wgt / 255.0f;
                acc[0] += p[0] * a;
                acc[1] += p[1] * a;
                acc[2] += p[2] * a;
                acc[3] += p[3] * wgt;
                wsum += wgt;
            }
            for (c = 0; c < 4; c++) dst[x * 4 + c] = acc[c] / wsum;
        }
    }

    /* vertical pass into the DIB (BGRA, premultiplied: what UpdateLayeredWindow's AC_SRC_ALPHA wants) */
    ZeroMemory(&bi, sizeof bi);
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = dw;
    bi.bmiHeader.biHeight = -dh;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    dib = CreateDIBSection(NULL, &bi, DIB_RGB_COLORS, (void **)&bits, NULL, 0);
    if (!dib || !bits)
    {
        if (dib) DeleteObject(dib);
        dib = NULL;
        goto done;
    }
    for (y = 0; y < dh; y++)
    {
        Span(y, dh, sh, &i0, &i1, &first, &last);
        for (x = 0; x < dw; x++)
        {
            float acc[4] = {0, 0, 0, 0}, wsum = 0;
            BYTE *q = bits + ((SIZE_T)y * (SIZE_T)dw + (SIZE_T)x) * 4;
            for (i = i0; i < i1; i++)
            {
                const float *p = rows + ((SIZE_T)i * (SIZE_T)dw + (SIZE_T)x) * 4;
                float wgt = i == i0 ? first : i == i1 - 1 ? last : 1.0f;
                for (c = 0; c < 4; c++) acc[c] += p[c] * wgt;
                wsum += wgt;
            }
            for (c = 0; c < 4; c++)
            {
                float v = acc[c] / wsum + 0.5f;
                q[c] = (BYTE)(v < 0.0f ? 0.0f : v > 255.0f ? 255.0f : v);
            }
            for (c = 0; c < 3; c++)
                if (q[c] > q[3]) q[c] = q[3];
        }
    }
    *outW = dw;
    *outH = dh;
done:
    if (rows) HeapFree(GetProcessHeap(), 0, rows);
    if (file) HeapFree(GetProcessHeap(), 0, file);
    return dib;
}

/* The borderless topmost layered popup of w x h, centred on the primary screen like eCSplash (not shown yet). */
static HWND CreateSplashWindow(HINSTANCE inst, int w, int h)
{
    WNDCLASSW wc;
    ZeroMemory(&wc, sizeof wc);
    wc.lpfnWndProc = SplashProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_APPSTARTING);
    wc.lpszClassName = L"G3WorldBanner";
    RegisterClassW(&wc);
    return CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE, wc.lpszClassName, L"Banner",
                           WS_POPUP, (GetSystemMetrics(SM_CXSCREEN) - w) / 2, (GetSystemMetrics(SM_CYSCREEN) - h) / 2, w, h,
                           NULL, NULL, inst, NULL);
}

/* logo-1: the anti-aliased logo with per-pixel alpha; NULL when splash_hd.bmp is missing or unusable. */
static HWND ShowAlphaSplash(HINSTANCE inst, const wchar_t *path)
{
    int w = 0, h = 0;
    HWND hwnd;
    HBITMAP dib = LoadAlphaSplash(path, &w, &h);
    HDC screen, mem;
    HGDIOBJ old;
    POINT src = {0, 0}, pos;
    SIZE sz;
    BLENDFUNCTION blend;
    BOOL ok = FALSE;
    if (!dib) return NULL;
    hwnd = CreateSplashWindow(inst, w, h);
    if (!hwnd)
    {
        DeleteObject(dib);
        return NULL;
    }
    pos.x = (GetSystemMetrics(SM_CXSCREEN) - w) / 2;
    pos.y = (GetSystemMetrics(SM_CYSCREEN) - h) / 2;
    sz.cx = w;
    sz.cy = h;
    blend.BlendOp = AC_SRC_OVER;
    blend.BlendFlags = 0;
    blend.SourceConstantAlpha = 255;
    blend.AlphaFormat = AC_SRC_ALPHA;
    screen = GetDC(NULL);
    mem = CreateCompatibleDC(screen);
    if (mem)
    {
        old = SelectObject(mem, dib);
        ok = UpdateLayeredWindow(hwnd, screen, &pos, &sz, mem, &src, 0, &blend, ULW_ALPHA);
        SelectObject(mem, old);
        DeleteDC(mem);
    }
    if (screen) ReleaseDC(NULL, screen);
    DeleteObject(dib); /* the layered window keeps its own copy */
    if (!ok)
    {
        DestroyWindow(hwnd);
        return NULL;
    }
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    return hwnd;
}

/* G3's way: splash.bmp 1:1 with the colour key 0x808080 (the fallback without splash_hd.bmp). */
static HWND ShowSplash(HINSTANCE inst, const wchar_t *bmpPath)
{
    BITMAP info;
    HWND hwnd;
    g_bitmap = (HBITMAP)LoadImageW(NULL, bmpPath, IMAGE_BITMAP, 0, 0, LR_LOADFROMFILE | LR_CREATEDIBSECTION);
    if (!g_bitmap) return NULL;
    GetObjectW(g_bitmap, sizeof info, &info);
    g_width = info.bmWidth;
    g_height = info.bmHeight < 0 ? -info.bmHeight : info.bmHeight;
    hwnd = CreateSplashWindow(inst, g_width, g_height);
    if (!hwnd) return NULL;
    SetLayeredWindowAttributes(hwnd, SPLASH_KEY, 0, LWA_COLORKEY);
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd);
    return hwnd;
}

static void Pump(void)
{
    MSG m;
    while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
}

/* The command line after argv[0] (CommandLineToArgvW rules for the program name: quoted, or up to the first blank). */
static const wchar_t *ArgsTail(const wchar_t *cmd)
{
    if (*cmd == L'"')
    {
        cmd++;
        while (*cmd && *cmd != L'"') cmd++;
        if (*cmd == L'"') cmd++;
    }
    else
        while (*cmd && *cmd != L' ' && *cmd != L'\t') cmd++;
    while (*cmd == L' ' || *cmd == L'\t') cmd++;
    return cmd;
}

static int FileExists(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

/* <dir><BIN_DIR>G3WorldPlayer.exe, else (the flat layout before fix-167) <dir>G3WorldPlayer.exe */
static void FindPlayer(const wchar_t *dir, wchar_t *target)
{
    swprintf(target, MAX_PATH, L"%s%s%s", dir, BIN_DIR, PLAYER_EXE);
    if (!FileExists(target)) swprintf(target, MAX_PATH, L"%s%s", dir, PLAYER_EXE);
}

/* <LocalLow>\G3World\G3World\ (created); 0 when the known folder is unavailable */
static int DataDir(wchar_t *out)
{
    PWSTR low = NULL;
    wchar_t *p;
    if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppDataLow, 0, NULL, &low)) || !low)
    {
        if (low) CoTaskMemFree(low);
        return 0;
    }
    swprintf(out, MAX_PATH, L"%s%s", low, DATA_DIR);
    CoTaskMemFree(low);
    for (p = out + 3; *p; p++) /* the missing folders, as Unity would create them */
        if (*p == L'\\')
        {
            *p = 0;
            CreateDirectoryW(out, NULL);
            *p = L'\\';
        }
    return 1;
}

/* A small ASCII file (launch.cfg / the marker) into buf; 0 when it does not exist. */
static int ReadSmall(const wchar_t *path, char *buf, DWORD size)
{
    DWORD got = 0;
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    if (!ReadFile(h, buf, size - 1, &got, NULL)) got = 0;
    buf[got] = 0;
    CloseHandle(h);
    return 1;
}

static void WriteSmall(const wchar_t *path, const char *text)
{
    DWORD put;
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    WriteFile(h, text, (DWORD)strlen(text), &put, NULL);
    CloseHandle(h);
}

/* The value of the first "key=value" line (key case-insensitive, blanks trimmed, # and ; comments) into out; 0 if none. */
static int CfgValue(const char *text, const char *key, char *out, size_t outSize)
{
    size_t keyLen = strlen(key);
    const char *line = text;
    if ((unsigned char)line[0] == 0xEF && (unsigned char)line[1] == 0xBB && (unsigned char)line[2] == 0xBF) line += 3;
    while (*line)
    {
        const char *end = strchr(line, '\n'), *k = line, *v;
        size_t n;
        if (!end) end = line + strlen(line);
        while (k < end && (*k == ' ' || *k == '\t')) k++;
        if (k + keyLen < end && _strnicmp(k, key, keyLen) == 0)
        {
            v = k + keyLen;
            while (v < end && (*v == ' ' || *v == '\t')) v++;
            if (v < end && *v == '=')
            {
                v++;
                while (v < end && (*v == ' ' || *v == '\t')) v++;
                n = 0;
                while (v + n < end && v[n] != '\r' && v[n] != ' ' && v[n] != '\t' && n + 1 < outSize) n++;
                memcpy(out, v, n);
                out[n] = 0;
                return 1;
            }
        }
        line = *end ? end + 1 : end;
    }
    return 0;
}

static wchar_t g_cfg[MAX_PATH], g_marker[MAX_PATH];

static void FallBackToDx11(const char *why)
{
    char text[512];
    sprintf_s(text, sizeof text, "# G3World launcher settings (Options > Video > Graphics API; applies at the next start)\n"
                                 "# fell back from DirectX 12: %s\n"
                                 "api=dx11\n", why);
    WriteSmall(g_cfg, text);
    DeleteFileW(g_marker);
}

static int g_cfgDiag; /* dx12-3: launch.cfg gpudiag: 0 off, 1 debug layer, 2 + GPU-based validation */

/* fix-211: API_DX12 / API_DX12_JOBS when this start uses D3D12 (api=dx12 or dx12mt / dx12jobs and the last D3D12 session did not
   fail); else API_DX11. */
static int ChooseDx12(void)
{
    wchar_t dir[MAX_PATH];
    char text[1024], value[32];
    int dx12 = API_DX11;
    if (!DataDir(dir)) return API_DX11;
    swprintf(g_cfg, MAX_PATH, L"%s%s", dir, CONFIG_FILE);
    swprintf(g_marker, MAX_PATH, L"%s%s", dir, MARKER_FILE);
    if (ReadSmall(g_cfg, text, sizeof text))
    {
        if (CfgValue(text, "api", value, sizeof value))
        {
            if (_stricmp(value, "dx12") == 0 || _stricmp(value, "d3d12") == 0 || strcmp(value, "12") == 0 ||
                _stricmp(value, "dx12mt") == 0 || _stricmp(value, "d3d12mt") == 0) dx12 = API_DX12;
            else if (_stricmp(value, "dx12jobs") == 0 || _stricmp(value, "d3d12jobs") == 0) dx12 = API_DX12_JOBS;
        }
        if (CfgValue(text, "gpudiag", value, sizeof value))
        {
            if (_stricmp(value, "gbv") == 0) g_cfgDiag = 2;
            else if (strcmp(value, "1") == 0 || _stricmp(value, "on") == 0 || _stricmp(value, "true") == 0 || _stricmp(value, "yes") == 0) g_cfgDiag = 1;
        }
    }
    if (dx12 == API_DX11)
    {
        DeleteFileW(g_marker);
        return 0;
    }
    if (ReadSmall(g_marker, text, sizeof text))
    {
        FallBackToDx11("the last DirectX 12 session did not end normally");
        return 0;
    }
    WriteSmall(g_marker, "dx12 session running\n");
    return dx12;
}

/* dx12-3: 1 when the forwarded arguments hold this switch as a whole word (case-sensitive, as typed). */
static int HasArg(const wchar_t *args, const wchar_t *sw)
{
    size_t n = wcslen(sw);
    const wchar_t *p = args;
    while ((p = wcsstr(p, sw)) != NULL)
    {
        int startOk = p == args || p[-1] == L' ' || p[-1] == L'\t' || p[-1] == L'"';
        wchar_t end = p[n];
        if (startOk && (end == 0 || end == L' ' || end == L'\t' || end == L'"')) return 1;
        p += n;
    }
    return 0;
}

static void Fail(const wchar_t *what, const wchar_t *path)
{
    wchar_t text[1200];
    swprintf(text, sizeof text / sizeof text[0], L"%s\n%s\n\nWindows error %lu.", what, path, GetLastError());
    MessageBoxW(NULL, text, L"Gothic 3 Remaster", MB_ICONERROR | MB_OK);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmdLine, int show)
{
    wchar_t dir[MAX_PATH], target[MAX_PATH], workDir[MAX_PATH], bmp[MAX_PATH], eventName[64], pre[160];
    wchar_t *slash, *line;
    const wchar_t *args;
    size_t lineLen;
    int dx12;
    HWND splash;
    HANDLE ready, waits[2];
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    DWORD started, exitCode = 1;
    (void)prev; (void)cmdLine; (void)show;

    BeDpiAware();
    if (!GetModuleFileNameW(NULL, dir, MAX_PATH)) return 1;
    slash = wcsrchr(dir, L'\\');
    if (slash) slash[1] = 0;

    if (!GetEnvironmentVariableW(TARGET_ENV, target, MAX_PATH)) FindPlayer(dir, target);
    /* the player's folder: its working directory and where splash.bmp is (fix-167) */
    wcscpy_s(workDir, MAX_PATH, target);
    slash = wcsrchr(workDir, L'\\');
    if (!slash) slash = wcsrchr(workDir, L'/');
    if (slash) slash[1] = 0; else wcscpy_s(workDir, MAX_PATH, dir);

    swprintf(bmp, MAX_PATH, L"%s%s", workDir, SPLASH_HD); /* logo-1: the anti-aliased logo first */
    if (!FileExists(bmp)) swprintf(bmp, MAX_PATH, L"%s%s", dir, SPLASH_HD);
    splash = ShowAlphaSplash(inst, bmp);
    if (!splash)
    {
        swprintf(bmp, MAX_PATH, L"%s%s", workDir, SPLASH_BMP);
        if (!FileExists(bmp)) swprintf(bmp, MAX_PATH, L"%s%s", dir, SPLASH_BMP);
        splash = ShowSplash(inst, bmp);
    }
    Pump(); /* the logo is on the desktop before the player is even created */

    swprintf(eventName, 64, L"Local\\G3WorldSplash-%lu", GetCurrentProcessId());
    ready = CreateEventW(NULL, TRUE, FALSE, eventName);
    SetEnvironmentVariableW(EVENT_ENV, ready ? eventName : NULL); /* inherited by the player */

    dx12 = ChooseDx12();
    args = ArgsTail(GetCommandLineW());
    /* the arguments put before the forwarded ones: the API, then the GPU diagnostics (dx12-3) */
    {
        int gbv = HasArg(args, GPU_DIAG_GBV) || g_cfgDiag == 2;
        int diag = gbv || HasArg(args, GPU_DIAG) || g_cfgDiag == 1;
        pre[0] = 0;
        if (dx12 == API_DX11) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, FORCE_DX11); }
        else if (dx12 == API_DX12) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, FORCE_DX12); }
        else if (dx12 == API_DX12_JOBS) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, FORCE_DX12_JOBS); }
        if (diag && dx12 != API_DX11) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, D3D12_DEBUG); }
        if (gbv && dx12 != API_DX11) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, D3D12_GBV); }
        if (diag && !HasArg(args, GPU_DIAG)) { wcscat_s(pre, 160, L" "); wcscat_s(pre, 160, GPU_DIAG); }
    }
    lineLen = wcslen(target) + wcslen(args) + wcslen(pre) + 6;
    line = (wchar_t *)HeapAlloc(GetProcessHeap(), 0, lineLen * sizeof(wchar_t));
    if (!line) return 1;
    swprintf(line, lineLen, L"\"%s\"%s%s%s", target, pre, *args ? L" " : L"", args);

    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    ZeroMemory(&pi, sizeof pi);
    if (!CreateProcessW(target, line, NULL, NULL, FALSE, 0, NULL, workDir, &si, &pi))
    {
        if (splash) DestroyWindow(splash);
        if (dx12) DeleteFileW(g_marker); /* not a D3D12 failure */
        Fail(L"The game could not be started:", target);
        return 1;
    }
    HeapFree(GetProcessHeap(), 0, line);
    AllowSetForegroundWindow(pi.dwProcessId);
    CloseHandle(pi.hThread);

    /* splash until the player's first frame, the player's exit or the timeout */
    waits[0] = pi.hProcess;
    waits[1] = ready;
    started = GetTickCount();
    while (splash)
    {
        DWORD elapsed = GetTickCount() - started, r;
        if (elapsed >= SPLASH_TIMEOUT_MS) break;
        r = MsgWaitForMultipleObjects(ready ? 2 : 1, waits, FALSE, SPLASH_TIMEOUT_MS - elapsed, QS_ALLINPUT);
        if (r == WAIT_OBJECT_0 + (ready ? 2u : 1u)) { Pump(); continue; }
        break; /* player gone, player ready, timeout or a failed wait */
    }
    if (splash) DestroyWindow(splash);
    if (g_bitmap) DeleteObject(g_bitmap);
    if (ready) CloseHandle(ready);

    WaitForSingleObject(pi.hProcess, INFINITE);
    if (!GetExitCodeProcess(pi.hProcess, &exitCode)) exitCode = 1;
    if (dx12) /* fix-211: any failed D3D12 session falls back */
    {
        if (exitCode == 0) DeleteFileW(g_marker);
        else FallBackToDx11("the DirectX 12 session ended with an error");
    }
    CloseHandle(pi.hProcess);
    return (int)exitCode;
}
