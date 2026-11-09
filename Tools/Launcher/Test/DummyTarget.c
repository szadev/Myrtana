/* Launcher test target (fix-152): stands in for G3WorldPlayer.exe. Writes its command line and working directory
 * (fix-167) to dummy_args.txt next to itself, opens a window after 3 s (like a player loading), sets
 * G3WORLD_SPLASH_EVENT after its "first frame", stays 4 s and exits with code 7. Build: ..\build.cmd <out.exe> DummyTarget.c; run the launcher with
 * G3LAUNCHER_TARGET=<out.exe>. */
#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmdLine, int show)
{
    wchar_t path[MAX_PATH], name[128], cwd[MAX_PATH], *slash;
    FILE *f;
    HWND w;
    HANDLE ev;
    MSG m;
    DWORD until;
    (void)prev; (void)show;
    GetModuleFileNameW(NULL, path, MAX_PATH);
    slash = wcsrchr(path, L'\\');
    if (slash) wcscpy_s(slash + 1, MAX_PATH - (size_t)(slash + 1 - path), L"dummy_args.txt");
    if (_wfopen_s(&f, path, L"w, ccs=UTF-8") == 0 && f)
    {
        if (!GetCurrentDirectoryW(MAX_PATH, cwd)) cwd[0] = 0;
        fwprintf(f, L"cmd=[%s]\nargs=[%s]\ncwd=[%s]\nevent=[%s]\n", GetCommandLineW(), cmdLine, cwd,
                 GetEnvironmentVariableW(L"G3WORLD_SPLASH_EVENT", name, 128) ? name : L"(none)");
        fclose(f);
    }
    Sleep(3000);
    w = CreateWindowExW(0, L"STATIC", L"G3World launcher dummy target", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                        100, 100, 640, 360, NULL, NULL, inst, NULL);
    UpdateWindow(w);
    if (GetEnvironmentVariableW(L"G3WORLD_SPLASH_EVENT", name, 128) && (ev = OpenEventW(EVENT_MODIFY_STATE, FALSE, name)))
    {
        SetEvent(ev);
        CloseHandle(ev);
    }
    until = GetTickCount() + 4000;
    while ((int)(until - GetTickCount()) > 0)
    {
        while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) DispatchMessageW(&m);
        Sleep(15);
    }
    return 7;
}
