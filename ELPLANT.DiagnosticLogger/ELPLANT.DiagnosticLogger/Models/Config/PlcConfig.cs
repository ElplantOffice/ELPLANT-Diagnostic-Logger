namespace ELPLANT.DiagnosticLogger.Models.Config;

public class PlcConfig
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string AmsNetId { get; set; } = string.Empty;

    public int Port { get; set; }

    public int RetentionDays { get; set; }

    public List<ParameterConfig> Parameters { get; set; } = [];
}