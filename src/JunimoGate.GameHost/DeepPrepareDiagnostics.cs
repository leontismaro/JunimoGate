using System.Text.Json;
using Android.Content;
using Android.Util;
using JunimoGate.Android;

namespace JunimoGate.GameHost;

public sealed record DeepPrepareMetrics(
    long DurationMilliseconds,
    int PackageManagerSnapshotCount,
    int ApkSourceOpenCount,
    int ApkFullHashCount,
    long ApkBytesHashed,
    string SourceWorkspaceStatus,
    long SourceWorkspaceDurationMilliseconds,
    int WorkspacePayloadHashPassCount,
    long WorkspacePayloadBytesHashed,
    string AppliedWorkspaceStatus,
    long AppliedWorkspaceDurationMilliseconds,
    int ManagedProbeCount,
    int NativeInventoryCount,
    int RecipeEvaluationCount,
    int RewriteCount);

internal sealed record DeepPrepareDiagnosticReport(
    string Schema,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string Code,
    DeepPrepareMetrics Metrics);

internal static class DeepPrepareDiagnostics
{
    private const string ReportSchema = "junimogate-deep-prepare-diagnostic/v1";
    private const int MaximumReportBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async ValueTask RecordAsync(
        Context context,
        GamePreparationResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Metrics is null)
            return;

        var metrics = result.Metrics;
        Log.Info(
            "JunimoGate.DeepPrepare",
            $"summary status={result.Status} code={result.Code} durationMs={metrics.DurationMilliseconds} " +
            $"packageSnapshots={metrics.PackageManagerSnapshotCount} apkOpens={metrics.ApkSourceOpenCount} " +
            $"apkFullHashes={metrics.ApkFullHashCount} apkBytesHashed={metrics.ApkBytesHashed} " +
            $"workspacePayloadHashPasses={metrics.WorkspacePayloadHashPassCount} " +
            $"workspacePayloadBytesHashed={metrics.WorkspacePayloadBytesHashed} " +
            $"managedProbes={metrics.ManagedProbeCount} nativeInventories={metrics.NativeInventoryCount} " +
            $"recipeEvaluations={metrics.RecipeEvaluationCount} rewrites={metrics.RewriteCount} " +
            $"sourceStatus={metrics.SourceWorkspaceStatus} appliedStatus={metrics.AppliedWorkspaceStatus}");

        try
        {
            var safe = context.ApplicationContext ?? context;
            var root = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(safe), "diagnostics");
            Directory.CreateDirectory(root);
            var report = new DeepPrepareDiagnosticReport(
                ReportSchema,
                DateTimeOffset.UtcNow,
                result.Status.ToString(),
                result.Code,
                metrics);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (bytes.Length > MaximumReportBytes)
                return;

            var path = Path.Combine(root, "last-deep-prepare.json");
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Diagnostics never change the result of the preparation transaction.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Logcat still contains the compact summary when private report persistence fails.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // One bounded stale temporary is harmless and can be replaced on the next run.
        }
    }
}
