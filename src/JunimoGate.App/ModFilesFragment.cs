using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System.Security.Cryptography;
using System.Text;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

internal interface IModFileBackHandler
{
    bool HandleModFileBack();

    void RequestModFileLeave(Action leave);
}

[Register("org.junimogate.app.ModFilesFragment")]
public sealed class ModFilesFragment : Fragment, IModFileBackHandler
{
    private CancellationTokenSource? cancellation;
    private ModFileService? files;
    private ModManagementCommandService? commands;
    private ModFileAdapter? adapter;
    private RecyclerView? list;
    private TextView? pathLabel;
    private TextView? empty;
    private MaterialButton? createButton;
    private LinearProgressIndicator? progress;
    private MainShellFragment? mainShell;
    private string libraryItemId = string.Empty;
    private string modName = string.Empty;
    private string relativeDirectory = string.Empty;

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        relativeDirectory = savedInstanceState?.GetString("path") ?? string.Empty;
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutString("path", relativeDirectory);
        base.OnSaveInstanceState(outState);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_files, container, false)
        ?? throw new InvalidOperationException("The Mod files layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        pathLabel = view.FindViewById<TextView>(Resource.Id.mod_files_path)!;
        empty = view.FindViewById<TextView>(Resource.Id.mod_files_empty)!;
        createButton = view.FindViewById<MaterialButton>(Resource.Id.mod_files_create)!;
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_files_progress)!;
        list = view.FindViewById<RecyclerView>(Resource.Id.mod_files_list)!;
        adapter = new ModFileAdapter(FormatMetadata, OpenEntry);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        createButton.Click += OnCreateClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        libraryItemId = Arguments?.GetString("libraryItemId") ?? string.Empty;
        modName = Arguments?.GetString("modName") ?? GetString(Resource.String.mod_files_title);
        cancellation = new CancellationTokenSource();
        var management = ((MainActivity)RequireActivity()).ModManagement;
        files = new ModFileService(management.Library);
        commands = management.Commands;
        mainShell = FindMainShell() ?? throw new InvalidOperationException("The Mod files shell is unavailable.");
        mainShell.SetEditorToolbar(modName, OnNavigateUp);
        _ = LoadAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        files = null;
        commands = null;
        if (mainShell is not null)
            mainShell.ClearEditorToolbar(OnNavigateUp);
        mainShell = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        list?.SetAdapter(null);
        if (createButton is not null)
            createButton.Click -= OnCreateClicked;
        list = null;
        adapter = null;
        pathLabel = null;
        empty = null;
        createButton = null;
        progress = null;
        base.OnDestroyView();
    }

    public bool HandleModFileBack()
    {
        RequestModFileLeave(() =>
        {
            if (relativeDirectory.Length == 0)
            {
                NavHostFragment.FindNavController(this).PopBackStack();
                return;
            }
            relativeDirectory = GetParent(relativeDirectory);
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = LoadAsync(lifetime.Token);
        });
        return true;
    }

    public void RequestModFileLeave(Action leave) => leave();

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (files is null)
            return;
        SetBusy(true);
        try
        {
            var entries = await files.ListAsync(libraryItemId, relativeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                pathLabel!.Text = relativeDirectory.Length == 0 ? modName : $"{modName}/{relativeDirectory}";
                adapter?.SetItems(entries);
                empty!.Visibility = entries.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            Log.Error("JunimoGate.Mods", "mod-files-read-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_files_read_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OpenEntry(ModFileEntry entry)
    {
        if (entry.IsDirectory)
        {
            relativeDirectory = entry.RelativePath;
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = LoadAsync(lifetime.Token);
            return;
        }
        if (!entry.CanEdit)
            return;

        OpenTextFile(entry.RelativePath);
    }

    private void OnCreateClicked(object? sender, EventArgs eventArgs)
    {
        var content = LayoutInflater.From(RequireContext())?.Inflate(
            Resource.Layout.dialog_mod_file_name,
            null,
            false)
            ?? throw new InvalidOperationException("The create-file dialog layout could not be created.");
        var input = content.FindViewById<TextInputEditText>(Resource.Id.mod_file_name_input)
            ?? throw new InvalidOperationException("The create-file name input is unavailable.");
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_file_create_title);
        dialog.SetView(content);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mod_file_create_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = CreateAsync(input.Text?.Trim() ?? string.Empty, lifetime.Token);
        });
        var shown = dialog.Show()
            ?? throw new InvalidOperationException("The create-file dialog could not be shown.");
        var create = shown.GetButton((int)DialogButtonType.Positive)
            ?? throw new InvalidOperationException("The create-file action is unavailable.");
        void UpdateCreateState() => create.Enabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => UpdateCreateState();
        UpdateCreateState();
        input.RequestFocus();
        shown.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible);
    }

    private async Task CreateAsync(string fileName, CancellationToken cancellationToken)
    {
        if (commands is null)
            return;
        SetBusy(true);
        try
        {
            var created = await commands.CreateModFileAsync(
                    libraryItemId,
                    relativeDirectory,
                    fileName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => OpenTextFile(created.RelativePath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ModContentInUseException)
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_file_game_running, ToastLength.Long)?.Show());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            Log.Error("JunimoGate.Mods", "mod-file-create-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_file_create_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OpenTextFile(string relativePath)
    {
        using var arguments = new Bundle();
        arguments.PutString("libraryItemId", libraryItemId);
        arguments.PutString("modName", modName);
        arguments.PutString("relativePath", relativePath);
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_mod_text_editor, arguments);
    }

    private string FormatMetadata(ModFileEntry entry)
    {
        if (entry.IsDirectory)
            return GetString(Resource.String.mod_files_folder);
        var size = global::Android.Text.Format.Formatter.FormatFileSize(RequireContext(), entry.Length) ?? "0 B";
        return entry.CanEdit
            ? FormatString(Resource.String.mod_files_editable, size)
            : FormatString(Resource.String.mod_files_read_only, size);
    }

    private void SetBusy(bool value)
    {
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        if (list is not null)
            list.Enabled = !value;
        if (createButton is not null)
            createButton.Enabled = !value;
    }

    private void OnNavigateUp(object? sender, AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs eventArgs) =>
        HandleModFileBack();

    private MainShellFragment? FindMainShell()
    {
        for (var fragment = ParentFragment; fragment is not null; fragment = fragment.ParentFragment)
        {
            if (fragment is MainShellFragment shell)
                return shell;
        }
        return null;
    }

    private string FormatString(int resourceId, string value) =>
        Resources?.GetString(resourceId, new Java.Lang.String(value)) ?? value;

    private static string GetParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }
}

