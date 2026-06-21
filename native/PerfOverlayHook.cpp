// PerfOverlayHook.dll
//
// Injected into a (no-anti-cheat) game to record frame-present timestamps and publish
// them to the Performance Overlay app via a named shared-memory section.
//
// Design goal: NEVER crash the host game. We only ever (a) swap a COM vtable function
// pointer (DXGI Present) and (b) swap Import-Address-Table entries (OpenGL / Vulkan).
// No instruction-stream patching, so the worst failure mode is "no data captured",
// not a crash.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <timeapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <stdio.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "winmm.lib")

// ----- shared memory layout (must match C# SharedHeader) -----
static const unsigned int RING_CAP = 2048;
#pragma pack(push, 1)
struct SharedData
{
    volatile LONG64 presentCount; // total presents recorded since start
    LONG64          qpcFreq;      // QueryPerformanceFrequency
    unsigned int    apiMask;      // bit0 = DXGI, bit1 = OpenGL, bit2 = Vulkan
    unsigned int    cap;          // ring capacity
    LONG64          timestamps[RING_CAP]; // ring of QPC counts
    volatile LONG   fpsCap;       // app-controlled FPS limit (0 = uncapped)
};
#pragma pack(pop)

static SharedData* g_shared = nullptr;
static HANDLE      g_map = nullptr;
static LONG64      g_qpcFreq = 0;

static volatile LONG64 g_lastPresentQpc = 0;

// ----- frame limiter -----
// Paces present calls to the app-set FPS cap. Uses a fixed-origin accumulator (schedule each
// frame at startTime + n*period) so per-frame render/present time is absorbed into the period
// and the rate lands exactly on the target — no drift. Hybrid sleep+spin for precision; runs
// on the game's render thread, like RivaTuner's limiter.
static double g_nextFrameQpc = 0;
static LONG   g_lastCap = 0;

static void ApplyCap()
{
    if (!g_shared || g_qpcFreq <= 0) return;
    LONG cap = g_shared->fpsCap;
    if (cap <= 0) { g_nextFrameQpc = 0; g_lastCap = 0; return; }

    double period = (double)g_qpcFreq / (double)cap; // QPC ticks per frame
    LARGE_INTEGER nowL; QueryPerformanceCounter(&nowL);
    double now = (double)nowL.QuadPart;

    // Reseed the schedule on the first frame or whenever the cap changes.
    if (g_nextFrameQpc == 0 || cap != g_lastCap)
    {
        g_lastCap = cap;
        g_nextFrameQpc = now + period;
        return;
    }

    // Self-heal: if the schedule drifted out of a sane window (behind, or >2 frames ahead),
    // reseed instead of waiting a wrong amount. This keeps the cap exact at any rate and
    // avoids the runaway/long-sleep seen at very short periods.
    // Reseed only on a real desync (more than a full frame behind, or > 2 frames ahead). This
    // also hard-bounds the wait below to <= ~2 frames, so a bad schedule can never make the
    // hook sleep for a long time and stall the game.
    double ahead = g_nextFrameQpc - now;
    if (ahead < -period || ahead > 2.0 * period)
    {
        g_nextFrameQpc = now + period;
        return;
    }

    // One coarse sleep stopping ~1.2ms short, then a precise busy-spin to the exact target.
    // (When the frame ran slightly long, remainMs <= 0 and we just fall through to the spin,
    // which exits immediately — the accumulator catches up without drifting the cap.)
    double remainMs = ahead * 1000.0 / g_qpcFreq;
    int sleepMs = (int)(remainMs - 1.2);
    if (sleepMs > 0) Sleep((DWORD)sleepMs);
    for (;;)
    {
        QueryPerformanceCounter(&nowL);
        if ((double)nowL.QuadPart >= g_nextFrameQpc) break;
        YieldProcessor();
    }

    g_nextFrameQpc += period;
}

static void RecordPresent(unsigned int apiBit)
{
    if (!g_shared) return;
    LARGE_INTEGER now; QueryPerformanceCounter(&now);

    // Collapse near-simultaneous double-fires (e.g. a game that triggers both Present and
    // Present1, or two swapchains) so we never over-count. 50µs is far below any real frame
    // interval (even 1000 FPS = 1000µs apart).
    LONG64 last = g_lastPresentQpc;
    if (last != 0 && g_qpcFreq > 0 && (double)(now.QuadPart - last) * 1000.0 / g_qpcFreq < 0.05)
        return;
    g_lastPresentQpc = now.QuadPart;

    LONG64 idx = InterlockedIncrement64(&g_shared->presentCount) - 1;
    g_shared->timestamps[idx % RING_CAP] = now.QuadPart;
    g_shared->apiMask |= apiBit;
}

