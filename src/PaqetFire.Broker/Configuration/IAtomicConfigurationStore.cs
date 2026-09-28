namespace PaqetFire.Broker.Configuration;

public interface IAtomicConfigurationStore
{
    Task DeleteAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string destinationPath,
        string validatedText,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);
}
