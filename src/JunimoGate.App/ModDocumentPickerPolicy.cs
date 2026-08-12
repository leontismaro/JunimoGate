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

    public static T? ResolveSingleDocument<T>(T? direct, T? singleClipItem, int clipItemCount)
        where T : class
    {
        if (direct is not null)
            return direct;
        return clipItemCount == 1 ? singleClipItem : null;
    }
}
