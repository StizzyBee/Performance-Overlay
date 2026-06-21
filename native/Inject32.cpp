// PerfOverlayInject32.exe  —  tiny 32-bit injector helper.
//
// An x64 process can't easily resolve the 32-bit LoadLibraryW address inside a WOW64
// game, so the main (x64) app shells out to this same-bitness helper to inject the
// 32-bit hook DLL.  Usage:  PerfOverlayInject32.exe <pid> "<dllPath>"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdlib.h>

int main()
{
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 3) return 1;

    DWORD pid = (DWORD)_wtoi(argv[1]);
    const wchar_t* dll = argv[2];
    if (pid == 0 || !dll || !*dll) return 1;

    HANDLE proc = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
    if (!proc) return 2;

    int rc = 0;
    SIZE_T bytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(proc, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { CloseHandle(proc); return 3; }

    if (!WriteProcessMemory(proc, remote, dll, bytes, nullptr)) { rc = 4; }
    else
    {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        FARPROC loadLib = GetProcAddress(k32, "LoadLibraryW");
        HANDLE th = CreateRemoteThread(proc, nullptr, 0, (LPTHREAD_START_ROUTINE)loadLib, remote, 0, nullptr);
        if (!th) rc = 5;
        else { WaitForSingleObject(th, 5000); CloseHandle(th); }
    }

    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);
    return rc;
}
