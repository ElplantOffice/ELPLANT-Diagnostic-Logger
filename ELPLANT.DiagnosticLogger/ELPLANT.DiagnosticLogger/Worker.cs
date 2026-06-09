using ELPLANT.DiagnosticLogger.Services.Retention;
using System.Collections.Concurrent;
using TwinCAT.Ads;
using ELPLANT.DiagnosticLogger.Models.Config;
using ELPLANT.DiagnosticLogger.Models.Dataset;
using ELPLANT.DiagnosticLogger.Services.Ads;
using ELPLANT.DiagnosticLogger.Services.Dataset;

namespace ELPLANT.DiagnosticLogger;

public class Worker : BackgroundService
{
    private const string ConnectionEventName = "__Connection";
    private const string SystemMode = "System";

    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AppConfig _config;
    private readonly DatasetBuffer _datasetBuffer;
    private readonly DatasetWriter _datasetWriter;
    private readonly DatasetRetentionService _retentionService;

    private readonly ConcurrentDictionary<string, object?> _lastAcceptedValues = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAcceptedTimesUtc = new();
    private readonly ConcurrentDictionary<string, bool> _connectionLostState = new();

    public Worker(
    ILogger<Worker> logger,
    ILoggerFactory loggerFactory,
    AppConfig config,
    DatasetBuffer datasetBuffer,
    DatasetWriter datasetWriter,
    DatasetRetentionService retentionService)

    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _datasetBuffer = datasetBuffer;
        _datasetWriter = datasetWriter;
        _retentionService = retentionService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartupInformation();

        var runningTasks = new List<Task>
        {
            RunDatasetWriterLoopAsync(stoppingToken)
        };

