using System.Text;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Configuration;

public sealed class MachineProfileCatalogStore(
    string catalogPath,
    IProfileSecretProtector secretProtector) : IMachineProfileCatalogStore
{
    private readonly string path = ValidatePath(catalogPath);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<ProfileCatalog?> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length > ProfileCatalogJson.MaximumDocumentBytes)
            {
                throw new InvalidDataException("The saved profile catalog is too large.");
            }

            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return ProfileCatalogJson.ReadProtected(json, secretProtector);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(ProfileCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var json = ProfileCatalogJson.WriteProtected(catalog, secretProtector);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Guid.NewGuid():N}.new";
            try
            {
                await using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                ProtectedConfigurationFile.Harden(path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ValidatePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The profile catalog path must be absolute.", nameof(value));
        }

        return Path.GetFullPath(value);
    }
}
