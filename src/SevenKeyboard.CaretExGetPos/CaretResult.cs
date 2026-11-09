namespace SevenKeyboard;

/// <summary>A detached caret rectangle in physical screen pixels.</summary>
/// <param name="X">Left screen coordinate.</param>
/// <param name="Y">Top screen coordinate.</param>
/// <param name="Width">Caret width.</param>
/// <param name="Height">Caret height.</param>
/// <param name="CaretType">JavaAccessBridge, UIATextPattern2, MSAA, GUIThreadInfo, or UIATextPattern.</param>
public readonly record struct CaretPosition(int X, int Y, int Width, int Height, string CaretType);
