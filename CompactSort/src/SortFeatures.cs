namespace CompactSort
{
    /// <summary>
    /// Feature switches for this build. The global pool and the shortcut-driven
    /// whole-container sort are intentionally disabled; the right-click compact sort
    /// remains the only sorting entry point.
    /// </summary>
    internal static class SortFeatures
    {
        public static readonly bool GlobalPoolEnabled = false;
        public static readonly bool PerContainerSortEnabled = false;
    }
}
