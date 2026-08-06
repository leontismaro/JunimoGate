namespace JunimoGate.Mods;

public sealed record ModManagementItem(
    string ItemId,
    string DisplayName,
    ModBundleDefinition? Bundle,
    ModBundleDefinition? RestorableBundle,
    IReadOnlyList<ModLibraryItem> Members)
{
    public bool IsBundle => Bundle is not null;

    public IReadOnlyList<string> SearchTerms => Members
        .SelectMany(item => new[]
        {
            item.Manifest.Name,
            item.Manifest.Author,
            item.Manifest.UniqueId,
        })
        .Prepend(DisplayName)
        .ToArray();
}

public sealed record ModManagementProjection(
    IReadOnlyList<ModManagementItem> Items,
    int ActualComponentCount)
{
    public static ModManagementProjection Create(ModLibraryIndex library)
    {
        ArgumentNullException.ThrowIfNull(library);
        library.Validate();
        var indexed = library.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ModManagementItem>();
        foreach (var bundle in library.BundleCatalog.Bundles)
        {
            var unlocked = library.BundleCatalog.UnlockOverrides
                .Where(value => value.FamilyKey == bundle.FamilyKey)
                .Select(value => value.UniqueId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var members = bundle.Members
                .Where(member => !unlocked.Contains(member.UniqueId))
                .Select(member => indexed[member.LibraryItemId])
                .ToArray();
            if (members.Length < 2)
                continue;
            foreach (var member in members)
                grouped.Add(member.LibraryItemId);
            items.Add(new ModManagementItem(
                $"bundle:{bundle.BundleId}",
                bundle.DisplayName,
                bundle,
                RestorableBundle: null,
                members));
        }

        items.AddRange(library.Items
            .Where(item => !grouped.Contains(item.LibraryItemId))
            .Select(item => new ModManagementItem(
                $"mod:{item.LibraryItemId}",
                item.Manifest.Name,
                Bundle: null,
                RestorableBundle: FindRestorableBundle(item, library.BundleCatalog),
                new[] { item })));
        return new ModManagementProjection(
            items
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ItemId, StringComparer.Ordinal)
                .ToArray(),
            library.Items.Count);
    }

    private static ModBundleDefinition? FindRestorableBundle(
        ModLibraryItem item,
        ModBundleCatalog catalog)
    {
        var bundle = catalog.Bundles.FirstOrDefault(value => value.Members.Any(member =>
            member.LibraryItemId == item.LibraryItemId));
        if (bundle is null)
            return null;
        return catalog.UnlockOverrides.Any(value =>
            value.FamilyKey == bundle.FamilyKey &&
            value.UniqueId.Equals(item.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase))
            ? bundle
            : null;
    }
}

public enum ModDependencyState
{
    NotInstalled,
    AvailableSingleVersion,
    AvailableMultipleVersions,
    DisabledInProfile,
    Satisfied,
    VersionMismatch,
}

public sealed record ModDependencyDiagnostic(
    string UniqueId,
    bool IsRequired,
    string? MinimumVersion,
    ModDependencyState State,
    ModProfileMember? ProfileMember,
    IReadOnlyList<ModLibraryItem> Candidates);

public static class ModDependencyAnalyzer
{
    public static IReadOnlyList<ModDependencyDiagnostic> Analyze(
        ModManagementItem item,
        ModLibraryIndex library,
        ModProfileV2 profile)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(profile);
        library.Validate();
        profile.Validate();
        var internalIds = item.Members
            .Select(member => member.Manifest.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requirements = new Dictionary<string, DependencyRequirement>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in item.Members)
        {
            if (member.Manifest.ContentPackForUniqueId is { } contentPackFor && !internalIds.Contains(contentPackFor))
                AddRequirement(requirements, contentPackFor, required: true, minimumVersion: null);
            foreach (var dependency in member.Manifest.Dependencies)
            {
                if (!internalIds.Contains(dependency.UniqueId))
                    AddRequirement(requirements, dependency.UniqueId, dependency.IsRequired, dependency.MinimumVersion);
            }
        }

