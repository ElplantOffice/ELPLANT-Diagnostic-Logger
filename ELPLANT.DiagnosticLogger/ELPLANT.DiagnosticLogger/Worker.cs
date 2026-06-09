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
        LogStartupInformation();

        var runningTasks = new List<Task>();

        runningTasks.Add(
            RunDatasetWriterLoopAsync(stoppingToken));

        foreach (var plc in _config.Plcs.Where(p => p.Enabled))
        {
            runningTasks.Add(
                RunPlcPeriodicLoopAsync(plc, stoppingToken));
        }

        if (runningTasks.Count == 1)
        {
            _logger.LogWarning(
                "No enabled PLCs configured. Only DatasetWriter loop is running.");
        }

        await Task.WhenAll(runningTasks);
    }

    private void LogStartupInformation()
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
            _logger.LogInformation(
                "PLC: {Name}, AMS: {AmsNetId}, Port: {Port}, Enabled: {Enabled}",
                plc.Name,
                plc.AmsNetId,
                plc.Port,
                plc.Enabled);

            foreach (var parameter in plc.Parameters)
            {
                _logger.LogInformation(
                    "Parameter: {Name}, Mode: {Mode}",
                    parameter.Name,
                    parameter.ReadMode);
            }
        }
    }

    private async Task RunPlcPeriodicLoopAsync(
        PlcConfig plc,
        CancellationToken stoppingToken)
    {
        var plcLogger =
            _loggerFactory.CreateLogger<PlcConnectionManager>();

        using var plcManager =
            new PlcConnectionManager(
                plc,
                plcLogger);

        _logger.LogInformation(
            "Connecting to PLC '{PlcName}'...",
            plc.Name);

        var connected =
            await plcManager.ConnectAsync();

        if (!connected)
        {
            _logger.LogWarning(
                "ADS connection failed for PLC '{PlcName}'. Periodic loop will not start.",
                plc.Name);

            return;
        }

        var periodicParameters = plc.Parameters
            .Where(p => p.ReadMode == ParameterReadMode.Periodic)
            .ToList();

        var onChangeParameters = plc.Parameters
            .Where(p => p.ReadMode == ParameterReadMode.OnChange)
            .ToList();

        foreach (var parameter in onChangeParameters)
        {
            _logger.LogInformation(
                "PLC '{PlcName}' parameter '{ParameterName}' is configured as OnChange. ADS notifications are not implemented yet.",
                plc.Name,
                parameter.Name);
        }

        if (periodicParameters.Count == 0)
        {
            _logger.LogInformation(
                "PLC '{PlcName}' has no Periodic parameters.",
                plc.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            return;
        }

        var nextReadTimes = periodicParameters.ToDictionary(
            parameter => parameter.Name,
            _ => DateTime.MinValue);

        _logger.LogInformation(
            "Started periodic read loop for PLC '{PlcName}' with {Count} parameter(s).",
            plc.Name,
            periodicParameters.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;

            foreach (var parameter in periodicParameters)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (!nextReadTimes.TryGetValue(parameter.Name, out var nextReadTime))
                {
                    nextReadTime = DateTime.MinValue;
                }

                if (nowUtc < nextReadTime)
                {
                    continue;
                }

                await ReadPeriodicParameterAsync(
                    plc,
                    plcManager,
                    parameter,
                    stoppingToken);

                var intervalMs = parameter.ReadIntervalMs ?? 1000;

                if (intervalMs < 100)
                {
                    intervalMs = 100;
                }

                nextReadTimes[parameter.Name] =
                    DateTime.UtcNow.AddMilliseconds(intervalMs);
            }

            await Task.Delay(50, stoppingToken);
        }
    }

    private async Task ReadPeriodicParameterAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        ParameterConfig parameter,
        CancellationToken stoppingToken)
    {
        try
        {
            var value =
                await plcManager.ReadParameterValueAsync(parameter);

            var record = new DatasetRecord
            {
                Timestamp = DateTime.UtcNow,
                PlcName = plc.Name,
                ParameterName = parameter.Name,
                ReadMode = parameter.ReadMode.ToString(),
                Value = value
            };

            _datasetBuffer.Enqueue(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read Periodic parameter '{ParameterName}' from PLC '{PlcName}'.",
                parameter.Name,
                plc.Name);
        }

        await Task.CompletedTask;
    }

    private async Task RunDatasetWriterLoopAsync(
        CancellationToken stoppingToken)
    {
        var writeIntervalSeconds = _config.Storage.WriteIntervalSeconds;

        if (writeIntervalSeconds <= 0)
        {
            writeIntervalSeconds = 10;
        }

        _logger.LogInformation(
            "DatasetWriter loop started. Write interval: {Seconds} second(s).",
            writeIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var writtenRecords =
                    await _datasetWriter.WritePendingRecordsAsync(stoppingToken);

                if (writtenRecords > 0)
                {
                    _logger.LogInformation(
                        "DatasetWriter flushed {Count} record(s). Buffer count after write = {BufferCount}",
                        writtenRecords,
                        _datasetBuffer.Count);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "DatasetWriter loop error.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(writeIntervalSeconds),
                stoppingToken);
        }
    }
}