using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Credentials;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ConfigImportSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Path to a profile JSON, e.g. profiles/microsoft.json.")]
    public required string File { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Accept the profile's review launch command without an interactive prompt. "
               + "Use only when the profile source is trusted (e.g. the in-repo Start-*.bat files).")]
    public bool Yes { get; init; }
}

/// <summary>
/// Imports a <see cref="ConfigProfile"/> (identity classes + review launch
/// command) into the user's config. Used by Start-internal.bat to apply the
/// Microsoft profile, but works for any org's profile file.
/// </summary>
internal sealed class ConfigImportCommand : AsyncCommand<ConfigImportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ConfigImportSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<ConfigImportCommand>();
        log.LogInformation("CLI config import started (file={File}, configPath={ConfigPath}, yes={Yes}).",
            settings.File, settings.ConfigPath ?? "<default>", settings.Yes);

        if (!System.IO.File.Exists(settings.File))
        {
            AnsiConsole.MarkupLine($"[red]Profile not found:[/] {Markup.Escape(settings.File)}");
            log.LogWarning("CLI config import failed: profile file not found: {File}.", settings.File);
            return 1;
        }

        ConfigProfile? profile;
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(settings.File);
            profile = JsonSerializer.Deserialize<ConfigProfile>(json, ConfigProfile.JsonOptions);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not parse profile:[/] {Markup.Escape(ex.Message)}");
            log.LogWarning(ex, "CLI config import failed to parse profile {File}.", settings.File);
            return 1;
        }

        if (profile is null)
        {
            AnsiConsole.MarkupLine("[red]Empty or invalid profile.[/]");
            log.LogWarning("CLI config import failed: profile deserialized to null for {File}.", settings.File);
            return 1;
        }

        var currentPlatform = PlatformKindDetector.Current;
        if (!profile.Supports(currentPlatform))
        {
            var declared = profile.SupportedPlatforms is { Count: > 0 }
                ? string.Join(", ", profile.SupportedPlatforms)
                : "<none>";
            AnsiConsole.MarkupLine(
                $"[red]Import rejected.[/] Profile does not support this platform ([bold]{currentPlatform}[/]). " +
                $"Supported platforms: [grey]{Markup.Escape(declared)}[/].");
            log.LogWarning("CLI config import rejected profile {File}: unsupported platform {Platform}.", settings.File, currentPlatform);
            return 1;
        }

        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);

        // SECURITY: LaunchCommand is the executable that runs on the next
        // Review click. Echo its full value and require explicit consent
        // before a profile (which may have been socially-engineered) can
        // persist it. --yes bypasses the prompt for the in-repo Start-*.bat
        // launchers that import the bundled profiles.
        var newLaunch = profile.ReviewLauncher?.LaunchCommand?.Trim();
        if (!string.IsNullOrWhiteSpace(newLaunch)
            && !string.Equals(newLaunch, config.ReviewLauncher.LaunchCommand, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[yellow]This profile sets the review launch command to:[/]");
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(newLaunch)}[/]");
            AnsiConsole.MarkupLine("[yellow]This command will be executed on your machine when you click Review.[/]");
            if (!settings.Yes)
            {
                // Fail closed when there's no interactive console (CI, piped
                // stdin): AnsiConsole.Confirm would otherwise read EOF and
                // could hang. Require an explicit --yes to apply this way.
                if (Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine("[red]Import aborted.[/] A profile launch command needs confirmation; "
                        + "re-run with [bold]--yes[/] to apply it non-interactively.");
                    log.LogWarning("CLI config import aborted due to non-interactive confirmation requirement for {File}.", settings.File);
                    return 1;
                }
                if (!AnsiConsole.Confirm("Apply this launch command?", defaultValue: false))
                {
                    AnsiConsole.MarkupLine("[red]Import aborted.[/] No changes were saved.");
                    log.LogInformation("CLI config import cancelled by user for {File}.", settings.File);
                    return 1;
                }
            }
        }

        var changes = profile.ApplyTo(config);
        if (changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Profile contained nothing to import.[/]");
            log.LogInformation("CLI config import completed with no-op for {File}.", settings.File);
            return 0;
        }

        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Imported[/] {Markup.Escape(string.Join(", ", changes))} from [cyan]{Markup.Escape(settings.File)}[/].");
        log.LogInformation("CLI config import completed (file={File}, changes={Changes}).", settings.File, string.Join(", ", changes));
        return 0;
    }
}
