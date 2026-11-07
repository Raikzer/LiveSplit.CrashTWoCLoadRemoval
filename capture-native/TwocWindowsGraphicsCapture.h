#pragma once
#include <stdint.h>

// C ABI, cdecl, undecorated exports on both Win32 and x64. All strings are UTF-8.
// Contexts must not be destroyed concurrently with CopyLatest. Creation is asynchronous.
enum TwgcStatus {
    TwgcSuccess = 0, TwgcPending = 1, TwgcBufferTooSmall = 2,
    TwgcUnavailable = 3, TwgcUnsupported = 4, TwgcError = 5
};
#ifdef __cplusplus
extern "C" {
#endif
int __cdecl TwgcCreate(int sourceKind, uintptr_t handle, void** context,
    char* error, uint32_t errorCapacity);
// Query with buffer=null/capacity=0. BufferTooSmall returns current dimensions and
// generation. Packed BGRA8, stride=width*4. A resize between query and copy can
// return BufferTooSmall again. Pending never returns a stale minimized frame.
int __cdecl TwgcCopyLatest(void* context, uint8_t* buffer, uint32_t capacity,
    uint32_t* width, uint32_t* height, uint32_t* stride, uint64_t* generation,
    char* error, uint32_t errorCapacity);
void __cdecl TwgcDestroy(void* context);
#ifdef __cplusplus
}
#endif
