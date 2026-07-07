using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace PrInbox.Cli;

/// <summary>
/// Shared <see cref="ILoggerFactory"/> for CLI commands. Backed by a Serilog
/// file sink at Debug level so all service-level debug output lands in the
/// rolling log without mixing with Spectre.Console's terminal output.
/// Log path: %APPDATA%/PrInbox/logs/pr-inbox-YYYYMMDD.log
/// </summary>
internal static class CliLoggerFactory
{
    private static readonly Lazy<ILoggerFactory> _instance = new(Create);

    public static ILoggerFactory Instance => _instance.Value;

    private static ILoggerFactory Create()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrInbox", "logs");

        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, "pr-inbox-.log");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 7)
            .CreateLogger();

        return new SerilogLoggerFactory(serilog, dispose: true);
    }
}