// ===================== DXGI (D3D10/11/12) =====================

typedef HRESULT(STDMETHODCALLTYPE* PFN_Present)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT(STDMETHODCALLTYPE* PFN_Present1)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
static PFN_Present g_origPresent = nullptr;
static PFN_Present1 g_origPresent1 = nullptr;

static HRESULT STDMETHODCALLTYPE Hooked_Present(IDXGISwapChain* sc, UINT sync, UINT flags)
{
    ApplyCap();
    RecordPresent(1);
    return g_origPresent(sc, sync, flags);
}

static HRESULT STDMETHODCALLTYPE Hooked_Present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p)
{
    ApplyCap();
    RecordPresent(1);
    return g_origPresent1(sc, sync, flags, p);
}

static void HookDXGI()
{
    if (g_origPresent) return;

    // dummy window
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"PerfOverlayDummyWnd";
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW,
                                0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd) return;

    DXGI_SWAP_CHAIN_DESC scd = {};
    scd.BufferCount = 1;
    scd.BufferDesc.Width = 64;
    scd.BufferDesc.Height = 64;
    scd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.OutputWindow = hwnd;
    scd.SampleDesc.Count = 1;
    scd.Windowed = TRUE;

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_DRIVER_TYPE types[] = { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP };
    HRESULT hr = E_FAIL;
    for (auto t : types)
    {
        hr = D3D11CreateDeviceAndSwapChain(nullptr, t, nullptr, 0, nullptr, 0,
                                           D3D11_SDK_VERSION, &scd, &sc, &dev, nullptr, &ctx);
        if (SUCCEEDED(hr)) break;
    }

    if (SUCCEEDED(hr) && sc)
    {
        void** vtbl = *reinterpret_cast<void***>(sc);
        // IDXGISwapChain::Present is vtable index 8 (covers D3D10/11/12 swapchains).
        DWORD oldProt;
        if (VirtualProtect(&vtbl[8], sizeof(void*), PAGE_READWRITE, &oldProt))
        {
            g_origPresent = reinterpret_cast<PFN_Present>(vtbl[8]);
            vtbl[8] = reinterpret_cast<void*>(&Hooked_Present);
            VirtualProtect(&vtbl[8], sizeof(void*), oldProt, &oldProt);
        }

        // Many DX12 / flip-model games present via IDXGISwapChain1::Present1 (vtable index 22).
        // Only hook it if the swapchain really is an IDXGISwapChain1 (so the vtable is that long).
        IDXGISwapChain1* sc1 = nullptr;
        if (SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&sc1))) && sc1)
        {
            void** vt1 = *reinterpret_cast<void***>(sc1);
            if (VirtualProtect(&vt1[22], sizeof(void*), PAGE_READWRITE, &oldProt))
            {
                g_origPresent1 = reinterpret_cast<PFN_Present1>(vt1[22]);
                vt1[22] = reinterpret_cast<void*>(&Hooked_Present1);
                VirtualProtect(&vt1[22], sizeof(void*), oldProt, &oldProt);
            }
            sc1->Release();
        }
    }

    if (sc) sc->Release();
    if (ctx) ctx->Release();
    if (dev) dev->Release();
    DestroyWindow(hwnd);
}

// ===================== IAT hooking helper =====================

static void* HookIATInModule(HMODULE mod, const char* dllName, const char* funcName, void* hook)
{
    BYTE* base = reinterpret_cast<BYTE*>(mod);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
    auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return nullptr;

    auto desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
    void* original = nullptr;
    for (; desc->Name; ++desc)
    {
        const char* name = reinterpret_cast<const char*>(base + desc->Name);
        if (_stricmp(name, dllName) != 0) continue;

        DWORD nameRva = desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk;
        auto nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + nameRva);
        auto addrThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);

        for (; nameThunk->u1.AddressOfData; ++nameThunk, ++addrThunk)
        {
            if (nameThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) continue;
            auto ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + nameThunk->u1.AddressOfData);
            if (strcmp(ibn->Name, funcName) != 0) continue;

            DWORD oldProt;
            if (VirtualProtect(&addrThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProt))
            {
                original = reinterpret_cast<void*>(addrThunk->u1.Function);
                if (original != hook)
                    addrThunk->u1.Function = reinterpret_cast<ULONGLONG>(hook);
                VirtualProtect(&addrThunk->u1.Function, sizeof(void*), oldProt, &oldProt);
            }
            return original;
        }
    }
    return nullptr;
}

