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