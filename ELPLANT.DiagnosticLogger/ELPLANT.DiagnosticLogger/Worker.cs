using ELPLANT.DiagnosticLogger.Models.Config;

namespace ELPLANT.DiagnosticLogger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AppConfig _config;

    public Worker(
        ILogger<Worker> logger,
        AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Application: {ApplicationName}", _config.ApplicationName);
        _logger.LogInformation("System: {SystemName}", _config.SystemName);

        _logger.LogInformation("Dataset Folder: {Folder}",
            _config.Storage.DatasetFolder);

        _logger.LogInformation("Application Log Folder: {Folder}",
            _config.Storage.ApplicationLogFolder);

        _logger.LogInformation("Configured PLC count: {Count}",
            _config.Plcs.Count);

        foreach (var plc in _config.Plcs)
        {
            _logger.LogInformation(
                "PLC: {Name}, AMS: {AmsNetId}, Port: {Port}, Enabled: {Enabled}",
                plc.Name,
                plc.AmsNetId,
                plc.Port,
                plc.Enabled);

            foreach (var parameter in plc.Parameters)
            {
                _logger.LogInformation(
                    "Parameter: {Name}, Mode: {Mode}",
                    parameter.Name,
                    parameter.ReadMode);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
        }
    }
}