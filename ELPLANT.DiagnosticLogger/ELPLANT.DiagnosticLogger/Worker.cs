using ELPLANT.DiagnosticLogger.Models.Config;
using ELPLANT.DiagnosticLogger.Models.Dataset;
using ELPLANT.DiagnosticLogger.Services.Ads;
using ELPLANT.DiagnosticLogger.Services.Dataset;

namespace ELPLANT.DiagnosticLogger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AppConfig _config;
    private readonly DatasetBuffer _datasetBuffer;
    private readonly DatasetWriter _datasetWriter;

    public Worker(
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        AppConfig config,
        DatasetBuffer datasetBuffer,
        DatasetWriter datasetWriter)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _datasetBuffer = datasetBuffer;
        _datasetWriter = datasetWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Application: {ApplicationName}",
            _config.ApplicationName);

        _logger.LogInformation(
            "System: {SystemName}",
            _config.SystemName);

        _logger.LogInformation(
            "Dataset Folder: {Folder}",
            _config.Storage.DatasetFolder);

        _logger.LogInformation(
            "Application Log Folder: {Folder}",
            _config.Storage.ApplicationLogFolder);

        _logger.LogInformation(
            "Configured PLC count: {Count}",
            _config.Plcs.Count);

        foreach (var plc in _config.Plcs)
        {
            if (!plc.Enabled)
            {
                _logger.LogInformation(
                    "PLC '{PlcName}' is disabled. Skipping.",
                    plc.Name);

                continue;
            }

            _logger.LogInformation(
                "Testing ADS connection to PLC '{PlcName}'...",
                plc.Name);

            var plcLogger =
                _loggerFactory.CreateLogger<PlcConnectionManager>();

            using var plcManager =
                new PlcConnectionManager(
                    plc,
                    plcLogger);

            var connected =
                await plcManager.ConnectAsync();

            if (!connected)
            {
                _logger.LogWarning(
                    "ADS connection test failed for PLC '{PlcName}'.",
                    plc.Name);

                continue;
            }

            _logger.LogInformation(
                "ADS connection test successful for PLC '{PlcName}'.",
                plc.Name);

            try
            {
                var machineRunning =
                    await plcManager.ReadValueAsync<bool>(
                        "App_Variables.g_tApp.tCond.bProductionRunning");

                _logger.LogInformation(
                    "MachineRunning = {Value}",
                    machineRunning);

                var record = new DatasetRecord
                {
                    Timestamp = DateTime.UtcNow,
                    PlcName = plc.Name,
                    ParameterName = "MachineRunning",
                    ReadMode = "OnChange",
                    Value = machineRunning
                };

                _datasetBuffer.Enqueue(record);

                _logger.LogInformation(
                    "Dataset buffer count before write = {Count}",
                    _datasetBuffer.Count);

                var writtenRecords =
                    await _datasetWriter.WritePendingRecordsAsync(stoppingToken);

                _logger.LogInformation(
                    "Dataset writer flushed {Count} record(s). Buffer count after write = {BufferCount}",
                    writtenRecords,
                    _datasetBuffer.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to read MachineRunning from PLC '{PlcName}'.",
                    plc.Name);
            }
        }

        _logger.LogInformation(
            "Initial ADS connection, read, buffer and dataset file test completed.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
        }
    }
}