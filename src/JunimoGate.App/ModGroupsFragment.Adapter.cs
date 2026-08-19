using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.RadioButton;
using Google.Android.Material.TextField;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;
using Stopwatch = System.Diagnostics.Stopwatch;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;

namespace JunimoGate.App;

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
    Action<ModProfileV2> share,
    Action<ModGroupListItem> delete) : RecyclerView.Adapter
{
    private List<ModGroupListItem> items = [];

    public override int ItemCount => items.Count;

    public void SetItems(IReadOnlyList<ModGroupListItem> value)
    {
        items = [.. value];
        NotifyDataSetChanged();
    }

    public void SetActiveProfile(string profileId)
    {
        var activeIndex = items.FindIndex(item => item.IsActive);
        var selectedIndex = items.FindIndex(item => item.Profile.Id == profileId);
        if (selectedIndex < 0 || activeIndex == selectedIndex)
            return;

        if (activeIndex >= 0)
        {
            var previous = items[activeIndex];
            items[activeIndex] = previous with
            {
                IsActive = false,
                CanDelete = previous.Profile.Id is not (ModProfileV2.NoModsId or "default"),
            };
            NotifyItemChanged(activeIndex);
        }

        items[selectedIndex] = items[selectedIndex] with
        {
            IsActive = true,
            CanDelete = false,
        };
        NotifyItemChanged(selectedIndex);
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_group, parent, false)
            ?? throw new InvalidOperationException("The Mod group item layout could not be created.");
        return new Holder(view, open, select, share, delete);
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
        private readonly MaterialRadioButton activeButton;
        private readonly MaterialButton shareButton;
        private readonly MaterialButton deleteButton;
        private readonly Action<ModProfileV2> open;
        private readonly Action<ModProfileV2> select;
        private readonly Action<ModProfileV2> share;
        private readonly Action<ModGroupListItem> delete;
        private ModGroupListItem? item;

        public Holder(
            View view,
            Action<ModProfileV2> open,
            Action<ModProfileV2> select,
            Action<ModProfileV2> share,
            Action<ModGroupListItem> delete) : base(view)
        {
            this.open = open;
            this.select = select;
            this.share = share;
            this.delete = delete;
            title = view.FindViewById<TextView>(Resource.Id.mod_group_title)!;
            summary = view.FindViewById<TextView>(Resource.Id.mod_group_summary)!;
            openButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_open)!;
            activeButton = view.FindViewById<MaterialRadioButton>(Resource.Id.mod_group_active)!;
            shareButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_share)!;
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_delete)!;
            openButton.Click += (_, _) => { if (item is not null) this.open(item.Profile); };
            activeButton.Click += (_, _) => SelectCurrent();
            ItemView.Click += (_, _) => SelectCurrent();
            shareButton.Click += (_, _) => { if (item is not null) this.share(item.Profile); };
            deleteButton.Click += (_, _) => { if (item is not null) this.delete(item); };
        }

        public void Bind(ModGroupListItem value, string detail)
        {
            item = value;
            title.Text = value.DisplayName;
            summary.Text = detail;
            activeButton.Checked = value.IsActive;
            var selectLabel = ItemView.Context?.GetString(
                value.IsActive ? Resource.String.mod_groups_selected : Resource.String.mod_groups_select);
            activeButton.ContentDescription = selectLabel;
            activeButton.TooltipText = selectLabel;
            deleteButton.Visibility = value.CanDelete ? ViewStates.Visible : ViewStates.Gone;
        }

        private void SelectCurrent()
        {
            if (item is { IsActive: false } value)
                select(value.Profile);
        }
    }
}
