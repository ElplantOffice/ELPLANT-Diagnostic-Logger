using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ELPLANT.DiagnosticLogger.Models.Config;

namespace ELPLANT.DiagnosticLogger.Services.Retention;

public class DatasetRetentionService
{
    private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromDays(1);

    private readonly AppConfig _config;
    private readonly ILogger<DatasetRetentionService> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastRetentionRunUtc = new();

    public DatasetRetentionService(
        AppConfig config,
        ILogger<DatasetRetentionService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task CleanupIfDueAsync(
        string plcName,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        if (_lastRetentionRunUtc.TryGetValue(plcName, out var lastRunUtc))
        {
            if (nowUtc - lastRunUtc < RetentionCheckInterval)
            {
                return;
            }
        }

        try
        {
            await CleanupPlcDatasetFileAsync(
                plcName,
                cancellationToken);

            _lastRetentionRunUtc[plcName] = nowUtc;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw;
        }
    }

    private async Task CleanupPlcDatasetFileAsync(
        string plcName,
        CancellationToken cancellationToken)
    {
        var retentionDays =
            GetRetentionDays(plcName);

        var cutoff =
            DateTimeOffset.Now.AddDays(-retentionDays);

        var safePlcName =
            MakeSafeFileName(plcName);

        var filePath = Path.Combine(
            _config.Storage.DatasetFolder,
            $"{safePlcName}_DatasetLogger.json");

        if (!File.Exists(filePath))
        {
            return;
        }

        var tempFilePath =
            $"{filePath}.tmp";

        if (File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);

                _logger.LogWarning(
                    "Deleted leftover temporary retention file for PLC '{PlcName}': {TempFilePath}",
                    plcName,
                    tempFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not delete leftover temporary retention file for PLC '{PlcName}'. Retention will be skipped.",
                    plcName);

                return;
            }
        }

        var totalLines = 0;
        var keptLines = 0;
        var removedLines = 0;
        var invalidLines = 0;

        try
        {
            await using var inputStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var reader = new StreamReader(inputStream);

            await using var outputStream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            await using var writer = new StreamWriter(outputStream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line =
                    await reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                totalLines++;

                if (!TryReadTimestamp(line, out var timestamp))
                {
                    invalidLines++;

                    await writer.WriteLineAsync(
                        line.AsMemory(),
                        cancellationToken);

                    keptLines++;

                    continue;
                }

                if (timestamp >= cutoff)
                {
                    await writer.WriteLineAsync(
                        line.AsMemory(),
                        cancellationToken);

                    keptLines++;
                }
                else
                {
                    removedLines++;
                }
            }

            await writer.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDeleteTempFile(
                tempFilePath,
                plcName);

            throw;
        }

        try
        {
            File.Move(
                tempFilePath,
                filePath,
                overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(
                tempFilePath,
                plcName);

            throw;
        }

        _logger.LogInformation(
            "Dataset retention completed for PLC '{PlcName}'. Removed {RemovedLines} record(s).",
            plcName,
            removedLines);
    }

    private int GetRetentionDays(
        string plcName)
    {
        var plc = _config.Plcs.FirstOrDefault(
            p => string.Equals(
                p.Name,
                plcName,
                StringComparison.OrdinalIgnoreCase));

        if (plc is null)
        {
            return 30;
        }

        if (plc.RetentionDays <= 0)
        {
            return 30;
        }

        return plc.RetentionDays;
    }

    private static bool TryReadTimestamp(
        string jsonLine,
        out DateTimeOffset timestamp)
    {
        timestamp = default;

        try
        {
            using var document =
                JsonDocument.Parse(jsonLine);

            if (!document.RootElement.TryGetProperty("Ts", out var tsProperty))
            {
                return false;
            }

            var tsText =
                tsProperty.GetString();

            if (string.IsNullOrWhiteSpace(tsText))
            {
                return false;
            }

            return DateTimeOffset.TryParse(
                tsText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out timestamp);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteTempFile(
        string tempFilePath,
        string plcName)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
            // Do not throw from cleanup. The caller will already log the main retention error.
        }
    }

    private static string MakeSafeFileName(
        string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value;
    }
}