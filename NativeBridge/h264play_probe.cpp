#include <windows.h>
#include <cstdio>
#include <cstring>

struct FrameInfo {
    int width;
    int height;
    int stamp;
    int type;
    int frameRate;
    unsigned int frameNumber;
};

using ConvertBmp = int(__cdecl*)(unsigned char*, int, int, int, int, const char*);
static ConvertBmp g_convertBmp = nullptr;
static char g_output[MAX_PATH * 4]{};
static volatile LONG g_saved = 0;

static void __stdcall decoded(int, unsigned char* buffer, int size,
    FrameInfo* info, int, int) {
    if (!buffer || !info || InterlockedCompareExchange(&g_saved, 1, 0) != 0)
        return;
    int result = g_convertBmp
        ? g_convertBmp(buffer, size, info->width, info->height, info->type, g_output)
        : 0;
    std::printf("decoded=%dx%d type=%d size=%d bmp=%d\n",
        info->width, info->height, info->type, size, result);
    if (!result) InterlockedExchange(&g_saved, 0);
}

template <typename T>
T load(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 3 || argc > 4) return 2;
    HMODULE player = LoadLibraryW(argv[1]);
    if (!player) return 3;
    auto getPort = load<int(__cdecl*)(int*)>(player, "H264_PLAY_GetPort");
    auto openFile = load<int(__cdecl*)(int, const char*)>(player, "H264_PLAY_OpenFile");
    auto getTime = load<int(__cdecl*)(int)>(player, "H264_PLAY_GetFileTime");
    auto getFrames = load<int(__cdecl*)(int)>(player, "H264_PLAY_GetFileTotalFrames");
    auto closeFile = load<int(__cdecl*)(int)>(player, "H264_PLAY_CloseFile");
    auto freePort = load<int(__cdecl*)(int)>(player, "H264_PLAY_FreePort");
    auto play = load<int(__cdecl*)(int, HWND)>(player, "H264_PLAY_Play");
    auto catchPic = load<int(__cdecl*)(int, const char*)>(player, "H264_PLAY_CatchPic");
    auto stop = load<int(__cdecl*)(int)>(player, "H264_PLAY_Stop");
    auto getError = load<int(__cdecl*)(int)>(player, "H264_PLAY_GetLastError");
    auto getFrame = load<int(__cdecl*)(int)>(player, "H264_PLAY_GetCurrentFrameNum");
    auto setDecode = load<int(__cdecl*)(int, void*)>(player, "H264_PLAY_SetDecCallBack");
    g_convertBmp = load<ConvertBmp>(player, "H264_PLAY_ConvertToBmpFile");
    if (!getPort || !openFile || !getTime || !getFrames || !closeFile || !freePort || !play || !catchPic || !stop) return 4;
    int port = -1;
    int got = getPort(&port);
    char path[MAX_PATH * 4]{};
    WideCharToMultiByte(CP_ACP, 0, argv[2], -1, path, sizeof(path), nullptr, nullptr);
    int opened = openFile(port, path);
    std::printf("getPort=%d port=%d open=%d seconds=%d frames=%d\n",
        got, port, opened, getTime(port), getFrames(port));
    if (opened && argc == 4) {
        HWND window = CreateWindowExW(0, L"STATIC", L"", WS_POPUP,
            -2000, -2000, 640, 360, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        ShowWindow(window, SW_SHOWNA);
        char output[MAX_PATH * 4]{};
        WideCharToMultiByte(CP_ACP, 0, argv[3], -1, output, sizeof(output), nullptr, nullptr);
        std::strcpy(g_output, output);
        if (setDecode) setDecode(port, reinterpret_cast<void*>(&decoded));
        int playing = play(port, window);
        Sleep(1600);
        int caught = catchPic(port, output);
        std::printf("play=%d frame=%d catch=%d error=%d\n", playing,
            getFrame ? getFrame(port) : -1, caught, getError ? getError(port) : -1);
        stop(port);
        DestroyWindow(window);
    }
    if (opened) closeFile(port);
    freePort(port);
    return opened ? 0 : 5;
}
