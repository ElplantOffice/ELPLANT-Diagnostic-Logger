using ELPLANT.DiagnosticLogger.Models.Config;
using Serilog;

namespace ELPLANT.DiagnosticLogger;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var appConfig = builder.Configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Application configuration could not be loaded.");

        Directory.CreateDirectory(appConfig.Storage.ApplicationLogFolder);
        Directory.CreateDirectory(appConfig.Storage.DatasetFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(appConfig.Storage.ApplicationLogFolder, "ApplicationLog-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: appConfig.Storage.ApplicationLogRetentionDays,
                shared: true)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);

        builder.Services.AddSingleton(appConfig);
        builder.Services.AddHostedService<Worker>();

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = $"{appConfig.ApplicationName} - {appConfig.SystemName}";
        });

        var host = builder.Build();

        try
        {
            Log.Information("Starting {ApplicationName} for system {SystemName}.",
                appConfig.ApplicationName,
                appConfig.SystemName);

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
}