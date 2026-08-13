namespace JunimoGate.App;

internal static class ModDocumentPickerPolicy
{
    public const string RequestMimeType = "*/*";

    public static IReadOnlyList<string> AcceptedMimeTypes { get; } =
    [
        "application/zip",
        "application/x-zip-compressed",
        "application/octet-stream",
    ];

    public static IReadOnlyList<T> ResolveDocuments<T>(T? direct, IEnumerable<T?> clipItems)
        where T : class
    {
        var result = new List<T>();
        var seen = new HashSet<T>();
        if (direct is not null && seen.Add(direct))
            result.Add(direct);
        foreach (var item in clipItems)
        {
            if (item is not null && seen.Add(item))
                result.Add(item);
        }
        return result;
    }
}
