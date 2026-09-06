using System;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BAR_Advanced_Launcher_2.Infrastructure;

/// <summary>
/// Holds the main window so services that need an HWND or a XamlRoot — the folder
/// picker and ContentDialog — can reach it without a static reference to the window
/// type itself.
/// </summary>
public sealed class WindowContext
{
    public Window? Window { get; private set; }

    public void Attach(Window window) => Window = window;

    /// <summary>The main window handle, required to initialise a WinRT picker.</summary>
    public IntPtr WindowHandle => Window is null ? IntPtr.Zero : WindowNative.GetWindowHandle(Window);

    /// <summary>The XamlRoot a ContentDialog must be placed in, or null before the shell loads.</summary>
    public XamlRoot? XamlRoot => Window?.Content?.XamlRoot;
}
