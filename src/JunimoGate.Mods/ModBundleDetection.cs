using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Mods;

public sealed record DetectedModBundle(
    string FamilyKey,
    string DisplayName,
    string? ProductDirectory,
    IReadOnlyList<ModArchiveCandidate> Members);

public sealed record ModBundleDetectionResult(
    IReadOnlyList<DetectedModBundle> Bundles,
    IReadOnlyList<ModArchiveCandidate> Standalone);

public static class ModBundleDetector
{
    private static readonly HashSet<string> GenericProductTokens = new(StringComparer.Ordinal)
    {
        "archive", "bundle", "code", "component", "content", "download", "downloaded", "framework",
        "mod", "mods", "pack",
    };

    private static readonly HashSet<string> GenericDirectoryNames = new(StringComparer.Ordinal)
    {
        "archive", "bundle", "download", "downloaded", "mod", "mods", "pack",
    };

    private static readonly HashSet<string> GenericAuthorTokens = new(StringComparer.Ordinal)
    {
        "and", "by", "team", "the", "with",
    };

    public static ModBundleDetectionResult Detect(IReadOnlyList<ModArchiveCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count < 2)
            return new ModBundleDetectionResult(Array.Empty<DetectedModBundle>(), candidates.ToArray());

        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] is null || candidates[index].Manifest is null ||
                string.IsNullOrWhiteSpace(candidates[index].Manifest.UniqueId))
            {
                throw new InvalidDataException("A Mod bundle candidate is malformed.");
            }
        }

        var edges = Enumerable.Range(0, candidates.Count)
            .Select(_ => new HashSet<int>())
            .ToArray();
        for (var first = 0; first < candidates.Count; first++)
        {
            for (var second = first + 1; second < candidates.Count; second++)
            {
                if (!ShouldBundle(candidates[first], candidates[second]))
                    continue;
                edges[first].Add(second);
                edges[second].Add(first);
            }
        }

        var visited = new bool[candidates.Count];
        var bundles = new List<DetectedModBundle>();
        var standalone = new List<ModArchiveCandidate>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (visited[index])
                continue;
            var component = ReadComponent(index, edges, visited);
            var duplicateUniqueIds = component
                .GroupBy(memberIndex => candidates[memberIndex].Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var duplicate in component.Where(memberIndex =>
                         duplicateUniqueIds.Contains(candidates[memberIndex].Manifest.UniqueId)))
            {
                standalone.Add(candidates[duplicate]);
            }

            var eligible = component
                .Where(memberIndex => !duplicateUniqueIds.Contains(candidates[memberIndex].Manifest.UniqueId))
                .ToHashSet();
            var eligibleComponents = ReadSubcomponents(eligible, edges);
            if (eligibleComponents.Count == 0)
                continue;
            foreach (var eligibleComponent in eligibleComponents)
            {
                if (eligibleComponent.Count < 2)
                {
                    standalone.Add(candidates[eligibleComponent[0]]);
                    continue;
                }

                var members = eligibleComponent
                    .Select(memberIndex => candidates[memberIndex])
                    .OrderBy(candidate => candidate.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var productDirectory = FindProductDirectory(members);
                var displayName = ChooseDisplayName(members, productDirectory);
                bundles.Add(new DetectedModBundle(
                    CreateFamilyKey(members, displayName),
                    displayName,
                    productDirectory,
                    members));
            }
        }

        return new ModBundleDetectionResult(
            bundles.OrderBy(bundle => bundle.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            standalone.OrderBy(candidate => candidate.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool ShouldBundle(ModArchiveCandidate first, ModArchiveCandidate second)
    {
        if (first.Manifest.UniqueId.Equals(second.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (HaveSameValidUpdateKey(first.Manifest, second.Manifest))
            return true;

        var sameProductName = HaveSameProductName(first.Manifest, second.Manifest);
        var sameProductDirectory = HaveSameProductDirectory(first, second);
        var directDependency = HasDirectDependency(first.Manifest, second.Manifest);
        var sameAuthor = AuthorsOverlap(first.Manifest.Author, second.Manifest.Author);
        var sameVersion = ModVersionStringComparer.Instance.Compare(
            first.Manifest.Version,
            second.Manifest.Version) == 0;

        if (sameProductName && sameProductDirectory && directDependency)
            return true;
        if (sameProductName && sameProductDirectory && sameAuthor && sameVersion)
            return true;
        if (sameProductName && directDependency && (sameAuthor || sameVersion))
            return true;
        return sameProductDirectory && directDependency && sameAuthor && sameVersion;
    }

    private static bool HaveSameValidUpdateKey(ModManifestSummary first, ModManifestSummary second)
    {
        var keys = first.UpdateKeys
            .Select(NormalizeUpdateKey)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        return second.UpdateKeys
            .Select(NormalizeUpdateKey)
            .Any(key => key is not null && keys.Contains(key));
    }

    private static string? NormalizeUpdateKey(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator < 1 || separator == trimmed.Length - 1 || trimmed.Any(char.IsWhiteSpace))
            return null;
        var provider = trimmed[..separator];
        var identity = trimmed[(separator + 1)..];
        if (provider.Any(character => !char.IsLetterOrDigit(character)) ||
            identity is "-1" or "0" || identity.Contains('?', StringComparison.Ordinal))
        {
            return null;
        }
        return trimmed.ToLowerInvariant();
    }

    private static bool HaveSameProductName(ModManifestSummary first, ModManifestSummary second)
    {
        var firstTokens = ProductTokens(first.Name);
        var secondTokens = ProductTokens(second.Name);
        var common = LongestCommonPhrase(firstTokens, secondTokens);
        if (common.Count >= 2)
            return true;
        if (common.Count != 1 || common[0].Length < 6)
            return false;
        var token = common[0];
        return ProductTokens(first.UniqueId).Contains(token) && ProductTokens(second.UniqueId).Contains(token);
    }

    private static bool HaveSameProductDirectory(ModArchiveCandidate first, ModArchiveCandidate second)
    {
        var common = DeepestCommonDirectory(first.RootPath, second.RootPath);
        if (common is null)
            return false;
        var name = common.Split('/').Last();
        var directoryTokens = ProductTokens(name);
        if (directoryTokens.Count == 0 || directoryTokens.All(GenericDirectoryNames.Contains))
            return false;
        return SharesMeaningfulPhrase(directoryTokens, ProductTokens(first.Manifest.Name)) &&
               SharesMeaningfulPhrase(directoryTokens, ProductTokens(second.Manifest.Name));
    }

    private static bool HasDirectDependency(ModManifestSummary first, ModManifestSummary second) =>
        first.Dependencies.Any(dependency => dependency.UniqueId.Equals(
            second.UniqueId,
            StringComparison.OrdinalIgnoreCase)) ||
        second.Dependencies.Any(dependency => dependency.UniqueId.Equals(
            first.UniqueId,
            StringComparison.OrdinalIgnoreCase));

    private static bool AuthorsOverlap(string first, string second)
    {
        var authors = AuthorTokens(first).ToHashSet(StringComparer.Ordinal);
        return AuthorTokens(second).Any(authors.Contains);
    }

    private static IReadOnlyList<int> ReadComponent(int start, IReadOnlyList<HashSet<int>> edges, bool[] visited)
    {
        var result = new List<int>();
        var pending = new Stack<int>();
        pending.Push(start);
        visited[start] = true;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            result.Add(current);
            foreach (var next in edges[current])
            {
                if (visited[next])
                    continue;
                visited[next] = true;
                pending.Push(next);
            }
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<int>> ReadSubcomponents(
        IReadOnlySet<int> eligible,
        IReadOnlyList<HashSet<int>> edges)
    {
        var result = new List<IReadOnlyList<int>>();
        var visited = new HashSet<int>();
        foreach (var start in eligible.Order())
        {
            if (!visited.Add(start))
                continue;
            var component = new List<int>();
            var pending = new Stack<int>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                component.Add(current);
                foreach (var next in edges[current])
                {
                    if (eligible.Contains(next) && visited.Add(next))
                        pending.Push(next);
                }
            }
            result.Add(component);
        }
        return result;
    }

    private static string? FindProductDirectory(IReadOnlyList<ModArchiveCandidate> members)
    {
        var segments = members[0].RootPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var member in members.Skip(1))
        {
            var current = member.RootPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var length = Math.Min(segments.Count, current.Length);
            var shared = 0;
            while (shared < length && segments[shared].Equals(current[shared], StringComparison.OrdinalIgnoreCase))
                shared++;
            segments.RemoveRange(shared, segments.Count - shared);
        }
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var candidate = segments[index];
            var tokens = ProductTokens(candidate);
            if (tokens.Count == 0 || tokens.All(GenericDirectoryNames.Contains))
                continue;
            if (members.All(member => SharesMeaningfulPhrase(tokens, ProductTokens(member.Manifest.Name))))
                return candidate;
        }
        return null;
    }

    private static string ChooseDisplayName(IReadOnlyList<ModArchiveCandidate> members, string? productDirectory)
    {
        if (!string.IsNullOrWhiteSpace(productDirectory))
            return productDirectory.Trim();

        var common = ProductTokens(members[0].Manifest.Name).ToArray();
        foreach (var member in members.Skip(1))
            common = LongestCommonPhrase(common, ProductTokens(member.Manifest.Name)).ToArray();
        if (common.Length > 0)
            return string.Join(' ', common.Select(TitleCaseToken));
        return members.Select(member => member.Manifest.Name.Trim())
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string CreateFamilyKey(IReadOnlyList<ModArchiveCandidate> members, string displayName)
    {
        var commonUpdateKeys = members
            .Select(member => member.Manifest.UpdateKeys.Select(NormalizeUpdateKey).Where(key => key is not null).Cast<string>().ToHashSet(StringComparer.Ordinal))
            .Aggregate((current, next) =>
            {
                current.IntersectWith(next);
                return current;
            });
        var identity = commonUpdateKeys.Order(StringComparer.Ordinal).FirstOrDefault() ??
                       $"{string.Join('-', ProductTokens(displayName))}|{MostCommonAuthor(members)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string MostCommonAuthor(IReadOnlyList<ModArchiveCandidate> members) => members
        .SelectMany(member => AuthorTokens(member.Manifest.Author))
        .GroupBy(author => author, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.Key)
        .FirstOrDefault() ?? "unknown";

    private static string? DeepestCommonDirectory(string first, string second)
    {
        var firstSegments = first.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var secondSegments = second.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Min(firstSegments.Length, secondSegments.Length);
        string? result = null;
        for (var index = 0; index < length; index++)
        {
            if (!firstSegments[index].Equals(secondSegments[index], StringComparison.OrdinalIgnoreCase))
                break;
            result = string.Join('/', firstSegments[..(index + 1)]);
        }
        return result;
    }

    private static IReadOnlyList<string> ProductTokens(string value) => Tokenize(value)
        .Where(token => !GenericProductTokens.Contains(token))
        .ToArray();

    private static IEnumerable<string> AuthorTokens(string value) => Tokenize(value)
        .Where(token => token.Length >= 3 && !GenericAuthorTokens.Contains(token));

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }
            if (current.Length == 0)
                continue;
            result.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static IReadOnlyList<string> LongestCommonPhrase(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        var bestLength = 0;
        var bestStart = 0;
        var lengths = new int[second.Count + 1];
        for (var firstIndex = 1; firstIndex <= first.Count; firstIndex++)
        {
            for (var secondIndex = second.Count; secondIndex >= 1; secondIndex--)
            {
                if (first[firstIndex - 1].Equals(second[secondIndex - 1], StringComparison.Ordinal))
                {
                    lengths[secondIndex] = lengths[secondIndex - 1] + 1;
                    if (lengths[secondIndex] > bestLength)
                    {
                        bestLength = lengths[secondIndex];
                        bestStart = firstIndex - bestLength;
                    }
                }
                else
                {
                    lengths[secondIndex] = 0;
                }
            }
        }
        return first.Skip(bestStart).Take(bestLength).ToArray();
    }

    private static bool SharesMeaningfulPhrase(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var common = LongestCommonPhrase(first, second);
        return common.Count >= 2 || common.Count == 1 && common[0].Length >= 6;
    }

    private static string TitleCaseToken(string value) => value.Length == 0
        ? value
        : char.ToUpperInvariant(value[0]) + value[1..];
}
