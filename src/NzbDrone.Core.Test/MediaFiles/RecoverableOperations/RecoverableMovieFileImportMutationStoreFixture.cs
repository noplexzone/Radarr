using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.RecoverableOperations
{
    [TestFixture]
    public class RecoverableMovieFileImportMutationStoreFixture : DbTest<RecoverableMovieFileImportMutationStore, RecoverableOperation>
    {
        private const string Owner = "import-worker";
        private Movie _movie;
        private MovieEditionSlot _slot;
        private RecoverableOperationRepository _repository;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.Path = "/movies/1").With(x => x.MovieFileId = 0).BuildNew();
            Db.Insert(_movie);
            _movie.Path = $"/movies/{_movie.Id}";
            Db.Update(_movie);
            _slot = new MovieEditionSlot { MovieId = _movie.Id, EditionName = "Director's Cut", CanonicalEditionKey = "director s cut", Monitored = true, DateAdded = DateTime.UtcNow, Aliases = new List<string>() };
            Db.Insert(_slot);
            _repository = new RecoverableOperationRepository(Mocker.Resolve<NzbDrone.Core.Datastore.IMainDatabase>());
            Mocker.GetMock<IRecoverableMovieFileImportMutationFaultInjector>().Setup(x => x.Check(It.IsAny<RecoverableMovieFileImportMutationStep>()));
        }

        [TestCase(MovieFileImportTarget.Main, false)]
        [TestCase(MovieFileImportTarget.Main, true)]
        [TestCase(MovieFileImportTarget.EditionSlot, false)]
        [TestCase(MovieFileImportTarget.EditionSlot, true)]
        [TestCase(MovieFileImportTarget.Unassigned, false)]
        public void should_atomically_apply_each_import_target(MovieFileImportTarget target, bool replace)
        {
            var outgoing = replace ? InsertOutgoing(target) : null;
            var operation = ApplyingOperation(target, outgoing);
            var beforeVersion = operation.Version;
            var committed = Subject.Apply(operation, Owner);
            committed.State.Should().Be(RecoverableOperationState.DatabaseCommitted);
            committed.Version.Should().Be(beforeVersion + 1);
            committed.ResultMovieFileId.Should().BeGreaterThan(0);
            committed.DatabaseCommittedAt.Should().NotBeNull();
            Db.All<RecoverableOperation>().Single(x => x.Id == operation.Id).ResultMovieFileId.Should().Be(committed.ResultMovieFileId);
            if (outgoing != null) Db.All<MovieFile>().Should().NotContain(x => x.Id == outgoing.Id);
            var incoming = Db.All<MovieFile>().Single(x => x.Id == committed.ResultMovieFileId);
            AssertDesiredRoundTrip(incoming, target);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(target == MovieFileImportTarget.Main ? incoming.Id : operation.Plan.ExpectedMovieFileId);
            if (target == MovieFileImportTarget.EditionSlot) Db.All<MovieFile>().Count(x => x.MovieEditionSlotId == _slot.Id).Should().Be(1);
        }

        [TestCase("id")]
        [TestCase("movie")]
        [TestCase("slot")]
        [TestCase("path")]
        [TestCase("size")]
        [TestCase("pointer")]
        public void stale_expected_state_should_reject_without_any_mutation(string mismatch)
        {
            var outgoing = InsertOutgoing(MovieFileImportTarget.Main);
            var operation = ApplyingOperation(MovieFileImportTarget.Main, outgoing);
            if (mismatch == "id") { Db.Delete(outgoing); outgoing.Id = 0; Db.Insert(outgoing); }
            else if (mismatch == "movie") { outgoing.MovieId = InsertOtherMovie().Id; Db.Update(outgoing); }
            else if (mismatch == "slot") { outgoing.MovieEditionSlotId = _slot.Id; Db.Update(outgoing); }
            else if (mismatch == "path") { outgoing.RelativePath = "changed.mkv"; Db.Update(outgoing); }
            else if (mismatch == "size") { outgoing.Size++; Db.Update(outgoing); }
            else { _movie.MovieFileId = 0; Db.Update(_movie); }
            var before = Db.All<MovieFile>().Select(FileIdentity).OrderBy(x => x).ToList();
            Action act = () => Subject.Apply(operation, Owner);
            act.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<MovieFile>().Select(FileIdentity).OrderBy(x => x).Should().Equal(before);
            AssertJournalUnchanged(operation);
        }

        [TestCase("quality")]
        [TestCase("languages")]
        [TestCase("originalPath")]
        public void changed_outgoing_metadata_should_reject_without_any_mutation(string mismatch)
        {
            var outgoing = InsertOutgoing(MovieFileImportTarget.Main);
            var operation = ApplyingOperation(MovieFileImportTarget.Main, outgoing);
            if (mismatch == "quality") outgoing.Quality = new QualityModel(Quality.WEBDL1080p);
            if (mismatch == "languages") outgoing.Languages = new List<Language> { Language.French };
            if (mismatch == "originalPath") outgoing.OriginalFilePath = "/changed/source.mkv";
            Db.Update(outgoing);

            Action act = () => Subject.Apply(operation, Owner);

            act.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == outgoing.Id);
            AssertJournalUnchanged(operation);
        }

        [TestCase(42, null)]
        [TestCase(null, "Different.mkv")]
        public void incoming_row_must_match_canonical_plan(long? size, string relativePath)
        {
            var operation = ApplyingOperation(MovieFileImportTarget.Unassigned, null, size, relativePath);

            Action act = () => Subject.Apply(operation, Owner);

            act.Should().Throw<RecoverableOperationValidationException>();
            Db.All<MovieFile>().Should().BeEmpty();
            AssertJournalUnchanged(operation);
        }

        [Test]
        public void dot_segment_destination_should_reject_without_any_mutation()
        {
            var operation = ApplyingOperation(MovieFileImportTarget.Unassigned, null, null, "../Outside.mkv", $"{_movie.Path}/../Outside.mkv");

            Action act = () => Subject.Apply(operation, Owner);

            act.Should().Throw<RecoverableOperationValidationException>();
            Db.All<MovieFile>().Should().BeEmpty();
            AssertJournalUnchanged(operation);
        }

        [TestCase(RecoverableMovieFileImportMutationStep.AfterOutgoingDelete)]
        [TestCase(RecoverableMovieFileImportMutationStep.AfterIncomingInsert)]
        [TestCase(RecoverableMovieFileImportMutationStep.AfterMainPointerUpdate)]
        [TestCase(RecoverableMovieFileImportMutationStep.AfterInvariantAudit)]
        [TestCase(RecoverableMovieFileImportMutationStep.BeforeCommit)]
        public void fault_at_any_database_boundary_should_roll_back_everything(RecoverableMovieFileImportMutationStep step)
        {
            var outgoing = InsertOutgoing(MovieFileImportTarget.Main);
            var operation = ApplyingOperation(MovieFileImportTarget.Main, outgoing);
            Mocker.GetMock<IRecoverableMovieFileImportMutationFaultInjector>().Setup(x => x.Check(step)).Throws(new InvalidOperationException("injected"));
            Action act = () => Subject.Apply(operation, Owner);
            act.Should().Throw<InvalidOperationException>().WithMessage("injected");
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == outgoing.Id && x.RelativePath == outgoing.RelativePath);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(outgoing.Id);
            AssertJournalUnchanged(operation);
        }

        [Test]
        public void occupied_slot_when_plan_expected_empty_should_reject_without_mutation()
        {
            var operation = ApplyingOperation(MovieFileImportTarget.EditionSlot, null);
            var occupant = InsertOutgoing(MovieFileImportTarget.EditionSlot);
            Action act = () => Subject.Apply(operation, Owner);
            act.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == occupant.Id);
            AssertJournalUnchanged(operation);
        }

        [Test]
        public void main_target_must_not_replace_a_slot_assigned_file()
        {
            var outgoing = InsertOutgoing(MovieFileImportTarget.EditionSlot);
            _movie.MovieFileId = outgoing.Id;
            Db.Update(_movie);
            var operation = ApplyingOperation(MovieFileImportTarget.Main, outgoing);

            Action act = () => Subject.Apply(operation, Owner);

            act.Should().Throw<RecoverableOperationValidationException>();
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == outgoing.Id && x.MovieEditionSlotId == _slot.Id);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(outgoing.Id);
            AssertJournalUnchanged(operation);
        }

        [Test]
        public void generated_id_must_not_be_pre_referenced_by_another_movie()
        {
            var outgoing = InsertOutgoing(MovieFileImportTarget.Main);
            var operation = ApplyingOperation(MovieFileImportTarget.Main, outgoing);
            var other = InsertOtherMovie();
            other.MovieFileId = outgoing.Id + 1;
            Db.Update(other);
            Action act = () => Subject.Apply(operation, Owner);
            act.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == outgoing.Id);
            AssertJournalUnchanged(operation);
        }

        [Test]
        public void stale_version_wrong_owner_and_expired_lease_should_be_rejected()
        {
            var operation = ApplyingOperation(MovieFileImportTarget.Unassigned, null);
            Action stale = () => Subject.Apply(new RecoverableOperation { Id = operation.Id, Version = operation.Version - 1 }, Owner);
            Action wrongOwner = () => Subject.Apply(operation, "other-worker");
            stale.Should().Throw<RecoverableOperationConcurrencyException>();
            wrongOwner.Should().Throw<RecoverableOperationConcurrencyException>();
            using (var connection = Mocker.Resolve<NzbDrone.Core.Datastore.IMainDatabase>().OpenConnection())
                connection.Execute("UPDATE \"MovieEditionFileOperations\" SET \"LeaseExpiresAt\"=@expired WHERE \"Id\"=@id", new { expired = DateTime.UtcNow.AddMinutes(-1), id = operation.Id });
            Action expired = () => Subject.Apply(operation, Owner);
            expired.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<MovieFile>().Should().BeEmpty();
            AssertJournalUnchanged(operation, true);
        }

        [Test]
        public void should_expose_provider_specific_static_insert_sql_shape()
        {
            var sqlite = RecoverableMovieFileImportMutationStore.IncomingInsertSql(false);
            var postgres = RecoverableMovieFileImportMutationStore.IncomingInsertSql(true);
            sqlite.Should().Contain("last_insert_rowid()").And.NotContain("RETURNING \"Id\"");
            postgres.Should().Contain("RETURNING \"Id\"").And.NotContain("last_insert_rowid()");
            postgres.Should().Contain("\"MovieEditionSlotId\"").And.Contain("\"OriginalFilePath\"").And.Contain("\"Edition\"");
        }

        private RecoverableOperation ApplyingOperation(MovieFileImportTarget target, MovieFile outgoing, long? desiredIncomingSize = null, string desiredIncomingRelativePath = null, string destinationPath = null)
        {
            var key = Guid.NewGuid().ToString("N");
            var root = $"{_movie.Path}/.radarr-recovery/{key}/";
            var desiredSlot = target == MovieFileImportTarget.EditionSlot ? _slot.Id : (int?)null;
            destinationPath ??= $"{_movie.Path}/Incoming.mkv";
            var request = new RecoverableOperationCreateRequest
            {
                OperationKey = key,
                ResourceKey = $"import:{key}",
                OperationType = RecoverableOperationType.Import,
                MovieId = _movie.Id,
                MovieFileId = outgoing?.Id,
                MovieEditionSlotId = desiredSlot,
                StagingRoot = root,
                Plan = new RecoverableOperationPlan
                {
                    Expected = new RecoverableOperationSnapshot { MovieId = _movie.Id, MovieFileId = outgoing?.Id, MovieEditionSlotId = desiredSlot, Path = $"{_movie.Path}/Old.mkv", Size = outgoing?.Size },
                    Desired = new RecoverableOperationSnapshot { MovieId = _movie.Id, MovieEditionSlotId = desiredSlot, DestinationPath = destinationPath, Size = 987654321 },
                    SourcePath = $"{_movie.Path}/download.mkv",
                    StagingPath = $"{root}candidate.mkv",
                    DestinationPath = destinationPath,
                    FinalizePath = $"{root}backup.mkv",
                    ExpectedSize = 987654321,
                    TransferMode = RecoverableTransferMode.Move,
                    ImportTarget = target,
                    ExpectedMovieFileId = _movie.MovieFileId,
                    ExpectedOutgoingMovieFile = outgoing == null ? null : Snapshot(outgoing),
                    DesiredIncomingMovieFile = DesiredSnapshot(desiredSlot, desiredIncomingSize, desiredIncomingRelativePath)
                }
            };
            var operation = _repository.CreatePending(request);
            var now = DateTime.UtcNow;
            operation = _repository.AcquireLease(operation.Id, operation.Version, Owner, now, now.AddMinutes(10));
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, Owner);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staged, Owner);
            return _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.ApplyingDatabase, Owner);
        }

        private MovieFile InsertOutgoing(MovieFileImportTarget target)
        {
            var file = new MovieFile { MovieId = _movie.Id, MovieEditionSlotId = target == MovieFileImportTarget.EditionSlot ? _slot.Id : null, RelativePath = "Old.mkv", Size = 1234, DateAdded = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc), Quality = new QualityModel(), Languages = new List<Language> { Language.English }, MediaInfo = new MediaInfoModel() };
            Db.Insert(file);
            if (target == MovieFileImportTarget.Main) { _movie.MovieFileId = file.Id; Db.Update(_movie); }
            return file;
        }

        private Movie InsertOtherMovie()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.MovieMetadataId = _movie.MovieMetadataId + 1)
                .With(x => x.Path = $"/movies/{Guid.NewGuid():N}")
                .With(x => x.MovieFileId = 0)
                .BuildNew();
            Db.Insert(movie);
            return movie;
        }

        private static RecoverableMovieFileRowSnapshot Snapshot(MovieFile f) => new() { Id = f.Id, MovieId = f.MovieId, MovieEditionSlotId = f.MovieEditionSlotId, RelativePath = f.RelativePath, Size = f.Size, Quality = f.Quality, Languages = f.Languages, DateAdded = f.DateAdded, SceneName = f.SceneName, ReleaseGroup = f.ReleaseGroup, MediaInfo = f.MediaInfo, OriginalFilePath = f.OriginalFilePath, IndexerFlags = f.IndexerFlags, Edition = f.Edition };

        private RecoverableMovieFileRowSnapshot DesiredSnapshot(int? slotId, long? size = null, string relativePath = null) => new()
        {
            MovieId = _movie.Id,
            MovieEditionSlotId = slotId,
            RelativePath = relativePath ?? "Incoming.mkv",
            Size = size ?? 987654321,
            Quality = new QualityModel(Quality.Bluray1080p, new Revision(2, 1, true)),
            Languages = new List<Language> { Language.French, Language.English },
            DateAdded = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            SceneName = "Incoming.Scene.1080p",
            ReleaseGroup = "GROUP",
            MediaInfo = new MediaInfoModel { AudioChannels = 8, AudioFormat = "TrueHD", AudioLanguages = new List<string> { "fra", "eng" }, Height = 1080, Width = 1920, Subtitles = new List<string> { "eng" }, VideoFormat = "HEVC", VideoHdrFormat = HdrFormat.DolbyVision },
            OriginalFilePath = "/downloads/Incoming.Scene.1080p.mkv",
            IndexerFlags = IndexerFlags.PTP_Golden | IndexerFlags.PTP_Approved,
            Edition = "Director's Cut"
        };

        private void AssertDesiredRoundTrip(MovieFile incoming, MovieFileImportTarget target)
        {
            var desired = DesiredSnapshot(target == MovieFileImportTarget.EditionSlot ? _slot.Id : null);
            incoming.Should().BeEquivalentTo(desired, options => options.ExcludingMissingMembers().Excluding(x => x.Id));
        }

        private void AssertJournalUnchanged(RecoverableOperation operation, bool ignoreLeaseExpiry = false)
        {
            var stored = Db.All<RecoverableOperation>().Single(x => x.Id == operation.Id);
            stored.State.Should().Be(RecoverableOperationState.ApplyingDatabase);
            stored.Version.Should().Be(operation.Version);
            stored.ResultMovieFileId.Should().BeNull();
            stored.DatabaseCommittedAt.Should().BeNull();
            if (!ignoreLeaseExpiry) stored.LeaseExpiresAt.Should().Be(operation.LeaseExpiresAt);
        }

        private static string FileIdentity(MovieFile file) => $"{file.Id}|{file.MovieId}|{file.MovieEditionSlotId}|{file.RelativePath}|{file.Size}";
    }
}
