namespace JunimoGate.Mods;

public sealed record ModProfileMemberMutationResult(
    ModProfileV2 Profile,
    int AddedMembers,
    int ReplacedMembers,
    int ChangedMembers);

public sealed class ModProfileMemberMutationService(ModProfileV2Repository profiles)
{
    public async ValueTask<ModProfileMemberMutationResult> AddOrReplaceAsync(
        ProfileId profileId,
        IReadOnlyList<ModLibraryItem> items,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        EnsureEditable(profileId);
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            item?.Validate();
            if (item is null || !distinct.Add(item.Manifest.UniqueId))
                throw new ArgumentException("Only one installed version per Mod can be added at a time.", nameof(items));
        }

        var current = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var members = current.Members.ToList();
        var added = 0;
        var replaced = 0;
        var changed = 0;
        foreach (var item in items)
        {
            var index = members.FindIndex(member =>
                member.UniqueId.Equals(item.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                members.Add(ModProfileMember.FromLibraryItem(item, enabled));
                added++;
                changed++;
                continue;
            }

            var existing = members[index];
            var replacement = ModProfileMember.FromLibraryItem(item, enabled) with
            {
                AddedAtUtc = existing.AddedAtUtc,
            };
            if (existing == replacement)
                continue;
            if (existing.LibraryItemId != replacement.LibraryItemId)
                replaced++;
            members[index] = replacement;
            changed++;
        }

        if (changed == 0)
            return new ModProfileMemberMutationResult(current, added, replaced, changed);
        var updated = await CommitAsync(current, members, cancellationToken).ConfigureAwait(false);
        return new ModProfileMemberMutationResult(updated, added, replaced, changed);
    }

    public async ValueTask<ModProfileMemberMutationResult> SetEnabledAsync(
        ProfileId profileId,
        IReadOnlyCollection<string> uniqueIds,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uniqueIds);
        EnsureEditable(profileId);
        var targets = NormalizeUniqueIds(uniqueIds);
        var current = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var members = current.Members.ToArray();
        var changed = 0;
        for (var index = 0; index < members.Length; index++)
        {
            if (targets.Contains(members[index].UniqueId) && members[index].Enabled != enabled)
            {
                members[index] = members[index] with { Enabled = enabled };
                changed++;
            }
        }
        if (changed == 0)
            return new ModProfileMemberMutationResult(current, 0, 0, 0);
        var updated = await CommitAsync(current, members, cancellationToken).ConfigureAwait(false);
        return new ModProfileMemberMutationResult(updated, 0, 0, changed);
    }

    public async ValueTask<ModProfileMemberMutationResult> RemoveAsync(
        ProfileId profileId,
        IReadOnlyCollection<string> uniqueIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uniqueIds);
        EnsureEditable(profileId);
        var targets = NormalizeUniqueIds(uniqueIds);
        var current = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var members = current.Members.Where(member => !targets.Contains(member.UniqueId)).ToArray();
        var changed = current.Members.Count - members.Length;
        if (changed == 0)
            return new ModProfileMemberMutationResult(current, 0, 0, 0);
        var updated = await CommitAsync(current, members, cancellationToken).ConfigureAwait(false);
        return new ModProfileMemberMutationResult(updated, 0, 0, changed);
    }

    public async ValueTask<ModProfileV2> UpdateMetadataAsync(
        ProfileId profileId,
        string displayName,
        string? description,
        ModAssemblyBindingPolicy? bindingPolicyOverride,
        CancellationToken cancellationToken = default)
    {
        EnsureEditable(profileId);
        var current = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await profiles.UpdateAsync(
                profileId,
                current.Revision,
                displayName,
                description,
                bindingPolicyOverride,
                current.Members,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<ModProfileV2> CommitAsync(
        ModProfileV2 current,
        IReadOnlyList<ModProfileMember> members,
        CancellationToken cancellationToken) =>
        profiles.UpdateAsync(
            ProfileId.Parse(current.Id),
            current.Revision,
            current.DisplayName,
            current.Description,
            current.AssemblyBindingPolicyOverride,
            members,
            cancellationToken);

    private static HashSet<string> NormalizeUniqueIds(IReadOnlyCollection<string> uniqueIds)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uniqueId in uniqueIds)
        {
            if (string.IsNullOrWhiteSpace(uniqueId) || uniqueId.Length > 256)
                throw new ArgumentException("A Mod UniqueID is invalid.", nameof(uniqueIds));
            result.Add(uniqueId);
        }
        return result;
    }

    private static void EnsureEditable(ProfileId profileId)
    {
        if (profileId.Value == ModProfileV2.NoModsId)
            throw new InvalidOperationException("The no-Mod Profile cannot be edited.");
    }
}
