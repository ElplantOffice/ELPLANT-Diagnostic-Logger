using TwinCAT.Ads;
using ELPLANT.DiagnosticLogger.Models.Config;

namespace ELPLANT.DiagnosticLogger.Services.Ads;

public class PlcConnectionManager : IDisposable
{
    private readonly PlcConfig _config;
    private readonly ILogger<PlcConnectionManager> _logger;

    private AdsClient? _adsClient;

    public PlcConnectionState State { get; private set; }
        = PlcConnectionState.Disconnected;

    public PlcConfig Config => _config;

    public bool IsConnected =>
        State == PlcConnectionState.Connected;

    public PlcConnectionManager(
        PlcConfig config,
        ILogger<PlcConnectionManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            State = PlcConnectionState.Connecting;

            _adsClient = new AdsClient();

            var address = new AmsAddress(
                _config.AmsNetId,
                _config.Port);

            await _adsClient.ConnectAsync(
                address,
                CancellationToken.None);

            State = PlcConnectionState.Connected;

            _logger.LogInformation(
                "PLC '{PlcName}' connected. AMS={AmsNetId}, Port={Port}",
                _config.Name,
                _config.AmsNetId,
                _config.Port);

            return true;
        }
        catch (Exception ex)
        {
            State = PlcConnectionState.Disconnected;

            _logger.LogWarning(
                ex,
                "PLC '{PlcName}' connection failed.",
                _config.Name);

            return false;
        }
    }

    public async Task<object?> ReadParameterValueAsync(ParameterConfig parameter)
    {
        if (parameter.ReadMode == ParameterReadMode.OnChange)
        {
            // In v1.0, OnChange parameters are treated as Boolean.
            return await ReadValueAsync<bool>(parameter.VarAddress);
        }

        if (parameter.ReadMode == ParameterReadMode.Periodic)
        {
            if (string.IsNullOrWhiteSpace(parameter.VarType))
            {
                _logger.LogWarning(
                    "Parameter '{ParameterName}' on PLC '{PlcName}' is Periodic but VarType is missing.",
                    parameter.Name,
                    _config.Name);

                return null;
            }

            return await ReadValueByTypeAsync(
                parameter.VarAddress,
                parameter.VarType);
        }

        _logger.LogWarning(
            "Parameter '{ParameterName}' on PLC '{PlcName}' has unsupported ReadMode '{ReadMode}'.",
            parameter.Name,
            _config.Name,
            parameter.ReadMode);

        return null;
    }

    public async Task<T?> ReadValueAsync<T>(string variableName)
    {
        if (_adsClient is null)
        {
            throw new InvalidOperationException(
                $"PLC '{_config.Name}' is not connected.");
        }

        try
        {
            var result = await _adsClient.ReadValueAsync<T>(
                variableName,
                CancellationToken.None);

            return result.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read variable '{VariableName}' from PLC '{PlcName}'.",
                variableName,
                _config.Name);

            return default;
        }
    }

    private async Task<object?> ReadValueByTypeAsync(
        string variableName,
        string varType)
    {
        return varType.Trim() switch
        {
            "System.Boolean" => await ReadValueAsync<bool>(variableName),
            "System.Byte" => await ReadValueAsync<byte>(variableName),
            "System.Int16" => await ReadValueAsync<short>(variableName),
            "System.Int32" => await ReadValueAsync<int>(variableName),
            "System.UInt16" => await ReadValueAsync<ushort>(variableName),
            "System.UInt32" => await ReadValueAsync<uint>(variableName),
            "System.Single" => await ReadValueAsync<float>(variableName),
            "System.Double" => await ReadValueAsync<double>(variableName),
            "System.String" => await ReadValueAsync<string>(variableName),

            _ => throw new NotSupportedException(
                $"Unsupported VarType '{varType}' for PLC '{_config.Name}'.")
        };
    }

    public void Disconnect()
    {
        try
        {
            _adsClient?.Dispose();
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