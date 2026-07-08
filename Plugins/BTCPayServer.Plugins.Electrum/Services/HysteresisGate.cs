namespace BTCPayServer.Plugins.Electrum.Services;

// Anti-flap gate for backend flips. A flip only happens once the desired backend
// has been the poll's decision for `requiredConsecutive` polls in a row and no
// flip occurred within the cooldown — otherwise a flapping NBX would thrash every
// wallet's backend (and run a fast-forward) every poll.
public static class HysteresisGate
{
    public const int RequiredToNbx = 4;       // ~2 min at 30s polls
    public const int RequiredToElectrum = 3;
    public const int CooldownPolls = 2;

    public static int RequiredFor(WalletBackend desired) =>
        desired == WalletBackend.Nbx ? RequiredToNbx : RequiredToElectrum;

    public static bool ShouldFlip(WalletBackend current, WalletBackend desired,
        int consecutiveAgree, int requiredConsecutive, bool cooldownElapsed) =>
        desired != current && consecutiveAgree >= requiredConsecutive && cooldownElapsed;
}