[Register("org.junimogate.app.ModTextEditorFragment")]
public sealed class ModTextEditorFragment : Fragment, IModFileBackHandler
{
    private const string OpenedLengthState = "openedLength";
    private const string OpenedLastWriteTicksState = "openedLastWriteTicks";
    private const string OpenedTextHashState = "openedTextHash";
    private const string DirtyState = "dirty";

    private CancellationTokenSource? cancellation;
    private ModFileService? files;
    private ModManagementCommandService? commands;
    private ModTextFile? opened;
    private TextInputEditText? editor;
    private MaterialButton? saveButton;
    private LinearProgressIndicator? progress;
    private MainShellFragment? mainShell;
    private string libraryItemId = string.Empty;
    private string relativePath = string.Empty;
    private string modName = string.Empty;
    private bool suppressTextChange;
    private bool busy;
    private bool dirty;
    private string? openedTextHash;

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState is null || !savedInstanceState.ContainsKey(OpenedLengthState))
            return;
        relativePath = Arguments?.GetString("relativePath") ?? string.Empty;
        opened = new ModTextFile(
            relativePath,
            string.Empty,
            savedInstanceState.GetLong(OpenedLengthState),
            new DateTimeOffset(savedInstanceState.GetLong(OpenedLastWriteTicksState), TimeSpan.Zero));
        openedTextHash = savedInstanceState.GetString(OpenedTextHashState);
        dirty = savedInstanceState.GetBoolean(DirtyState);
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        if (opened is not null)
        {
            outState.PutLong(OpenedLengthState, opened.Length);
            outState.PutLong(OpenedLastWriteTicksState, opened.LastWriteTimeUtc.UtcDateTime.Ticks);
            outState.PutString(OpenedTextHashState, openedTextHash);
            outState.PutBoolean(DirtyState, dirty);
        }
        base.OnSaveInstanceState(outState);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_text_editor, container, false)
        ?? throw new InvalidOperationException("The Mod text editor layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        editor = view.FindViewById<TextInputEditText>(Resource.Id.mod_file_editor_text)!;
        saveButton = view.FindViewById<MaterialButton>(Resource.Id.mod_file_editor_save)!;
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_file_editor_progress)!;
        view.FindViewById<TextView>(Resource.Id.mod_file_editor_path)!.Text = Arguments?.GetString("relativePath") ?? string.Empty;
        editor.TextChanged += OnTextChanged;
        saveButton.Click += OnSaveClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        libraryItemId = Arguments?.GetString("libraryItemId") ?? string.Empty;
        relativePath = Arguments?.GetString("relativePath") ?? string.Empty;
        modName = Arguments?.GetString("modName") ?? GetString(Resource.String.mod_files_title);
        cancellation = new CancellationTokenSource();
        var management = ((MainActivity)RequireActivity()).ModManagement;
        files = new ModFileService(management.Library);
        commands = management.Commands;
        mainShell = FindMainShell() ?? throw new InvalidOperationException("The Mod text editor shell is unavailable.");
        mainShell.SetEditorToolbar(Path.GetFileName(relativePath), OnNavigateUp);
        if (opened is null)
            _ = LoadAsync(cancellation.Token);
        else
            SetBusy(false);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        files = null;
        commands = null;
        if (mainShell is not null)
            mainShell.ClearEditorToolbar(OnNavigateUp);
        mainShell = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (editor is not null)
            editor.TextChanged -= OnTextChanged;
        if (saveButton is not null)
            saveButton.Click -= OnSaveClicked;
        editor = null;
        saveButton = null;
        progress = null;
        base.OnDestroyView();
    }

    public bool HandleModFileBack()
    {
        RequestModFileLeave(() => NavHostFragment.FindNavController(this).PopBackStack());
        return true;
    }

    public void RequestModFileLeave(Action leave)
    {
        ArgumentNullException.ThrowIfNull(leave);
        if (!dirty)
        {
            leave();
            return;
        }
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_file_discard_title);
        dialog.SetMessage(Resource.String.mod_file_discard_message);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetNeutralButton(Resource.String.mod_file_discard_action, (_, _) =>
        {
            dirty = false;
            leave();
        });
        dialog.SetPositiveButton(Resource.String.mod_file_save_and_leave, (_, _) =>
        {
            if (!busy && cancellation is { IsCancellationRequested: false } lifetime)
                _ = SaveAsync(lifetime.Token, leave);
        });
        dialog.Show();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (files is null)
            return;
        SetBusy(true);
        try
        {
            var file = await files.ReadTextAsync(libraryItemId, relativePath, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                opened = file;
                openedTextHash = HashText(file.Text);
                suppressTextChange = true;
                editor!.Text = file.Text;
                editor.SetSelection(editor.Text?.Length ?? 0);
                suppressTextChange = false;
                dirty = false;
                UpdateSaveState();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            Log.Error("JunimoGate.Mods", "mod-file-open-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_file_open_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken, Action? afterSave = null)
    {
        if (commands is null || opened is null || editor is null)
            return;
        SetBusy(true);
        try
        {
            var text = editor.Text ?? string.Empty;
            var saved = await commands.EditModFileAsync(libraryItemId, opened, text, cancellationToken)
                .ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                opened = saved;
                openedTextHash = HashText(saved.Text);
                dirty = false;
                UpdateSaveState();
                Toast.MakeText(RequireContext(), Resource.String.mod_file_saved, ToastLength.Short)?.Show();
                afterSave?.Invoke();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ModContentInUseException)
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_file_game_running, ToastLength.Long)?.Show());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Error("JunimoGate.Mods", "mod-file-save-failed", exception);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() => Toast.MakeText(
                    RequireContext(),
                    exception is InvalidOperationException
                        ? Resource.String.mod_file_changed
                        : Resource.String.mod_file_save_failed,
                    ToastLength.Long)?.Show());
            }
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OnTextChanged(object? sender, global::Android.Text.TextChangedEventArgs eventArgs)
    {
        if (suppressTextChange || opened is null)
            return;
        dirty = !string.Equals(HashText(editor?.Text ?? string.Empty), openedTextHash, StringComparison.Ordinal);
        UpdateSaveState();
    }

    private void OnSaveClicked(object? sender, EventArgs eventArgs)
    {
        if (!busy && dirty && cancellation is { IsCancellationRequested: false } lifetime)
            _ = SaveAsync(lifetime.Token);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Invisible;
        if (editor is not null)
            editor.Enabled = !value && opened is not null;
        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        if (saveButton is not null)
            saveButton.Enabled = !busy && dirty && opened is not null;
    }

    private void OnNavigateUp(object? sender, AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs eventArgs) =>
        HandleModFileBack();

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private MainShellFragment? FindMainShell()
    {
        for (var fragment = ParentFragment; fragment is not null; fragment = fragment.ParentFragment)
        {
            if (fragment is MainShellFragment shell)
                return shell;
        }
        return null;
    }
}

