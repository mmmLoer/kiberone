using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class VpnReservationTests
{
    [Fact]
    public void One_private_key_cannot_be_reserved_for_two_clients_even_after_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kiberone-vpn-reservations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secret = LocationPassword.Create("test-password");
            var locations = new[]
            {
                new LocationSecretRecord("ШБ", secret.Salt, secret.Hash),
                new LocationSecretRecord("АРТЕША", secret.Salt, secret.Hash)
            };
            var first = new ClassroomHubStore(directory, locations);
            var fingerprint = VpnConfigIdentity.Fingerprint("[Interface]\nPrivateKey = private-key-1\n");
            var request = new VpnPeerReservationRequest("ШБ", "test-password", "pc-1", "path-nl", "slot-1", fingerprint);
            Assert.Equal("pc-1", first.ReserveVpnPeer(request).ClientId);
            Assert.Equal("pc-1", first.ReserveVpnPeer(request).ClientId);

            var restarted = new ClassroomHubStore(directory, locations);
            Assert.Equal("slot-1", restarted.GetVpnReservation("ШБ", "test-password", "pc-1")?.Slot);
            Assert.Throws<VpnPeerConflictException>(() => restarted.ReserveVpnPeer(request with { ClientId = "pc-2" }));
            Assert.Throws<VpnPeerConflictException>(() => restarted.ReserveVpnPeer(request with
            {
                Location = "АРТЕША",
                ClientId = "pc-2"
            }));
            Assert.Throws<VpnPeerConflictException>(() => restarted.ReserveVpnPeer(request with
            {
                Location = "АРТЕША",
                ClientId = "pc-2",
                Fingerprint = VpnConfigIdentity.Fingerprint("PrivateKey = different-key")
            }));
            Assert.Throws<VpnPeerConflictException>(() => restarted.ReserveVpnPeer(request with
            {
                Slot = "slot-2",
                Fingerprint = VpnConfigIdentity.Fingerprint("PrivateKey = private-key-2")
            }));
            Assert.Throws<UnauthorizedAccessException>(() => restarted.GetVpnReservation("ШБ", "wrong", "pc-1"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Concurrent_reservations_have_exactly_one_owner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kiberone-vpn-reservations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secret = LocationPassword.Create("test-password");
            var store = new ClassroomHubStore(directory,
                [new LocationSecretRecord("ШБ", secret.Salt, secret.Hash)]);
            var fingerprint = VpnConfigIdentity.Fingerprint("PrivateKey = shared-key");
            var successes = 0;
            Parallel.For(0, 20, index =>
            {
                try
                {
                    store.ReserveVpnPeer(new VpnPeerReservationRequest("ШБ", "test-password",
                        "pc-" + index, "path-nl", "slot-1", fingerprint));
                    Interlocked.Increment(ref successes);
                }
                catch (VpnPeerConflictException) { }
            });
            Assert.Equal(1, successes);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
