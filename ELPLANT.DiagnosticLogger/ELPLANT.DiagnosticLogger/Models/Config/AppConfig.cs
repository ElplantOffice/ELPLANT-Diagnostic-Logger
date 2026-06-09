using TwinCAT.Ads.Configuration;

namespace ELPLANT.DiagnosticLogger.Models.Config;

public class AppConfig
{
    public string ApplicationName { get; set; } = string.Empty;

    public string SystemName { get; set; } = string.Empty;

    public StorageConfig Storage { get; set; } = new();

    public AdsConfig Ads { get; set; } = new();

    public AcquisitionConfig Acquisition { get; set; } = new();

    public List<PlcConfig> Plcs { get; set; } = [];
}