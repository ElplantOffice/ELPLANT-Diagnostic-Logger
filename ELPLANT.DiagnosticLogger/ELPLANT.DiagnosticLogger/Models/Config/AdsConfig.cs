namespace ELPLANT.DiagnosticLogger.Models.Config;

public class AdsConfig
{
    public int ConnectTimeoutSeconds { get; set; } = 5;

    public int ReadTimeoutSeconds { get; set; } = 2;

    public int ReconnectIntervalSeconds { get; set; } = 10;
}