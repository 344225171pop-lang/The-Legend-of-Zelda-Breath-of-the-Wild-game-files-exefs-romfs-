namespace BotwExtractor.Ui
{
    // Cross-activity runtime state. Kept Android-free so tests can drive it.
    public static class AppState
    {
        // True while an extraction job is running; the cache cleaner must not
        // delete the temp dir in that case.
        public static volatile bool IsExtracting;

        // Shared runtime log history used by both the main window and the exporter.
        public static readonly LogStore Logs = new LogStore();
    }
}
