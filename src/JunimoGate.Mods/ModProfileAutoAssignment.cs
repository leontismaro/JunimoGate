namespace JunimoGate.Mods;

public sealed record ModProfileAutoAssignmentResult(
    string ProfileId,
    int AddedMembers,
    int ExistingMembers,
    int AmbiguousUniqueIds,
    bool BlockedByReadOnlyProfile);

public static class ModProfileMissingMemberReconnector
{
    public static async ValueTask<int> ReconnectAsync(
        ModProfileV2Repository profiles,
        IReadOnlyList<ModLibraryItem> availableItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(availableItems);
        foreach (var item in availableItems)
            item?.Validate();
        if (availableItems.Any(item => item is null))
            throw new InvalidDataException("The available Mod list contains a null item.");

        var availableById = availableItems.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var candidatesByUniqueId = availableItems
            .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var reconnected = 0;
        var listed = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var listedProfile in listed.Where(profile => profile.Id != ModProfileV2.NoModsId))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = attempt == 0
                    ? listedProfile
                    : await profiles.ReadAsync(ProfileId.Parse(listedProfile.Id), cancellationToken).ConfigureAwait(false);
                var mutations = 0;
                var resolvedMembers = 0;
                var members = profile.Members.Select(member =>
                {
                    if (member.LibraryItemId is not null &&
                        availableById.TryGetValue(member.LibraryItemId, out var current) &&
                        current.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
                    {
                        return member;
                    }

                    if (!candidatesByUniqueId.TryGetValue(member.UniqueId, out var candidates))
                    {
                        if (member.LibraryItemId is not null)
                            mutations++;
                        return member with { LibraryItemId = null };
                    }
                    var resolved = ResolveCandidate(candidates, member.ExpectedVersion);
                    if (resolved is null)
                    {
                        if (member.LibraryItemId is not null)
                            mutations++;
                        return member with { LibraryItemId = null };
                    }
                    mutations++;
                    resolvedMembers++;
                    return member with
                    {
                        LibraryItemId = resolved.LibraryItemId,
                        ExpectedName = resolved.Manifest.Name,
                        ExpectedVersion = resolved.Manifest.Version,
                        ExpectedAuthor = resolved.Manifest.Author,
                    };
                }).ToArray();
                if (mutations == 0)
                    break;

                try
                {
                    _ = await profiles.UpdateAsync(
                            ProfileId.Parse(profile.Id),
                            profile.Revision,
                            profile.DisplayName,
                            profile.Description,
                            profile.AssemblyBindingPolicyOverride,
                            members,
                            cancellationToken)
                        .ConfigureAwait(false);
                    reconnected += resolvedMembers;
                    break;
                }
                catch (InvalidOperationException) when (attempt < 2)
                {
                    // The Profile changed concurrently; recalculate against the latest revision.
                }
            }
        }
        return reconnected;
    }

    private static ModLibraryItem? ResolveCandidate(
        IReadOnlyList<ModLibraryItem> candidates,
        string expectedVersion)
    {
        if (candidates.Count == 1)
            return candidates[0];
        var versionCandidates = candidates
            .Where(item => item.Manifest.Version.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return versionCandidates.Length == 1 ? versionCandidates[0] : null;
    }
}

public static class ModProfileAutoAssignment
{
    public static async ValueTask<ModProfileAutoAssignmentResult> AddImportedToActiveProfileAsync(
        ActiveModProfileSelectionRepository activeProfiles,
        ModProfileV2Repository profiles,
        IReadOnlyList<ModLibraryItem> importedItems,
        CancellationToken cancellationToken = default) =>
        await AddImportedToActiveProfileAsync(
                activeProfiles,
                profiles,
                importedItems,
                importedItems,
                cancellationToken)
            .ConfigureAwait(false);

    public static async ValueTask<ModProfileAutoAssignmentResult> AddImportedToActiveProfileAsync(
        ActiveModProfileSelectionRepository activeProfiles,
        ModProfileV2Repository profiles,
        IReadOnlyList<ModLibraryItem> availableItems,
        IReadOnlyList<ModLibraryItem> importedItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(availableItems);
        ArgumentNullException.ThrowIfNull(importedItems);
        foreach (var item in availableItems)
            item?.Validate();
        foreach (var item in importedItems)
            item?.Validate();
        if (availableItems.Any(item => item is null) || importedItems.Any(item => item is null))
            throw new InvalidDataException("The available or imported Mod list contains a null item.");

        var availableByUniqueId = availableItems
            .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = await activeProfiles
                .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            var profileId = active.Validate();
            if (profileId.Value == ModProfileV2.NoModsId)
            {
                return new ModProfileAutoAssignmentResult(
                    profileId.Value,
                    AddedMembers: 0,
                    ExistingMembers: 0,
                    AmbiguousUniqueIds: 0,
                    BlockedByReadOnlyProfile: true);
            }

            var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            var existingMembers = profile.Members
                .ToDictionary(member => member.UniqueId, StringComparer.OrdinalIgnoreCase);
            var updatedMembers = profile.Members.ToList();
            var additions = new List<ModProfileMember>();
            var attached = 0;
            var existing = 0;
            var ambiguous = 0;
            foreach (var group in importedItems
                         .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var candidates = group
                    .GroupBy(item => item.LibraryItemId, StringComparer.Ordinal)
                    .Select(candidate => candidate.First())
                    .ToArray();
                if (existingMembers.TryGetValue(group.Key, out var existingMember))
                {
                    if (existingMember.LibraryItemId is not null)
                    {
                        existing++;
                        continue;
                    }
                    var exactVersionCandidates = availableByUniqueId
                        .GetValueOrDefault(group.Key, Array.Empty<ModLibraryItem>())
                        .Where(item => string.Equals(
                            item.Manifest.Version,
                            existingMember.ExpectedVersion,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (exactVersionCandidates.Length == 1)
                    {
                        var item = exactVersionCandidates[0];
                        var index = updatedMembers.FindIndex(member =>
                            string.Equals(member.UniqueId, group.Key, StringComparison.OrdinalIgnoreCase));
                        updatedMembers[index] = existingMember with
                        {
                            LibraryItemId = item.LibraryItemId,
                            ExpectedName = item.Manifest.Name,
                            ExpectedVersion = item.Manifest.Version,
                            ExpectedAuthor = item.Manifest.Author,
                        };
                        attached++;
                    }
                    else if (exactVersionCandidates.Length > 1)
                    {
                        ambiguous++;
                    }
                    else
                    {
                        existing++;
                    }
                    continue;
                }
                if (candidates.Length != 1)
                {
                    ambiguous++;
                    continue;
                }
                additions.Add(ModProfileMember.FromLibraryItem(candidates[0], enabled: true));
            }

            if (additions.Count == 0 && attached == 0)
            {
                return new ModProfileAutoAssignmentResult(
                    profile.Id,
                    AddedMembers: 0,
                    existing,
                    ambiguous,
                    BlockedByReadOnlyProfile: false);
            }

            try
            {
                _ = await profiles.UpdateAsync(
                        profileId,
                        profile.Revision,
                        profile.DisplayName,
                        profile.Description,
                        profile.AssemblyBindingPolicyOverride,
                    updatedMembers.Concat(additions).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ModProfileAutoAssignmentResult(
                    profile.Id,
                    additions.Count + attached,
                    existing,
                    ambiguous,
                    BlockedByReadOnlyProfile: false);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                // The group changed concurrently; recalculate against its latest revision.
            }
        }

        throw new InvalidOperationException("The active Mod Profile kept changing during import assignment.");
    }
}
