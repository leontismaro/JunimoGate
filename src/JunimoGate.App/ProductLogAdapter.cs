using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using JunimoGate.Core;

namespace JunimoGate.App;

internal sealed record ProductLogDisplayEntry(int Id, ProductLogEntry Entry);

internal sealed class ProductLogAdapter : RecyclerView.Adapter
{
    private IReadOnlyList<ProductLogDisplayEntry> entries = [];
    private readonly HashSet<int> expanded = [];

    public override int ItemCount => entries.Count;

    public void Submit(IReadOnlyList<ProductLogDisplayEntry> value)
    {
        entries = value;
        expanded.RemoveWhere(id => value.All(item => item.Id != id));
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_log_entry, parent, false)
            ?? throw new InvalidOperationException("The log row could not be created.");
        return new Holder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var display = entries[position];
        var item = display.Entry;
        var view = (Holder)holder;
        var context = view.ItemView.Context
            ?? throw new InvalidOperationException("The log row context is unavailable.");
        view.Time.Text = item.Time;
        view.Level.Text = GetLevelLabel(item.Level);
        view.Level.SetTextColor(new Color(ContextCompat.GetColor(context, GetLevelColor(item.Level))));
        view.Source.Text = item.IsPartial
            ? context.GetString(Resource.String.logs_partial_entry)
            : item.Source;
        view.Message.Text = item.Message;
        view.Message.SetMaxLines(expanded.Contains(display.Id) ? int.MaxValue : 4);
        view.Repeat.Text = item.RepeatCount > 1 ? $"x {item.RepeatCount}" : string.Empty;
        view.Repeat.Visibility = item.RepeatCount > 1 ? ViewStates.Visible : ViewStates.Gone;

        view.ItemView.Click -= view.Click;
        view.Click = (_, _) =>
        {
            if (!expanded.Add(display.Id))
                expanded.Remove(display.Id);
            NotifyItemChanged(position);
        };
        view.ItemView.Click += view.Click;
    }

    private static string GetLevelLabel(ProductLogLevel level) => level switch
    {
        ProductLogLevel.Trace => "TRACE",
        ProductLogLevel.Debug => "DEBUG",
        ProductLogLevel.Info => "INFO",
        ProductLogLevel.Alert => "ALERT",
        ProductLogLevel.Warn => "WARN",
        ProductLogLevel.Error => "ERROR",
        ProductLogLevel.Critical => "CRITICAL",
        _ => "...",
    };

    private static int GetLevelColor(ProductLogLevel level) => level switch
    {
        ProductLogLevel.Warn => Resource.Color.junimo_warning,
        ProductLogLevel.Error or ProductLogLevel.Critical => Resource.Color.junimo_error,
        ProductLogLevel.Alert => Resource.Color.junimo_primary,
        ProductLogLevel.Trace or ProductLogLevel.Debug => Resource.Color.junimo_outline,
        _ => Resource.Color.junimo_on_surface_variant,
    };

    private sealed class Holder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        public TextView Time { get; } = itemView.FindViewById<TextView>(Resource.Id.log_entry_time)!;
        public TextView Level { get; } = itemView.FindViewById<TextView>(Resource.Id.log_entry_level)!;
        public TextView Source { get; } = itemView.FindViewById<TextView>(Resource.Id.log_entry_source)!;
        public TextView Repeat { get; } = itemView.FindViewById<TextView>(Resource.Id.log_entry_repeat)!;
        public TextView Message { get; } = itemView.FindViewById<TextView>(Resource.Id.log_entry_message)!;
        public EventHandler? Click { get; set; }
    }
}
