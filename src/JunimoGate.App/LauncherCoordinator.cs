using Android.Content;
using Android.Util;
using JunimoGate.Android;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.App;

internal enum LauncherStatus
{
    Checking,
    NeedsPreparation,
    Preparing,
    RecoveryAvailable,
    Recovering,
    Ready,
    ModConfigurationInvalid,
    Launching,
    GameNotInstalled,
    Unsupported,
    Failed,
}

internal enum LauncherMessageKey
{
    CheckingInstalledGame,
    CheckingWorkspace,
    NeedsPreparation,
    RecoveryAvailable,
    PreparingGame,
    PreparedGameChanged,
    GameUpdated,
    Recovering,
    GameNotInstalled,
    Unsupported,
    Failed,
    Ready,
    ModConfigurationInvalid,
    Launching,
}

internal sealed record LauncherState(
    LauncherStatus Status,
    LauncherMessageKey Message,
    bool ShowProgress,
    bool CanLaunch,
    string? Detail = null,
    ModAssemblyBindingPolicy AssemblyBindingPolicy = ModAssemblyBindingPolicy.HighestCompatible,
    bool CanConfigureProfile = false);

internal sealed class LauncherCoordinator : IDisposable
{
    private readonly Context context;
    private readonly ModProfileRepository profiles;
    private readonly ModProfileV2Repository profilesV2;
    private readonly ActiveModProfileSelectionRepository activeProfiles;
    private readonly ModLibraryRepository library;
    private readonly LauncherSettingsRepository launcherSettings;
    private readonly string profilesRoot;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private ProfileId profileId = ProfileId.Parse("default");
    private PreparedGameHandle? preparedGame;
    private PendingGameLaunchOutcome? pendingRecovery;
    private ModProfile? profile;
    private LauncherSettings? settings;
    private bool disposed;

    public LauncherCoordinator(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
        var userData = AndroidPrivateStorage.GetUserDataRoot(this.context);
        profilesRoot = Path.Combine(userData, "profiles");
        profiles = new ModProfileRepository(profilesRoot);
        profilesV2 = new ModProfileV2Repository(profilesRoot);
        activeProfiles = new ActiveModProfileSelectionRepository(profilesRoot);
        library = new ModLibraryRepository(Path.Combine(userData, "mods"));
        launcherSettings = new LauncherSettingsRepository(Path.Combine(userData, "settings"));
        CurrentState = new LauncherState(
            LauncherStatus.Checking,
            LauncherMessageKey.CheckingInstalledGame,
            ShowProgress: true,
            CanLaunch: false);
    }

    public event Action<LauncherState>? StateChanged;

    public LauncherState CurrentState { get; private set; }

    public async Task<GameLaunchHandle?> InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(context, cancellationToken).ConfigureAwait(false);
            await LoadActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            var pending = await GameLaunchRegistry.TryReadPendingOutcomeAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                preparedGame = null;
                if (pending.Status == GameLaunchOutcomeStatus.Running)
                {
                    await GameLaunchRegistry.CompletePendingRunningAsync(context, pending, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    pendingRecovery = pending;
                    Publish(new LauncherState(
                        LauncherStatus.RecoveryAvailable,
                        LauncherMessageKey.RecoveryAvailable,
                        ShowProgress: false,
                        CanLaunch: true));
                    return null;
                }
            }
            pendingRecovery = null;

            if (preparedGame is not null)
            {
                Publish(ReadyState(preparedGame));
                return null;
            }

