using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.OS;
using AndroidX.Fragment.App;
using AndroidX.Navigation.Fragment;
using Google.Android.Material.TextField;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;

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
    }

    public override void OnStart()
    {
        base.OnStart();
        host = Activity as ILauncherUiHost
            ?? throw new InvalidOperationException("The Settings screen requires a launcher host.");
        host.LauncherStateChanged += OnStateChanged;
        Render(host.CurrentState);
        RenderLanguage();
    }

    public override void OnStop()
    {
        if (host is not null)
            host.LauncherStateChanged -= OnStateChanged;
        host = null;
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
        bindingPolicy = null;
        language = null;
        environmentCard = null;
        saveBackupsCard = null;
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
