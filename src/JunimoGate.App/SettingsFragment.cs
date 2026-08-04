using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.TextField;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.SettingsFragment")]
public sealed class SettingsFragment : Fragment
{
    private ILauncherUiHost? host;
    private MaterialAutoCompleteTextView? bindingPolicy;
    private bool syncing;

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
    }

    public override void OnStart()
    {
        base.OnStart();
        host = Activity as ILauncherUiHost
            ?? throw new InvalidOperationException("The Settings screen requires a launcher host.");
        host.LauncherStateChanged += OnStateChanged;
        Render(host.CurrentState);
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
        bindingPolicy = null;
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
}
