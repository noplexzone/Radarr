using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.RecoverableOperations
{
    [TestFixture]
    public class RecoverableMovieFileDeletionCoordinatorFixture : DbTest<RecoverableMovieFileDeletionCoordinator, RecoverableOperation>
    {
        private Movie _movie;
        private MovieFile _movieFile;
        private string _root;
        private string _source;
        private bool _sourceExists;
        private bool _backupExists;
        private bool _stagingFolderExists;
        private RecoverableOperationRepository _repository;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(TempFolder, "movies");
            _movie = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.Path = Path.Combine(_root, "Movie")).With(x => x.MovieFileId = 0).With(x => x.Tags = new HashSet<int> { 7 }).BuildNew();
            _movie.MovieMetadata.Value.InCinemas = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            _movie.MovieMetadata.Value.PhysicalRelease = new DateTime(2024, 4, 5, 0, 0, 0, DateTimeKind.Utc);
            _movie.MovieMetadata.Value.DigitalRelease = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
            _movie.MovieMetadata.Value.Overview = "durable overview";
            _movie.MovieMetadata.Value.Genres = new List<string> { "Drama", "Science Fiction" };
            _movie.MovieMetadata.Value.Images = new List<MediaCover.MediaCover> { new(MediaCoverTypes.Poster, "https://example.invalid/poster.jpg") { Url = "/poster.jpg" } };
            _movie.MovieMetadata.Value.OriginalLanguage = Language.French;
            Db.Insert(_movie);
            _movieFile = Builder<MovieFile>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.MovieId = _movie.Id)
                .With(x => x.RelativePath = "Movie.mkv")
                .With(x => x.Size = 100L)
                .With(x => x.Quality = new QualityModel())
                .With(x => x.Languages = new List<Language> { Language.English })
                .With(x => x.SceneName = "Scene.Release.1080p")
                .With(x => x.DateAdded = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc))
                .With(x => x.MediaInfo = new MediaInfoModel
                {
                    AudioChannels = 6,
                    AudioFormat = "E-AC-3",
                    AudioLanguages = new List<string> { "eng" },
                    Height = 1080,
                    Width = 1920,
                    Subtitles = new List<string> { "eng" },
                    VideoFormat = "HEVC",
                    VideoHdrFormat = HdrFormat.Hdr10
                })
                .BuildNew();
            Db.Insert(_movieFile);
            _movieFile.Movie = null;
            _movieFile.Path = null;
            _movie.MovieFileId = _movieFile.Id;
            Db.Update(_movie);
            _source = Path.Combine(_movie.Path, _movieFile.RelativePath);
            _sourceExists = true;
            _backupExists = false;
            _stagingFolderExists = false;
            _repository = new RecoverableOperationRepository(Mocker.Resolve<IMainDatabase>());
            Mocker.SetConstant<IRecoverableOperationRepository>(_repository);
            var leasePolicy = new RecoverableOperationLeasePolicy();
            Mocker.SetConstant<IRecoverableOperationLeasePolicy>(leasePolicy);
            Mocker.SetConstant<IRecoverableOperationLeaseHeartbeat>(new RecoverableOperationLeaseHeartbeat(_repository, leasePolicy));

            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetParentFolder(_movie.Path)).Returns(_root);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetParentFolder(_source)).Returns(_movie.Path);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.FolderExists(_movie.Path)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.FolderExists(It.IsAny<string>())).Returns((string path) => path == _movie.Path || (_stagingFolderExists && path.Contains(".radarr-recovery")));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.FileExists(It.IsAny<string>())).Returns((string path) => path == _source ? _sourceExists : path.Contains(".radarr-recovery", StringComparison.Ordinal) && path.EndsWith(Path.DirectorySeparatorChar + "Movie.mkv", StringComparison.Ordinal) && _backupExists);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetFileSize(It.IsAny<string>())).Returns((string path) => path == _source && _sourceExists || path.Contains(".radarr-recovery", StringComparison.Ordinal) && _backupExists ? 100L : 0L);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.CreateFolder(It.IsAny<string>())).Callback(() => _stagingFolderExists = true);
            Mocker.GetMock<IDiskTransferService>().Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Move, false)).Callback((string from, string to, TransferMode _, bool __) =>
            {
                if (from == _source)
                {
                    _sourceExists = false;
                    _backupExists = true;
                }
                else
                {
                    _backupExists = false;
                    _sourceExists = true;
                }
            });
            Mocker.GetMock<IRecycleBinProvider>().Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Callback(() => _backupExists = false);
        }

        [Test]
        public void should_persist_pending_before_staging_and_atomically_remove_exact_row_and_main_pointer()
        {
            Mocker.GetMock<IDiskTransferService>().Setup(x => x.TransferFile(_source, It.IsAny<string>(), TransferMode.Move, false)).Callback((string _, string __, TransferMode ___, bool ____) =>
            {
                var active = Db.All<RecoverableOperation>().Should().ContainSingle().Subject;
                active.State.Should().Be(RecoverableOperationState.Staging);
                active.Plan.Expected.MovieFileId.Should().Be(_movieFile.Id);
                Path.GetFileName(active.Plan.FinalizePath).Should().Be("Movie.mkv");
                _sourceExists = false;
                _backupExists = true;
            });

            Subject.DeleteMovieFile(_movie, _movieFile);

            Db.All<MovieFile>().Should().NotContain(x => x.Id == _movieFile.Id);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(0);
            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            StoredModel.ActiveResourceKey.Should().BeNull();
            _sourceExists.Should().BeFalse();
            _backupExists.Should().BeFalse();
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), "Movie"), Times.Once);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.Is<MovieFileDeletedEvent>(e =>
                e.Reason == DeleteMediaFileReason.Manual &&
                e.MovieFile.Path == _source &&
                e.MovieFile.Movie == _movie &&
                e.MovieFile.Movie.Tags.Contains(7))), Times.Once);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.IsAny<DeleteCompletedEvent>()), Times.Once);
        }

        [Test]
        public void missing_source_should_remain_a_journaled_database_only_cleanup()
        {
            _sourceExists = false;

            Subject.DeleteMovieFile(_movie, _movieFile);

            Db.All<MovieFile>().Should().NotContain(x => x.Id == _movieFile.Id);
            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void stale_source_size_should_fail_before_transfer_and_roll_back_journal()
        {
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetFileSize(_source)).Returns(99L);

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);

            act.Should().Throw<RecoverableOperationConcurrencyException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
            _sourceExists.Should().BeTrue();
            Mocker.GetMock<IDiskTransferService>().Verify(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void database_fault_after_staging_should_restore_and_roll_back()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterDatabaseMutation)).Throws(new IOException("db fault"));

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);

            act.Should().Throw<IOException>();
            Db.All<MovieFile>().Should().Contain(x => x.Id == _movieFile.Id);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(_movieFile.Id);
            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
            _sourceExists.Should().BeTrue();
            _backupExists.Should().BeFalse();
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void stale_cross_movie_state_after_staging_should_fail_closed_and_restore_source()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                  .Setup(x => x.Check(RecoverableOperationFaultPoint.BeforeDatabaseTransaction))
                  .Callback(() =>
                  {
                      _movieFile.MovieId = _movie.Id + 100;
                      Db.Update(_movieFile);
                  });

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);

            act.Should().Throw<RecoverableOperationConcurrencyException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
            _sourceExists.Should().BeTrue();
            _backupExists.Should().BeFalse();
            Db.All<MovieFile>().Should().Contain(x => x.Id == _movieFile.Id);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void process_death_after_stage_should_be_restored_by_fresh_recovery()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterStageTransfer)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            active.State.Should().Be(RecoverableOperationState.Staging);
            _backupExists.Should().BeTrue();
            _sourceExists.Should().BeFalse();

            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);
            Subject.Recover(StoredModel);

            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
            _sourceExists.Should().BeTrue();
            Db.All<MovieFile>().Should().Contain(x => x.Id == _movieFile.Id);
        }

        [Test]
        public void process_death_after_database_commit_should_finalize_and_complete_without_duplicate_events()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterDatabaseCommit)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            active.State.Should().Be(RecoverableOperationState.DatabaseCommitted);
            Db.All<MovieFile>().Should().NotContain(x => x.Id == _movieFile.Id);

            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);
            Subject.Recover(StoredModel);
            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.Is<MovieFileDeletedEvent>(e =>
                e.MovieFile.Quality != null &&
                e.MovieFile.Languages.Count == 1 &&
                e.MovieFile.Languages[0].Id == Language.English.Id &&
                e.MovieFile.ReleaseGroup == _movieFile.ReleaseGroup &&
                e.MovieFile.Path == _source &&
                e.MovieFile.Movie.Title == _movie.Title &&
                e.MovieFile.Movie.Year == _movie.Year &&
                e.MovieFile.Movie.ImdbId == _movie.ImdbId &&
                e.MovieFile.Movie.Tags.Contains(7))), Times.Once);
        }

        [Test]
        public void conflicting_source_and_backup_should_require_recovery_and_preserve_both()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterStageTransfer)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            _sourceExists = true;
            var active = StoredModel;

            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);
            Subject.Recover(StoredModel);

            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            _sourceExists.Should().BeTrue();
            _backupExists.Should().BeTrue();
        }

        [Test]
        public void dispatched_event_mask_should_make_finalizing_recovery_idempotent()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterEventDispatch)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            active.State.Should().Be(RecoverableOperationState.Finalizing);
            active.EventDispatchMask.Should().Be(3);

            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);
            Subject.Recover(StoredModel);

            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.IsAny<MovieFileDeletedEvent>()), Times.Once);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.IsAny<DeleteCompletedEvent>()), Times.Once);
        }

        [Test]
        public void active_movie_file_resource_should_reject_a_second_delete()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterPendingCommit)).Throws(new RecoverableOperationProcessDeathException());
            Action first = () => Subject.DeleteMovieFile(_movie, _movieFile);
            first.Should().Throw<RecoverableOperationProcessDeathException>();

            Action second = () => Subject.DeleteMovieFile(_movie, _movieFile);
            second.Should().Throw<RecoverableOperationResourceConflictException>();
            Db.All<RecoverableOperation>().Should().ContainSingle();
        }

        [Test]
        public void recovered_event_should_roundtrip_every_webhook_consumed_field()
        {
            _movieFile.Movie = _movie;
            var expectedMovie = new WebhookMovie(_movie, _movieFile, new List<string>());
            var expectedFile = new WebhookMovieFile(_movieFile);
            MovieFileDeletedEvent recoveredEvent = null;
            Mocker.GetMock<IEventAggregator>()
                  .Setup(x => x.PublishEventStrict(It.IsAny<MovieFileDeletedEvent>()))
                  .Callback<MovieFileDeletedEvent>(x => recoveredEvent = x);
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                  .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterDatabaseCommit))
                  .Throws(new RecoverableOperationProcessDeathException());

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);

            Subject.Recover(StoredModel);

            recoveredEvent.Should().NotBeNull();
            new WebhookMovie(recoveredEvent.MovieFile.Movie, recoveredEvent.MovieFile, new List<string>()).Should().BeEquivalentTo(expectedMovie);
            new WebhookMovieFile(recoveredEvent.MovieFile).Should().BeEquivalentTo(expectedFile);
        }

        [Test]
        public void strict_handler_failure_should_leave_uncertain_bit_and_quarantine_without_final_bit()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Setup(x => x.PublishEventStrict(It.IsAny<MovieFileDeletedEvent>()))
                  .Throws(new IOException("handler failed"));

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);

            act.Should().Throw<IOException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            ((RecoverableOperationEventDispatchMask)StoredModel.EventDispatchMask).Should().HaveFlag(RecoverableOperationEventDispatchMask.MovieFileDeletedInProgress);
            ((RecoverableOperationEventDispatchMask)StoredModel.EventDispatchMask).Should().NotHaveFlag(RecoverableOperationEventDispatchMask.MovieFileDeleted);
        }

        [Test]
        public void death_after_publication_before_completion_should_never_republish_on_recovery()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                  .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterMovieFileDeletedPublish))
                  .Throws(new RecoverableOperationProcessDeathException());

            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            ((RecoverableOperationEventDispatchMask)active.EventDispatchMask).Should().HaveFlag(RecoverableOperationEventDispatchMask.MovieFileDeletedInProgress);
            ((RecoverableOperationEventDispatchMask)active.EventDispatchMask).Should().NotHaveFlag(RecoverableOperationEventDispatchMask.MovieFileDeleted);

            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);
            Subject.Recover(StoredModel);

            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.IsAny<MovieFileDeletedEvent>()), Times.Once);
            Mocker.GetMock<IEventAggregator>().Verify(x => x.PublishEventStrict(It.IsAny<DeleteCompletedEvent>()), Times.Never);
        }

        [Test]
        public void successful_dispatch_should_set_only_final_bits()
        {
            Subject.DeleteMovieFile(_movie, _movieFile);

            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            StoredModel.EventDispatchMask.Should().Be((long)(RecoverableOperationEventDispatchMask.MovieFileDeleted | RecoverableOperationEventDispatchMask.DeleteCompleted));
        }

        [Test]
        public void recovery_should_preserve_wrong_size_backup_and_require_operator_recovery()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                  .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterStageTransfer))
                  .Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Subject.DeleteMovieFile(_movie, _movieFile);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var active = StoredModel;
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetFileSize(active.Plan.FinalizePath)).Returns(99L);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            ExpireLease(active);

            Subject.Recover(StoredModel);

            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            _sourceExists.Should().BeFalse();
            _backupExists.Should().BeTrue();
            Mocker.GetMock<IDiskTransferService>().Verify(x => x.TransferFile(active.Plan.FinalizePath, _source, TransferMode.Move, false), Times.Never);
        }

        private void ExpireLease(RecoverableOperation operation)
        {
            operation.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            Db.Update(operation);
        }
    }
}
