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

    private readonly ConcurrentDictionary<string, object?> _lastAcceptedValues = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAcceptedTimesUtc = new();
    private readonly ConcurrentDictionary<string, bool> _connectionLostState = new();

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
            "Write Interval: {WriteIntervalSeconds} second(s)",
            _config.Storage.WriteIntervalSeconds);

        _logger.LogInformation(
            "ADS: ConnectTimeout={ConnectTimeoutSeconds}s, ReadTimeout={ReadTimeoutSeconds}s, ReconnectInterval={ReconnectIntervalSeconds}s",
            _config.Ads.ConnectTimeoutSeconds,
            _config.Ads.ReadTimeoutSeconds,
            _config.Ads.ReconnectIntervalSeconds);

        _logger.LogInformation(
            "Acquisition: DefaultPeriodicReadInterval={DefaultPeriodicReadIntervalSeconds}s",
            _config.Acquisition.DefaultPeriodicReadIntervalSeconds);

        _logger.LogInformation(
            "Configured PLC count: {Count}",
            _config.Plcs.Count);

        foreach (var plc in _config.Plcs)
        {
            _logger.LogInformation(
                "PLC: {Name}, AMS: {AmsNetId}, Port: {Port}, Enabled: {Enabled}, RetentionDays: {RetentionDays}",
                plc.Name,
                plc.AmsNetId,
                plc.Port,
                plc.Enabled,
                plc.RetentionDays);

            foreach (var parameter in plc.Parameters)
            {
                _logger.LogInformation(
                    "Parameter: {Name}, Mode: {Mode}, Address: {Address}, Type: {Type}, ReadIntervalSeconds: {ReadIntervalSeconds}, Offset: {Offset}, ForceWriteIntervalSeconds: {ForceWriteIntervalSeconds}",
                    parameter.Name,
                    parameter.ReadMode,
                    parameter.VarAddress,
                    parameter.VarType ?? "default",
                    GetReadIntervalSeconds(parameter),
                    parameter.Offset ?? 0,
                    GetForceWriteIntervalSeconds(plc, parameter));
            }
        }
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

                _logger.LogWarning(
                    ex,
                    "PLC '{PlcName}' session failed and will be restarted after {ReconnectIntervalSeconds} second(s).",
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

        _logger.LogInformation(
            "Connecting to PLC '{PlcName}'...",
            plc.Name);

        var connected =
            await plcManager.ConnectAsync(stoppingToken);

        if (!connected)
        {
            MarkConnectionLost(
                plc);

            _logger.LogWarning(
                "ADS connection failed for PLC '{PlcName}'. Will retry after {ReconnectIntervalSeconds} second(s).",
                plc.Name,
                _config.Ads.ReconnectIntervalSeconds);

            return;
        }

        await VerifyPlcCommunicationAsync(
            plc,
            plcManager,
            stoppingToken);

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

        _logger.LogInformation(
            "Verifying ADS communication for PLC '{PlcName}' by reading configured parameters...",
            plc.Name);

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

        _logger.LogInformation(
            "ADS communication verified for PLC '{PlcName}'.",
            plc.Name);
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

        if (!_connectionLostState.TryGetValue(key, out var wasLost) ||
            !wasLost)
        {
            return;
        }

        _connectionLostState[key] = false;

        EnqueueConnectionEvent(
            plc,
            DatasetWriteReason.ConnectionRestored);
    }

    private async Task WriteInitialSnapshotsAsync(
        PlcConfig plc,
        PlcConnectionManager plcManager,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Writing initial snapshots for PLC '{PlcName}'.",
            plc.Name);

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

        _logger.LogInformation(
            "Initial snapshots completed for PLC '{PlcName}'.",
            plc.Name);
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
            _logger.LogInformation(
                "PLC '{PlcName}' has no OnChange parameters.",
                plc.Name);

            return;
        }

        foreach (var parameter in onChangeParameters)
        {
            plcManager.AddOnChangeNotification(parameter);

            _logger.LogInformation(
                "OnChange notification registered for PLC '{PlcName}', parameter '{ParameterName}', type {Type}, offset {Offset}, force interval {ForceWriteIntervalSeconds} second(s).",
                plc.Name,
                parameter.Name,
                parameter.VarType ?? "default",
                parameter.Offset ?? 0,
                GetForceWriteIntervalSeconds(plc, parameter));
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

        _logger.LogInformation(
            "Started parameter loop for PLC '{PlcName}'. Periodic parameters: {PeriodicCount}, total parameters with force snapshot: {TotalCount}.",
            plc.Name,
            periodicParameters.Count,
            allParameters.Count);

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

            _logger.LogInformation(
                "OnChange notification accepted from PLC '{PlcName}', parameter '{ParameterName}' = {Value}",
                plc.Name,
                parameter.Name,
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

        if (reason == DatasetWriteReason.InitialSnapshot ||
            reason == DatasetWriteReason.ForceWrite)
        {
            _logger.LogInformation(
                "{Reason} enqueued for PLC '{PlcName}', parameter '{ParameterName}' = {Value}",
                reason,
                plc.Name,
                parameter.Name,
                value);
        }
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

        _logger.LogInformation(
            "Connection event for PLC '{PlcName}': {Reason}",
            plc.Name,
            reason);
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

        _logger.LogInformation(
            "DatasetWriter loop started. Write interval: {Seconds} second(s).",
            writeIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_datasetBuffer.Count > 0)
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