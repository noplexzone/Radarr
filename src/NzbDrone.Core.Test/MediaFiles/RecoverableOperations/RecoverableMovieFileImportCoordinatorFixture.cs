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
    public class RecoverableMovieFileImportCoordinatorFixture : DbTest<RecoverableMovieFileImportCoordinator, RecoverableOperation>
    {
        private static readonly byte[] IncomingBytes = { 1, 2, 3, 4, 5 };
        private static readonly byte[] OutgoingBytes = { 7, 8, 9 };
        private Movie _movie;
        private MovieEditionSlot _slot;
        private MovieFile _outgoing;
        private MovieFile _desired;
        private LocalMovie _localMovie;
        private string _moviePath;
        private string _source;
        private string _destination;
        private RecoverableOperationRepository _repository;
        private RecoverableMovieFileImportMutationStore _mutationStore;
        private readonly List<TransferMode> _requestedModes = new();

        [SetUp]
        public void Setup()
        {
            _requestedModes.Clear();
            _moviePath = Path.Combine(TempFolder, "library", "Movie");
            Directory.CreateDirectory(_moviePath);
            _movie = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.Path = _moviePath).With(x => x.MovieFileId = 0).BuildNew();
            Db.Insert(_movie);
            _slot = new MovieEditionSlot { MovieId = _movie.Id, EditionName = "Director's Cut", CanonicalEditionKey = "director s cut", Monitored = true, DateAdded = DateTime.UtcNow, Aliases = new List<string>() };
            Db.Insert(_slot);
            _repository = new RecoverableOperationRepository(Mocker.Resolve<IMainDatabase>());
            Mocker.SetConstant<IRecoverableOperationRepository>(_repository);
            var policy = new RecoverableOperationLeasePolicy();
            Mocker.SetConstant<IRecoverableOperationLeasePolicy>(policy);
            Mocker.SetConstant<IRecoverableOperationLeaseHeartbeat>(new RecoverableOperationLeaseHeartbeat(_repository, policy));
            _mutationStore = new RecoverableMovieFileImportMutationStore(Mocker.Resolve<IMainDatabase>(), Mocker.Resolve<IRecoverableMovieFileImportMutationFaultInjector>());
            Mocker.SetConstant<IRecoverableMovieFileImportMutationStore>(_mutationStore);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.FolderExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetFileSize(It.IsAny<string>())).Returns((string path) => new FileInfo(path).Length);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.GetFileAttributes(It.IsAny<string>())).Returns((string path) => File.GetAttributes(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.OpenReadStream(It.IsAny<string>())).Returns((string path) => File.OpenRead(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.CreateFolder(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.DeleteFile(It.IsAny<string>())).Callback((string path) => File.Delete(path));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>())).Callback((string path, bool recursive) => Directory.Delete(path, recursive));
            Mocker.GetMock<IDiskTransferService>().Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), false)).Returns((string from, string to, TransferMode mode, bool _) =>
            {
                _requestedModes.Add(mode);
                return Transfer(from, to, mode);
            });
            Mocker.GetMock<IRecycleBinProvider>().Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns((string path, string _) => { File.Delete(path); return null; });
            ConfigureTarget(MovieFileImportTarget.Main, true);
        }

        [TestCase(MovieFileImportTarget.Main, false)]
        [TestCase(MovieFileImportTarget.Main, true)]
        [TestCase(MovieFileImportTarget.EditionSlot, false)]
        [TestCase(MovieFileImportTarget.EditionSlot, true)]
        [TestCase(MovieFileImportTarget.Unassigned, false)]
        public void should_persist_each_target_with_exact_pointer_and_slot_behavior(MovieFileImportTarget target, bool replace)
        {
            ConfigureTarget(target, replace);
            var oldPointer = _movie.MovieFileId;
            var result = Import(TransferMode.Copy);
            result.IsImported.Should().BeTrue();
            result.FinalizationPending.Should().BeFalse();
            File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(target == MovieFileImportTarget.Main ? result.ImportedMovieFile.Id : oldPointer);
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == result.ImportedMovieFile.Id && x.MovieEditionSlotId == (target == MovieFileImportTarget.EditionSlot ? _slot.Id : null));
            if (replace) Db.All<MovieFile>().Should().NotContain(x => x.Id == _outgoing.Id);
            if (target == MovieFileImportTarget.EditionSlot) Db.All<MovieFile>().Where(x => x.MovieEditionSlotId == _slot.Id).Should().ContainSingle();
            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
        }

        [TestCase(TransferMode.Move, false, TransferMode.Move)]
        [TestCase(TransferMode.Copy, true, TransferMode.Copy)]
        [TestCase(TransferMode.HardLinkOrCopy, true, TransferMode.HardLinkOrCopy)]
        public void should_forward_transfer_mode_and_apply_exact_source_semantics(TransferMode requested, bool sourcePreserved, TransferMode firstMode)
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            Import(requested);
            File.Exists(_source).Should().Be(sourcePreserved);
            _requestedModes.First().Should().Be(firstMode);
            _requestedModes.Skip(1).Should().OnlyContain(x => x == TransferMode.Move);
        }

        [Test]
        public void database_apply_should_only_run_after_exact_destination_and_backup_evidence()
        {
            var wrapper = new Mock<IRecoverableMovieFileImportMutationStore>();
            wrapper.Setup(x => x.Apply(It.IsAny<RecoverableOperation>(), It.IsAny<string>())).Returns((RecoverableOperation op, string owner) =>
            {
                op.State.Should().Be(RecoverableOperationState.ApplyingDatabase);
                File.ReadAllBytes(op.Plan.DestinationPath).Should().Equal(IncomingBytes);
                File.ReadAllBytes(op.Plan.FinalizePath).Should().Equal(OutgoingBytes);
                File.Exists(op.Plan.StagingPath).Should().BeFalse();
                return _mutationStore.Apply(op, owner);
            });
            Mocker.SetConstant<IRecoverableMovieFileImportMutationStore>(wrapper.Object);
            Import(TransferMode.Copy);
            wrapper.Verify(x => x.Apply(It.IsAny<RecoverableOperation>(), It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void unexpected_destination_before_pending_should_create_no_operation()
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            File.WriteAllBytes(_destination, OutgoingBytes);
            Action act = () => Import(TransferMode.Copy);
            act.Should().Throw<RecoverableOperationValidationException>();
            Db.All<RecoverableOperation>().Should().BeEmpty();
            File.ReadAllBytes(_destination).Should().Equal(OutgoingBytes);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [Test]
        public void destination_collision_after_pending_should_quarantine_and_preserve_unowned_file()
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterPendingCommit)).Callback(() => File.WriteAllBytes(_destination, IncomingBytes));
            Action act = () => Import(TransferMode.Copy);
            act.Should().Throw<RecoverableOperationValidationException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            Mocker.GetMock<IDiskProvider>().Verify(x => x.DeleteFile(_destination), Times.Never);
        }

        [Test]
        public void rooted_outgoing_relative_path_should_reject_before_journal_and_preserve_external_file()
        {
            var external = Path.Combine(TempFolder, "outside-old.mkv");
            File.WriteAllBytes(external, OutgoingBytes);
            File.Delete(_destination);
            _outgoing.RelativePath = external;
            Db.Update(_outgoing);
            _outgoing = Db.All<MovieFile>().Single(x => x.Id == _outgoing.Id);

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<RecoverableOperationValidationException>()
               .WithMessage("*outgoing relative path*");
            Db.All<RecoverableOperation>().Should().BeEmpty();
            File.ReadAllBytes(external).Should().Equal(OutgoingBytes);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void active_source_or_target_resource_should_reject_second_import(bool sameSource)
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterPendingCommit)).Throws(new RecoverableOperationProcessDeathException());
            Action first = () => Import(TransferMode.Copy);
            first.Should().Throw<RecoverableOperationProcessDeathException>();
            if (sameSource)
            {
                _destination = Path.Combine(_moviePath, "Other.mkv");
                _desired = Desired("Other.mkv", null);
            }
            else
            {
                _source = Path.Combine(TempFolder, "other-download.mkv");
                File.WriteAllBytes(_source, IncomingBytes);
                _desired.OriginalFilePath = _source;
                _localMovie.Path = _source;
            }
            Action second = () => Import(TransferMode.Copy);
            second.Should().Throw<RecoverableOperationResourceConflictException>();
            Db.All<RecoverableOperation>().Should().ContainSingle();
        }

        [Test]
        public void active_outgoing_path_should_conflict_with_another_import_source()
        {
            ConfigureTarget(MovieFileImportTarget.EditionSlot, true, "Incoming.mkv", "Old.mkv");
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterPendingCommit)).Throws(new RecoverableOperationProcessDeathException());
            Action first = () => Import(TransferMode.Copy);
            first.Should().Throw<RecoverableOperationProcessDeathException>();
            _source = Path.Combine(_moviePath, "Old.mkv");
            _desired = Desired("Other.mkv", null);
            _desired.Size = OutgoingBytes.Length;
            _localMovie = Local(MovieFileImportTarget.Unassigned);
            _localMovie.Size = OutgoingBytes.Length;
            _outgoing = null;
            Action second = () => Import(TransferMode.Copy);
            second.Should().Throw<RecoverableOperationResourceConflictException>();
        }

        [TestCase(RecoverableOperationFaultPoint.AfterImportSourceToCandidate)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportOutgoingToBackup)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportDestinationTransfer)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportCandidateToDestination)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportStaged)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportApplyingDatabase)]
        public void ordinary_precommit_fault_should_restore_exact_preimport_topology(RecoverableOperationFaultPoint point)
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(point)).Throws(new IOException("injected"));
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<IOException>().WithMessage("injected");
            AssertExactPreImportTopology();
            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
        }

        [Test]
        public void mutation_conflict_should_restore_files_and_roll_back_without_recycling()
        {
            Mocker.GetMock<IRecoverableMovieFileImportMutationStore>().Setup(x => x.Apply(It.IsAny<RecoverableOperation>(), It.IsAny<string>())).Throws(new RecoverableOperationConcurrencyException(42));
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationConcurrencyException>();
            AssertExactPreImportTopology();
            StoredModel.State.Should().Be(RecoverableOperationState.RolledBack);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [TestCase(RecoverableOperationFaultPoint.AfterImportSourceToCandidate, RecoverableOperationState.Staging)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportStaged, RecoverableOperationState.Staged)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportDatabaseCommit, RecoverableOperationState.DatabaseCommitted)]
        public void process_death_should_leave_active_evidence_without_compensation(RecoverableOperationFaultPoint point, RecoverableOperationState expectedState)
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(point)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            StoredModel.State.Should().Be(expectedState);
            StoredModel.ActiveResourceKey.Should().NotBeNull();
            File.Exists(_source).Should().BeFalse();
            if (point == RecoverableOperationFaultPoint.AfterImportSourceToCandidate)
            {
                File.ReadAllBytes(StoredModel.Plan.StagingPath).Should().Equal(IncomingBytes);
                File.ReadAllBytes(_destination).Should().Equal(OutgoingBytes);
            }
            else
            {
                File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
                File.ReadAllBytes(StoredModel.Plan.FinalizePath).Should().Equal(OutgoingBytes);
            }
        }

        [Test]
        public void wrong_size_source_should_reject_before_journal_or_transfer()
        {
            File.WriteAllBytes(_source, new byte[] { 1 });
            Action act = () => Import(TransferMode.Copy);
            act.Should().Throw<IOException>();
            Db.All<RecoverableOperation>().Should().BeEmpty();
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [TestCase("candidate")]
        [TestCase("destination")]
        [TestCase("backup")]
        public void wrong_size_coordinator_evidence_should_quarantine_without_recycle(string evidence)
        {
            var count = 0;
            Mocker.GetMock<IDiskTransferService>().Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), false)).Returns((string from, string to, TransferMode mode, bool _) =>
            {
                count++;
                var result = Transfer(from, to, mode);
                if (evidence == "candidate" && count == 1 || evidence == "backup" && count == 2 || evidence == "destination" && count == 3) File.WriteAllBytes(to, new byte[] { 4 });
                return result;
            });
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<IOException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void ambiguous_duplicate_incoming_topology_should_quarantine_and_preserve_both()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate)).Callback(() =>
            {
                File.Copy(StoredModel.Plan.StagingPath, _source);
                throw new IOException("duplicate topology");
            });
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<IOException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            File.ReadAllBytes(StoredModel.Plan.StagingPath).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_destination).Should().Equal(OutgoingBytes);
            Mocker.GetMock<IRecycleBinProvider>().Verify(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void edition_slot_owned_by_another_movie_should_reject_without_journal()
        {
            ConfigureTarget(MovieFileImportTarget.EditionSlot, false);
            var other = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.MovieMetadataId = _movie.MovieMetadataId + 1).With(x => x.Path = Path.Combine(TempFolder, "other")).With(x => x.MovieFileId = 0).BuildNew();
            Db.Insert(other);
            _slot.MovieId = other.Id;
            Db.Update(_slot);
            Action act = () => Import(TransferMode.Copy);
            act.Should().Throw<RecoverableOperationConcurrencyException>();
            Db.All<RecoverableOperation>().Should().BeEmpty();
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [TestCase("target")]
        [TestCase("relative")]
        [TestCase("destination")]
        [TestCase("mode")]
        public void malformed_target_path_or_mode_should_create_no_journal(string malformed)
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            var target = MovieFileImportTarget.Unassigned;
            var mode = TransferMode.Copy;
            if (malformed == "target") target = (MovieFileImportTarget)999;
            if (malformed == "relative") _desired.RelativePath = "../escape.mkv";
            if (malformed == "destination") _desired.Path = Path.Combine(TempFolder, "outside.mkv");
            if (malformed == "mode") mode = (TransferMode)999;
            Action act = () => Subject.Import(_desired, _localMovie, mode, null, _movie.MovieFileId, target);
            act.Should().Throw<RecoverableOperationValidationException>();
            Db.All<RecoverableOperation>().Should().BeEmpty();
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [Test]
        public void recycle_failure_should_report_imported_with_finalization_pending()
        {
            Mocker.GetMock<IRecycleBinProvider>().Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("recycle failed"));
            var result = Import(TransferMode.Copy);
            result.IsImported.Should().BeTrue();
            result.FinalizationPending.Should().BeTrue();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            StoredModel.ResultMovieFileId.Should().Be(result.ImportedMovieFile.Id);
        }

        [Test]
        public void destination_through_directory_symlink_should_reject_before_journal_or_external_touch()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The Linux directory-symlink regression is not applicable on Windows.");
            }

            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            var external = Path.Combine(TempFolder, "external-library");
            var link = Path.Combine(_moviePath, "linked");
            Directory.CreateDirectory(external);
            Directory.CreateSymbolicLink(link, external);
            _destination = Path.Combine(link, "Imported.mkv");
            _desired = Desired(Path.Combine("linked", "Imported.mkv"), null);

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<RecoverableOperationValidationException>().WithMessage("*symbolic link*reparse point*");
            Db.All<RecoverableOperation>().Should().BeEmpty();
            File.Exists(Path.Combine(external, "Imported.mkv")).Should().BeFalse();
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [Test]
        public void destination_parent_changed_to_symlink_after_staging_should_reject_external_write()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The Linux directory-symlink regression is not applicable on Windows.");
            }

            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            var external = Path.Combine(TempFolder, "external-race");
            var linked = Path.Combine(_moviePath, "linked-race");
            var displaced = Path.Combine(_moviePath, "linked-original");
            Directory.CreateDirectory(external);
            Directory.CreateDirectory(linked);
            _destination = Path.Combine(linked, "Imported.mkv");
            _desired = Desired(Path.Combine("linked-race", "Imported.mkv"), null);
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate))
                .Callback(() =>
                {
                    Directory.Move(linked, displaced);
                    Directory.CreateSymbolicLink(linked, external);
                });

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<RecoverableOperationValidationException>().WithMessage("*symbolic link*reparse point*");
            File.Exists(Path.Combine(external, "Imported.mkv")).Should().BeFalse();
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
        }

        [Test]
        public void replacement_at_finalize_path_during_recycle_should_survive()
        {
            var replacement = new byte[] { 8, 8, 8 };
            Mocker.GetMock<IRecycleBinProvider>().Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns((string claimed, string _) =>
            {
                File.WriteAllBytes(StoredModel.Plan.FinalizePath, replacement);
                File.Delete(claimed);
                return null;
            });

            var result = Import(TransferMode.Copy);

            result.IsImported.Should().BeTrue();
            result.FinalizationPending.Should().BeTrue();
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(StoredModel.Plan.FinalizePath).Should().Equal(replacement);
        }

        [Test]
        public void replacement_at_destination_during_claimed_rollback_delete_should_survive()
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            var replacement = new byte[] { 8, 8, 8, 8, 8 };
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportStaged))
                .Throws(new IOException("rollback"));
            Mocker.GetMock<IDiskProvider>().Setup(x => x.DeleteFile(It.Is<string>(path => path.Contains("rollback-delete-")))).Callback((string claimed) =>
            {
                File.WriteAllBytes(_destination, replacement);
                File.Delete(claimed);
            });

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<IOException>().WithMessage("rollback");
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_destination).Should().Equal(replacement);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
        }

        [Test]
        public void active_staging_path_should_conflict_with_another_import_source()
        {
            var deaths = 0;
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate))
                .Callback(() =>
                {
                    if (deaths++ == 0)
                    {
                        throw new RecoverableOperationProcessDeathException();
                    }
                });
            Action first = () => Import(TransferMode.Move);
            first.Should().Throw<RecoverableOperationProcessDeathException>();
            var firstOperation = StoredModel;
            var stagedSource = firstOperation.Plan.StagingPath;
            File.ReadAllBytes(stagedSource).Should().Equal(IncomingBytes);

            _source = stagedSource;
            _destination = Path.Combine(_moviePath, "Other.mkv");
            _desired = Desired("Other.mkv", null);
            _localMovie = Local(MovieFileImportTarget.Unassigned);
            _outgoing = null;

            Action second = () => Import(TransferMode.Copy);

            second.Should().Throw<RecoverableOperationResourceConflictException>();
            Db.All<RecoverableOperation>().Should().ContainSingle(x => x.Id == firstOperation.Id);
            File.ReadAllBytes(stagedSource).Should().Equal(IncomingBytes);
        }

        [Test]
        public void same_size_replacement_at_staged_should_be_preserved_and_quarantined()
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            var replacement = new byte[] { 9, 9, 9, 9, 9 };
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportStaged)).Callback(() =>
            {
                File.Delete(_destination);
                File.WriteAllBytes(_destination, replacement);
                throw new IOException("destination replaced");
            });

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<IOException>().WithMessage("destination replaced");
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_destination).Should().Equal(replacement);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            Mocker.GetMock<IDiskProvider>().Verify(x => x.DeleteFile(_destination), Times.Never);
            Mocker.GetMock<IDiskTransferService>().Verify(x => x.TransferFile(_destination, It.IsAny<string>(), TransferMode.Move, false), Times.Never);
        }

        [Test]
        public void unexpected_destination_after_distinct_outgoing_backup_should_quarantine()
        {
            ConfigureTarget(MovieFileImportTarget.EditionSlot, true, "Incoming.mkv", "Old.mkv");
            var unexpected = new byte[] { 6, 6, 6, 6, 6 };
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportOutgoingToBackup)).Callback(() =>
            {
                File.WriteAllBytes(_destination, unexpected);
                throw new IOException("destination occupied");
            });

            Action act = () => Import(TransferMode.Copy);

            act.Should().Throw<IOException>().WithMessage("destination occupied");
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_destination).Should().Equal(unexpected);
            File.ReadAllBytes(Path.Combine(_moviePath, "Old.mkv")).Should().Equal(OutgoingBytes);
        }

        [Test]
        public void heartbeat_failure_after_recycle_action_should_return_committed_result()
        {
            var realHeartbeat = new RecoverableOperationLeaseHeartbeat(_repository, new RecoverableOperationLeasePolicy());
            var heartbeat = new Mock<IRecoverableOperationLeaseHeartbeat>();
            var recycled = false;
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string path, string _) =>
                {
                    File.Delete(path);
                    recycled = true;
                    return null;
                });
            heartbeat.Setup(x => x.Run(It.IsAny<RecoverableOperation>(), It.IsAny<string>(), It.IsAny<Action>())).Returns((RecoverableOperation operation, string owner, Action action) =>
            {
                var result = realHeartbeat.Run(operation, owner, action);
                if (recycled)
                {
                    throw new RecoverableOperationLeaseHeartbeatException("heartbeat failed after action", new IOException("renewal failed"));
                }

                return result;
            });
            Mocker.SetConstant<IRecoverableOperationLeaseHeartbeat>(heartbeat.Object);

            var result = Import(TransferMode.Copy);

            result.IsImported.Should().BeTrue();
            result.FinalizationPending.Should().BeTrue();
            StoredModel.ResultMovieFileId.Should().Be(result.ImportedMovieFile.Id);
            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.Exists(StoredModel.Plan.FinalizePath).Should().BeFalse();
        }

        [Test]
        public void hardlink_fallback_should_persist_actual_copy_mode_before_interruption()
        {
            ConfigureTarget(MovieFileImportTarget.Unassigned, false);
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate))
                .Throws(new RecoverableOperationProcessDeathException());

            Action act = () => Import(TransferMode.HardLinkOrCopy);

            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var property = StoredModel.Plan.GetType().GetProperty("ActualTransferMode");
            property.Should().NotBeNull("actual fallback mode must be durable recovery evidence");
            property.GetValue(StoredModel.Plan).Should().Be(RecoverableTransferMode.Copy);
            File.ReadAllBytes(StoredModel.Plan.StagingPath).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
        }

        [TestCase(RecoverableOperationFaultPoint.AfterPendingCommit)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportSourceTransfer)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportSourceToCandidate)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportOutgoingToBackup)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportDestinationTransfer)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportStaged)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportApplyingDatabase)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportDatabaseCommit)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportRecycleAction)]
        [TestCase(RecoverableOperationFaultPoint.AfterImportRecycle)]
        public void recovery_should_resume_each_process_death_boundary_idempotently(RecoverableOperationFaultPoint point)
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(point)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            var interrupted = StoredModel;
            ExpireLease(interrupted);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();

            const string owner = "restart-owner";
            var leased = Acquire(interrupted, owner);
            Subject.Recover(leased, owner);
            Subject.Recover(StoredModel, owner).State.Should().Be(RecoverableOperationState.Completed, "recovery failed: {0}", StoredModel.LastError);

            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            StoredModel.ResultMovieFileId.Should().NotBeNull();
            StoredModel.ActiveResourceKey.Should().BeNull();
            File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
            File.Exists(_source).Should().BeFalse();
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == StoredModel.ResultMovieFileId);
            Db.All<MovieFile>().Should().NotContain(x => x.Id == _outgoing.Id);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(StoredModel.ResultMovieFileId.Value);
        }

        [Test]
        public void ambiguous_move_duplicate_during_recovery_should_quarantine_without_mutation()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate)).Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            File.Copy(StoredModel.Plan.StagingPath, _source);
            ExpireLease(StoredModel);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            var leased = Acquire(StoredModel, "restart-owner");

            Subject.Recover(leased, "restart-owner");

            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            File.ReadAllBytes(StoredModel.Plan.StagingPath).Should().Equal(IncomingBytes);
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == _outgoing.Id);
        }

        [Test]
        public void persisted_rollback_should_restore_owned_destination_and_backup_idempotently_after_restart()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportStaged))
                .Throws(new IOException("begin rollback"));
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportRollbackBegin))
                .Throws(new RecoverableOperationProcessDeathException());

            Action act = () => Import(TransferMode.Move);

            act.Should().Throw<RecoverableOperationProcessDeathException>();
            StoredModel.State.Should().Be(RecoverableOperationState.RollingBack);
            StoredModel.Plan.RollbackDestinationOwned.Should().BeTrue();
            File.Exists(_source).Should().BeFalse();
            File.Exists(StoredModel.Plan.StagingPath).Should().BeFalse();
            File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
            File.ReadAllBytes(StoredModel.Plan.FinalizePath).Should().Equal(OutgoingBytes);
            ExpireLease(StoredModel);
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            var leased = Acquire(StoredModel, "restart-owner");

            Subject.Recover(leased, "restart-owner");
            var second = Subject.Recover(StoredModel, "restart-owner");

            second.State.Should().Be(RecoverableOperationState.RolledBack);
            AssertExactPreImportTopology();
        }

        [Test]
        public void persisted_rollback_without_durable_destination_ownership_should_fail_closed()
        {
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportStaged))
                .Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            ExpireLease(StoredModel);
            var leased = Acquire(StoredModel, "restart-owner");
            var historical = _repository.Transition(leased.Id, leased.State, leased.Version, RecoverableOperationState.RollingBack, "restart-owner");
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();

            Subject.Recover(historical, "restart-owner");

            StoredModel.State.Should().Be(RecoverableOperationState.RecoveryRequired);
            File.Exists(_source).Should().BeFalse();
            File.ReadAllBytes(_destination).Should().Equal(IncomingBytes);
            File.ReadAllBytes(StoredModel.Plan.FinalizePath).Should().Equal(OutgoingBytes);
        }

        [Test]
        public void recovery_hashes_should_run_under_heartbeat_and_keep_versions_current()
        {
            var realHeartbeat = new RecoverableOperationLeaseHeartbeat(_repository, new RecoverableOperationLeasePolicy());
            var heartbeat = new Mock<IRecoverableOperationLeaseHeartbeat>();
            var insideHeartbeat = false;
            var requireHeartbeat = false;
            var unwrappedRecoveryHash = false;
            heartbeat.Setup(x => x.Run(It.IsAny<RecoverableOperation>(), It.IsAny<string>(), It.IsAny<Action>())).Returns((RecoverableOperation operation, string owner, Action action) =>
            {
                return realHeartbeat.Run(operation, owner, () =>
                {
                    insideHeartbeat = true;
                    try
                    {
                        action();
                    }
                    finally
                    {
                        insideHeartbeat = false;
                    }
                });
            });
            Mocker.SetConstant<IRecoverableOperationLeaseHeartbeat>(heartbeat.Object);
            Mocker.GetMock<IDiskProvider>().Setup(x => x.OpenReadStream(It.IsAny<string>())).Returns((string path) =>
            {
                if (requireHeartbeat && !insideHeartbeat)
                {
                    unwrappedRecoveryHash = true;
                }

                return File.OpenRead(path);
            });
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportStaged))
                .Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            ExpireLease(StoredModel);
            var leased = Acquire(StoredModel, "restart-owner");
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            requireHeartbeat = true;

            Subject.Recover(leased, "restart-owner");

            unwrappedRecoveryHash.Should().BeFalse();
            StoredModel.State.Should().Be(RecoverableOperationState.Completed);
            heartbeat.Verify(x => x.Run(It.IsAny<RecoverableOperation>(), "restart-owner", It.IsAny<Action>()), Times.AtLeast(3));
        }

        [Test]
        public void recovery_heartbeat_failure_during_hash_should_prevent_later_mutation()
        {
            var realHeartbeat = new RecoverableOperationLeaseHeartbeat(_repository, new RecoverableOperationLeasePolicy());
            var heartbeat = new Mock<IRecoverableOperationLeaseHeartbeat>();
            var failRecoveryHeartbeat = false;
            heartbeat.Setup(x => x.Run(It.IsAny<RecoverableOperation>(), It.IsAny<string>(), It.IsAny<Action>())).Returns((RecoverableOperation operation, string owner, Action action) =>
            {
                if (failRecoveryHeartbeat)
                {
                    throw new RecoverableOperationLeaseHeartbeatException("lost recovery lease", new RecoverableOperationConcurrencyException(operation.Id));
                }

                return realHeartbeat.Run(operation, owner, action);
            });
            Mocker.SetConstant<IRecoverableOperationLeaseHeartbeat>(heartbeat.Object);
            Mocker.GetMock<IRecoverableOperationFaultInjector>()
                .Setup(x => x.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate))
                .Throws(new RecoverableOperationProcessDeathException());
            Action act = () => Import(TransferMode.Move);
            act.Should().Throw<RecoverableOperationProcessDeathException>();
            ExpireLease(StoredModel);
            var leased = Acquire(StoredModel, "restart-owner");
            Mocker.GetMock<IRecoverableOperationFaultInjector>().Reset();
            failRecoveryHeartbeat = true;

            Action recover = () => Subject.Recover(leased, "restart-owner");

            recover.Should().Throw<RecoverableOperationLeaseHeartbeatException>();
            StoredModel.State.Should().Be(RecoverableOperationState.Staging);
            File.ReadAllBytes(StoredModel.Plan.StagingPath).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_destination).Should().Equal(OutgoingBytes);
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == _outgoing.Id);
        }

        private RecoverableOperation Acquire(RecoverableOperation operation, string owner)
        {
            var now = DateTime.UtcNow;
            return _repository.AcquireLease(operation.Id, operation.Version, owner, now, now.AddMinutes(5));
        }

        private void ExpireLease(RecoverableOperation operation)
        {
            operation.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            Storage.Update(operation);
        }

        private RecoverableMovieFileImportResult Import(TransferMode mode) => Subject.Import(_desired, _localMovie, mode, _outgoing, _movie.MovieFileId, _localMovie.ImportTarget);

        private void ConfigureTarget(MovieFileImportTarget target, bool replace, string destinationName = "Movie.mkv", string outgoingName = null)
        {
            foreach (var file in Db.All<MovieFile>().ToList()) Db.Delete(file);
            foreach (var path in Directory.GetFiles(_moviePath)) File.Delete(path);
            _movie.MovieFileId = 0;
            Db.Update(_movie);
            _destination = Path.Combine(_moviePath, destinationName);
            _source = Path.Combine(TempFolder, "download.mkv");
            File.WriteAllBytes(_source, IncomingBytes);
            _outgoing = null;
            if (replace)
            {
                outgoingName ??= destinationName;
                _outgoing = new MovieFile { MovieId = _movie.Id, MovieEditionSlotId = target == MovieFileImportTarget.EditionSlot ? _slot.Id : null, RelativePath = outgoingName, Size = OutgoingBytes.Length, DateAdded = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), Quality = new QualityModel(), Languages = new List<Language> { Language.English }, MediaInfo = new MediaInfoModel() };
                Db.Insert(_outgoing);
                File.WriteAllBytes(Path.Combine(_moviePath, outgoingName), OutgoingBytes);
                if (target == MovieFileImportTarget.Main) { _movie.MovieFileId = _outgoing.Id; Db.Update(_movie); }
            }
            _desired = Desired(destinationName, target == MovieFileImportTarget.EditionSlot ? _slot.Id : null);
            _localMovie = Local(target);
        }

        private MovieFile Desired(string relativePath, int? slotId) => new() { MovieId = _movie.Id, MovieEditionSlotId = slotId, RelativePath = relativePath, Path = Path.Combine(_moviePath, relativePath), Size = IncomingBytes.Length, DateAdded = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc), Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.French }, MediaInfo = new MediaInfoModel(), OriginalFilePath = _source };
        private LocalMovie Local(MovieFileImportTarget target) => new() { Path = _source, Size = IncomingBytes.Length, Movie = _movie, ImportTarget = target };

        private void AssertExactPreImportTopology()
        {
            File.ReadAllBytes(_source).Should().Equal(IncomingBytes);
            File.ReadAllBytes(_destination).Should().Equal(OutgoingBytes);
            Db.All<MovieFile>().Should().ContainSingle(x => x.Id == _outgoing.Id);
            Db.All<Movie>().Single(x => x.Id == _movie.Id).MovieFileId.Should().Be(_outgoing.Id);
            File.Exists(StoredModel.Plan.StagingPath).Should().BeFalse();
            File.Exists(StoredModel.Plan.FinalizePath).Should().BeFalse();
        }

        private static TransferMode Transfer(string source, string destination, TransferMode mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (mode == TransferMode.Move) { File.Move(source, destination); return TransferMode.Move; }
            File.Copy(source, destination);
            return mode == TransferMode.HardLinkOrCopy ? TransferMode.Copy : mode;
        }
    }
}
