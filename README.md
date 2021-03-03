# LiveSplit.CrashTWoCLoadRemoval
LiveSplit component to automatically detect and remove loads from Crash Bandicoot: The Wrath of Cortex.

This is adapted from the NST load remover https://github.com/thomasneff/LiveSplit.CrashNSTLoadRemoval
and from https://github.com/Maschell/LiveSplit.PokemonRedBlue for the base component code.

# Other Image/Video-Based AutoSplitters and/or Load Removers
This component 
*only removes and detects loading screens from the japanese and english PS2 as well as the english XBox version of Crash Bandicoot: The Wrath of Cortex, nothing else!*

If you want to

 * Auto-split based on a folder of split images: https://github.com/Toufool/Auto-Split by https://github.com/Toufool
 * Auto-split and remove loads based on scriptable events from a video feed: https://github.com/ROMaster2/LiveSplit.VideoAutoSplit by https://github.com/ROMaster2
 * Auto-split and remove loads from only the black screens: https://github.com/thomasneff/LiveSplit.BlackScreenDetector by https://github.com/thomasneff/.
 * Detect text within an image: https://github.com/tesseract-ocr/tesseract

# Special Thanks
Special thanks go to PeteThePlayer, DylWingo and various others from the Crash Speedrunning Community, who helped me with testing/debugging.

# How does it work?
*FOR PS2*

The method works by taking a "screenshot" (currently 800x600, it needs to be this large to prevent some death animations from being incorrectly detected as a black screen, but will be downsized to save resources) from your selected capture at the top, where "LOADING" is displayed when playing TWoC. It will then first check if the "screenshot" (i will simply refer to it as image from here) is fully black and will pause the timer. When the blackscreen ends it will resume the timer and note the current time in case an actual load screen follows. After this it will actually be looking for the LOADING text at the top of the screen for 360 frames (6 seconds). 

To do this it will first preprocess the image by comparing color values to color the actual loading text fully black while the background is fully white. This image will then be fed into the tesseract optical character recognition AI to extract the text within the image. If the extracted text equals LOADING the timer will revert back to the time that was noted at the end of predecessing black screen and pause the timer again. Finally it will unpause the timer as soon as the upcoming blackscreen ends.

*FOR XBOX*

Coming Soon™

# Missing Features
Support for Autosplit still needs to be reconfigured.

# Known Issues
If you want to use the AutoSplitter functionality (when it's available), **all your Splits need to have different names!**. If you have Splits that share the same name, the AutoSplitter is not able to differentiate between them.

# Settings
The contents of the TWoCLoadRemoval.zip (once publicly available) go into your "Components" folder within your LiveSplit folder.

Add this to LiveSplit by going into your Layout Editor -> Add -> Control -> CrashTWoCLoadRemoval.

You can specify to capture either the full primary Display (default) or an open window. This window has to be open (not minimized) but does not have to be in the foreground. If you plan to use the load remover with PCSX2, I would refrain from capturing the actual screen the emulator is running on and recommend capturing an OBS window because instead. Otherwise the screen capture runs into weird issues, when the emu is using the software renderer.