            Publish(new LauncherState(
                LauncherStatus.Checking,
                LauncherMessageKey.CheckingWorkspace,
                ShowProgress: true,
                CanLaunch: false));
            preparedGame = await GameLaunchRegistry.TryOpenActiveAsync(context, cancellationToken)
                .ConfigureAwait(false);
            Publish(preparedGame is null
                ? new LauncherState(
                    LauncherStatus.NeedsPreparation,
                    LauncherMessageKey.NeedsPreparation,
                    ShowProgress: false,
                    CanLaunch: true)
                : ReadyState(preparedGame));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "initialize-failed", exception);
            preparedGame = null;
            Publish(FailedState());
            return null;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<GameLaunchHandle?> TryCreateLaunchAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState.Status == LauncherStatus.RecoveryAvailable && pendingRecovery is not null)
            {
                var pending = pendingRecovery;
                pendingRecovery = null;
                return await RecoverPendingLaunchAsync(pending, cancellationToken).ConfigureAwait(false);
            }

            if (CurrentState.Status is LauncherStatus.NeedsPreparation or LauncherStatus.Failed)
            {
                Publish(new LauncherState(
                    LauncherStatus.Preparing,
                    LauncherMessageKey.PreparingGame,
                    ShowProgress: true,
                    CanLaunch: false));
                var retried = await GameDeepPrepareCoordinator
                    .PrepareAsync(context, new PreparationProgress(this), cancellationToken)
                    .ConfigureAwait(false);
                PublishPreparationResult(retried);
            }

            if (CurrentState.Status != LauncherStatus.Ready || preparedGame is null || profile is null)
                return null;

            PublishLaunching();
            var modSelection = await BuildCurrentModSelectionAsync(cancellationToken).ConfigureAwait(false);
            var issue = modSelection is null
                ? await GameLaunchRegistry.TryIssueLaunchAsync(
                    context,
                    preparedGame,
                    ProfileLaunchSelection.From(profile),
                    cancellationToken).ConfigureAwait(false)
                : await GameLaunchRegistry.TryIssueLaunchAsync(
                    context,
                    preparedGame,
                    modSelection,
                    cancellationToken).ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

            Log.Warn("JunimoGate.Launcher", $"launch-request-not-issued status={issue.Status}");

            if (issue.Status == GameLaunchIssueStatus.ActiveSnapshotChanged)
            {
                preparedGame = null;
                Publish(new LauncherState(
                    LauncherStatus.Checking,
                    LauncherMessageKey.PreparedGameChanged,
                    ShowProgress: true,
                    CanLaunch: false));
                var refreshed = await GameDeepPrepareCoordinator
                    .PrepareOrReuseAsync(context, new PreparationProgress(this), cancellationToken)
                    .ConfigureAwait(false);
                PublishPreparationResult(refreshed);
                return null;
            }

            if (issue.Status == GameLaunchIssueStatus.ProfileChanged)
            {
                await LoadActiveProfileAsync(cancellationToken).ConfigureAwait(false);
                Publish(ReadyState(preparedGame));
                return null;
            }

            Publish(new LauncherState(
                LauncherStatus.Preparing,
                LauncherMessageKey.GameUpdated,
                ShowProgress: true,
                CanLaunch: false));
            var prepared = await GameDeepPrepareCoordinator
                .PrepareAsync(context, new PreparationProgress(this), cancellationToken)
                .ConfigureAwait(false);
            PublishPreparationResult(prepared);
            if (!prepared.IsReady || preparedGame is null)
                return null;

            PublishLaunching();
            modSelection = await BuildCurrentModSelectionAsync(cancellationToken).ConfigureAwait(false);
            issue = modSelection is null
                ? await GameLaunchRegistry.TryIssueLaunchAsync(
                    context,
                    preparedGame,
                    ProfileLaunchSelection.From(profile),
                    cancellationToken).ConfigureAwait(false)
                : await GameLaunchRegistry.TryIssueLaunchAsync(
                    context,
                    preparedGame,
                    modSelection,
                    cancellationToken).ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

            Log.Error("JunimoGate.Launcher", $"launch-request-rejected-after-prepare status={issue.Status}");
            preparedGame = null;
            Publish(FailedState("launch_state_changed_after_prepare"));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModSelectionException exception)
        {
            Log.Error("JunimoGate.Mods", "launch-selection-failed", exception.InnerException ?? exception);
            Publish(ModConfigurationInvalidState());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "launch-request-failed", exception);
            preparedGame = null;
            Publish(FailedState());
        }
        finally
        {
            operationLock.Release();
        }

        return null;
    }

    public async ValueTask UpdateBindingPolicyAsync(
        ModAssemblyBindingPolicy policy,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState.Status != LauncherStatus.Ready || preparedGame is null || profile is null)
                return;
            settings = await launcherSettings.UpdateAsync(
                    settings?.Revision ?? throw new InvalidOperationException("The Launcher settings are unavailable."),
                    value => value with { DefaultAssemblyBindingPolicy = policy },
                    cancellationToken)
                .ConfigureAwait(false);
            Publish(ReadyState(preparedGame));
        }
        finally
        {
            operationLock.Release();
        }
    }

    public void ReportLaunchFailure()
    {
        if (!disposed)
        {
            preparedGame = null;
            pendingRecovery = null;
            Publish(FailedState());
        }
    }

    public async ValueTask RefreshProfileAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            if (preparedGame is not null && CurrentState.Status is LauncherStatus.Ready or LauncherStatus.ModConfigurationInvalid)
                Publish(ReadyState(preparedGame));
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<GameLaunchHandle?> RecoverLaunchDispatchFailureAsync(
        GameLaunchHandle launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        try
        {
            await GameLaunchRegistry.RecordOutcomeAsync(
                    context,
                    launch.Key,
                    GameLaunchOutcomeStatus.Failed,
                    GameStartupStage.LaunchRequest,
                    "activity_start_failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn("JunimoGate.Launcher", "launch-dispatch-outcome-failed", exception);
        }
        return await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameLaunchHandle?> RecoverPendingLaunchAsync(
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        pendingRecovery = null;
        Log.Warn(
            "JunimoGate.Recovery",
            $"startup-failure attempt={pending.AttemptId[..8]} stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
        await GameLaunchRegistry.CompletePendingFailureAsync(context, pending, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Code == "mod_loading_failed" || pending.RecoveryLevel >= 2)
        {
            Log.Error(
                "JunimoGate.Recovery",
                $"recovery-stopped stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
            Publish(FailedState(pending.Code));
            return null;
        }

        for (var level = pending.RecoveryLevel + 1; level <= 2; level++)
        {
            Log.Info(
                "JunimoGate.Recovery",
                $"recovery-started stage={pending.Stage} code={pending.Code} level={level}");
            Publish(new LauncherState(
                LauncherStatus.Recovering,
                LauncherMessageKey.Recovering,
                ShowProgress: true,
                CanLaunch: false));
            var prepared = await GameDeepPrepareCoordinator
                .RecoverAsync(
                    context,
                    pending.Snapshot,
                    pending.Stage,
                    level,
                    new PreparationProgress(this),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!prepared.IsReady)
            {
                Log.Warn(
                    "JunimoGate.Recovery",
                    $"recovery-prepare-failed status={prepared.Status} code={prepared.Code} level={level}");
                if (prepared.Status is GamePreparationStatus.GameNotInstalled or GamePreparationStatus.Unsupported || level == 2)
                {
                    PublishPreparationResult(prepared);
                    return null;
                }
                continue;
            }

            preparedGame = prepared.PreparedGame;
            PublishLaunching();
            var issue = pending.ModSelection is null
                ? await GameLaunchRegistry.TryIssueLegacyLaunchAsync(
                    context,
                    preparedGame!,
                    pending.Profile,
                    level,
                    cancellationToken).ConfigureAwait(false)
                : await GameLaunchRegistry.TryIssueLaunchAsync(
                    context,
                    preparedGame!,
                    pending.ModSelection,
                    level,
                    cancellationToken).ConfigureAwait(false);
            if (issue.IsIssued)
            {
                Log.Info("JunimoGate.Recovery", $"recovery-launch-issued level={level}");
                return issue.Launch;
            }
            Log.Warn(
                "JunimoGate.Recovery",
                $"recovery-launch-not-issued status={issue.Status} level={level}");
            if (level == 2)
                break;
        }

        preparedGame = null;
        pendingRecovery = null;
        Log.Error("JunimoGate.Recovery", "recovery-exhausted");
        Publish(FailedState("startup_recovery_exhausted"));
        return null;
    }

    private async ValueTask LoadActiveProfileAsync(CancellationToken cancellationToken)
    {
        var active = await activeProfiles
            .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
            .ConfigureAwait(false);
        profileId = active.Validate();
        profile = await profiles
            .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _ = await profilesV2
                .ReadAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            _ = await new LegacyModProfileMigrator(profilesRoot, library, profilesV2)
                .MigrateAsync(ProfileId.Parse("default"), "Default", cancellationToken)
                .ConfigureAwait(false);
            profile = await profiles
                .ReadAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
        }
        settings = await LauncherSettingsMigration.MigrateLegacyDefaultPolicyAsync(
                launcherSettings,
                profilesV2,
                profile.AssemblyBindingPolicy,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ModLaunchSelectionSnapshot?> BuildCurrentModSelectionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await LoadActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            ModProfileV2 selectedProfile;
            try
            {
                selectedProfile = await profilesV2.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException) when (profileId.Value == "default")
            {
                try
                {
                    var migration = await new LegacyModProfileMigrator(profilesRoot, library, profilesV2)
                        .MigrateAsync(profileId, "Default", cancellationToken)
                        .ConfigureAwait(false);
                    selectedProfile = migration.Profile;
                    profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
                    settings = await LauncherSettingsMigration.MigrateLegacyDefaultPolicyAsync(
                            launcherSettings,
                            profilesV2,
                            profile.AssemblyBindingPolicy,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or InvalidOperationException)
                {
                    Log.Warn("JunimoGate.Mods", "legacy-profile-migration-fallback", exception);
                    return null;
                }
            }

            var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
            var globalBindingPolicy = settings?.DefaultAssemblyBindingPolicy
                ?? throw new InvalidOperationException("The global Mod settings are unavailable.");
            return ModLaunchSelectionBuilder.Build(
                selectedProfile,
                index,
                globalBindingPolicy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            throw new ModSelectionException(exception);
        }
    }

    public void Dispose()
    {
        disposed = true;
        StateChanged = null;
    }

    private void Publish(LauncherState state)
    {
        if (disposed || CurrentState == state)
            return;
        if (settings is not null)
            state = state with { AssemblyBindingPolicy = settings.DefaultAssemblyBindingPolicy };
        CurrentState = state;
        Log.Info("JunimoGate.Launcher", $"state:{state.Status}");
        StateChanged?.Invoke(state);
    }

    private void PublishPreparationResult(GamePreparationResult result)
    {
        Log.Info(
            "JunimoGate.Launcher",
            $"preparation-result status={result.Status} code={result.Code} ready={(result.IsReady ? 1 : 0)}");
        preparedGame = result.IsReady ? result.PreparedGame : null;
        Publish(Map(result));
    }

    private LauncherState Map(GamePreparationResult result) => result.Status switch
    {
        GamePreparationStatus.Ready => ReadyState(result.PreparedGame!),
        GamePreparationStatus.GameNotInstalled => new LauncherState(
            LauncherStatus.GameNotInstalled,
            LauncherMessageKey.GameNotInstalled,
            ShowProgress: false,
            CanLaunch: false),
        GamePreparationStatus.Unsupported => new LauncherState(
            LauncherStatus.Unsupported,
            LauncherMessageKey.Unsupported,
            ShowProgress: false,
            CanLaunch: false,
            Detail: result.Code),
        _ => FailedState(result.Code),
    };

    private static LauncherState FailedState(string code = "launcher_failed") => new(
        LauncherStatus.Failed,
        LauncherMessageKey.Failed,
        ShowProgress: false,
        CanLaunch: true,
        Detail: code);

    private LauncherState ReadyState(PreparedGameHandle handle) => new(
        LauncherStatus.Ready,
        LauncherMessageKey.Ready,
        ShowProgress: false,
        CanLaunch: true,
        Detail: handle.VersionName,
        AssemblyBindingPolicy: settings?.DefaultAssemblyBindingPolicy ?? ModAssemblyBindingPolicy.HighestCompatible,
        CanConfigureProfile: true);

    private LauncherState ModConfigurationInvalidState() => new(
        LauncherStatus.ModConfigurationInvalid,
        LauncherMessageKey.ModConfigurationInvalid,
        ShowProgress: false,
        CanLaunch: false,
        AssemblyBindingPolicy: settings?.DefaultAssemblyBindingPolicy ?? ModAssemblyBindingPolicy.HighestCompatible,
        CanConfigureProfile: true);

    private void PublishLaunching() => Publish(new LauncherState(
        LauncherStatus.Launching,
        LauncherMessageKey.Launching,
        ShowProgress: true,
        CanLaunch: false));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PreparationProgress(LauncherCoordinator owner) : IProgress<GamePreparationProgress>
    {
        public void Report(GamePreparationProgress value)
        {
            var status = value.Stage == GamePreparationStage.Preparing
                ? LauncherStatus.Preparing
                : LauncherStatus.Checking;
            var message = value.Stage switch
            {
                GamePreparationStage.Checking => LauncherMessageKey.CheckingWorkspace,
                GamePreparationStage.Discovering => LauncherMessageKey.CheckingInstalledGame,
                _ => LauncherMessageKey.PreparingGame,
            };
            owner.Publish(new LauncherState(
                status,
                message,
                ShowProgress: true,
                CanLaunch: false));
        }
    }

    private sealed class ModSelectionException(Exception innerException)
        : Exception("The current Mod launch selection is invalid.", innerException);
}