internal sealed class ModFileAdapter(
    Func<ModFileEntry, string> formatMetadata,
    Action<ModFileEntry> open) : RecyclerView.Adapter
{
    private IReadOnlyList<ModFileEntry> items = Array.Empty<ModFileEntry>();

    public override int ItemCount => items.Count;

    public void SetItems(IReadOnlyList<ModFileEntry> value)
    {
        items = value;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_file, parent, false)
            ?? throw new InvalidOperationException("The Mod file row could not be created.");
        return new Holder(view, formatMetadata, open);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
        ((Holder)holder).Bind(items[position]);

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly ImageView icon;
        private readonly TextView name;
        private readonly TextView metadata;
        private readonly ImageView openIcon;
        private readonly Func<ModFileEntry, string> formatMetadata;
        private readonly Action<ModFileEntry> open;
        private ModFileEntry? entry;

        public Holder(View view, Func<ModFileEntry, string> formatMetadata, Action<ModFileEntry> open) : base(view)
        {
            this.formatMetadata = formatMetadata;
            this.open = open;
            icon = view.FindViewById<ImageView>(Resource.Id.mod_file_icon)!;
            name = view.FindViewById<TextView>(Resource.Id.mod_file_name)!;
            metadata = view.FindViewById<TextView>(Resource.Id.mod_file_meta)!;
            openIcon = view.FindViewById<ImageView>(Resource.Id.mod_file_open)!;
            ItemView.Click += (_, _) =>
            {
                if (entry is { IsDirectory: true } or { CanEdit: true })
                    this.open(entry);
            };
        }

        public void Bind(ModFileEntry value)
        {
            entry = value;
            name.Text = value.Name;
            metadata.Text = formatMetadata(value);
            icon.SetImageResource(value.IsDirectory ? Resource.Drawable.ic_folder_24 : Resource.Drawable.ic_description_24);
            var canOpen = value.IsDirectory || value.CanEdit;
            ItemView.Clickable = canOpen;
            ItemView.Focusable = canOpen;
            ItemView.Alpha = canOpen ? 1f : 0.5f;
            openIcon.Visibility = canOpen ? ViewStates.Visible : ViewStates.Invisible;
        }
    }
}
