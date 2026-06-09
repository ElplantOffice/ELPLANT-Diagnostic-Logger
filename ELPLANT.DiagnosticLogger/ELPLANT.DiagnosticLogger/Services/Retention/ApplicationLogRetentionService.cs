using Microsoft.Extensions.Logging;

namespace ELPLANT.DiagnosticLogger.Services.Retention;

public class ApplicationLogRetentionService
{
    private readonly ILogger<ApplicationLogRetentionService> _logger;

    public ApplicationLogRetentionService(
        ILogger<ApplicationLogRetentionService> logger)
    {
        _logger = logger;
    }

    public void Cleanup(
        string logFolder,
        int retentionDays)
    {
        if (!Directory.Exists(logFolder))
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

        var removedFiles = 0;

        foreach (var file in Directory.GetFiles(logFolder, "*.txt"))
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(file);

                if (lastWriteUtc < cutoffUtc)
                {
                    File.Delete(file);
                    removedFiles++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to delete application log file '{FileName}'.",
                    Path.GetFileName(file));
            }
        }

        _logger.LogInformation(
            "Application log retention completed. Removed {RemovedFiles} file(s).",
            removedFiles);
    }
}