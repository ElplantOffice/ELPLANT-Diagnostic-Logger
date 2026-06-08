namespace ELPLANT.DiagnosticLogger.Models.Config;

public enum PlcConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}