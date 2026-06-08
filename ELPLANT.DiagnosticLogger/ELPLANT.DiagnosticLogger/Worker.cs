using ELPLANT.DiagnosticLogger.Models.Config;
using ELPLANT.DiagnosticLogger.Services.Ads;

namespace ELPLANT.DiagnosticLogger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AppConfig _config;

    public Worker(
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        AppConfig config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            "Configured PLC count: {Count}",
            _config.Plcs.Count);

        foreach (var plc in _config.Plcs)
        {
            if (!plc.Enabled)
            {
                _logger.LogInformation(
                    "PLC '{PlcName}' is disabled. Skipping.",
                    plc.Name);

                continue;
            }

            _logger.LogInformation(
                "Testing ADS connection to PLC '{PlcName}'...",
                plc.Name);

            var plcLogger =
                _loggerFactory.CreateLogger<PlcConnectionManager>();

            using var plcManager =
                new PlcConnectionManager(
                    plc,
                    plcLogger);

            var connected =
                await plcManager.ConnectAsync();

            if (connected)
            {
                _logger.LogInformation(
                    "ADS connection test successful for PLC '{PlcName}'.",
                    plc.Name);
            }
            else
            {
                _logger.LogWarning(
                    "ADS connection test failed for PLC '{PlcName}'.",
                    plc.Name);
            }
        }

        _logger.LogInformation(
            "Initial ADS connection test completed.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
        }
    }
}