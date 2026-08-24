using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.Test.Framework;
namespace NzbDrone.Core.Test.MediaFiles.RecoverableOperations
{
    [TestFixture]
    public class RecoverableOperationRepositoryFixture : DbTest<RecoverableOperationRepository, RecoverableOperation>
    {
        [Test] public void should_validate_create_and_roundtrip_plan() { Action invalid = () => Subject.CreatePending(new RecoverableOperationCreateRequest()); invalid.Should().Throw<RecoverableOperationValidationException>(); var c = Subject.CreatePending(Request("roundtrip", "movie:1")); var s = Subject.GetById(c.Id); s.State.Should().Be(RecoverableOperationState.Pending); s.Version.Should().Be(1); s.ActiveResourceKey.Should().Be("movie:1"); s.Plan.Expected.MovieId.Should().Be(1); s.Plan.Desired.DestinationPath.Should().Be("/movies/1/Movie.mkv"); s.Plan.EventFacts.Should().ContainKey("source"); s.CreatedAt.Kind.Should().Be(DateTimeKind.Utc); }
        [Test] public void should_reject_collision_and_terminal_states_free_resource() { var a = Subject.CreatePending(Request("first", "movie:1")); Action duplicate = () => Subject.CreatePending(Request("second", "movie:1")); duplicate.Should().Throw<RecoverableOperationResourceConflictException>().WithMessage("*movie:1*").Which.InnerException.Should().BeNull(); foreach (var next in new[] { RecoverableOperationState.Staging, RecoverableOperationState.Staged, RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.DatabaseCommitted, RecoverableOperationState.Finalizing, RecoverableOperationState.Completed }) a = Subject.Transition(a.Id, a.State, a.Version, next); a.ActiveResourceKey.Should().BeNull(); a.CompletedAt.Should().NotBeNull(); Subject.CreatePending(Request("second", "movie:1")).Should().NotBeNull(); var r = Subject.CreatePending(Request("rollback", "movie:2")); r = Subject.Transition(r.Id, r.State, r.Version, RecoverableOperationState.RollingBack); r = Subject.Transition(r.Id, r.State, r.Version, RecoverableOperationState.RolledBack); r.ActiveResourceKey.Should().BeNull(); Subject.CreatePending(Request("rollback-next", "movie:2")).Should().NotBeNull(); }
        [Test] public void should_fail_closed_and_set_commit_timestamp() { var o = Subject.CreatePending(Request("transitions", "movie:3")); Action illegal = () => Subject.Transition(o.Id, o.State, o.Version, RecoverableOperationState.Staged); illegal.Should().Throw<RecoverableOperationTransitionException>(); o = Subject.Transition(o.Id, o.State, o.Version, RecoverableOperationState.Staging); Action stale = () => Subject.Transition(o.Id, o.State, 1, RecoverableOperationState.Staged); stale.Should().Throw<RecoverableOperationConcurrencyException>(); o = Subject.Transition(o.Id, o.State, o.Version, RecoverableOperationState.Staged); o = Subject.Transition(o.Id, o.State, o.Version, RecoverableOperationState.ApplyingDatabase); o = Subject.Transition(o.Id, o.State, o.Version, RecoverableOperationState.DatabaseCommitted); o.DatabaseCommittedAt.Should().NotBeNull(); }
        [Test] public void should_acquire_expire_and_renew_lease() { var o = Subject.CreatePending(Request("lease", "movie:4")); var now = DateTime.UtcNow; o = Subject.AcquireLease(o.Id, o.Version, "a", now, now.AddMinutes(1)); Action competing = () => Subject.AcquireLease(o.Id, o.Version, "b", now.AddSeconds(1), now.AddMinutes(2)); competing.Should().Throw<RecoverableOperationConcurrencyException>(); Action wrong = () => Subject.RenewLease(o.Id, o.Version, "b", now.AddSeconds(2), now.AddMinutes(2)); wrong.Should().Throw<RecoverableOperationConcurrencyException>(); o = Subject.RenewLease(o.Id, o.Version, "a", now.AddSeconds(2), now.AddMinutes(2)); o = Subject.AcquireLease(o.Id, o.Version, "b", now.AddMinutes(3), now.AddMinutes(4)); o.LeaseOwner.Should().Be("b"); }
        [Test] public void live_lease_should_block_classification_and_non_owner_transition() { var operation = Subject.CreatePending(Request("live-lease", "movie:live-lease")); var now = DateTime.UtcNow; operation = Subject.AcquireLease(operation.Id, operation.Version, "owner-a", now, now.AddMinutes(5)); var service = new RecoverableOperationRecoveryService(Subject); service.Classify(10).Should().BeEmpty(); Action nonOwner = () => Subject.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, "owner-b"); nonOwner.Should().Throw<RecoverableOperationConcurrencyException>(); operation = Subject.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, "owner-a"); operation.State.Should().Be(RecoverableOperationState.Staging); }
        [Test] public void should_accept_every_legal_transition() { var pairs = new[] { (RecoverableOperationState.Pending, RecoverableOperationState.Staging), (RecoverableOperationState.Pending, RecoverableOperationState.RollingBack), (RecoverableOperationState.Pending, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.Staging, RecoverableOperationState.Staged), (RecoverableOperationState.Staging, RecoverableOperationState.RollingBack), (RecoverableOperationState.Staging, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.Staged, RecoverableOperationState.ApplyingDatabase), (RecoverableOperationState.Staged, RecoverableOperationState.RollingBack), (RecoverableOperationState.Staged, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.DatabaseCommitted), (RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.RollingBack), (RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.DatabaseCommitted, RecoverableOperationState.Finalizing), (RecoverableOperationState.DatabaseCommitted, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.Finalizing, RecoverableOperationState.Completed), (RecoverableOperationState.Finalizing, RecoverableOperationState.RecoveryRequired), (RecoverableOperationState.RollingBack, RecoverableOperationState.RolledBack), (RecoverableOperationState.RollingBack, RecoverableOperationState.RecoveryRequired) }; foreach (var pair in pairs) { var o = Reach(pair.Item1); o = pair.Item2 == RecoverableOperationState.RecoveryRequired ? Subject.MarkRecoveryRequired(o.Id, o.State, o.Version, "classification only") : Subject.Transition(o.Id, o.State, o.Version, pair.Item2); o.State.Should().Be(pair.Item2); } }
        [Test] public void recovery_required_should_be_quarantined_and_reject_reclassification_or_leasing() { var o = Subject.CreatePending(Request("quarantine", "movie:quarantine")); o = Subject.MarkRecoveryRequired(o.Id, o.State, o.Version, "manual recovery"); var version = o.Version; var attempts = o.AttemptCount; Action again = () => Subject.MarkRecoveryRequired(o.Id, o.State, o.Version, "overwrite"); again.Should().Throw<RecoverableOperationTransitionException>(); Action lease = () => Subject.AcquireLease(o.Id, o.Version, "worker", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)); lease.Should().Throw<RecoverableOperationConcurrencyException>(); Action undefined = () => Subject.MarkRecoveryRequired(o.Id, (RecoverableOperationState)999, o.Version, "invalid"); undefined.Should().Throw<RecoverableOperationTransitionException>(); var persisted = Subject.GetById(o.Id); persisted.Version.Should().Be(version); persisted.AttemptCount.Should().Be(attempts); persisted.LastError.Should().Be("manual recovery"); persisted.LeaseOwner.Should().BeNull(); }
        [Test] public void create_should_reject_incomplete_inconsistent_or_unstageable_plans() { var requests = new[] { Request("é", "resource:unicode"), Request("bad-operation", "resource:operation", operationType: (RecoverableOperationType)999), Request("bad-transfer", "resource:transfer", transferMode: (RecoverableTransferMode)999), Request("missing-source", "resource:source", missingSource: true), Request("wrong-movie", "resource:movie", snapshotMovieId: 2), Request("relative-root", "resource:relative", stagingRoot: "relative/root"), Request("outside-root", "resource:outside", stagingRoot: "/outside/.radarr-recovery/outside-root/") }; foreach (var request in requests) { Action create = () => Subject.CreatePending(request); create.Should().Throw<RecoverableOperationValidationException>(); } Db.All<RecoverableOperation>().Should().BeEmpty(); }
        [Test] public void should_record_bounded_error_attempt_with_cas() { var o = Subject.CreatePending(Request("attempt", "movie:attempt")); o = Subject.RecordErrorAttempt(o.Id, o.Version, "failed safely", DateTime.UtcNow); o.AttemptCount.Should().Be(1); o.LastError.Should().Be("failed safely"); o.LastAttemptAt.Should().NotBeNull(); Action stale = () => Subject.RecordErrorAttempt(o.Id, 1, "stale", DateTime.UtcNow); stale.Should().Throw<RecoverableOperationConcurrencyException>(); }
        RecoverableOperation Reach(RecoverableOperationState state) { var key = Guid.NewGuid().ToString("N"); var o = Subject.CreatePending(Request(key, "resource:" + key)); if (state == RecoverableOperationState.Pending) return o; if (state == RecoverableOperationState.RollingBack) return Subject.Transition(o.Id, o.State, o.Version, state); foreach (var next in new[] { RecoverableOperationState.Staging, RecoverableOperationState.Staged, RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.DatabaseCommitted, RecoverableOperationState.Finalizing }) { o = Subject.Transition(o.Id, o.State, o.Version, next); if (o.State == state) return o; } throw new InvalidOperationException(); }
        [Test] public void interface_should_remain_narrow() { typeof(IRecoverableOperationRepository).GetMethods().Select(x => x.Name).Should().NotContain(new[] { "Insert", "Update", "Delete", "Upsert", "SetFields", "Purge" }); }

        [Test]
        public void should_reserve_multiple_resources_in_canonical_order()
        {
            var operation = Subject.CreatePending(Request("multi", null, resources: new[] { "target:z", " source:a ", "destination:m" }));
            ResourceKeys(operation.Id).Should().Equal("destination:m", "source:a", "target:z");
            operation.ResourceKey.Should().Be("destination:m");
            operation.ActiveResourceKey.Should().Be("destination:m");
        }

        [Test]
        public void should_reject_a_normalized_duplicate_across_legacy_and_multi_resource_inputs()
        {
            Action create = () => Subject.CreatePending(Request("combined", " legacy:primary ", resources: new[] { "source:a", "legacy:primary" }));
            create.Should().Throw<RecoverableOperationValidationException>().WithMessage("*legacy:primary*");
            Db.All<RecoverableOperation>().Should().BeEmpty();
            Db.All<RecoverableOperationResource>().Should().BeEmpty();
        }

        [Test]
        public void duplicate_operation_key_should_preserve_domain_exception_translation()
        {
            Subject.CreatePending(Request("same-operation", "resource:first"));
            Action duplicate = () => Subject.CreatePending(Request("same-operation", "resource:second"));
            duplicate.Should().Throw<RecoverableOperationResourceConflictException>().WithMessage("*resource:second*");
        }

        [Test]
        public void should_roll_back_entire_create_when_any_resource_overlaps_and_allow_unrelated_sets()
        {
            var first = Subject.CreatePending(Request("multi-first", null, resources: new[] { "target:one", "source:shared", "destination:one" }));
            Action overlap = () => Subject.CreatePending(Request("multi-second", null, resources: new[] { "target:two", "source:shared", "destination:two" }));
            overlap.Should().Throw<RecoverableOperationResourceConflictException>().WithMessage("*source:shared*").Which.InnerException.Should().BeNull();
            Db.All<RecoverableOperation>().Should().ContainSingle().Which.Id.Should().Be(first.Id);
            Db.All<RecoverableOperationResource>().Should().HaveCount(3);
            Subject.CreatePending(Request("unrelated", null, resources: new[] { "source:other", "target:other" })).Should().NotBeNull();
        }

        [Test]
        public void terminal_states_should_free_all_resources_but_recovery_required_should_retain_them()
        {
            var completed = Subject.CreatePending(Request("multi-complete", null, resources: new[] { "complete:a", "complete:b" }));
            foreach (var next in new[] { RecoverableOperationState.Staging, RecoverableOperationState.Staged, RecoverableOperationState.ApplyingDatabase, RecoverableOperationState.DatabaseCommitted, RecoverableOperationState.Finalizing, RecoverableOperationState.Completed }) completed = Subject.Transition(completed.Id, completed.State, completed.Version, next);
            ResourceKeys(completed.Id).Should().BeEmpty();
            Subject.CreatePending(Request("multi-complete-reuse", null, resources: new[] { "complete:a", "complete:b" })).Should().NotBeNull();

            var rolledBack = Subject.CreatePending(Request("multi-rollback", null, resources: new[] { "rollback:a", "rollback:b" }));
            rolledBack = Subject.Transition(rolledBack.Id, rolledBack.State, rolledBack.Version, RecoverableOperationState.RollingBack);
            rolledBack = Subject.Transition(rolledBack.Id, rolledBack.State, rolledBack.Version, RecoverableOperationState.RolledBack);
            ResourceKeys(rolledBack.Id).Should().BeEmpty();
            Subject.CreatePending(Request("multi-rollback-reuse", null, resources: new[] { "rollback:a", "rollback:b" })).Should().NotBeNull();

            var quarantined = Subject.CreatePending(Request("multi-quarantine", null, resources: new[] { "quarantine:a", "quarantine:b" }));
            quarantined = Subject.MarkRecoveryRequired(quarantined.Id, quarantined.State, quarantined.Version, "manual recovery");
            ResourceKeys(quarantined.Id).Should().Equal("quarantine:a", "quarantine:b");
            Action conflict = () => Subject.CreatePending(Request("multi-quarantine-conflict", null, resources: new[] { "quarantine:b", "other" }));
            conflict.Should().Throw<RecoverableOperationResourceConflictException>();
        }

        [Test]
        public void invalid_duplicate_or_oversized_resource_sets_should_insert_nothing()
        {
            var requests = new[] { Request("resources-empty", null, resources: Array.Empty<string>()), Request("resources-blank", null, resources: new[] { "valid", " " }), Request("resources-null", null, resources: new[] { "valid", null }), Request("legacy-blank", " ", resources: new[] { "valid" }), Request("resources-duplicate", null, resources: new[] { "duplicate", " duplicate " }), Request("resources-oversized", null, resources: new[] { new string('x', 513) }) };
            foreach (var request in requests) { Action create = () => Subject.CreatePending(request); create.Should().Throw<RecoverableOperationValidationException>(); }
            Db.All<RecoverableOperation>().Should().BeEmpty();
            Db.All<RecoverableOperationResource>().Should().BeEmpty();
        }

        private IReadOnlyList<string> ResourceKeys(int operationId) => Db.All<RecoverableOperationResource>().Where(resource => resource.OperationId == operationId).OrderBy(resource => resource.Id).Select(resource => resource.ResourceKey).ToList();

        internal static RecoverableOperationCreateRequest Request(string key, string resource, RecoverableOperationType operationType = RecoverableOperationType.Move, RecoverableTransferMode transferMode = RecoverableTransferMode.Move, int movieId = 1, int? snapshotMovieId = null, string stagingRoot = null, bool missingSource = false, IReadOnlyCollection<string> resources = null) { var snapshotMovie = snapshotMovieId ?? movieId; var root = stagingRoot ?? $"/movies/{movieId}/.radarr-recovery/{key}/"; return new() { OperationKey = key, ResourceKey = resource, ResourceKeys = resources, OperationType = operationType, MovieId = movieId, MovieFileId = 10, MovieEditionSlotId = 20, StagingRoot = root, Plan = new() { Expected = new() { MovieId = snapshotMovie, MovieFileId = 10, MovieEditionSlotId = 20, Path = $"/movies/{movieId}/Old.mkv", Size = 100 }, Desired = new() { MovieId = snapshotMovie, MovieFileId = 10, MovieEditionSlotId = 20, DestinationPath = $"/movies/{movieId}/Movie.mkv", Size = 100 }, SourcePath = missingSource ? null : $"/movies/{movieId}/Old.mkv", StagingPath = $"{root.TrimEnd('/')}/candidate", DestinationPath = $"/movies/{movieId}/Movie.mkv", FinalizePath = $"{root.TrimEnd('/')}/backup", ExpectedSize = 100, TransferMode = transferMode, EventFacts = { { "source", "test" } } } }; }
    }
}