        foreach (var plc in _config.Plcs.Where(p => p.Enabled))
        {
            runningTasks.Add(
                RunPlcReconnectLoopAsync(
                    plc,
                    stoppingToken));
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
            "{ApplicationName} started. System='{SystemName}'. PLC count={PlcCount}.",
            _config.ApplicationName,
            _config.SystemName,
            _config.Plcs.Count(p => p.Enabled));
    }

    private async Task RunPlcReconnectLoopAsync(
    PlcConfig plc,
    CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSinglePlcSessionAsync(
                    plc,
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                MarkConnectionLost(
                    plc);

                _logger.LogDebug(
                    ex,
                    "PLC '{PlcName}' session is still unavailable. Retry in {ReconnectIntervalSeconds} second(s).",
                    plc.Name,
                    _config.Ads.ReconnectIntervalSeconds);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Ads.ReconnectIntervalSeconds),
                    stoppingToken);
            }
        }
    }

    private async Task RunSinglePlcSessionAsync(
    PlcConfig plc,
    CancellationToken stoppingToken)
    {
        var plcLogger =
            _loggerFactory.CreateLogger<PlcConnectionManager>();

        using var plcManager =
            new PlcConnectionManager(
                plc,
                _config.Ads,
                plcLogger);

        plcManager.OnAdsNotificationReceived += (_, e) =>
        {
            HandleAdsNotification(
                plc,
                e);
        };

        var connected =
            await plcManager.ConnectAsync(stoppingToken);

        if (!connected)
        {
            MarkConnectionLost(
                plc);

            return;
        }

        await VerifyPlcCommunicationAsync(
            plc,
            plcManager,
            stoppingToken);

        try
        {
            await _retentionService.CleanupIfDueAsync(
                plc.Name,
                stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Dataset retention failed for PLC '{PlcName}'. PLC session will continue normally.",
                plc.Name);
        }

        MarkConnectionRestored(
            plc);

        await WriteInitialSnapshotsAsync(
            plc,
            plcManager,
            stoppingToken);

        RegisterOnChangeNotifications(
            plc,
            plcManager);

        await RunParameterLoopAsync(
            plc,
            plcManager,
            stoppingToken);
    }

    private async Task VerifyPlcCommunicationAsync(
    PlcConfig plc,
    PlcConnectionManager plcManager,
    CancellationToken stoppingToken)
    {
        if (plc.Parameters.Count == 0)
        {
            _logger.LogWarning(
                "PLC '{PlcName}' has no configured parameters. ADS communication cannot be verified by reading a parameter.",
                plc.Name);

            return;
        }


        foreach (var parameter in plc.Parameters)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _ = await plcManager.ReadParameterValueAsync(
                parameter,
                stoppingToken);
        }

    }

    private void MarkConnectionLost(
    PlcConfig plc)
    {
        var key = plc.Name;

        if (_connectionLostState.TryGetValue(key, out var alreadyLost) &&
            alreadyLost)
        {
            return;
        }

        _connectionLostState[key] = true;

        EnqueueConnectionEvent(
            plc,
            DatasetWriteReason.ConnectionLost);
    }

    private void MarkConnectionRestored(
    PlcConfig plc)
    {
        var key = plc.Name;

        if (!_connectionLostState.TryGetValue(key, out var wasLost))
        {
            _connectionLostState[key] = false;

            _logger.LogInformation(
                "PLC '{PlcName}' connected.",
                plc.Name);

            return;
        }

        if (!wasLost)
        {
            return;
        }

        _connectionLostState[key] = false;

        _logger.LogInformation(
            "PLC '{PlcName}' connection restored.",
            plc.Name);

        EnqueueConnectionEvent(
            plc,
            DatasetWriteReason.ConnectionRestored);
    }

    private async Task WriteInitialSnapshotsAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        CancellationToken stoppingToken)
    {
       
        foreach (var parameter in plc.Parameters)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await ReadParameterAndWriteAsync(
                plc,
                plcManager,
                parameter,
                DatasetWriteReason.InitialSnapshot,
                stoppingToken);
        }

    }

    private void RegisterOnChangeNotifications(
        PlcConfig plc,
        PlcConnectionManager plcManager)
    {
        var onChangeParameters = plc.Parameters
            .Where(p => p.ReadMode == ParameterReadMode.OnChange)
            .ToList();

        if (onChangeParameters.Count == 0)
        {
           
            return;
        }

        foreach (var parameter in onChangeParameters)
        {
            plcManager.AddOnChangeNotification(parameter);
        }
    }
    private async Task RunParameterLoopAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        CancellationToken stoppingToken)
    {
        var periodicParameters = plc.Parameters
            .Where(p => p.ReadMode == ParameterReadMode.Periodic)
            .ToList();

        var allParameters = plc.Parameters
            .ToList();

        var nowUtc = DateTime.UtcNow;

        var nextPeriodicReadTimesUtc = periodicParameters.ToDictionary(
            parameter => GetParameterKey(plc, parameter),
            parameter => nowUtc.AddSeconds(GetReadIntervalSeconds(parameter)));

        var nextHealthCheckUtc =
            nowUtc.AddSeconds(_config.Ads.ReconnectIntervalSeconds);


        while (!stoppingToken.IsCancellationRequested)
        {
            nowUtc = DateTime.UtcNow;

            foreach (var parameter in periodicParameters)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                var key = GetParameterKey(plc, parameter);

                if (!nextPeriodicReadTimesUtc.TryGetValue(key, out var nextReadTimeUtc))
                {
                    nextReadTimeUtc = nowUtc.AddSeconds(GetReadIntervalSeconds(parameter));
                }

                if (nowUtc >= nextReadTimeUtc)
                {
                    await ReadParameterAndMaybeWriteAsync(
                        plc,
                        plcManager,
                        parameter,
                        DatasetWriteReason.Periodic,
                        stoppingToken);

                    nextPeriodicReadTimesUtc[key] =
                        DateTime.UtcNow.AddSeconds(
                            GetReadIntervalSeconds(parameter));
                }
            }

            foreach (var parameter in allParameters)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (IsForceWriteDue(
                        plc,
                        parameter,
                        DateTime.UtcNow))
                {
                    await ReadParameterAndWriteAsync(
                        plc,
                        plcManager,
                        parameter,
                        DatasetWriteReason.ForceWrite,
                        stoppingToken);
                }
            }

            if (allParameters.Count > 0 &&
                DateTime.UtcNow >= nextHealthCheckUtc)
            {
                await RunCommunicationHealthCheckAsync(
                    plc,
                    plcManager,
                    allParameters[0],
                    stoppingToken);

                nextHealthCheckUtc =
                    DateTime.UtcNow.AddSeconds(_config.Ads.ReconnectIntervalSeconds);
            }

            await Task.Delay(50, stoppingToken);
        }
    }

    private async Task RunCommunicationHealthCheckAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        ParameterConfig parameter,
        CancellationToken stoppingToken)
    {
        _ = await plcManager.ReadParameterValueAsync(
            parameter,
            stoppingToken);

        _logger.LogDebug(
            "Communication health check OK for PLC '{PlcName}'.",
            plc.Name);
    }

    private async Task ReadParameterAndMaybeWriteAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        ParameterConfig parameter,
        DatasetWriteReason reason,
        CancellationToken stoppingToken)
    {
        var value =
            await plcManager.ReadParameterValueAsync(
                parameter,
                stoppingToken);

        if (!ShouldAcceptValueByOffset(plc, parameter, value))
        {
            return;
        }

        EnqueueDatasetRecord(
            plc,
            parameter,
            value,
            reason);

        UpdateLastAcceptedValue(
            plc,
            parameter,
            value);
    }

    private async Task ReadParameterAndWriteAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        ParameterConfig parameter,
        DatasetWriteReason reason,
        CancellationToken stoppingToken)
    {
        var value =
            await plcManager.ReadParameterValueAsync(
                parameter,
                stoppingToken);

        EnqueueDatasetRecord(
            plc,
            parameter,
            value,
            reason);

        UpdateLastAcceptedValue(
            plc,
            parameter,
            value);
    }

    private void HandleAdsNotification(
        PlcConfig plc,
        AdsNotificationExEventArgs e)
    {
        try
        {
            if (e.UserData is not ParameterConfig parameter)
            {
                _logger.LogWarning(
                    "Received ADS notification from PLC '{PlcName}' but UserData is not ParameterConfig.",
                    plc.Name);

                return;
            }

            var value = e.Value;

            if (!ShouldAcceptOnChangeValue(
                    plc,
                    parameter,
                    value))
            {
                return;
            }

            EnqueueDatasetRecord(
                plc,
                parameter,
                value,
                DatasetWriteReason.OnChange);

            UpdateLastAcceptedValue(
                plc,
                parameter,
                value);

        }
        catch (Exception ex)
        {
            MarkConnectionLost(
                plc);

            _logger.LogWarning(
                ex,
                "Failed to handle ADS notification from PLC '{PlcName}'.",
                plc.Name);
        }
    }

    private bool ShouldAcceptOnChangeValue(
        PlcConfig plc,
        ParameterConfig parameter,
        object? currentValue)
    {
        var key = GetParameterKey(
            plc,
            parameter);

        if (!_lastAcceptedValues.TryGetValue(key, out var previousValue))
        {
            return true;
        }

        if (ValuesAreEqual(previousValue, currentValue))
        {
            _logger.LogDebug(
                "OnChange notification ignored for parameter '{ParameterName}' because value did not change. Value: {Value}",
                parameter.Name,
                currentValue);

            return false;
        }

        return ShouldAcceptValueByOffset(
            plc,
            parameter,
            currentValue);
    }

    private bool ShouldAcceptValueByOffset(
        PlcConfig plc,
        ParameterConfig parameter,
        object? currentValue)
    {
        var offset = parameter.Offset ?? 0;

        if (offset <= 0)
        {
            return true;
        }

        var key = GetParameterKey(
            plc,
            parameter);

        if (!TryConvertToDouble(currentValue, out var currentNumericValue))
        {
            return ShouldAcceptNonNumericChange(
                key,
                currentValue);
        }

        if (!_lastAcceptedValues.TryGetValue(key, out var previousValue))
        {
            return true;
        }

        if (!TryConvertToDouble(previousValue, out var previousNumericValue))
        {
            return true;
        }

        var difference =
            Math.Abs(currentNumericValue - previousNumericValue);

        if (difference >= offset)
        {
            return true;
        }

        _logger.LogDebug(
            "Value ignored for parameter '{ParameterName}'. Difference {Difference} is smaller than offset {Offset}.",
            parameter.Name,
            difference,
            offset);

        return false;
    }

    private bool ShouldAcceptNonNumericChange(
        string key,
        object? currentValue)
    {
        if (!_lastAcceptedValues.TryGetValue(key, out var previousValue))
        {
            return true;
        }

        return !Equals(previousValue, currentValue);
    }

    private bool IsForceWriteDue(
        PlcConfig plc,
        ParameterConfig parameter,
        DateTime nowUtc)
    {
        var key = GetParameterKey(
            plc,
            parameter);

        if (!_lastAcceptedTimesUtc.TryGetValue(key, out var lastAcceptedTimeUtc))
        {
            return true;
        }

        var forceWriteIntervalSeconds =
            GetForceWriteIntervalSeconds(
                plc,
                parameter);

        var elapsedSeconds =
            (nowUtc - lastAcceptedTimeUtc).TotalSeconds;

        return elapsedSeconds >= forceWriteIntervalSeconds;
    }

    private static bool ValuesAreEqual(
        object? previousValue,
        object? currentValue)
    {
        if (previousValue is null && currentValue is null)
        {
            return true;
        }

        if (previousValue is null || currentValue is null)
        {
            return false;
        }

        if (TryConvertToDouble(previousValue, out var previousNumericValue) &&
            TryConvertToDouble(currentValue, out var currentNumericValue))
        {
            return Math.Abs(previousNumericValue - currentNumericValue) < double.Epsilon;
        }

        return Equals(previousValue, currentValue);
    }

    private void EnqueueDatasetRecord(
        PlcConfig plc,
        ParameterConfig parameter,
        object? value,
        DatasetWriteReason reason)
    {
        var record = new DatasetRecord
        {
            PlcName = plc.Name,
            Ts = DateTimeOffset.Now,
            Name = parameter.Name,
            Mode = parameter.ReadMode?.ToString() ?? string.Empty,
            Reason = reason.ToString(),
            V = value
        };

        _datasetBuffer.Enqueue(record);

    }

    private void EnqueueConnectionEvent(
    PlcConfig plc,
    DatasetWriteReason reason)
    {
        var record = new DatasetRecord
        {
            PlcName = plc.Name,
            Ts = DateTimeOffset.Now,
            Name = ConnectionEventName,
            Mode = SystemMode,
            Reason = reason.ToString(),
            V = string.Empty
        };

        _datasetBuffer.Enqueue(record);

        switch (reason)
        {
            case DatasetWriteReason.ConnectionRestored:
                _logger.LogInformation(
                    "PLC '{PlcName}' connected.",
                    plc.Name);
                break;

            case DatasetWriteReason.ConnectionLost:
                _logger.LogWarning(
                    "PLC '{PlcName}' connection lost.",
                    plc.Name);
                break;
        }
    }

    private void UpdateLastAcceptedValue(
        PlcConfig plc,
        ParameterConfig parameter,
        object? value)
    {
        var key = GetParameterKey(
            plc,
            parameter);

        _lastAcceptedValues[key] = value;
        _lastAcceptedTimesUtc[key] = DateTime.UtcNow;
    }

    private static string GetParameterKey(
        PlcConfig plc,
        ParameterConfig parameter)
    {
        return $"{plc.Name}|{parameter.Name}|{parameter.VarAddress}";
    }

    private int GetReadIntervalSeconds(
        ParameterConfig parameter)
    {
        if (parameter.ReadIntervalSeconds.HasValue &&
            parameter.ReadIntervalSeconds.Value > 0)
        {
            return parameter.ReadIntervalSeconds.Value;
        }

        if (_config.Acquisition.DefaultPeriodicReadIntervalSeconds > 0)
        {
            return _config.Acquisition.DefaultPeriodicReadIntervalSeconds;
        }

        return 10;
    }

    private static int GetForceWriteIntervalSeconds(
        PlcConfig plc,
        ParameterConfig parameter)
    {
        if (parameter.ForceWriteIntervalSeconds.HasValue &&
            parameter.ForceWriteIntervalSeconds.Value > 0)
        {
            return parameter.ForceWriteIntervalSeconds.Value;
        }

        return GetDefaultForceWriteIntervalSeconds(plc);
    }

    private static int GetDefaultForceWriteIntervalSeconds(
        PlcConfig plc)
    {
        var retentionDays = plc.RetentionDays;

        if (retentionDays <= 0)
        {
            retentionDays = 30;
        }

        return retentionDays * 24 * 60 * 60;
    }

    private static bool TryConvertToDouble(
        object? value,
        out double result)
    {
        result = 0;

        if (value is null)
        {
            return false;
        }

        try
        {
            result = Convert.ToDouble(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunDatasetWriterLoopAsync(
        CancellationToken stoppingToken)
    {
        var writeIntervalSeconds = _config.Storage.WriteIntervalSeconds;

        if (writeIntervalSeconds <= 0)
        {
            writeIntervalSeconds = 10;
        }


        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_datasetBuffer.Count > 0)
                {
                    var writtenRecords =
                        await _datasetWriter.WritePendingRecordsAsync(stoppingToken);
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