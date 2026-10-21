#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>

// Fault injection only: fails every prediction and deliberately delays destruction.
// No Vulkan or model implementation; never ship this DLL with the component.
extern "C" int __cdecl TwmCreate(const wchar_t*, const wchar_t*, int, void** handle, char*, unsigned) {
    *handle = HeapAlloc(GetProcessHeap(), 0, 1); return *handle ? 0 : 1;
}
extern "C" int __cdecl TwmPredict(void*, const float*, unsigned, float*, char* error, unsigned capacity) {
    const char* text = "injected native inference failure";
    strncpy_s(error, capacity, text, _TRUNCATE); return 1;
}
extern "C" int __cdecl TwmGetDeviceName(void*, char* name, unsigned capacity) {
    strncpy_s(name, capacity, "fault injection fixture", _TRUNCATE); return 0;
}
extern "C" void __cdecl TwmDestroy(void* handle) {
    Sleep(2000);
    wchar_t marker[32768];
    if (GetEnvironmentVariableW(L"TWOC_TEST_DESTROY_MARKER", marker, 32768)) {
        HANDLE file = CreateFileW(marker, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
    HeapFree(GetProcessHeap(), 0, handle);
}
