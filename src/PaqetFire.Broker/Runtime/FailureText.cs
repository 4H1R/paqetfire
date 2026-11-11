using PaqetFire.Core.Connections;

namespace PaqetFire.Broker.Runtime;

/// <summary>
/// Turns a failure and its causes into one sentence chain. The outer exceptions often say
/// only where it failed ("at the Xray engine"); the reason is several causes deep.
/// </summary>
public static class FailureText
{
    private const int MaximumLength = 1200;

    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        List<string> parts = [];
        for (var current = error; current is not null; current = current.InnerException)
        {
            var message = current switch
            {
                // Its own message is generic; the engine and operation are what it adds.
                ConnectionTransitionException transition => $"{transition.Engine} failed during {transition.Operation}.",
                AggregateException => null,
                _ => current.Message.Trim(),
            };

            if (!string.IsNullOrEmpty(message) && !parts.Any(part => part.Contains(message, StringComparison.Ordinal)))
            {
                parts.Add(message);
            }

            if (current is ConnectionTransitionException { RollbackErrors.Count: > 0 } withRollback)
            {
                parts.Add($"Stopping the engines afterwards also failed: {string.Join(" ", withRollback.RollbackErrors.Select(rollback => rollback.Message))}");
            }
        }

        var text = string.Join(" ", parts);
        return text.Length > MaximumLength ? text[..MaximumLength] + "…" : text;
    }

    /// <summary>The causes below a transition, for a message that already names the engine.</summary>
    public static string? DescribeCause(ConnectionTransitionException transition) =>
        transition.InnerException is { } cause ? Describe(cause) : null;
}
