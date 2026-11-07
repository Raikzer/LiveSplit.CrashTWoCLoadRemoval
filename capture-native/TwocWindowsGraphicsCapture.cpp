#include "TwocWindowsGraphicsCapture.h"
#include "CaptureGeometry.h"
#include <windows.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
namespace {
struct DpiScope {
    DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    ~DpiScope() { if (previous) SetThreadDpiAwarenessContext(previous); }
};
twgc::Rect RectOf(RECT r) { return { r.left, r.top, r.right, r.bottom }; }
bool ClientRect(HWND hwnd, twgc::Rect& result) {
    DpiScope dpi;
    RECT r{};
    if (!GetClientRect(hwnd, &r)) return false;
    POINT first{ r.left, r.top }, last{ r.right, r.bottom };
    if (!ClientToScreen(hwnd, &first) || !ClientToScreen(hwnd, &last)) return false;
    result = { first.x, first.y, last.x, last.y };
    return true;
}
bool MonitorRect(HMONITOR monitor, twgc::Rect& result) {
    DpiScope dpi;
    MONITORINFO info{ sizeof(info) };
    if (!GetMonitorInfoW(monitor, &info)) return false;
    result = RectOf(info.rcMonitor);
    return true;
}
void Message(char* destination, uint32_t capacity, const char* message) noexcept {
    if (!destination || !capacity) return;
    const size_t length = (std::min)(strlen(message), static_cast<size_t>(capacity - 1));
    memcpy(destination, message, length);
    destination[length] = '\0';
}
std::string ExceptionText() {
    try { throw; }
    catch (hresult_error const& ex) {
        try { return to_string(ex.message()); } catch (...) { return "Windows capture operation failed."; }
    }
    catch (std::exception const& ex) { try { return ex.what(); } catch (...) { return "Native capture exception."; } }
    catch (...) { return "Unknown native capture exception."; }
}
bool SupportedVersion() noexcept {
    using VersionFunction = LONG (WINAPI*)(OSVERSIONINFOW*);
    auto module = GetModuleHandleW(L"ntdll.dll");
    auto getVersion = reinterpret_cast<VersionFunction>(GetProcAddress(module, "RtlGetVersion"));
    OSVERSIONINFOW version{ sizeof(version) };
    return getVersion && getVersion(&version) == 0 && (version.dwMajorVersion > 10 ||
        (version.dwMajorVersion == 10 && version.dwBuildNumber >= 18362));
}
void EnsureModulePinned() {
    static std::once_flag once;
    // Revoking a WinRT event does not drain delegates already executing. Keep
    // their machine code mapped even after callers release their LoadLibrary ref.
    static const char moduleAddress = 0;
    std::call_once(once, [] {
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&moduleAddress), &module)) {
            const DWORD error = GetLastError();
            throw hresult_error(error ? HRESULT_FROM_WIN32(error) : E_FAIL,
                L"Unable to retain the native capture module for safe callback lifetime.");
        }
        // GET_MODULE_HANDLE_EX_FLAG_PIN intentionally lasts until process exit.
    });
}
struct State {
    std::mutex mutex;
    std::condition_variable wake;
    bool stop = false, frameSignal = false, closedSignal = false, discardRequested = false;
    int status = TwgcPending;
    std::string message = "Waiting for the first capture frame.";
    std::vector<uint8_t> latest;
    uint32_t width = 0, height = 0, stride = 0;
    uint64_t generation = 0, epoch = 0;
    int kind;
    uintptr_t handle;
    State(int sourceKind, uintptr_t sourceHandle) : kind(sourceKind), handle(sourceHandle) {}
};
struct Context { std::shared_ptr<State> state; std::thread owner; };
void ClearLocked(State& state, int status, const char* message) {
    state.latest.clear();
    state.width = state.height = state.stride = 0;
    state.status = status;
    state.message = message;
    ++state.epoch;
}
void SetStatus(std::shared_ptr<State> const& state, int status, const char* message) {
    std::lock_guard<std::mutex> lock(state->mutex);
    ClearLocked(*state, status, message);
}
void Signal(std::weak_ptr<State> weak, bool closed) noexcept {
    if (auto state = weak.lock()) {
        std::lock_guard<std::mutex> lock(state->mutex);
        if (closed) state->closedSignal = true; else state->frameSignal = true;
        state->wake.notify_one();
    }
}
// Only the MTA owner constructs, accesses and closes WinRT/D3D objects.
void Capture(std::shared_ptr<State> const& state) {
    init_apartment(apartment_type::multi_threaded);
    struct Apartment { ~Apartment() { uninit_apartment(); } } apartment;
    if (!GraphicsCaptureSession::IsSupported()) {
        SetStatus(state, TwgcUnsupported, "Windows Graphics Capture is unavailable on this system.");
        return;
    }
    // Minimized and empty client windows remain retryable even before the first
    // item/pool exists. Never invent an allocation or publish a placeholder frame.
    while (state->kind == 1) {
        const HWND hwnd = reinterpret_cast<HWND>(state->handle);
        if (!IsWindow(hwnd)) { SetStatus(state, TwgcUnavailable, "The capture window was closed."); return; }
        twgc::Rect client{};
        if (!IsIconic(hwnd) && ClientRect(hwnd, client) && client.right > client.left && client.bottom > client.top) break;
        SetStatus(state, TwgcPending, "Waiting for a restored window with a nonempty client area.");
        std::unique_lock<std::mutex> lock(state->mutex);
        state->wake.wait_for(lock, std::chrono::milliseconds(50), [&] { return state->stop; });
        if (state->stop) { ClearLocked(*state, TwgcUnavailable, "Capture stopped."); return; }
    }
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> immediate;
    D3D_FEATURE_LEVEL level{};
    check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, device.put(), &level, immediate.put()));
    com_ptr<IInspectable> inspectable;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(), inspectable.put()));
    auto directDevice = inspectable.as<IDirect3DDevice>();
    auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{ nullptr };
    if (state->kind == 1) {
        check_hresult(interop->CreateForWindow(reinterpret_cast<HWND>(state->handle),
            guid_of<GraphicsCaptureItem>(), put_abi(item)));
    } else {
        check_hresult(interop->CreateForMonitor(reinterpret_cast<HMONITOR>(state->handle),
            guid_of<GraphicsCaptureItem>(), put_abi(item)));
    }
    auto size = item.Size();
    uint32_t unusedStride = 0, unusedBytes = 0;
    if (!twgc::PackedExtent(size.Width, size.Height, unusedStride, unusedBytes)) {
        SetStatus(state, TwgcError, "Capture target dimensions are empty or exceed the 256 MiB frame limit.");
        return;
    }
    auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(directDevice,
        DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
    auto session = pool.CreateCaptureSession(item);
    // Preserve the system border. Cursor capture is optional on Windows 10 2004+.
    if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
        L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsCursorCaptureEnabled")) {
        if (auto cursorSession = session.try_as<IGraphicsCaptureSession2>()) cursorSession.IsCursorCaptureEnabled(false);
    }
    const std::weak_ptr<State> weak = state;
    // Explicit cleanup also runs during initialization, frame processing and device exceptions.
    struct Cleanup {
        Direct3D11CaptureFramePool& pool;
        GraphicsCaptureSession& session;
        GraphicsCaptureItem& item;
        event_token arrived{}, closed{};
        ~Cleanup() noexcept {
            try { if (arrived.value) pool.FrameArrived(arrived); } catch (...) {}
            try { if (closed.value) item.Closed(closed); } catch (...) {}
            try { session.Close(); } catch (...) {}
            try { pool.Close(); } catch (...) {}
        }
    } cleanup{ pool, session, item };
    cleanup.arrived = pool.FrameArrived([weak](auto const&, auto const&) noexcept { Signal(weak, false); });
    cleanup.closed = item.Closed([weak](auto const&, auto const&) noexcept { Signal(weak, true); });
    session.StartCapture();
    com_ptr<ID3D11Texture2D> staging;
    uint32_t stagingWidth = 0, stagingHeight = 0;
    for (;;) {
        bool discard = false;
        uint64_t epoch = 0;
        {
            std::unique_lock<std::mutex> lock(state->mutex);
            state->wake.wait_for(lock, std::chrono::milliseconds(50), [&] {
                return state->stop || state->closedSignal || state->frameSignal || state->discardRequested;
            });
            if (state->stop) { ClearLocked(*state, TwgcUnavailable, "Capture stopped."); break; }
            if (state->closedSignal) { ClearLocked(*state, TwgcUnavailable, "The capture target was closed."); break; }
            state->frameSignal = false;
            discard = state->discardRequested;
            state->discardRequested = false;
            epoch = state->epoch;
        }
        if (state->kind == 1) {
            HWND hwnd = reinterpret_cast<HWND>(state->handle);
            if (!IsWindow(hwnd)) { SetStatus(state, TwgcUnavailable, "The capture window was closed."); break; }
            if (IsIconic(hwnd)) {
                SetStatus(state, TwgcPending, "The capture window is minimized; waiting for restoration.");
                discard = true;
            }
        } else {
            twgc::Rect monitor{};
            if (!MonitorRect(reinterpret_cast<HMONITOR>(state->handle), monitor)) {
                SetStatus(state, TwgcUnavailable, "The capture monitor is no longer available."); break;
            }
        }
        check_hresult(device->GetDeviceRemovedReason());
        if (discard) { while (auto frame = pool.TryGetNextFrame()) frame.Close(); continue; }
        auto frame = pool.TryGetNextFrame();
        if (!frame) continue;
        const auto content = frame.ContentSize();
        if (content.Width <= 0 || content.Height <= 0) {
            frame.Close();
            SetStatus(state, TwgcPending, "Waiting for a nonempty capture frame after the window changed.");
            continue;
        }
        if (!twgc::PackedExtent(content.Width, content.Height, unusedStride, unusedBytes)) {
            frame.Close();
            SetStatus(state, TwgcError, "Capture frame dimensions are empty or exceed the 256 MiB frame limit.");
            break;
        }
        if (content.Width != size.Width || content.Height != size.Height) {
            frame.Close();
            SetStatus(state, TwgcPending, "The capture target is resizing; waiting for a complete frame.");
            size = content;
            pool.Recreate(directDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            staging = nullptr;
            continue;
        }
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(guid_of<ID3D11Texture2D>(), texture.put_void()));
        D3D11_TEXTURE2D_DESC description{};
        texture->GetDesc(&description);
        if (description.Width != static_cast<uint32_t>(content.Width) || description.Height != static_cast<uint32_t>(content.Height)) {
            frame.Close();
            SetStatus(state, TwgcPending, "Waiting for a capture texture matching the target size.");
            continue;
        }
        twgc::Crop crop{};
        twgc::Rect client{};
        if (state->kind == 1) {
            const HWND hwnd = reinterpret_cast<HWND>(state->handle);
            if (!ClientRect(hwnd, client)) { frame.Close(); SetStatus(state, TwgcPending, "Waiting for the window client geometry."); continue; }
            DpiScope dpi;
            RECT dwm{}, window{};
            bool normalized = false;
            if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &dwm, sizeof(dwm))))
                normalized = twgc::CropFromOrigin(client, RectOf(dwm), content.Width, content.Height, crop);
            if (!normalized && GetWindowRect(hwnd, &window))
                normalized = twgc::CropFromOrigin(client, RectOf(window), content.Width, content.Height, crop);
            if (!normalized)
                normalized = twgc::CropFromOrigin(client, client, content.Width, content.Height, crop);
            if (!normalized) {
                frame.Close();
                SetStatus(state, TwgcPending, "Capture geometry does not match the physical client/window bounds; restore or resize the window and retry.");
                continue;
            }
        } else {
            twgc::Rect monitor{};
            if (!MonitorRect(reinterpret_cast<HMONITOR>(state->handle), monitor) ||
                !twgc::CropFromOrigin(monitor, monitor, content.Width, content.Height, crop)) {
                frame.Close(); SetStatus(state, TwgcPending, "Waiting for a capture frame matching the physical monitor bounds."); continue;
            }
        }
        if (!staging || stagingWidth != crop.width || stagingHeight != crop.height) {
            D3D11_TEXTURE2D_DESC cpu{};
            cpu.Width = crop.width; cpu.Height = crop.height;
            cpu.MipLevels = 1; cpu.ArraySize = 1; cpu.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            cpu.SampleDesc.Count = 1; cpu.Usage = D3D11_USAGE_STAGING; cpu.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            staging = nullptr;
            check_hresult(device->CreateTexture2D(&cpu, nullptr, staging.put()));
            stagingWidth = crop.width; stagingHeight = crop.height;
        }
        D3D11_BOX box{ crop.x, crop.y, 0, crop.x + crop.width, crop.y + crop.height, 1 };
        immediate->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, texture.get(), 0, &box);
        frame.Close(); // Release WGC frame before mapping or recreating the pool.
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check_hresult(immediate->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
        struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture, 0); } } unmap{ immediate.get(), staging.get() };
        if (!mapped.pData || mapped.RowPitch < crop.stride) throw hresult_error(E_FAIL, L"Capture staging texture has an invalid row pitch.");
        std::vector<uint8_t> pixels(crop.bytes);
        for (uint32_t row = 0; row < crop.height; ++row)
            memcpy(pixels.data() + static_cast<size_t>(row) * crop.stride,
                static_cast<uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, crop.stride);
        check_hresult(device->GetDeviceRemovedReason());
        std::lock_guard<std::mutex> lock(state->mutex);
        if (state->stop || state->closedSignal || state->epoch != epoch) continue;
        if (state->kind == 1) {
            twgc::Rect current{};
            HWND hwnd = reinterpret_cast<HWND>(state->handle);
            if (!IsWindow(hwnd)) { ClearLocked(*state, TwgcUnavailable, "The capture window was closed."); break; }
            if (IsIconic(hwnd) || !ClientRect(hwnd, current) || current.right - current.left != crop.width || current.bottom - current.top != crop.height) {
                ClearLocked(*state, TwgcPending, "Waiting for a complete frame after the window changed."); continue;
            }
        }
        state->latest = std::move(pixels);
        state->width = crop.width; state->height = crop.height; state->stride = crop.stride;
        ++state->generation;
        state->status = TwgcSuccess;
        state->message.clear();
    }
}
void Run(std::shared_ptr<State> state) noexcept {
    try { Capture(state); }
    catch (...) {
        try { const auto message = ExceptionText(); SetStatus(state, TwgcError, message.c_str()); }
        catch (...) { try { SetStatus(state, TwgcError, "Native capture failed while reporting an exception."); } catch (...) {} }
    }
}
}

