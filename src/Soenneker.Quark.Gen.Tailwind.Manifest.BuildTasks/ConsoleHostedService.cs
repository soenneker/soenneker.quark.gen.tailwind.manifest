using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

/// <summary>
/// Represents the console hosted service.
/// </summary>
public sealed class ConsoleHostedService : IHostedService
{
    private readonly ILogger<ConsoleHostedService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IQuarkTailwindManifestGenerator _runner;
    private readonly BuildTasksCommandLineArgs _args;

    private int? _exitCode;

    public ConsoleHostedService(ILogger<ConsoleHostedService> logger, IHostApplicationLifetime appLifetime,
        IQuarkTailwindManifestGenerator runner, BuildTasksCommandLineArgs args)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _runner = runner;
        _args = args;
    }

    /// <summary>
    /// Starts the Console Hosted Service and begins its background work.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the Console Hosted Service has started.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _appLifetime.ApplicationStarted.Register(() =>
        {
            Task.Run(async () =>
            {
                _logger.LogInformation("Starting Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks...");

                try
                {
                    _exitCode = await _runner.Run(_args.Args, cancellationToken).AsTask();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unhandled exception");
                    _exitCode = 1;
                }
                finally
                {
                    _appLifetime.StopApplication();
                }
            }, cancellationToken);
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Console Hosted Service and waits for its background work to finish.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the Console Hosted Service has stopped.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
        _logger.LogInformation("Stopping Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks with exit code {ExitCode}.", Environment.ExitCode);
        return Task.CompletedTask;
    }
}
