using System.Security.Cryptography;

namespace JunimoGate.Extraction;

public sealed record ExtractedAssemblyFile(
    string Name,
    string FullPath,
    long Size,
    string Sha256);

/// <summary>
/// Writes assembly images to a same-volume staging directory, hashes completed files,
/// and atomically publishes each file without overwriting existing output.
/// </summary>
public sealed class AssemblyExtractionTransaction : IDisposable, IAsyncDisposable
{
    private readonly string outputDirectory;
    private readonly string stagingDirectory;
    private readonly HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public AssemblyExtractionTransaction(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(this.outputDirectory);
        stagingDirectory = Path.Combine(this.outputDirectory, $".junimogate-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
    }

    public string OutputDirectory => outputDirectory;

    public string StagingDirectory => stagingDirectory;

    public async ValueTask<ExtractedAssemblyFile> ExtractAsync(
        AssemblyStoreV2 store,
        AssemblyStoreItem item,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(item);

        var outputName = ValidateAssemblyBaseName(item.Name);
        if (!reservedNames.Add(outputName))
        {
            throw new IOException($"Duplicate assembly output name '{outputName}' is not allowed.");
        }

        var destinationPath = Path.Combine(outputDirectory, outputName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"Assembly output '{destinationPath}' already exists; overwrite is not allowed.");
        }

        var stagingPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            long size;
            string sha256;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await using var hashingOutput = new HashingWriteStream(output, hash);
                await store.CopyAssemblyImageToAsync(item, hashingOutput, cancellationToken).ConfigureAwait(false);
                await hashingOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                size = hashingOutput.BytesWritten;
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            File.Move(stagingPath, destinationPath, overwrite: false);
            return new ExtractedAssemblyFile(outputName, destinationPath, size, sha256);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    public static string ValidateAssemblyBaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." ||
            !name.Equals(name.Trim(), StringComparison.Ordinal) ||
            name.EndsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(['/', '\\', '\0', '<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            name.Any(char.IsControl) ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
            IsReservedWindowsDeviceName(Path.GetFileNameWithoutExtension(name)))
        {
            throw new ArgumentException($"Unsafe assembly basename '{name}'.", nameof(name));
        }

        return name;
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               name[3] is >= '1' and <= '9';
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryDeleteDirectory(stagingDirectory);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The original extraction failure is more useful than cleanup failure.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Staging cleanup is best-effort on disposal.
        }
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly IncrementalHash hash;

        public HashingWriteStream(Stream inner, IncrementalHash hash)
        {
            this.inner = inner;
            this.hash = hash;
        }

        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
            BytesWritten = checked(BytesWritten + count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            hash.AppendData(buffer, offset, count);
            BytesWritten = checked(BytesWritten + count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The transaction owns and disposes the underlying file stream.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
