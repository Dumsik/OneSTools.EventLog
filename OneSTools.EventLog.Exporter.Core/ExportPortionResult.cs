namespace OneSTools.EventLog.Exporter.Core
{
    public class ExportPortionResult
    {
        public bool Success { get; }
        public int ItemsCount { get; }

        public ExportPortionResult(bool success, int itemsCount)
        {
            Success = success;
            ItemsCount = itemsCount;
        }
    }
}
