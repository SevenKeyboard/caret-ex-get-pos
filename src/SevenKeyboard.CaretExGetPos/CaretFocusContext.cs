namespace SevenKeyboard;

// HWND focus guards reject obvious target switches. Windows does not provide an
// atomic snapshot spanning every provider; movement inside one field can still race.
internal readonly record struct CaretFocusContext(nint Window, nint Focus)
{
    internal static unsafe CaretFocusContext Capture()
    {
        nint window = CaretNative.GetForegroundWindow();
        CaretNative.GuiThreadInfo info = new() { Size = (uint)sizeof(CaretNative.GuiThreadInfo) };
        nint focus = CaretNative.GetGUIThreadInfo(0, ref info) != 0 ? info.Focus : 0;
        return new(window, focus);
    }

    internal bool IsCurrent => Window != 0 && Capture() == this;
}
