namespace SevenKeyboard;

/// <summary>Controls provider scheduling and result preference for one query.</summary>
public enum CaretQueryMode
{
    /// <summary>Original sequential lookup, including the per-window learned preference.</summary>
    Sequential = 0,
    /// <summary>
    /// Experimental overlap of Java, MSAA, Win32, and the shared UIA worker. Ignores learned preferences
    /// and chooses Java, UIA TextPattern2, MSAA, GUIThreadInfo, then UIA TextPattern.
    /// </summary>
    ParallelPriority = 1,
}