// Apply an IAT hook across every currently-loaded module; returns the first real target seen.
static void* HookIATAllModules(const char* dllName, const char* funcName, void* hook)
{
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &needed)) return nullptr;
    int count = needed / sizeof(HMODULE);
    void* real = nullptr;
    for (int i = 0; i < count; ++i)
    {
        void* r = HookIATInModule(mods[i], dllName, funcName, hook);
        if (r && r != hook && !real) real = r;
    }
    return real;
}

// ===================== OpenGL =====================

typedef BOOL(WINAPI* PFN_SwapBuffers)(HDC);
static PFN_SwapBuffers g_origWglSwap = nullptr;
static PFN_SwapBuffers g_origGdiSwap = nullptr;

static BOOL WINAPI Hooked_wglSwapBuffers(HDC hdc) { ApplyCap(); RecordPresent(2); return g_origWglSwap ? g_origWglSwap(hdc) : TRUE; }
static BOOL WINAPI Hooked_SwapBuffers(HDC hdc)    { ApplyCap(); RecordPresent(2); return g_origGdiSwap ? g_origGdiSwap(hdc) : TRUE; }

// ===================== Vulkan =====================
// Minimal typedefs (avoid a hard dependency on the Vulkan SDK headers).

#define VKAPI_PTR __stdcall
typedef void* VkDevice;
typedef void* VkInstance;
typedef void* VkQueue;
typedef int   VkResult;
typedef void (VKAPI_PTR* PFN_vkVoidFunction)(void);
typedef PFN_vkVoidFunction(VKAPI_PTR* PFN_vkGetDeviceProcAddr)(VkDevice, const char*);
typedef PFN_vkVoidFunction(VKAPI_PTR* PFN_vkGetInstanceProcAddr)(VkInstance, const char*);
typedef VkResult(VKAPI_PTR* PFN_vkQueuePresentKHR)(VkQueue, const void*);

static PFN_vkGetDeviceProcAddr   g_origVkGetDevProc = nullptr;
static PFN_vkGetInstanceProcAddr g_origVkGetInstProc = nullptr;
static PFN_vkQueuePresentKHR     g_realVkQueuePresent = nullptr;

static VkResult VKAPI_PTR Hooked_vkQueuePresentKHR(VkQueue queue, const void* info)
{
    ApplyCap();
    RecordPresent(4);
    return g_realVkQueuePresent ? g_realVkQueuePresent(queue, info) : 0;
}

static PFN_vkVoidFunction VKAPI_PTR Hooked_vkGetDeviceProcAddr(VkDevice dev, const char* name)
{
    PFN_vkVoidFunction f = g_origVkGetDevProc ? g_origVkGetDevProc(dev, name) : nullptr;
    if (f && name && strcmp(name, "vkQueuePresentKHR") == 0)
    {
        g_realVkQueuePresent = reinterpret_cast<PFN_vkQueuePresentKHR>(f);
        return reinterpret_cast<PFN_vkVoidFunction>(&Hooked_vkQueuePresentKHR);
    }
    return f;
}

static PFN_vkVoidFunction VKAPI_PTR Hooked_vkGetInstanceProcAddr(VkInstance inst, const char* name)
{
    PFN_vkVoidFunction f = g_origVkGetInstProc ? g_origVkGetInstProc(inst, name) : nullptr;
    if (f && name && strcmp(name, "vkQueuePresentKHR") == 0)
    {
        g_realVkQueuePresent = reinterpret_cast<PFN_vkQueuePresentKHR>(f);
        return reinterpret_cast<PFN_vkVoidFunction>(&Hooked_vkQueuePresentKHR);
    }
    if (f && name && strcmp(name, "vkGetDeviceProcAddr") == 0)
    {
        g_origVkGetDevProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(f);
        return reinterpret_cast<PFN_vkVoidFunction>(&Hooked_vkGetDeviceProcAddr);
    }
    return f;
}

// ===================== GetProcAddress (catches dynamic Vulkan/OpenGL loaders) =====================
// Many Vulkan games (and DXVK/VKD3D) load vulkan-1.dll dynamically and resolve entry points
// via GetProcAddress rather than the import table, so IAT hooks alone miss them. Intercepting
// GetProcAddress lets us hand back our wrappers in that case too. Fast-pathed on the first
// character so non-matching lookups cost almost nothing.

