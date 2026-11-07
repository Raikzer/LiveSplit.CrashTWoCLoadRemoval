# Native Windows Graphics Capture bridge

`TwocWindowsGraphicsCapture.dll` is an optional, separate native bridge for Windows
10 version 1903/build 18362 and later. It does not use Vulkan, GDI capture, managed
WinRT packages, a capture picker, or access prompts. Windows controls the normal
capture border. Where supported, the session excludes the cursor.

## Build

Install Visual Studio 2022 C++ build tools (v143) and Windows SDK 10.0.22621.0, then:

```powershell
.\capture-native\build.ps1
# Optional: -MSBuild 'C:\path\to\MSBuild.exe' -Architecture Win32
```

The standalone project uses C++17, C++/WinRT headers supplied by the SDK and the
static CRT (`/MT`). It links WindowsApp, D3D11, RuntimeObject, Dwmapi and User32
system libraries. No Vulkan SDK or NuGet restore is needed. The build outputs are:

- `capture-native/bin/Win32/TwocWindowsGraphicsCapture.dll`
- `capture-native/bin/x64/TwocWindowsGraphicsCapture.dll`

C++/WinRT's Microsoft copyright and MIT terms are included in
[`LICENSE.CppWinRT.txt`](LICENSE.CppWinRT.txt).

`-WindowsSdkVersion` can select another installed compatible SDK. The 19041 SDK's
older C++/WinRT headers fail strict MSVC 14.44 name lookup; 22621 is the tested
default. Building with this newer SDK does not change the minimum runtime version.

## ABI and ownership

`TwocWindowsGraphicsCapture.h` defines the three undecorated C exports and their
cdecl signatures. Source kind 1 accepts an HWND; kind 2 accepts an HMONITOR. Status
values are `Success=0`, `Pending=1`, `BufferTooSmall=2`, `Unavailable=3`,
`Unsupported=4`, and `Error=5`. Error text is bounded UTF-8; the caller may omit the
error buffer. Invalid arguments return Error and an unavailable source returns
Unavailable. `TwgcCreate` zeros its context output before validation.

Create returns quickly after starting a dedicated MTA owner thread. It does not
wait for WinRT activation, D3D initialization or the first frame. Initialization
failures are returned by subsequent CopyLatest calls. All WinRT and D3D access,
event revocation, session/pool closure and COM release happen on that owner thread.
Callbacks retain only a weak reference to shared state and signal the owner.

Call CopyLatest with a null buffer and zero capacity to query the current frame.
BufferTooSmall provides width, height, stride and generation; allocate enough
storage and retry, allowing another BufferTooSmall if the target resizes. A frame
is packed BGRA8 with stride `width*4`. The bytes are owned by the bridge until
copied into caller storage. Frame extents and allocation arithmetic are checked
against a 256 MiB limit. CopyLatest can reuse a valid static frame indefinitely;
there is no capture-age timeout.

Windows return their complete physical client area, excluding borders and title
bars. Capture origins are resolved only when the frame size exactly matches DWM
extended frame bounds, physical GetWindowRect, or client bounds. The client crop
must fit entirely inside that origin. Inconsistent or changing geometry returns
Pending with an actionable message; pixels are never guessed, scaled, or replaced
with a synthetic black frame. Monitors return the complete physical monitor bitmap
with local origin (0,0). Staging reads respect D3D RowPitch.

Minimize and resize invalidate cached data and return Pending. Restoration waits
for a new frame. Closed or disconnected sources return terminal Unavailable;
device failures return terminal Error. Stop clears the cache and joins the owner.
Destroy must not run concurrently with CopyLatest on the same context. Call Destroy
once for each successful Create and retain the caller's DLL reference until it
returns; callers can then release their own LoadLibrary reference. Before the
first valid Create starts its owner thread, the bridge pins its module with
GetModuleHandleEx(FROM_ADDRESS | PIN). Invalid argument/source calls do not pin
it. Event revocation cannot drain callbacks already executing, so the module
mapping intentionally remains until process exit even after Destroy and
FreeLibrary. Sessions, frame pools, D3D/COM objects and owner threads still stop
and release fully. A failure to pin returns Error before any owner starts. Destroy may
wait for D3D/driver work, so applications should dispose away from their UI thread.
A null Destroy is harmless. No C++ exception crosses the C ABI.

## Focused checks

```powershell
.\capture-native\test.ps1
```

These tests build both architectures, verify undecorated exports and safe invalid
arguments, and test extent/crop arithmetic, including negative desktop coordinates,
DPI-sized bounds, containment and the allocation limit. They never create a valid
capture context. Never assume an arbitrary nonzero numeric window/monitor handle
is invalid. Live capture checks must use an explicitly approved test-owned window;
they are not part of this script.
