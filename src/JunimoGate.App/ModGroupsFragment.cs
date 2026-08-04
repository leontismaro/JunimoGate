using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;

namespace JunimoGate.App;

[Register("org.junimogate.app.ModGroupsFragment")]
public sealed class ModGroupsFragment : Fragment
{
    private CancellationTokenSource? cancellation;
    private ModProfileV2Repository? profiles;
    private ActiveModProfileSelectionRepository? selection;
    private ModLibraryRepository? library;
    private ModGroupAdapter? adapter;
    private MaterialButton? createButton;
    private LinearProgressIndicator? progress;
    private TextView? empty;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_groups, container, false)
        ?? throw new InvalidOperationException("The Mod groups layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        createButton = view.FindViewById<MaterialButton>(Resource.Id.mod_groups_create)
            ?? throw new InvalidOperationException("The create-group action is unavailable.");
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_groups_progress)
            ?? throw new InvalidOperationException("The group progress indicator is unavailable.");
        empty = view.FindViewById<TextView>(Resource.Id.mod_groups_empty)
            ?? throw new InvalidOperationException("The group empty state is unavailable.");
        var list = view.FindViewById<RecyclerView>(Resource.Id.mod_groups_list)
            ?? throw new InvalidOperationException("The group list is unavailable.");
        adapter = new ModGroupAdapter(FormatSummary, OpenProfile, SelectProfile, RequestDelete);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        createButton.Click += OnCreateClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        var userData = AndroidPrivateStorage.GetUserDataRoot(RequireContext());
        var profilesRoot = Path.Combine(userData, "profiles");
        profiles = new ModProfileV2Repository(profilesRoot);
        selection = new ActiveModProfileSelectionRepository(profilesRoot);
        library = new ModLibraryRepository(Path.Combine(userData, "mods"));
        _ = RefreshAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        profiles = null;
        selection = null;
        library = null;
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (createButton is not null)
            createButton.Click -= OnCreateClicked;
        createButton = null;
        progress = null;
        empty = null;
        adapter = null;
        base.OnDestroyView();
    }

    private void OnCreateClicked(object? sender, EventArgs eventArgs)
    {
        var input = new EditText(RequireContext())
        {
            Hint = GetString(Resource.String.mod_groups_name_hint),
        };
        input.SetSingleLine(true);
        input.SetPadding(48, 0, 48, 0);
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_groups_create_title);
        dialog.SetView(input);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mod_groups_create_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = CreateAsync(input.Text ?? string.Empty, lifetime.Token);
        });
        dialog.Show();
    }

    private async Task CreateAsync(string displayName, CancellationToken cancellationToken)
    {
        if (profiles is null)
            return;
        SetBusy(true);
        try
        {
            var profile = await profiles.CreateAsync(displayName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() => OpenProfile(profile));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels creation.
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                          InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-create-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_create_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OpenProfile(ModProfileV2 profile)
    {
        var arguments = new Bundle();
        arguments.PutString("profileId", profile.Id);
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_mod_group_editor, arguments);
    }

    private void SelectProfile(ModProfileV2 profile)
    {
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = SelectAsync(profile, lifetime.Token);
    }

    private async Task SelectAsync(ModProfileV2 profile, CancellationToken cancellationToken)
    {
        if (selection is null)
            return;
        SetBusy(true);
        try
        {
            var current = await selection.OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            await selection.SetAsync(current.Revision, ProfileId.Parse(profile.Id), cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => (Activity as ILauncherUiHost)?.RefreshProfile());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels selection.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-select-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_select_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void RequestDelete(ModGroupListItem item)
    {
        if (!item.CanDelete)
            return;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_groups_delete_title);
        dialog.SetMessage(FormatString(
            Resource.String.mod_groups_delete_message,
            new JString(item.DisplayName)));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_delete_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = DeleteAsync(item.Profile, lifetime.Token);
        });
        dialog.Show();
    }

    private async Task DeleteAsync(ModProfileV2 profile, CancellationToken cancellationToken)
    {
        if (profiles is null)
            return;
        SetBusy(true);
        try
        {
            await profiles.DeleteAsync(ProfileId.Parse(profile.Id), cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels deletion.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-delete-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_delete_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (profiles is null || selection is null || library is null)
            return;
        SetBusy(true);
        try
        {
            var groups = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
            var active = await selection.OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
            var ids = index.Items.Select(item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
            var rows = groups.Select(profile => new ModGroupListItem(
                    profile,
                    GetDisplayName(profile),
                    profile.Id == active.ActiveProfileId,
                    profile.Members.Count(member => member.LibraryItemId is null || !ids.Contains(member.LibraryItemId)),
                    profile.Id is not (ModProfileV2.NoModsId or "default") && profile.Id != active.ActiveProfileId))
                .ToArray();
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                adapter?.SetItems(rows);
                if (empty is not null)
                    empty.Visibility = rows.Length == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels refresh.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "profile-list-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_read_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private string GetDisplayName(ModProfileV2 profile) => profile.Id switch
    {
        ModProfileV2.NoModsId => GetString(Resource.String.mods_no_mods_group),
        "default" => GetString(Resource.String.mods_default_group),
        _ => profile.DisplayName,
    };

    private string FormatSummary(ModGroupListItem item)
    {
        var active = item.IsActive ? GetString(Resource.String.mod_groups_active) : GetString(Resource.String.mod_groups_inactive);
        return FormatString(
            Resource.String.mod_groups_item_summary,
            Java.Lang.Integer.ValueOf(item.Profile.Members.Count(member => member.Enabled)),
            Java.Lang.Integer.ValueOf(item.Profile.Members.Count(member => !member.Enabled)),
            Java.Lang.Integer.ValueOf(item.MissingCount),
            new JString(active));
    }

    private void SetBusy(bool value)
    {
        if (createButton is not null)
            createButton.Enabled = !value;
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Mod group string resource is unavailable.");
}

internal sealed record ModGroupListItem(
    ModProfileV2 Profile,
    string DisplayName,
    bool IsActive,
    int MissingCount,
    bool CanDelete);

internal sealed class ModGroupAdapter(
    Func<ModGroupListItem, string> formatSummary,
    Action<ModProfileV2> open,
    Action<ModProfileV2> select,
    Action<ModGroupListItem> delete) : RecyclerView.Adapter
{
    private IReadOnlyList<ModGroupListItem> items = Array.Empty<ModGroupListItem>();

    public override int ItemCount => items.Count;

    public void SetItems(IReadOnlyList<ModGroupListItem> value)
    {
        items = value;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_group, parent, false)
            ?? throw new InvalidOperationException("The Mod group item layout could not be created.");
        return new Holder(view, open, select, delete);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = items[position];
        ((Holder)holder).Bind(item, formatSummary(item));
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView title;
        private readonly TextView summary;
        private readonly MaterialButton openButton;
        private readonly MaterialButton selectButton;
        private readonly MaterialButton deleteButton;
        private readonly Action<ModProfileV2> open;
        private readonly Action<ModProfileV2> select;
        private readonly Action<ModGroupListItem> delete;
        private ModGroupListItem? item;

        public Holder(
            View view,
            Action<ModProfileV2> open,
            Action<ModProfileV2> select,
            Action<ModGroupListItem> delete) : base(view)
        {
            this.open = open;
            this.select = select;
            this.delete = delete;
            title = view.FindViewById<TextView>(Resource.Id.mod_group_title)!;
            summary = view.FindViewById<TextView>(Resource.Id.mod_group_summary)!;
            openButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_open)!;
            selectButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_select)!;
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_delete)!;
            openButton.Click += (_, _) => { if (item is not null) this.open(item.Profile); };
            selectButton.Click += (_, _) => { if (item is not null) this.select(item.Profile); };
            deleteButton.Click += (_, _) => { if (item is not null) this.delete(item); };
        }

        public void Bind(ModGroupListItem value, string detail)
        {
            item = value;
            title.Text = value.DisplayName;
            summary.Text = detail;
            selectButton.Enabled = !value.IsActive;
            selectButton.SetText(value.IsActive ? Resource.String.mod_groups_selected : Resource.String.mod_groups_select);
            deleteButton.Visibility = value.CanDelete ? ViewStates.Visible : ViewStates.Gone;
        }
    }
}