typedef FARPROC(WINAPI* PFN_GetProcAddr)(HMODULE, LPCSTR);
static PFN_GetProcAddr g_origGetProcAddr = nullptr;

static FARPROC WINAPI Hooked_GetProcAddr(HMODULE mod, LPCSTR name)
{
    FARPROC real = g_origGetProcAddr ? g_origGetProcAddr(mod, name) : GetProcAddress(mod, name);
    // ignore ordinals/null and fast-path on first char (our targets start with 'w' or 'v')
    if (!real || !name || reinterpret_cast<ULONG_PTR>(name) <= 0xFFFF) return real;
    char c = name[0];
    if (c != 'w' && c != 'v') return real;

    if (strcmp(name, "wglSwapBuffers") == 0) { g_origWglSwap = reinterpret_cast<PFN_SwapBuffers>(real); return reinterpret_cast<FARPROC>(&Hooked_wglSwapBuffers); }
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) { g_origVkGetInstProc = reinterpret_cast<PFN_vkGetInstanceProcAddr>(real); return reinterpret_cast<FARPROC>(&Hooked_vkGetInstanceProcAddr); }
    if (strcmp(name, "vkGetDeviceProcAddr") == 0) { g_origVkGetDevProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(real); return reinterpret_cast<FARPROC>(&Hooked_vkGetDeviceProcAddr); }
    return real;
}

// ===================== setup =====================

static void ApplyIATHooks()
{
    void* r;
    r = HookIATAllModules("opengl32.dll", "wglSwapBuffers", &Hooked_wglSwapBuffers);
    if (r) g_origWglSwap = reinterpret_cast<PFN_SwapBuffers>(r);
    r = HookIATAllModules("gdi32.dll", "SwapBuffers", &Hooked_SwapBuffers);
    if (r) g_origGdiSwap = reinterpret_cast<PFN_SwapBuffers>(r);

    r = HookIATAllModules("vulkan-1.dll", "vkGetDeviceProcAddr", &Hooked_vkGetDeviceProcAddr);
    if (r) g_origVkGetDevProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(r);
    r = HookIATAllModules("vulkan-1.dll", "vkGetInstanceProcAddr", &Hooked_vkGetInstanceProcAddr);
    if (r) g_origVkGetInstProc = reinterpret_cast<PFN_vkGetInstanceProcAddr>(r);

    // catch dynamic loaders (kernel32 + kernelbase both export GetProcAddress)
    r = HookIATAllModules("kernel32.dll", "GetProcAddress", &Hooked_GetProcAddr);
    if (r) g_origGetProcAddr = reinterpret_cast<PFN_GetProcAddr>(r);
    r = HookIATAllModules("kernelbase.dll", "GetProcAddress", &Hooked_GetProcAddr);
    if (r && !g_origGetProcAddr) g_origGetProcAddr = reinterpret_cast<PFN_GetProcAddr>(r);
}

static bool SetupSharedMemory()
{
    LARGE_INTEGER f; QueryPerformanceFrequency(&f); g_qpcFreq = f.QuadPart;

    wchar_t name[64];
    swprintf_s(name, L"Local\\PerfOverlayHook_%lu", GetCurrentProcessId());
    g_map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                               0, sizeof(SharedData), name);
    if (!g_map) return false;
    g_shared = reinterpret_cast<SharedData*>(MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedData)));
    if (!g_shared) return false;

    g_shared->presentCount = 0;
    g_shared->qpcFreq = g_qpcFreq;
    g_shared->apiMask = 0;
    g_shared->cap = RING_CAP;
    g_shared->fpsCap = 0;
    return true;
}

static DWORD WINAPI InitThread(LPVOID)
{
    if (!SetupSharedMemory()) return 0;
    timeBeginPeriod(1); // 1ms timer resolution so the frame limiter's Sleep(1) is precise
    HookDXGI();
    // Re-apply IAT hooks periodically to catch DLLs the game loads later.
    for (int i = 0; i < 600; ++i) // ~5 minutes of coverage, then steady state
    {
        ApplyIATHooks();
        Sleep(500);
    }
    // keep applying occasionally forever (cheap)
    for (;;) { ApplyIATHooks(); Sleep(5000); }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
    }
    return TRUE;
}
