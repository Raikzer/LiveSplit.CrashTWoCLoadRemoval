# Windows Graphics Capture

In the updated 2.1 pre-release, **Setup → Use Windows Graphics Capture** selects
Windows Graphics Capture for the window or monitor in the existing capture-source
dropdown. It applies to load detection, previews, saved samples and Training mode.
The checkbox defaults off and is saved in the LiveSplit layout. Older layouts
continue to use the existing GDI capture method.

Choose a source, enable the checkbox and check both previews before calibrating.
Windows are captured at their physical client size, excluding the title bar.
The four capture boundaries still use the **400 × 160** preview coordinates.
Windows Graphics Capture handles display scaling automatically; the GDI scaling
setting is retained for use when the checkbox is turned off.

Windows Graphics Capture needs Windows 10 version 1903 (build 18362) or later and
a compatible graphics device. The packaged component still requires a Windows
version supported by .NET Framework 4.8.1. The normal Windows capture border is
retained. A minimized or closed window cannot supply usable frames; restore or
reselect it. Capture status appears on Setup. The remover releases its Game Time
pause while waiting for a frame or when capture fails, and never substitutes a
black image or silently switches capture methods. Training skips ticks while
waiting for its first frame or a restored window; a capture failure stops the
recording with its existing partial session preserved.

Install both architecture folders from the package. LiveSplit loads
`Components/x86/TwocWindowsGraphicsCapture.dll` or
`Components/x64/TwocWindowsGraphicsCapture.dll` according to its process
architecture. This library uses Windows' Direct3D 11 capture APIs and a statically
linked C++ runtime. Neither the Windows SDK nor Vulkan SDK is needed to use this
capture option. The experimental ML detector still uses Vulkan compute for
inference and needs a Vulkan-capable graphics driver.

For development, build `capture-native/build.ps1` with Visual Studio 2022 C++
tools and Windows SDK **10.0.22621.0** (including its C++/WinRT headers).
The main `Build.ps1` also builds this bridge for both architectures.
`capture-native/test.ps1` runs geometry and ABI checks without creating a
capture session. Managed verification uses injected sessions and synthetic images.
The optional live verification captures only a window created by its own test;
it does not capture a user's desktop, game or other application.

```powershell
.\tests\graphics-capture.ps1                  # injected sessions; no live capture
.\tests\graphics-capture.ps1 -LiveNativeSmoke # also captures its own synthetic window
```
