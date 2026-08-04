namespace JunimoGate.Mods;

public sealed record ModProfileAutoAssignmentResult(
    string ProfileId,
    int AddedMembers,
    int ExistingMembers,
    int AmbiguousUniqueIds,
    bool BlockedByReadOnlyProfile);

public static class ModProfileAutoAssignment
{
    public static async ValueTask<ModProfileAutoAssignmentResult> AddImportedToActiveProfileAsync(
        ActiveModProfileSelectionRepository activeProfiles,
        ModProfileV2Repository profiles,
        IReadOnlyList<ModLibraryItem> importedItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(importedItems);
        foreach (var item in importedItems)
            item?.Validate();
        if (importedItems.Any(item => item is null))
            throw new InvalidDataException("The imported Mod list contains a null item.");

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
                    var exactVersionCandidates = candidates
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
