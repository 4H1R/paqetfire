using System.Text;

namespace PaqetFire.Broker.Configuration;

public sealed class AtomicConfigurationStore : IAtomicConfigurationStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _protectedRoot;
    private readonly string _protectedRootPrefix;
    private readonly HashSet<string> _brokerOwnedDestinations;
    private readonly SemaphoreSlim _commitLock = new(1, 1);

    public AtomicConfigurationStore(
        string protectedConfigurationRoot,
        IEnumerable<string> brokerOwnedDestinationPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedConfigurationRoot);
        ArgumentNullException.ThrowIfNull(brokerOwnedDestinationPaths);
        if (!Path.IsPathFullyQualified(protectedConfigurationRoot))
        {
            throw new ArgumentException(
                "The protected configuration root must be an absolute path.",
                nameof(protectedConfigurationRoot));
        }

        _protectedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(protectedConfigurationRoot));
        if (!Directory.Exists(_protectedRoot))
        {
            throw new DirectoryNotFoundException(
                $"The protected configuration root does not exist: '{_protectedRoot}'.");
        }

        EnsureExistingPathIsNotReparsePoint(_protectedRoot);
        _protectedRootPrefix = _protectedRoot + Path.DirectorySeparatorChar;
        _brokerOwnedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destinationPath in brokerOwnedDestinationPaths)
        {
            var destination = ValidateContainedDestination(destinationPath);
            _brokerOwnedDestinations.Add(destination);
            EnsureDestinationParentExists(destination);
            var backup = ValidateSidecarPath(destination + ".bak");
            if (File.Exists(backup))
            {
                ProtectedConfigurationFile.Harden(backup);
            }
            if (File.Exists(destination))
            {
                ProtectedConfigurationFile.Harden(destination);
            }
        }

        if (_brokerOwnedDestinations.Count == 0)
        {
            throw new ArgumentException(
                "At least one exact broker-owned configuration destination is required.",
                nameof(brokerOwnedDestinationPaths));
        }
    }

    public async Task WriteAsync(
        string destinationPath,
        string validatedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validatedText);
        var destination = ValidateDestination(destinationPath);

        await _commitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            destination = ValidateDestination(destination);
            var backup = ValidateSidecarPath(destination + ".bak");
            await CommitTextAsync(destination, backup, validatedText, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    public async Task RollbackAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = ValidateDestination(destinationPath);

        await _commitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            destination = ValidateDestination(destination);
            var backup = ValidateSidecarPath(destination + ".bak");
            if (!File.Exists(backup))
            {
                throw new FileNotFoundException(
                    "No last-known-good configuration backup exists.",
                    backup);
            }

            EnsureExistingPathIsNotReparsePoint(backup);
            ProtectedConfigurationFile.Harden(backup);
            await CommitFileCopyAsync(backup, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    private async Task CommitTextAsync(
        string destination,
        string backup,
        string validatedText,
        CancellationToken cancellationToken)
    {
        var temporary = CreateTemporaryPath(destination);
        try
        {
            var bytes = Utf8WithoutBom.GetBytes(validatedText);
            await using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CommitTemporaryFile(temporary, destination, backup);
            temporary = string.Empty;
        }
        finally
        {
            DeleteTemporaryFileIfPresent(temporary);
        }
    }

    private async Task CommitFileCopyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var temporary = CreateTemporaryPath(destination);
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = ProtectedConfigurationFile.CreateNew(temporary))
            {
                await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CommitTemporaryFile(temporary, destination, backupPath: null);
            temporary = string.Empty;
        }
        finally
        {
            DeleteTemporaryFileIfPresent(temporary);
        }
    }

    private void CommitTemporaryFile(
        string temporary,
        string destination,
        string? backupPath)
    {
        ValidateDestination(destination);
        EnsureExistingPathIsNotReparsePoint(temporary);
        ProtectedConfigurationFile.Harden(temporary);

        if (File.Exists(destination))
        {
            EnsureExistingPathIsNotReparsePoint(destination);
            ProtectedConfigurationFile.Harden(destination);
            if (backupPath is not null)
            {
                ValidateSidecarPath(backupPath);
            }

            File.Replace(temporary, destination, backupPath, ignoreMetadataErrors: false);
        }
        else
        {
            File.Move(temporary, destination);
        }

        ProtectedConfigurationFile.Harden(destination);
    }

    private string ValidateDestination(string destinationPath)
    {
        var destination = ValidateContainedDestination(destinationPath);
        if (!_brokerOwnedDestinations.Contains(destination))
        {
            throw new UnauthorizedAccessException(
                "The path is not an exact broker-owned configuration destination.");
        }

        EnsureDestinationParentExists(destination);
        return destination;
    }

    private string ValidateContainedDestination(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException(
                "A broker-owned configuration destination must be an absolute path.",
                nameof(destinationPath));
        }

        var destination = Path.GetFullPath(destinationPath);
        if (!destination.StartsWith(_protectedRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The configuration destination is outside the protected configuration root.");
        }

        var relativePath = Path.GetRelativePath(_protectedRoot, destination);
        if (relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Alternate data streams are not valid configuration destinations.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException(
                "A broker-owned configuration destination must have a parent directory.",
                nameof(destinationPath));
        }

        EnsureContainedPathHasNoReparsePoints(destination);
        return destination;
    }

    private void EnsureDestinationParentExists(string destination)
    {
        // Validate every existing ancestor before creating anything beneath it.
        EnsureContainedPathHasNoReparsePoints(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureContainedPathHasNoReparsePoints(destination);
    }

    private string ValidateSidecarPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_protectedRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "A configuration sidecar path escaped the protected configuration root.");
        }

        EnsureContainedPathHasNoReparsePoints(fullPath);
        return fullPath;
    }

    private void EnsureContainedPathHasNoReparsePoints(string path)
    {
        EnsureExistingPathIsNotReparsePoint(_protectedRoot);
        var relativePath = Path.GetRelativePath(_protectedRoot, path);
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = _protectedRoot;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureExistingPathIsNotReparsePoint(current);
            }
        }
    }

    private static void EnsureExistingPathIsNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                $"Reparse points are not allowed in broker-owned configuration paths: '{path}'.");
        }
    }

    private string CreateTemporaryPath(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = Path.Combine(directory, $".paqetfire-{Guid.NewGuid():N}.tmp");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return ValidateSidecarPath(candidate);
            }
        }

        throw new IOException("Unable to allocate a unique temporary configuration path.");
    }

    private static void DeleteTemporaryFileIfPresent(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
