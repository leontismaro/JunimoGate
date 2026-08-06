using Android.Content;
using JunimoGate.Core;
using AndroidDateFormat = Android.Text.Format.DateFormat;

namespace JunimoGate.App;

internal sealed class SaveUiFormatter(Context context)
{
    private readonly Context context = context;

    public string Title(SaveGameMetadata metadata) => metadata.FarmName is { Length: > 0 } farm
        ? FormatString(Resource.String.saves_farm_title, new Java.Lang.String(farm))
        : context.GetString(Resource.String.saves_unnamed) ?? "—";

    public string Summary(SaveGameMetadata metadata)
    {
        var player = metadata.PlayerName ?? context.GetString(Resource.String.saves_unknown_farmer) ?? "—";
        if (metadata.Year is not { } year || metadata.Season is not { } season || metadata.Day is not { } day)
            return player;
        var seasonText = context.GetString(season switch
        {
            0 => Resource.String.season_spring,
            1 => Resource.String.season_summer,
            2 => Resource.String.season_fall,
            3 => Resource.String.season_winter,
            _ => Resource.String.season_unknown,
        }) ?? "—";
        return FormatString(
            Resource.String.saves_game_date,
            new Java.Lang.String(player),
            Java.Lang.Integer.ValueOf(year),
            new Java.Lang.String(seasonText),
            Java.Lang.Integer.ValueOf(day));
    }

    public string Details(SaveGameMetadata metadata, DateTimeOffset updated)
    {
        var time = metadata.PlayTime is { } played
            ? FormatPlayTime(played)
            : context.GetString(Resource.String.saves_play_time_unknown) ?? "—";
        var updatedText = FormatDateTime(updated);
        return metadata.GameVersion is { Length: > 0 } version
            ? FormatString(
                Resource.String.saves_details_with_version,
                new Java.Lang.String(time),
                new Java.Lang.String(updatedText),
                new Java.Lang.String(version))
            : FormatString(
                Resource.String.saves_details,
                new Java.Lang.String(time),
                new Java.Lang.String(updatedText));
    }

    public string BackupDetails(SaveBackupEntry backup) => FormatString(
        Resource.String.saves_backup_details,
        Java.Lang.Integer.ValueOf(backup.SaveEntryCount),
        new Java.Lang.String(global::Android.Text.Format.Formatter.FormatShortFileSize(context, backup.Size) ?? $"{backup.Size} B"));

    public string BackupContents(SaveBackupEntry backup) => FormatString(
        Resource.String.saves_backup_contains,
        new Java.Lang.String(string.Join("、", backup.Saves.Select(save => Title(save.Metadata)))));

    public string SaveExportFileName(LiveSaveGameEntry save)
    {
        var name = SanitizeFileName(save.Metadata.FarmName ?? "Stardew-save");
        return $"{name}-{DateTime.UtcNow:yyyyMMdd}.zip";
    }

    public string BackupExportFileName(SaveBackupEntry backup) =>
        $"Stardew-backup-{backup.LastWriteTimeUtc:yyyyMMdd-HHmm}.zip";

    public string FormatDateTime(DateTimeOffset value)
    {
        using var date = new Java.Util.Date(value.ToUnixTimeMilliseconds());
        var dateText = AndroidDateFormat.GetMediumDateFormat(context)?.Format(date) ?? "—";
        var timeText = AndroidDateFormat.GetTimeFormat(context)?.Format(date) ?? "—";
        return FormatString(
            Resource.String.date_time_value,
            new Java.Lang.String(dateText),
            new Java.Lang.String(timeText));
    }

    public string FormatPlayTime(TimeSpan value)
    {
        var hours = (int)value.TotalHours;
        return hours > 0
            ? FormatString(
                Resource.String.saves_play_time_hours_minutes,
                Java.Lang.Integer.ValueOf(hours),
                Java.Lang.Integer.ValueOf(value.Minutes))
            : FormatString(
                Resource.String.saves_play_time_minutes,
                Java.Lang.Integer.ValueOf(Math.Max(0, value.Minutes)));
    }

    private string FormatString(int resourceId, params Java.Lang.Object[] arguments) =>
        context.Resources?.GetString(resourceId, arguments) ?? "—";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var characters = value.Trim()
            .Select(character => invalid.Contains(character) || character is '/' or '\\' || char.IsControl(character)
                ? '_'
                : character)
            .ToArray();
        var result = new string(characters).Trim('.', ' ');
        return result.Length == 0 ? "Stardew-save" : result;
    }
}
