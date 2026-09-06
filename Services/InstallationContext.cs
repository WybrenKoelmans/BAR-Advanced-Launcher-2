using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class InstallationContext : IInstallationContext
{
    private readonly ILogger<InstallationContext> _logger;
    private readonly ISettingsService _settings;
    private readonly IBarInstallationLocator _locator;
    private readonly IEngineCatalog _engines;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Task? _initialization;

    public InstallationContext(
        ILogger<InstallationContext> logger,
        ISettingsService settings,
        IBarInstallationLocator locator,
        IEngineCatalog engines)
    {
        _logger = logger;
        _settings = settings;
        _locator = locator;
        _engines = engines;
    }

    public BarInstallation? Current { get; private set; }

    public IReadOnlyList<EngineBuild> Engines { get; private set; } = Array.Empty<EngineBuild>();

    public IReadOnlyList<InstallationProbe> LastProbes { get; private set; } = Array.Empty<InstallationProbe>();

    public bool IsBusy { get; private set; }

    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _initialization ??= RunInitializeAsync(cancellationToken);

    private async Task RunInitializeAsync(CancellationToken cancellationToken)
    {
        // Step 1 of the explicit sequence: settings must be readable before the locator
        // can consider the remembered path.
        await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        try
        {
            LastProbes = await _locator.ProbeAllAsync(cancellationToken).ConfigureAwait(false);

            BarInstallation? installation = null;
            foreach (InstallationProbe probe in LastProbes)
            {
                if (probe.IsValid)
                {
                    installation = probe.Installation;
                    break;
                }
            }

            await ApplyAsync(installation, remember: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task<InstallationProbe> SetInstallationAsync(
        string rootPath,
        InstallationSource source = InstallationSource.ManualPick,
        CancellationToken cancellationToken = default)
    {
        InstallationProbe probe = _locator.Validate(rootPath, source);

        if (!probe.IsValid)
        {
            _logger.LogWarning("Rejected {Path} as a BAR install: {Reason}.", probe.Path, probe.Reason);
            return probe;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        try
        {
            await ApplyAsync(probe.Installation, remember: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }

        RaiseChanged();
        return probe;
    }

    /// <summary>
    /// Step 2-4: adopt an install, discover its engines, and persist the choice. The
    /// caller holds <see cref="_gate"/> and raises <see cref="Changed"/> afterwards, so
    /// observers never see an install whose engine list has not caught up yet.
    /// </summary>
    private async Task ApplyAsync(
        BarInstallation? installation,
        bool remember,
        CancellationToken cancellationToken)
    {
        Current = installation;

        if (installation is null)
        {
            Engines = Array.Empty<EngineBuild>();
            return;
        }

        Engines = await _engines.DiscoverAsync(installation, cancellationToken).ConfigureAwait(false);

        if (remember)
        {
            _settings.Current.RememberInstall(installation.RootPath);
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
