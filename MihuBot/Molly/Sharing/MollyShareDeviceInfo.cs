#nullable enable

namespace MihuBot.Molly.Sharing;

/// <summary>
/// A device's file sharing state as shown on the admin dashboard: only what S knows without opening anything, and
/// nothing about who shares with whom.
/// </summary>
/// <param name="IdentityBoundAt">When the device's current Signal identity was bound; null while unbound.</param>
/// <param name="LastActivityAt">The device's last share request, which keeps its grants alive.</param>
/// <param name="SameIdentityDevices">Other registered devices bound to the same identity (linked devices).</param>
/// <param name="Reservations">ENTRYs reserved for creates that haven't happened yet.</param>
/// <param name="Owned">The grants this device created, oldest first.</param>
/// <param name="ReceivedAvailable">Grants to the device's current identity that weren't started.</param>
/// <param name="ReceivedStarted">Grants to the device's current identity being recovered.</param>
/// <param name="ReceivedReleased">Grants to the device's current identity that were released.</param>
/// <param name="PendingTrusteeRequests">Started recoveries in which the device's current identity is a trustee that hasn't approved.</param>
public sealed record MollyShareDeviceInfo(DateTime? IdentityBoundAt, DateTime? LastActivityAt, Guid[] SameIdentityDevices, int Reservations, MollyShareOwnedInfo[] Owned, int ReceivedAvailable, int ReceivedStarted, int ReceivedReleased, int PendingTrusteeRequests);

/// <summary>A grant as shown on the admin dashboard.</summary>
/// <param name="Threshold">M, known once started.</param>
/// <param name="Trustees">N, known once started (0 before).</param>
/// <param name="ExpiresAt">
/// When maintenance deletes the grant unless the owner device stays active (active and started grants) or it is deleted
/// first (released ones). Null if the owner device was wiped: maintenance deletes the grant on its next (hourly) run.
/// </param>
public sealed record MollyShareOwnedInfo(Guid Id, MollyShareState State, DateTime CreatedAt, DateTime? StartedAt, DateTime? ReleasedAt, int? Threshold, int Trustees, int Approvals, DateTime? ExpiresAt);
