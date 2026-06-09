using ELPLANT.DiagnosticLogger.Models.Config;
using ELPLANT.DiagnosticLogger.Models.Dataset;
using ELPLANT.DiagnosticLogger.Services.Dataset;
using ELPLANT.DiagnosticLogger.Services.Retention;
using Serilog;

namespace ELPLANT.DiagnosticLogger;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var appConfig = builder.Configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Application configuration could not be loaded.");

        ValidateConfiguration(appConfig);

        Directory.CreateDirectory(appConfig.Storage.ApplicationLogFolder);
        Directory.CreateDirectory(appConfig.Storage.DatasetFolder);

        var applicationLogRetentionDays =
            GetApplicationLogRetentionDays(appConfig);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(appConfig.Storage.ApplicationLogFolder, "ApplicationLog-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: applicationLogRetentionDays,
                shared: true)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);

        builder.Services.AddSingleton(appConfig);
        builder.Services.AddSingleton<DatasetBuffer>();
        builder.Services.AddSingleton<DatasetRetentionService>();
        builder.Services.AddSingleton<DatasetWriter>();

        builder.Services.AddHostedService<Worker>();

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = $"{appConfig.ApplicationName} - {appConfig.SystemName}";
        });

        var host = builder.Build();

        try
        {
            Log.Information(
                "Starting {ApplicationName} for system {SystemName}.",
                appConfig.ApplicationName,
                appConfig.SystemName);

            Log.Information(
                "Application log retention calculated from PLC RetentionDays. Retention: {RetentionDays} day(s).",
                applicationLogRetentionDays);

            Log.Information(
                "ADS defaults: ConnectTimeout={ConnectTimeoutSeconds}s, ReadTimeout={ReadTimeoutSeconds}s, ReconnectInterval={ReconnectIntervalSeconds}s.",
                appConfig.Ads.ConnectTimeoutSeconds,
                appConfig.Ads.ReadTimeoutSeconds,
                appConfig.Ads.ReconnectIntervalSeconds);

            Log.Information(
                "Acquisition defaults: DefaultPeriodicReadInterval={DefaultPeriodicReadIntervalSeconds}s.",
                appConfig.Acquisition.DefaultPeriodicReadIntervalSeconds);

            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service terminated unexpectedly.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int GetApplicationLogRetentionDays(AppConfig appConfig)
    {
        var enabledPlcs = appConfig.Plcs
            .Where(plc => plc.Enabled)
            .ToList();

        if (enabledPlcs.Count == 0)
        {
            return 30;
        }

        var maxRetentionDays = enabledPlcs
            .Max(plc => plc.RetentionDays);

        if (maxRetentionDays <= 0)
        {
            return 30;
        }

        return maxRetentionDays;
    }

    private static void ValidateConfiguration(AppConfig appConfig)
    {
        if (string.IsNullOrWhiteSpace(appConfig.ApplicationName))
        {
            throw new InvalidOperationException("ApplicationName is required.");
        }

        if (string.IsNullOrWhiteSpace(appConfig.SystemName))
        {
            throw new InvalidOperationException("SystemName is required.");
        }

        if (string.IsNullOrWhiteSpace(appConfig.Storage.DatasetFolder))
        {
            throw new InvalidOperationException("Storage.DatasetFolder is required.");
        }

        if (string.IsNullOrWhiteSpace(appConfig.Storage.ApplicationLogFolder))
        {
            throw new InvalidOperationException("Storage.ApplicationLogFolder is required.");
        }

        if (appConfig.Storage.WriteIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Storage.WriteIntervalSeconds must be greater than 0.");
        }

        if (appConfig.Ads.ConnectTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Ads.ConnectTimeoutSeconds must be greater than 0.");
        }

        if (appConfig.Ads.ReadTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Ads.ReadTimeoutSeconds must be greater than 0.");
        }

        if (appConfig.Ads.ReconnectIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Ads.ReconnectIntervalSeconds must be greater than 0.");
        }

        if (appConfig.Acquisition.DefaultPeriodicReadIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Acquisition.DefaultPeriodicReadIntervalSeconds must be greater than 0.");
        }

        foreach (var plc in appConfig.Plcs)
        {
            if (string.IsNullOrWhiteSpace(plc.Name))
            {
                throw new InvalidOperationException("PLC Name is required.");
            }

            if (string.IsNullOrWhiteSpace(plc.AmsNetId))
            {
                throw new InvalidOperationException($"PLC '{plc.Name}' AmsNetId is required.");
            }

            if (plc.Port <= 0)
            {
                throw new InvalidOperationException($"PLC '{plc.Name}' Port must be greater than 0.");
            }

            if (plc.RetentionDays <= 0)
            {
                throw new InvalidOperationException($"PLC '{plc.Name}' RetentionDays must be greater than 0.");
            }

            foreach (var parameter in plc.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name))
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}' contains parameter without Name.");
                }

                if (string.IsNullOrWhiteSpace(parameter.VarAddress))
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}', parameter '{parameter.Name}' VarAddress is required.");
                }

                if (parameter.ReadMode is null)
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}', parameter '{parameter.Name}' ReadMode is required.");
                }

                if (parameter.ReadIntervalSeconds.HasValue &&
                    parameter.ReadIntervalSeconds.Value <= 0)
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}', parameter '{parameter.Name}' ReadIntervalSeconds must be greater than 0.");
                }

                if (parameter.ForceWriteIntervalSeconds.HasValue &&
                    parameter.ForceWriteIntervalSeconds.Value <= 0)
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}', parameter '{parameter.Name}' ForceWriteIntervalSeconds must be greater than 0.");
                }

                if (parameter.Offset.HasValue &&
                    parameter.Offset.Value < 0)
                {
                    throw new InvalidOperationException($"PLC '{plc.Name}', parameter '{parameter.Name}' Offset cannot be negative.");
                }
            }
        }
    }
}