// src/Noctis/Services/AccountFeatures.cs
namespace Noctis.Services;

/// <summary>
/// The desktop's phone-account features: creating the account, pairing / signing in phones
/// (noctisSignIn and device keys on the Noctis server) and Sync between devices. Parked as
/// "Coming soon" while false: the Account &amp; Sync cards show disabled, the sync ledger is not
/// wired, and a SyncEnabled flag or an account left in the data folder by earlier builds
/// activates nothing. The Phone app card parks with them: its switch is Noctis Server itself, so
/// a NoctisServerEnabled flag left by an earlier build starts no server either (kept on disk for
/// the build that ships it). Web Remote does not depend on it. Flip to true to ship them.
/// </summary>
public static class AccountFeatures
{
    public static readonly bool Enabled = true;
}
