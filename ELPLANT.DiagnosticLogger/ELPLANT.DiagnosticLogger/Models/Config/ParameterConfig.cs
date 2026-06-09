namespace ELPLANT.DiagnosticLogger.Models.Config;

public class ParameterConfig
{
    public string Name { get; set; } = string.Empty;

    public string VarAddress { get; set; } = string.Empty;

    public string? VarType { get; set; }

    public ParameterReadMode? ReadMode { get; set; }

    public int? ReadIntervalSeconds { get; set; }

    public double? Offset { get; set; }

    public int? ForceWriteIntervalSeconds { get; set; }
}