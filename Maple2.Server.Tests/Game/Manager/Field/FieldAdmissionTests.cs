using Maple2.Server.Game.Manager.Field;

namespace Maple2.Server.Tests.Game.Manager.Field;

public class FieldAdmissionTests {
    [Test]
    public void PendingTransfersConsumeCapacityBeforeClientsFinishLoading() {
        var admission = new FieldAdmission(4);
        for (int character = 1; character <= 4; character++) {
            Assert.That(admission.TryReserve(character, new object()), Is.True);
        }
        Assert.That(admission.IsFull, Is.True);
        Assert.That(admission.TryReserve(5, new object()), Is.False);
    }

    [Test]
    public void FailedOrAbandonedTransferReleasesItsSlot() {
        var admission = new FieldAdmission(1);
        var owner = new object();
        Assert.That(admission.TryReserve(1, owner), Is.True);
        admission.Release(1, owner);
        Assert.That(admission.IsFull, Is.False);
        Assert.That(admission.TryReserve(2, new object()), Is.True);
    }

    [Test]
    public void StaleSessionCleanupCannotReleaseAReconnectedPlayer() {
        var admission = new FieldAdmission(1);
        var oldSession = new object();
        var newSession = new object();
        Assert.That(admission.TryReserve(1, oldSession), Is.True);
        Assert.That(admission.TryReserve(1, newSession), Is.True);
        admission.Release(1, oldSession);
        Assert.That(admission.TryReserve(2, new object()), Is.False);
        admission.Release(1, newSession);
        Assert.That(admission.TryReserve(2, new object()), Is.True);
    }
}
