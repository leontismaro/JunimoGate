namespace JunimoGate.Mods;

public sealed record ModLaunchSelectionItem(
    string LibraryItemId,
    string UniqueId,
    string RelativeModRoot,
    long ContentGeneration = 1)
{
    public void Validate()
    {
        if (!ModLibraryItemId.IsValid(LibraryItemId) || string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256 ||
            RelativeModRoot != $"library/{LibraryItemId}/files" || ContentGeneration < 1)
        {
            throw new InvalidDataException("The Mod launch selection item is malformed.");
        }
    }
}

public sealed record ModLaunchSelectionSnapshot(
    string Schema,
    string SelectionId,
    string ProfileId,
    long ProfileRevision,
    long LibraryRevision,
    ModAssemblyBindingPolicy AssemblyBindingPolicy,
    IReadOnlyList<ModLaunchSelectionItem> Items,
    DateTimeOffset CreatedAtUtc)
{
    public const string CurrentSchema = "junimogate-mod-launch-selection/v2";

    public ProfileId Validate()
    {
        if (Schema != CurrentSchema || !IsSelectionId(SelectionId) ||
            !JunimoGate.Mods.ProfileId.TryParse(ProfileId, out var profileId) ||
            ProfileRevision < 1 || LibraryRevision < 1 || !Enum.IsDefined(AssemblyBindingPolicy) ||
            Items is null || Items.Count > ModProfileV2.MaximumMembers || CreatedAtUtc == default)
        {
            throw new InvalidDataException("The Mod launch selection is malformed.");
        }

        var libraryIds = new HashSet<string>(StringComparer.Ordinal);
        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            item?.Validate();
            if (item is null || !libraryIds.Add(item.LibraryItemId) || !uniqueIds.Add(item.UniqueId))
                throw new InvalidDataException("The Mod launch selection contains a duplicate or null item.");
        }
        return profileId;
    }

    public bool Matches(
        ModProfileV2 profile,
        ModLibraryIndex library,
        ModAssemblyBindingPolicy defaultBindingPolicy = ModAssemblyBindingPolicy.HighestCompatible)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(library);
        _ = Validate();
        _ = profile.Validate();
        library.Validate();
        if (!Enum.IsDefined(defaultBindingPolicy) || profile.Id != ProfileId ||
            profile.Revision != ProfileRevision ||
            AssemblyBindingPolicy != (profile.AssemblyBindingPolicyOverride ?? defaultBindingPolicy))
            return false;

        var selected = profile.Members
            .Where(static member => member.Enabled)
            .ToDictionary(static member => member.UniqueId, StringComparer.OrdinalIgnoreCase);
        var indexed = library.Items.ToDictionary(static item => item.LibraryItemId, StringComparer.Ordinal);
        if (selected.Count != Items.Count)
            return false;
        foreach (var item in Items)
        {
            if (!selected.TryGetValue(item.UniqueId, out var member) || member.LibraryItemId != item.LibraryItemId ||
                !indexed.TryGetValue(item.LibraryItemId, out var libraryItem) ||
                libraryItem.ContentGeneration != item.ContentGeneration ||
                !libraryItem.Manifest.UniqueId.Equals(item.UniqueId, StringComparison.OrdinalIgnoreCase) ||
                libraryItem.RelativeStoragePath != item.RelativeModRoot)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSelectionId(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class ModLaunchSelectionBuilder
{
    public static ModLaunchSelectionSnapshot Build(
        ModProfileV2 profile,
        ModLibraryIndex library,
        ModAssemblyBindingPolicy defaultBindingPolicy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(library);
        _ = profile.Validate();
        library.Validate();
        if (!Enum.IsDefined(defaultBindingPolicy))
            throw new ArgumentOutOfRangeException(nameof(defaultBindingPolicy));

        var indexed = library.Items.ToDictionary(static item => item.LibraryItemId, StringComparer.Ordinal);
        var items = new List<ModLaunchSelectionItem>();
        foreach (var member in profile.Members.Where(static member => member.Enabled))
        {
            if (member.LibraryItemId is null || !indexed.TryGetValue(member.LibraryItemId, out var libraryItem))
                throw new InvalidDataException($"Enabled Mod '{member.UniqueId}' is missing from the library.");
            if (!libraryItem.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Enabled Mod '{member.UniqueId}' points to a different library identity.");
            items.Add(new ModLaunchSelectionItem(
                libraryItem.LibraryItemId,
                libraryItem.Manifest.UniqueId,
                libraryItem.RelativeStoragePath,
                libraryItem.ContentGeneration));
        }

        var snapshot = new ModLaunchSelectionSnapshot(
            ModLaunchSelectionSnapshot.CurrentSchema,
            Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            profile.Id,
            profile.Revision,
            library.Revision,
            profile.AssemblyBindingPolicyOverride ?? defaultBindingPolicy,
            items
                .OrderBy(static item => item.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.LibraryItemId, StringComparer.Ordinal)
                .ToArray(),
            DateTimeOffset.UtcNow);
        _ = snapshot.Validate();
        return snapshot;
    }
}

public static class ModLaunchSelectionPathResolver
{
    public static IReadOnlyList<string> ResolveExistingRoots(
        string modsRoot,
        ModLaunchSelectionSnapshot selection)
    {
        if (string.IsNullOrWhiteSpace(modsRoot) || !Path.IsPathFullyQualified(modsRoot))
            throw new ArgumentException("The Mod library root must be absolute.", nameof(modsRoot));
        ArgumentNullException.ThrowIfNull(selection);
        _ = selection.Validate();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRoot));
        var resolved = new List<string>(selection.Items.Count);
        foreach (var item in selection.Items)
        {
            var relative = item.RelativeModRoot.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(path))
                throw new InvalidDataException($"Selected Mod '{item.UniqueId}' is outside the library or missing.");
            resolved.Add(path);
        }
        return resolved;
    }
}
