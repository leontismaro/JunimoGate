using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.Core.OS;
using AndroidX.Fragment.App;
using AndroidX.Navigation.Fragment;
using Google.Android.Material.TextField;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.SettingsFragment")]
public sealed class SettingsFragment : Fragment
{
    private ILauncherUiHost? host;
    private MaterialAutoCompleteTextView? bindingPolicy;
    private MaterialAutoCompleteTextView? language;
    private bool syncing;
    private View? environmentCard;
    private View? saveBackupsCard;
    private SwitchCompat? addImportedMods;
    private SwitchCompat? confirmDeletion;
    private LauncherSettingsRepository? settingsRepository;
    private CancellationTokenSource? settingsCancellation;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_settings, container, false)
        ?? throw new InvalidOperationException("The Settings layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        bindingPolicy = view.FindViewById<MaterialAutoCompleteTextView>(Resource.Id.settings_binding_policy)
            ?? throw new InvalidOperationException("The dependency policy control is unavailable.");
        var labels = new[]
        {
            GetString(Resource.String.binding_highest_compatible),
            GetString(Resource.String.binding_strict),
            GetString(Resource.String.binding_first_loaded),
        };
        bindingPolicy.Adapter = new ArrayAdapter<string>(RequireContext(), global::Android.Resource.Layout.SimpleListItem1, labels);
        bindingPolicy.ItemClick += OnPolicyClicked;
        language = view.FindViewById<MaterialAutoCompleteTextView>(Resource.Id.settings_language)
            ?? throw new InvalidOperationException("The language control is unavailable.");
        language.Adapter = new ArrayAdapter<string>(
            RequireContext(),
            global::Android.Resource.Layout.SimpleListItem1,
            new[]
            {
                GetString(Resource.String.language_system),
                GetString(Resource.String.language_simplified_chinese),
                GetString(Resource.String.language_english),
            });
        language.ItemClick += OnLanguageClicked;
        environmentCard = view.FindViewById(Resource.Id.settings_environment_card);
        environmentCard!.Click += OnEnvironmentClicked;
        saveBackupsCard = view.FindViewById(Resource.Id.settings_save_backups_card);
        saveBackupsCard!.Click += OnSaveBackupsClicked;
        addImportedMods = view.FindViewById<SwitchCompat>(Resource.Id.settings_add_imported_mods)
            ?? throw new InvalidOperationException("The imported Mod setting is unavailable.");
        confirmDeletion = view.FindViewById<SwitchCompat>(Resource.Id.settings_confirm_mod_deletion)
            ?? throw new InvalidOperationException("The Mod deletion setting is unavailable.");
        addImportedMods.CheckedChange += OnAddImportedModsChanged;
        confirmDeletion.CheckedChange += OnConfirmDeletionChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        host = Activity as ILauncherUiHost
            ?? throw new InvalidOperationException("The Settings screen requires a launcher host.");
        host.LauncherStateChanged += OnStateChanged;
        Render(host.CurrentState);
        RenderLanguage();
        settingsCancellation = new CancellationTokenSource();
        settingsRepository = new LauncherSettingsRepository(Path.Combine(
            AndroidPrivateStorage.GetUserDataRoot(RequireContext()),
            "settings"));
        _ = LoadSettingsAsync(settingsCancellation.Token);
    }

    public override void OnStop()
    {
        if (host is not null)
            host.LauncherStateChanged -= OnStateChanged;
        host = null;
        settingsCancellation?.Cancel();
        settingsCancellation?.Dispose();
        settingsCancellation = null;
        settingsRepository = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (bindingPolicy is not null)
            bindingPolicy.ItemClick -= OnPolicyClicked;
        if (language is not null)
            language.ItemClick -= OnLanguageClicked;
        if (environmentCard is not null)
            environmentCard.Click -= OnEnvironmentClicked;
        if (saveBackupsCard is not null)
            saveBackupsCard.Click -= OnSaveBackupsClicked;
        if (addImportedMods is not null)
            addImportedMods.CheckedChange -= OnAddImportedModsChanged;
        if (confirmDeletion is not null)
            confirmDeletion.CheckedChange -= OnConfirmDeletionChanged;
        bindingPolicy = null;
        language = null;
        environmentCard = null;
        saveBackupsCard = null;
        addImportedMods = null;
        confirmDeletion = null;
        base.OnDestroyView();
    }

    private void OnStateChanged(LauncherState state) => Render(state);

    private void Render(LauncherState state)
    {
        if (bindingPolicy is null)
            return;
        syncing = true;
        bindingPolicy.SetText(GetString(state.AssemblyBindingPolicy switch
        {
            ModAssemblyBindingPolicy.HighestCompatible => Resource.String.binding_highest_compatible,
            ModAssemblyBindingPolicy.Strict => Resource.String.binding_strict,
            ModAssemblyBindingPolicy.FirstLoaded => Resource.String.binding_first_loaded,
            _ => Resource.String.binding_highest_compatible,
        }), filter: false);
        bindingPolicy.Enabled = state.CanConfigureProfile;
        syncing = false;
    }

    private void OnPolicyClicked(object? sender, AdapterView.ItemClickEventArgs eventArgs)
    {
        if (syncing)
            return;
        host?.UpdateBindingPolicy(eventArgs.Position switch
        {
            0 => ModAssemblyBindingPolicy.HighestCompatible,
            1 => ModAssemblyBindingPolicy.Strict,
            2 => ModAssemblyBindingPolicy.FirstLoaded,
            _ => throw new InvalidOperationException("The selected Mod dependency policy is invalid."),
        });
    }

    private void OnEnvironmentClicked(object? sender, EventArgs eventArgs) => host?.OpenEnvironment();

    private void OnSaveBackupsClicked(object? sender, EventArgs eventArgs) => host?.OpenSaveBackups();

    private void OnAddImportedModsChanged(object? sender, CompoundButton.CheckedChangeEventArgs eventArgs)
    {
        if (!syncing && settingsCancellation is { IsCancellationRequested: false } lifetime)
            _ = UpdateSettingsAsync(addImported: eventArgs.IsChecked, confirmDelete: null, lifetime.Token);
    }

    private void OnConfirmDeletionChanged(object? sender, CompoundButton.CheckedChangeEventArgs eventArgs)
    {
        if (!syncing && settingsCancellation is { IsCancellationRequested: false } lifetime)
            _ = UpdateSettingsAsync(addImported: null, confirmDelete: eventArgs.IsChecked, lifetime.Token);
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (settingsRepository is null)
            return;
        try
        {
            var settings = await settingsRepository.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded && !cancellationToken.IsCancellationRequested)
                Activity?.RunOnUiThread(() => RenderSettings(settings));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Settings", "settings-read-failed", exception);
        }
    }

    private async Task UpdateSettingsAsync(
        bool? addImported,
        bool? confirmDelete,
        CancellationToken cancellationToken)
    {
        if (settingsRepository is null)
            return;
        try
        {
            var current = await settingsRepository.ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = await settingsRepository.UpdateAsync(
                    current.Revision,
                    value => value with
                    {
                        AddImportedModsToActiveProfile = addImported ?? value.AddImportedModsToActiveProfile,
                        ConfirmLibraryDeletion = confirmDelete ?? value.ConfirmLibraryDeletion,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsAdded && !cancellationToken.IsCancellationRequested)
                Activity?.RunOnUiThread(() => RenderSettings(updated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Settings", "settings-update-failed", exception);
            await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RenderSettings(LauncherSettings settings)
    {
        if (addImportedMods is null || confirmDeletion is null)
            return;
        syncing = true;
        addImportedMods.Checked = settings.AddImportedModsToActiveProfile;
        confirmDeletion.Checked = settings.ConfirmLibraryDeletion;
        syncing = false;
    }

    private void RenderLanguage()
    {
        if (language is null)
            return;
        var tags = AppCompatDelegate.ApplicationLocales?.ToLanguageTags() ?? string.Empty;
        language.SetText(GetString(tags switch
        {
            "zh-CN" or "zh-Hans" => Resource.String.language_simplified_chinese,
            "en" => Resource.String.language_english,
            _ => Resource.String.language_system,
        }), filter: false);
    }

    private void OnLanguageClicked(object? sender, AdapterView.ItemClickEventArgs eventArgs)
    {
        var locales = eventArgs.Position switch
        {
            0 => LocaleListCompat.EmptyLocaleList,
            1 => LocaleListCompat.ForLanguageTags("zh-CN"),
            2 => LocaleListCompat.ForLanguageTags("en"),
            _ => throw new InvalidOperationException("The selected application language is invalid."),
        } ?? throw new InvalidOperationException("The selected application locale could not be created.");
        AppCompatDelegate.ApplicationLocales = locales;
    }
}
