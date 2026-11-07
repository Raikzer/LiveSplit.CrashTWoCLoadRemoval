#include "../CaptureGeometry.h"
#include "../TwocWindowsGraphicsCapture.h"
#include <windows.h>
#include <cassert>
#include <cstring>
#include <iostream>
#include <type_traits>

int wmain(int argc, wchar_t** argv) {
    uint32_t stride = 0, bytes = 0;
    assert(twgc::PackedExtent(1920, 1080, stride, bytes) && stride == 7680 && bytes == 8294400);
    assert(!twgc::PackedExtent(0, 1080, stride, bytes));
    assert(!twgc::PackedExtent(-1, 1080, stride, bytes));
    assert(!twgc::PackedExtent(INT64_MAX, INT64_MAX, stride, bytes));
    assert(twgc::PackedExtent(8192, 8192, stride, bytes) && bytes == twgc::MaxFrameBytes);
    assert(!twgc::PackedExtent(8192, 8193, stride, bytes));
    twgc::Crop crop{};
    // DWM frame, negative monitor coordinates and DPI-expanded physical geometry.
    assert(twgc::CropFromOrigin({-1900, 40, -620, 760}, {-1908, 10, -612, 768}, 1296, 758, crop));
    assert(crop.x == 8 && crop.y == 30 && crop.width == 1280 && crop.height == 720 && crop.stride == 5120);
    assert(!twgc::CropFromOrigin({0, 0, 1280, 720}, {8, 0, 1304, 758}, 1296, 758, crop));
    assert(!twgc::CropFromOrigin({0, 0, 1280, 720}, {0, 0, 1280, 720}, 1920, 1080, crop));
    assert(!twgc::CropFromOrigin({0, 0, 1281, 720}, {0, 0, 1280, 720}, 1280, 720, crop));
    assert(twgc::CropFromOrigin({0, 0, 1280, 720}, {0, 0, 1280, 720}, 1280, 720, crop) && crop.x == 0 && crop.y == 0);
    assert(argc == 2);
    HMODULE module = LoadLibraryW(argv[1]);
    assert(module);
    auto create = reinterpret_cast<decltype(&TwgcCreate)>(GetProcAddress(module, "TwgcCreate"));
    auto copy = reinterpret_cast<decltype(&TwgcCopyLatest)>(GetProcAddress(module, "TwgcCopyLatest"));
    auto destroy = reinterpret_cast<decltype(&TwgcDestroy)>(GetProcAddress(module, "TwgcDestroy"));
    assert(create && copy && destroy);
    char error[256]{};
    void* context = reinterpret_cast<void*>(1);
    assert(create(0, 0, &context, error, sizeof(error)) == TwgcError && !context && error[0]);
    assert(create(1, 0, &context, error, sizeof(error)) == TwgcError && !context);
    assert(create(1, 1, nullptr, error, sizeof(error)) == TwgcError);
    assert(create(2, 0, &context, error, sizeof(error)) == TwgcError && !context);
    // Never probe a nonzero HWND/HMONITOR: a numeric handle can name a real target.
    char tiny[1]{'x'};
    assert(create(0, 0, &context, tiny, sizeof(tiny)) == TwgcError && tiny[0] == 0);
    uint32_t width = 42, height = 42;
    uint64_t generation = 42;
    assert(copy(nullptr, nullptr, 0, &width, &height, &stride, &generation, error, sizeof(error)) == TwgcError);
    assert(width == 0 && height == 0 && stride == 0 && generation == 0);
    destroy(nullptr);
    assert(FreeLibrary(module));
    // Safe rejected arguments never start an owner or pin this module.
    assert(GetModuleHandleW(L"TwocWindowsGraphicsCapture.dll") == nullptr);
    std::cout << "Geometry and non-capture C ABI checks passed.\n";
}
