using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

// Only the shared native backend may construct/use this owner. Windows_run creates
// DLL-owned windows on this thread; their procedures are never replaced or borrowed.
internal sealed unsafe class JavaAccessBridge : IDisposable
{
    private nint _module;
    private string? _loadedPath;
    private bool _initialized;
    private delegate* unmanaged[Cdecl]<nint, int> _isJavaWindow;
    private delegate* unmanaged[Cdecl]<nint, int*, long*, int> _getFocus;
    private delegate* unmanaged[Cdecl]<int, long, TextRect*, int, int> _getCaret;
    private delegate* unmanaged[Cdecl]<int, long, void> _release;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TextRect
    {
        internal int X, Y, Width, Height;
        internal TextRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    }

    internal static string ResolvePath(string? path)
    {
        if (path is not null && !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Java Access Bridge override must be an absolute DLL path.", nameof(path));
        return Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "dll", "x64", "windowsaccessbridge-64.dll"));
    }

    internal bool TryInitialize(string path)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        if (_module != 0) return _initialized && StringComparer.OrdinalIgnoreCase.Equals(_loadedPath, path);
        nint candidate = 0;
        try
        {
            // Exact top-level path. Dependencies may come only from that directory or System32.
            // In particular an invalid override never falls back to the bundled DLL or PATH.
            candidate = CaretNative.LoadLibraryEx(path, 0, 0x00000100 | 0x00000800);
            if (candidate == 0 ||
                !NativeLibrary.TryGetExport(candidate, "Windows_run", out nint run) ||
                !NativeLibrary.TryGetExport(candidate, "isJavaWindow", out nint isJavaWindow) ||
                !NativeLibrary.TryGetExport(candidate, "getAccessibleContextWithFocus", out nint getFocus) ||
                !NativeLibrary.TryGetExport(candidate, "getCaretLocation", out nint getCaret) ||
                !NativeLibrary.TryGetExport(candidate, "releaseJavaObject", out nint release)) return false;
            _isJavaWindow = (delegate* unmanaged[Cdecl]<nint, int>)isJavaWindow;
            _getFocus = (delegate* unmanaged[Cdecl]<nint, int*, long*, int>)getFocus;
            _getCaret = (delegate* unmanaged[Cdecl]<int, long, TextRect*, int, int>)getCaret;
            _release = (delegate* unmanaged[Cdecl]<int, long, void>)release;
            // Publish ownership before native initialization. A live backend initializes
            // only once; its final cleanup unloads on this same thread.
            using var context = new CaretDpi.Scope(CaretDpi.SystemAware);
            _module = candidate;
            _loadedPath = path;
            candidate = 0;
            ((delegate* unmanaged[Cdecl]<void>)run)(); // OpenJDK declares void, NOT BOOL.
            _initialized = true;
            return true;
        }
        catch (Exception) { return false; }
        finally { if (candidate != 0) _ = CaretNative.FreeLibrary(candidate); }
    }

    internal CaretPosition? TryGet(nint window)
    {
        if (!_initialized || window == 0) return null;
        int vmId = 0;
        long context = 0;
        try
        {
            using var scope = new CaretDpi.Scope(CaretDpi.SystemAware);
            if (_isJavaWindow(window) == 0) return null;
            int focused = _getFocus(window, &vmId, &context);
            if (focused == 0 || context == 0) return null;
            TextRect rect = default;
            if (_getCaret(vmId, context, &rect, 0) == 0) return null;
            return CaretDpi.JavaToPhysical(window, rect);
        }
        catch (Exception) { return null; }
        finally
        {
            // Some providers return a reference even on a failed query; release exactly once.
            if (context != 0) _release(vmId, context);
        }
    }

    public void Dispose()
    {
        if (_module == 0) return;
        // OpenJDK DllMain(DLL_PROCESS_DETACH) deletes the global bridge, whose
        // destructor destroys its window. The shared backend calls this only on
        // the initializing thread, after its final accepted query has completed.
        if (CaretNative.FreeLibrary(_module) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot unload Java Access Bridge.");
        _module = 0;
        _initialized = false;
        _loadedPath = null;
        _isJavaWindow = null;
        _getFocus = null;
        _getCaret = null;
        _release = null;
    }
}
