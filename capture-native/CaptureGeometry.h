#pragma once
#include <cstdint>
#include <limits>

namespace twgc {
constexpr uint64_t MaxFrameBytes = 256ull * 1024ull * 1024ull;
struct Rect { int64_t left, top, right, bottom; };
struct Crop { uint32_t x, y, width, height, stride, bytes; };
inline bool PackedExtent(int64_t width, int64_t height, uint32_t& stride, uint32_t& bytes) noexcept {
    if (width <= 0 || height <= 0 || width > UINT32_MAX / 4) return false;
    const uint64_t row = static_cast<uint64_t>(width) * 4;
    if (static_cast<uint64_t>(height) > MaxFrameBytes / row) return false;
    stride = static_cast<uint32_t>(row);
    bytes = static_cast<uint32_t>(row * height);
    return true;
}
inline bool CropFromOrigin(Rect client, Rect origin, int64_t width, int64_t height, Crop& crop) noexcept {
    if (origin.right - origin.left != width || origin.bottom - origin.top != height) return false;
    const int64_t x = client.left - origin.left, y = client.top - origin.top;
    const int64_t w = client.right - client.left, h = client.bottom - client.top;
    if (x < 0 || y < 0 || w <= 0 || h <= 0 || x > width || y > height || w > width - x || h > height - y) return false;
    uint32_t stride = 0, bytes = 0;
    if (!PackedExtent(w, h, stride, bytes)) return false;
    crop = { static_cast<uint32_t>(x), static_cast<uint32_t>(y), static_cast<uint32_t>(w), static_cast<uint32_t>(h), stride, bytes };
    return true;
}
}