extern "C" int __cdecl TwgcCreate(int sourceKind, uintptr_t handle, void** context, char* error, uint32_t errorCapacity) {
    if (context) *context = nullptr;
    Message(error, errorCapacity, "");
    try {
        if (!context || (sourceKind != 1 && sourceKind != 2) || !handle) {
            Message(error, errorCapacity, "A context output, source kind 1 (window) or 2 (monitor), and nonzero handle are required."); return TwgcError;
        }
        if (!SupportedVersion()) { Message(error, errorCapacity, "Windows Graphics Capture requires Windows 10 version 1903 (build 18362) or later."); return TwgcUnsupported; }
        twgc::Rect extent{};
        if ((sourceKind == 1 && !IsWindow(reinterpret_cast<HWND>(handle))) ||
            (sourceKind == 2 && !MonitorRect(reinterpret_cast<HMONITOR>(handle), extent))) {
            Message(error, errorCapacity, "The selected capture source is no longer available."); return TwgcUnavailable;
        }
        EnsureModulePinned();
        auto result = std::make_unique<Context>();
        result->state = std::make_shared<State>(sourceKind, handle);
        result->owner = std::thread(Run, result->state);
        *context = result.release();
        return TwgcSuccess;
    } catch (...) {
        try { const auto message = ExceptionText(); Message(error, errorCapacity, message.c_str()); }
        catch (...) { Message(error, errorCapacity, "Native capture failed while reporting an exception."); }
        return TwgcError;
    }
}
extern "C" int __cdecl TwgcCopyLatest(void* context, uint8_t* buffer, uint32_t capacity,
    uint32_t* width, uint32_t* height, uint32_t* stride, uint64_t* generation, char* error, uint32_t errorCapacity) {
    if (width) *width = 0; if (height) *height = 0; if (stride) *stride = 0; if (generation) *generation = 0;
    Message(error, errorCapacity, "");
    try {
        if (!context || !width || !height || !stride || !generation || (!buffer && capacity)) {
            Message(error, errorCapacity, "A valid context and all frame output pointers are required; null buffer requires zero capacity."); return TwgcError;
        }
        auto state = static_cast<Context*>(context)->state;
        std::lock_guard<std::mutex> lock(state->mutex);
        if (state->status == TwgcUnavailable || state->status == TwgcError || state->status == TwgcUnsupported) {
            Message(error, errorCapacity, state->message.c_str()); return state->status;
        }
        if (state->closedSignal) ClearLocked(*state, TwgcUnavailable, "The capture target was closed.");
        else if (state->kind == 1) {
            HWND hwnd = reinterpret_cast<HWND>(state->handle);
            twgc::Rect client{};
            if (!IsWindow(hwnd)) ClearLocked(*state, TwgcUnavailable, "The capture window was closed.");
            else if (IsIconic(hwnd)) {
                ClearLocked(*state, TwgcPending, "The capture window is minimized; waiting for restoration.");
                state->discardRequested = true; state->wake.notify_one();
            } else if (!ClientRect(hwnd, client) || (!state->latest.empty() &&
                (client.right - client.left != state->width || client.bottom - client.top != state->height))) {
                ClearLocked(*state, TwgcPending, "The capture window is resizing; waiting for a complete frame.");
                state->discardRequested = true; state->wake.notify_one();
            }
        } else {
            twgc::Rect monitor{};
            if (!MonitorRect(reinterpret_cast<HMONITOR>(state->handle), monitor))
                ClearLocked(*state, TwgcUnavailable, "The capture monitor is no longer available.");
            else if (!state->latest.empty() && (monitor.right - monitor.left != state->width || monitor.bottom - monitor.top != state->height)) {
                ClearLocked(*state, TwgcPending, "The monitor is resizing; waiting for a complete frame.");
                state->discardRequested = true; state->wake.notify_one();
            }
        }
        if (state->latest.empty()) { Message(error, errorCapacity, state->message.c_str()); return state->status; }
        *width = state->width; *height = state->height; *stride = state->stride; *generation = state->generation;
        if (!buffer || capacity < state->latest.size()) return TwgcBufferTooSmall;
        memcpy(buffer, state->latest.data(), state->latest.size());
        return TwgcSuccess;
    } catch (...) {
        try { const auto message = ExceptionText(); Message(error, errorCapacity, message.c_str()); }
        catch (...) { Message(error, errorCapacity, "Native capture failed while reporting an exception."); }
        return TwgcError;
    }
}
extern "C" void __cdecl TwgcDestroy(void* context) {
    try {
        std::unique_ptr<Context> owned(static_cast<Context*>(context));
        if (!owned) return;
        { std::lock_guard<std::mutex> lock(owned->state->mutex); owned->state->stop = true; owned->state->wake.notify_one(); }
        if (owned->owner.joinable()) owned->owner.join();
    } catch (...) { /* Never propagate a C++ exception across the ABI. */ }
}
