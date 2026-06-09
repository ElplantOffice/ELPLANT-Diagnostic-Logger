using TwinCAT.Ads;
using ELPLANT.DiagnosticLogger.Models.Config;

namespace ELPLANT.DiagnosticLogger.Services.Ads;

public class PlcConnectionManager : IDisposable
{
    private readonly PlcConfig _config;
    private readonly AdsConfig _adsConfig;
    private readonly ILogger<PlcConnectionManager> _logger;
    private readonly List<uint> _notificationHandles = [];

    private AdsClient? _adsClient;

    public event EventHandler<AdsNotificationExEventArgs>? OnAdsNotificationReceived;

    public PlcConnectionState State { get; private set; }
        = PlcConnectionState.Disconnected;

    public PlcConfig Config => _config;

    public bool IsConnected =>
        State == PlcConnectionState.Connected;

    public PlcConnectionManager(
        PlcConfig config,
        AdsConfig adsConfig,
        ILogger<PlcConnectionManager> logger)
    {
        _config = config;
        _adsConfig = adsConfig;
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            State = PlcConnectionState.Connecting;

            _adsClient = new AdsClient();

            _adsClient.AdsNotificationEx += AdsClient_AdsNotificationEx;

            var address = new AmsAddress(
                _config.AmsNetId,
                _config.Port);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeoutCts.CancelAfter(
                TimeSpan.FromSeconds(_adsConfig.ConnectTimeoutSeconds));

            await _adsClient.ConnectAsync(
                address,
                timeoutCts.Token);

            State = PlcConnectionState.Connected;

            _logger.LogInformation(
                "PLC '{PlcName}' connected. AMS={AmsNetId}, Port={Port}",
                _config.Name,
                _config.AmsNetId,
                _config.Port);

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            State = PlcConnectionState.Disconnected;

            _logger.LogWarning(
                "PLC '{PlcName}' connection timeout after {TimeoutSeconds} second(s).",
                _config.Name,
                _adsConfig.ConnectTimeoutSeconds);

            Disconnect();

            return false;
        }
        catch (Exception ex)
        {
            State = PlcConnectionState.Disconnected;

            _logger.LogWarning(
                ex,
                "PLC '{PlcName}' connection failed.",
                _config.Name);

            Disconnect();

            return false;
        }
    }

    public uint AddOnChangeNotification(ParameterConfig parameter)
    {
        if (_adsClient is null)
        {
            State = PlcConnectionState.Disconnected;

            throw new InvalidOperationException(
                $"PLC '{_config.Name}' is not connected.");
        }

        var dotNetType = ResolveDotNetType(parameter);

        var settings = new NotificationSettings(
            AdsTransMode.OnChange,
            cycleTime: 100,
            maxDelay: 0);

        var handle = _adsClient.AddDeviceNotificationEx(
            parameter.VarAddress,
            settings,
            parameter,
            dotNetType);

        _notificationHandles.Add(handle);

        _logger.LogInformation(
            "Registered OnChange notification for PLC '{PlcName}', parameter '{ParameterName}', type {Type}, offset {Offset}, handle {Handle}.",
            _config.Name,
            parameter.Name,
            dotNetType.Name,
            parameter.Offset ?? 0,
            handle);

        return handle;
    }

    public async Task<object?> ReadParameterValueAsync(
        ParameterConfig parameter,
        CancellationToken cancellationToken)
    {
        if (parameter.ReadMode is null)
        {
            throw new InvalidOperationException(
                $"Parameter '{parameter.Name}' on PLC '{_config.Name}' has no ReadMode.");
        }

        if (parameter.ReadMode == ParameterReadMode.OnChange ||
            parameter.ReadMode == ParameterReadMode.Periodic)
        {
            return await ReadValueByResolvedTypeAsync(
                parameter,
                cancellationToken);
        }

        throw new NotSupportedException(
            $"Parameter '{parameter.Name}' on PLC '{_config.Name}' has unsupported ReadMode '{parameter.ReadMode}'.");
    }

    public async Task<T> ReadValueAsync<T>(
        string variableName,
        CancellationToken cancellationToken)
    {
        if (_adsClient is null)
        {
            State = PlcConnectionState.Disconnected;

            throw new InvalidOperationException(
                $"PLC '{_config.Name}' is not connected.");
        }

        try
        {
            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeoutCts.CancelAfter(
                TimeSpan.FromSeconds(_adsConfig.ReadTimeoutSeconds));

            var result = await _adsClient.ReadValueAsync<T>(
                variableName,
                timeoutCts.Token);

            if (!result.Succeeded)
            {
                State = PlcConnectionState.Disconnected;

                throw new InvalidOperationException(
                    $"ADS read failed. PLC='{_config.Name}', Variable='{variableName}', ErrorCode='{result.ErrorCode}'.");
            }

            return result.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            State = PlcConnectionState.Disconnected;

            throw new TimeoutException(
                $"ADS read timeout after {_adsConfig.ReadTimeoutSeconds} second(s). PLC='{_config.Name}', Variable='{variableName}'.");
        }
        catch
        {
            State = PlcConnectionState.Disconnected;
            throw;
        }
    }

    private async Task<object?> ReadValueByResolvedTypeAsync(
        ParameterConfig parameter,
        CancellationToken cancellationToken)
    {
        var dotNetType = ResolveDotNetType(parameter);

        if (dotNetType == typeof(bool))
        {
            return await ReadValueAsync<bool>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(byte))
        {
            return await ReadValueAsync<byte>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(short))
        {
            return await ReadValueAsync<short>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(int))
        {
            return await ReadValueAsync<int>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(ushort))
        {
            return await ReadValueAsync<ushort>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(uint))
        {
            return await ReadValueAsync<uint>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(float))
        {
            return await ReadValueAsync<float>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(double))
        {
            return await ReadValueAsync<double>(
                parameter.VarAddress,
                cancellationToken);
        }

        if (dotNetType == typeof(string))
        {
            return await ReadValueAsync<string>(
                parameter.VarAddress,
                cancellationToken);
        }

        throw new NotSupportedException(
            $"Unsupported VarType '{parameter.VarType}' for PLC '{_config.Name}'.");
    }

    private static Type ResolveDotNetType(ParameterConfig parameter)
    {
        var varType = parameter.VarType?.Trim();

        if (string.IsNullOrWhiteSpace(varType))
        {
            return typeof(bool);
        }

        return varType switch
        {
            "System.Boolean" => typeof(bool),
            "System.Byte" => typeof(byte),
            "System.Int16" => typeof(short),
            "System.Int32" => typeof(int),
            "System.UInt16" => typeof(ushort),
            "System.UInt32" => typeof(uint),
            "System.Single" => typeof(float),
            "System.Double" => typeof(double),
            "System.String" => typeof(string),

            _ => throw new NotSupportedException(
                $"Unsupported VarType '{varType}' for parameter '{parameter.Name}'.")
        };
    }

    private void AdsClient_AdsNotificationEx(
        object? sender,
        AdsNotificationExEventArgs e)
    {
        OnAdsNotificationReceived?.Invoke(this, e);
    }

    public void Disconnect()
    {
        try
        {
            if (_adsClient is not null)
            {
                foreach (var handle in _notificationHandles)
                {
                    try
                    {
                        _adsClient.DeleteDeviceNotification(handle);

                        _logger.LogInformation(
                            "Deleted ADS notification handle {Handle} for PLC '{PlcName}'.",
                            handle,
                            _config.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to delete ADS notification handle {Handle} for PLC '{PlcName}'.",
                            handle,
                            _config.Name);
                    }
                }

                _notificationHandles.Clear();

                _adsClient.AdsNotificationEx -= AdsClient_AdsNotificationEx;

                _adsClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error while disconnecting PLC '{PlcName}'.",
                _config.Name);
        }
        finally
        {
            _adsClient = null;
            State = PlcConnectionState.Disconnected;
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}