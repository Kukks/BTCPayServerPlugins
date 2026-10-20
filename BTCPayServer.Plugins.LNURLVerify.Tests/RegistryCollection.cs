using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

// The registries are process-wide statics and a running poller touches every tracked invoice, so tests
// that read registry state must not overlap a poller started by another class.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegistryCollection
{
    public const string Name = "Registry";
}
