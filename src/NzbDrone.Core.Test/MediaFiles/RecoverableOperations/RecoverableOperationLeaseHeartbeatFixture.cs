using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.RecoverableOperations
{
    [TestFixture]
    public class RecoverableOperationLeaseHeartbeatFixture : DbTest<RecoverableOperationRepository, RecoverableOperation>
    {
        [Test]
        public void should_keep_the_lease_live_while_filesystem_work_is_blocked()
        {
            var operation = Subject.CreatePending(RecoverableOperationRepositoryFixture.Request("heartbeat", "movie:heartbeat"));
            var policy = new FastLeasePolicy();
            var owner = "owner-a";
            var now = DateTime.UtcNow;
            operation = Subject.AcquireLease(operation.Id, operation.Version, owner, now, now.Add(policy.LeaseDuration));
            var acquiredVersion = operation.Version;
            var heartbeat = new RecoverableOperationLeaseHeartbeat(Subject, policy);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();

            var work = Task.Run(() => heartbeat.Run(operation, owner, () =>
            {
                entered.Set();
                release.Wait();
            }));

            entered.Wait();
            var leaseAtEntry = Subject.GetById(operation.Id);
            leaseAtEntry.LeaseExpiresAt.Should().NotBeNull();
            SpinWait.SpinUntil(() => DateTime.UtcNow > leaseAtEntry.LeaseExpiresAt.Value.AddMilliseconds(40), TimeSpan.FromSeconds(3)).Should().BeTrue();
            var current = Subject.GetById(operation.Id);
            current.Version.Should().BeGreaterThan(leaseAtEntry.Version);
            current.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);

            var firstRenewedVersion = current.Version;
            var firstRenewedExpiry = current.LeaseExpiresAt.Value;
            SpinWait.SpinUntil(() => DateTime.UtcNow > firstRenewedExpiry.AddMilliseconds(40), TimeSpan.FromSeconds(3)).Should().BeTrue();
            current = Subject.GetById(operation.Id);
            current.Version.Should().BeGreaterThan(firstRenewedVersion);
            current.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);

            Action competingRecovery = () => Subject.AcquireLease(current.Id, current.Version, "owner-b", DateTime.UtcNow, DateTime.UtcNow.Add(policy.LeaseDuration));
            competingRecovery.Should().Throw<RecoverableOperationConcurrencyException>();

            release.Set();
            work.GetAwaiter().GetResult().Version.Should().BeGreaterThan(acquiredVersion);
        }

        private sealed class FastLeasePolicy : IRecoverableOperationLeasePolicy
        {
            public TimeSpan LeaseDuration => TimeSpan.FromMilliseconds(250);
            public TimeSpan HeartbeatInterval => TimeSpan.FromMilliseconds(25);
        }
    }
}
