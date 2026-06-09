namespace ELPLANT.DiagnosticLogger.Models.Config;

public class StorageConfig
{
    public string DatasetFolder { get; set; } = string.Empty;

    public string ApplicationLogFolder { get; set; } = string.Empty;

    public int WriteIntervalSeconds { get; set; } = 10;
}