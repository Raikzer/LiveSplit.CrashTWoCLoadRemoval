#pragma once
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef TWOC_RUNTIME_BUILD
#define TWM_API
#else
#define TWM_API __declspec(dllimport)
#endif

TWM_API int __cdecl TwmCreate(const wchar_t* model_path, const wchar_t* shader_directory,
    int enable_validation, void** handle, char* error, unsigned capacity);
TWM_API int __cdecl TwmPredict(void* handle, const float* pixels, unsigned pixel_count,
    float* confidence, char* error, unsigned capacity);
TWM_API int __cdecl TwmGetDeviceName(void* handle, char* name, unsigned capacity);
// Call only after all other callers using this handle have stopped.
TWM_API void __cdecl TwmDestroy(void* handle);

#ifdef __cplusplus
}
#endif
