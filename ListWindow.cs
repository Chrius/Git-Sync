namespace GitSync;

/// <summary>
/// Which lines of a long list are on screen, for a list that only has controls
/// for those (see RepoList). Kept apart from the control so the tests can
/// check it without a window.
/// </summary>
internal static class ListWindow
{
    /// <summary>How far the list can scroll: the last line at the bottom edge, or 0 when everything fits.</summary>
    internal static int MaxOffset(int count, int lineHeight, int viewHeight) =>
        (int)Math.Clamp(((long)count * lineHeight) - viewHeight, 0, int.MaxValue);

    internal static int Clamp(int offset, int count, int lineHeight, int viewHeight) =>
        Math.Clamp(offset, 0, MaxOffset(count, lineHeight, viewHeight));

    /// <summary>The first line on screen, and how many of its pixels are scrolled off above the top.</summary>
    internal static (int First, int Shift) FirstVisible(int offset, int lineHeight) =>
        lineHeight <= 0 ? (0, 0) : (offset / lineHeight, offset % lineHeight);

    /// <summary>
    /// Rows that cover the view at any offset: the lines that fit, plus one,
    /// since a line partly scrolled off at the top leaves room for part of
    /// another at the bottom.
    /// </summary>
    internal static int RowsNeeded(int lineHeight, int viewHeight) =>
        lineHeight <= 0 ? 0 : ((Math.Max(0, viewHeight) + lineHeight - 1) / lineHeight) + 1;
}