        var indexed = library.Items.ToDictionary(value => value.LibraryItemId, StringComparer.Ordinal);
        return requirements.Values
            .Select(requirement => CreateDiagnostic(requirement, library, profile, indexed))
            .OrderByDescending(diagnostic => diagnostic.IsRequired)
            .ThenBy(diagnostic => diagnostic.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRequirement(
        IDictionary<string, DependencyRequirement> requirements,
        string uniqueId,
        bool required,
        string? minimumVersion)
    {
        if (!requirements.TryGetValue(uniqueId, out var current))
        {
            requirements[uniqueId] = new DependencyRequirement(uniqueId, required, minimumVersion);
            return;
        }
        requirements[uniqueId] = current with
        {
            IsRequired = current.IsRequired || required,
            MinimumVersion = MaxVersion(current.MinimumVersion, minimumVersion),
        };
    }

    private static ModDependencyDiagnostic CreateDiagnostic(
        DependencyRequirement requirement,
        ModLibraryIndex library,
        ModProfileV2 profile,
        IReadOnlyDictionary<string, ModLibraryItem> indexed)
    {
        var candidates = library.Items
            .Where(item => item.Manifest.UniqueId.Equals(requirement.UniqueId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Manifest.Version, ModVersionStringComparer.Instance)
            .ThenBy(item => item.LibraryItemId, StringComparer.Ordinal)
            .ToArray();
        var profileMember = profile.Members.FirstOrDefault(member =>
            member.UniqueId.Equals(requirement.UniqueId, StringComparison.OrdinalIgnoreCase));
        if (profileMember is not null && !profileMember.Enabled)
        {
            return new ModDependencyDiagnostic(
                requirement.UniqueId,
                requirement.IsRequired,
                requirement.MinimumVersion,
                ModDependencyState.DisabledInProfile,
                profileMember,
                candidates);
        }
        if (profileMember?.LibraryItemId is { } selectedId && indexed.TryGetValue(selectedId, out var selected))
        {
            var state = MeetsMinimum(selected.Manifest.Version, requirement.MinimumVersion)
                ? ModDependencyState.Satisfied
                : ModDependencyState.VersionMismatch;
            return new ModDependencyDiagnostic(
                requirement.UniqueId,
                requirement.IsRequired,
                requirement.MinimumVersion,
                state,
                profileMember,
                candidates);
        }

        var available = candidates.Length switch
        {
            0 => ModDependencyState.NotInstalled,
            1 => ModDependencyState.AvailableSingleVersion,
            _ => ModDependencyState.AvailableMultipleVersions,
        };
        return new ModDependencyDiagnostic(
            requirement.UniqueId,
            requirement.IsRequired,
            requirement.MinimumVersion,
            available,
            profileMember,
            candidates);
    }

    private static bool MeetsMinimum(string version, string? minimumVersion) =>
        minimumVersion is null || ModVersionStringComparer.Instance.Compare(version, minimumVersion) >= 0;

    private static string? MaxVersion(string? first, string? second)
    {
        if (first is null)
            return second;
        if (second is null)
            return first;
        return ModVersionStringComparer.Instance.Compare(first, second) >= 0 ? first : second;
    }

    private sealed record DependencyRequirement(string UniqueId, bool IsRequired, string? MinimumVersion);
}

internal sealed class ModVersionStringComparer : IComparer<string>
{
    public static ModVersionStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        var first = ParsedVersion.Parse(x);
        var second = ParsedVersion.Parse(y);
        if (!first.IsValid || !second.IsValid)
            return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        var length = Math.Max(first.Numbers.Length, second.Numbers.Length);
        for (var index = 0; index < length; index++)
        {
            var left = index < first.Numbers.Length ? first.Numbers[index] : 0;
            var right = index < second.Numbers.Length ? second.Numbers[index] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
                return comparison;
        }
        if (first.Prerelease.Length == 0 && second.Prerelease.Length != 0)
            return 1;
        if (first.Prerelease.Length != 0 && second.Prerelease.Length == 0)
            return -1;
        return ComparePrerelease(first.Prerelease, second.Prerelease);
    }

    private static int ComparePrerelease(string first, string second)
    {
        var left = first.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var right = second.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = int.TryParse(left[index], out var leftNumber);
            var rightNumeric = int.TryParse(right[index], out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric != rightNumeric)
                comparison = leftNumeric ? -1 : 1;
            else
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left[index], right[index]);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private sealed record ParsedVersion(bool IsValid, int[] Numbers, string Prerelease)
    {
        public static ParsedVersion Parse(string value)
        {
            var normalized = value.Trim();
            var build = normalized.IndexOf('+');
            if (build >= 0)
                normalized = normalized[..build];
            var separator = normalized.IndexOf('-');
            var release = separator >= 0 ? normalized[..separator] : normalized;
            var prerelease = separator >= 0 ? normalized[(separator + 1)..] : string.Empty;
            var parts = release.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var numbers = new int[parts.Length];
            var valid = parts.Length > 0;
            for (var index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(parts[index], out numbers[index]) || numbers[index] < 0)
                {
                    valid = false;
                    break;
                }
            }
            return new ParsedVersion(valid, numbers, prerelease);
        }
    }
}

public sealed class ModBundleProfileMutationService(
    ModLibraryRepository library,
    ModProfileMemberMutationService members)
{
    public async ValueTask<ModProfileMemberMutationResult> AddOrReplaceAsync(
        ProfileId profileId,
        string bundleId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var items = await ResolveAsync(bundleId, cancellationToken).ConfigureAwait(false);
        return await members.AddOrReplaceAsync(profileId, items, enabled, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModProfileMemberMutationResult> SetEnabledAsync(
        ProfileId profileId,
        string bundleId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var items = await ResolveAsync(bundleId, cancellationToken).ConfigureAwait(false);
        return await members.SetEnabledAsync(
                profileId,
                items.Select(item => item.Manifest.UniqueId).ToArray(),
                enabled,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ModProfileMemberMutationResult> RemoveAsync(
        ProfileId profileId,
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        var items = await ResolveAsync(bundleId, cancellationToken).ConfigureAwait(false);
        return await members.RemoveAsync(
                profileId,
                items.Select(item => item.Manifest.UniqueId).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ModLibraryItem>> ResolveAsync(
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        if (!ModContentId.IsValid(bundleId))
            throw new ArgumentException("The Mod bundle ID is invalid.", nameof(bundleId));
        var snapshot = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var item = ModManagementProjection.Create(snapshot).Items.FirstOrDefault(value =>
            value.Bundle?.BundleId == bundleId);
        return item?.Members ?? throw new KeyNotFoundException("The Mod bundle is not currently visible.");
    }
}
