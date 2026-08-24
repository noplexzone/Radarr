using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using Dapper;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableMovieFileDeletionCoordinator
    {
        void DeleteMovieFile(Movie movie, MovieFile movieFile);
    }

    public interface IRecoverableMovieFileDeletionRecoveryHandler
    {
        void Recover(RecoverableOperation operation);
    }

    public sealed class RecoverableOperationProcessDeathException : Exception
    {
        public RecoverableOperationProcessDeathException(string message = "Injected recoverable-operation process death")
            : base(message)
        {
        }
    }

    public sealed class RecoverableMovieFileDeletionCoordinator : IRecoverableMovieFileDeletionCoordinator, IRecoverableMovieFileDeletionRecoveryHandler
    {
        private const string MissingSourceFact = "sourceMissingBeforeStaging";
        private const string MoviePathFact = "moviePath";
        private const string RelativePathFact = "relativePath";
        private const string RecycleSubfolderFact = "recycleSubfolder";
        private const string LeasePrefix = "delete-file-";
        private readonly IRecoverableOperationRepository _repository;
        private readonly IMainDatabase _database;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRecoverableOperationFaultInjector _faultInjector;
        private readonly IRecoverableOperationLeaseHeartbeat _leaseHeartbeat;
        private readonly IRecoverableOperationLeasePolicy _leasePolicy;
        private readonly Logger _logger;

        public RecoverableMovieFileDeletionCoordinator(IRecoverableOperationRepository repository,
                                                        IMainDatabase database,
                                                        IDiskProvider diskProvider,
                                                        IDiskTransferService diskTransferService,
                                                        IRecycleBinProvider recycleBinProvider,
                                                        IEventAggregator eventAggregator,
                                                        IRecoverableOperationFaultInjector faultInjector,
                                                        IRecoverableOperationLeaseHeartbeat leaseHeartbeat,
                                                        IRecoverableOperationLeasePolicy leasePolicy,
                                                        Logger logger)
        {
            _repository = repository;
            _database = database;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _recycleBinProvider = recycleBinProvider;
            _eventAggregator = eventAggregator;
            _faultInjector = faultInjector;
            _leaseHeartbeat = leaseHeartbeat;
            _leasePolicy = leasePolicy;
            _logger = logger;
        }

        public void DeleteMovieFile(Movie movie, MovieFile movieFile)
        {
            var request = BuildRequest(movie, movieFile);
            movieFile.Movie = movie;
            movieFile.Path = request.Plan.SourcePath;
            var operation = _repository.CreatePending(request);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterPendingCommit);
            var owner = LeasePrefix + Guid.NewGuid().ToString("N");
            operation = Acquire(operation, owner);

            try
            {
                Execute(operation, movieFile, owner);
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch (RecoverableOperationLeaseHeartbeatException exception)
            {
                QuarantineAfterLeaseFailure(operation.Id, owner, exception);
                throw;
            }
            catch (Exception exception)
            {
                RollBackBeforeCommit(operation.Id, owner, exception);
                throw;
            }
        }

        public void Recover(RecoverableOperation operation)
        {
            if (operation.OperationType != RecoverableOperationType.Delete)
            {
                throw new RecoverableOperationValidationException("The movie-file deletion recovery handler only accepts Delete operations.");
            }

            var owner = LeasePrefix + Guid.NewGuid().ToString("N");
            var acquired = false;
            try
            {
                operation = Acquire(operation, owner);
                acquired = true;

                if (HasUncertainDispatch(operation))
                {
                    MarkRecoveryRequired(operation, owner, "A compatibility event dispatch was interrupted after side effects may have begun; operator recovery is required and the event will not be replayed automatically.");
                    return;
                }

                var sourceExists = _diskProvider.FileExists(operation.Plan.SourcePath);
                var backupExists = _diskProvider.FileExists(operation.Plan.FinalizePath);
                var databaseState = InspectDatabase(operation);

                if (databaseState == DeleteDatabaseState.Before && backupExists && !sourceExists)
                {
                    if (_diskProvider.GetFileSize(operation.Plan.FinalizePath) != operation.Plan.ExpectedSize)
                    {
                        MarkRecoveryRequired(operation, owner, "Rollback backup size does not match the durable expected size; evidence was preserved.");
                        return;
                    }

                    operation = MoveToRollingBack(operation, owner);
                    operation = RunFilesystemOperation(operation, owner, () => _diskTransferService.TransferFile(operation.Plan.FinalizePath, operation.Plan.SourcePath, TransferMode.Move));
                    if (!_diskProvider.FileExists(operation.Plan.SourcePath) ||
                        _diskProvider.FileExists(operation.Plan.FinalizePath) ||
                        _diskProvider.GetFileSize(operation.Plan.SourcePath) != operation.Plan.ExpectedSize)
                    {
                        MarkRecoveryRequired(operation, owner, "Rollback transfer could not prove one exact-size source copy; evidence was preserved.");
                        return;
                    }

                    _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
                    return;
                }

                if (databaseState == DeleteDatabaseState.Before && !backupExists && sourceExists && operation.State == RecoverableOperationState.Pending)
                {
                    operation = MoveToRollingBack(operation, owner);
                    _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
                    return;
                }

                if (databaseState == DeleteDatabaseState.Before && !backupExists && !sourceExists && SourceWasMissing(operation))
                {
                    operation = MoveToRollingBack(operation, owner);
                    _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
                    return;
                }

                if (databaseState == DeleteDatabaseState.After && !sourceExists)
                {
                    CompleteDatabaseCommitted(operation, owner);
                    return;
                }

                MarkRecoveryRequired(operation, owner, $"Delete recovery could not prove a safe state (database={databaseState}, source={sourceExists}, backup={backupExists}).");
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (acquired)
                {
                    QuarantineRecoveryFailure(operation.Id, owner, exception);
                }

                throw;
            }
        }

        private RecoverableOperationCreateRequest BuildRequest(Movie movie, MovieFile movieFile)
        {
            if (movie == null || movie.Id <= 0 || string.IsNullOrWhiteSpace(movie.Path) || !Path.IsPathRooted(movie.Path))
            {
                throw new RecoverableOperationValidationException("A persisted movie with a rooted path is required.");
            }

            if (movieFile == null || movieFile.Id <= 0 || movieFile.MovieId != movie.Id || string.IsNullOrWhiteSpace(movieFile.RelativePath))
            {
                throw new RecoverableOperationValidationException("The persisted movie file must belong to the movie.");
            }

            var moviePath = NormalizeDirectory(movie.Path);
            var sourcePath = Path.GetFullPath(Path.Combine(moviePath, movieFile.RelativePath));
            if (!Contained(sourcePath, moviePath) || sourcePath.PathEquals(moviePath))
            {
                throw new RecoverableOperationValidationException("The normalized movie file path must be contained by the movie path.");
            }

            if (!_diskProvider.FolderExists(movie.Path))
            {
                // Missing movie folders are supported only when the database row is being cleaned up.
                _logger.Debug("Movie folder is absent; journaling DB-only movie-file cleanup for {0}.", sourcePath);
            }

            var sourceExists = _diskProvider.FileExists(sourcePath);
            var operationKey = "delete-" + movie.Id.ToString(CultureInfo.InvariantCulture) + "-" + movieFile.Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var paths = RecoverableOperationStagingPolicy.GetPaths(movie.Path, operationKey);
            var backupPath = Path.Combine(paths.Root, Path.GetFileName(sourcePath));
            if (_diskProvider.FolderExists(paths.Root) || _diskProvider.FileExists(paths.Root) || _diskProvider.FileExists(backupPath))
            {
                throw new RecoverableOperationValidationException("The deterministic recovery staging path already exists.");
            }

            var rootFolder = _diskProvider.GetParentFolder(movie.Path);
            var recycleSubfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(sourcePath));
            var facts = new Dictionary<string, string>
            {
                [MissingSourceFact] = (!sourceExists).ToString(CultureInfo.InvariantCulture),
                [MoviePathFact] = Path.GetFullPath(movie.Path),
                [RelativePathFact] = movieFile.RelativePath,
                [RecycleSubfolderFact] = recycleSubfolder ?? string.Empty
            };

            return new RecoverableOperationCreateRequest
            {
                OperationKey = operationKey,
                ResourceKey = "movie-file:" + movieFile.Id.ToString(CultureInfo.InvariantCulture),
                OperationType = RecoverableOperationType.Delete,
                MovieId = movie.Id,
                MovieFileId = movieFile.Id,
                MovieEditionSlotId = movieFile.MovieEditionSlotId,
                StagingRoot = paths.Root,
                Plan = new RecoverableOperationPlan
                {
                    Expected = new RecoverableOperationSnapshot { MovieId = movie.Id, MovieFileId = movieFile.Id, MovieEditionSlotId = movieFile.MovieEditionSlotId, Path = sourcePath, DestinationPath = sourcePath, Size = movieFile.Size },
                    Desired = new RecoverableOperationSnapshot { MovieId = movie.Id, MovieFileId = null, MovieEditionSlotId = null, Path = sourcePath, DestinationPath = sourcePath, Size = movieFile.Size },
                    SourcePath = sourcePath,
                    StagingPath = backupPath,
                    DestinationPath = sourcePath,
                    FinalizePath = backupPath,
                    ExpectedSize = movieFile.Size,
                    TransferMode = RecoverableTransferMode.Move,
                    EventFacts = facts,
                    MovieFileEvent = new RecoverableMovieFileEventSnapshot
                    {
                        Id = movieFile.Id,
                        MovieId = movieFile.MovieId,
                        MovieEditionSlotId = movieFile.MovieEditionSlotId,
                        RelativePath = movieFile.RelativePath,
                        Size = movieFile.Size,
                        Quality = movieFile.Quality,
                        Languages = movieFile.Languages,
                        ReleaseGroup = movieFile.ReleaseGroup,
                        SceneName = movieFile.SceneName,
                        DateAdded = movieFile.DateAdded,
                        MediaInfo = movieFile.MediaInfo,
                        IndexerFlags = movieFile.IndexerFlags,
                        Edition = movieFile.Edition
                    },
                    MovieEvent = new RecoverableMovieEventSnapshot
                    {
                        Id = movie.Id,
                        MovieMetadataId = movie.MovieMetadataId,
                        Path = Path.GetFullPath(movie.Path),
                        Title = movie.Title,
                        Year = movie.Year,
                        TmdbId = movie.TmdbId,
                        ImdbId = movie.ImdbId,
                        InCinemas = movie.MovieMetadata.Value.InCinemas,
                        PhysicalRelease = movie.MovieMetadata.Value.PhysicalRelease,
                        DigitalRelease = movie.MovieMetadata.Value.DigitalRelease,
                        Overview = movie.MovieMetadata.Value.Overview,
                        Genres = new List<string>(movie.MovieMetadata.Value.Genres),
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>(movie.MovieMetadata.Value.Images),
                        OriginalLanguage = movie.MovieMetadata.Value.OriginalLanguage,
                        Tags = new HashSet<int>(movie.Tags)
                    }
                }
            };
        }

        private void Execute(RecoverableOperation operation, MovieFile movieFile, string owner)
        {
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, owner);
            if (!SourceWasMissing(operation))
            {
                if (_diskProvider.GetFileSize(operation.Plan.SourcePath) != operation.Plan.ExpectedSize)
                {
                    throw new RecoverableOperationConcurrencyException(operation.Id);
                }

                if (_diskProvider.FileExists(operation.Plan.FinalizePath) || _diskProvider.FileExists(operation.StagingRoot) || _diskProvider.FolderExists(operation.StagingRoot))
                {
                    throw new RecoverableOperationValidationException("Recovery staging collided before transfer.");
                }

                operation = RunFilesystemOperation(operation, owner, () => _diskProvider.CreateFolder(operation.StagingRoot));
                operation = RunFilesystemOperation(operation, owner, () => _diskTransferService.TransferFile(operation.Plan.SourcePath, operation.Plan.FinalizePath, TransferMode.Move));
                if (_diskProvider.FileExists(operation.Plan.SourcePath) ||
                    !_diskProvider.FileExists(operation.Plan.FinalizePath) ||
                    _diskProvider.GetFileSize(operation.Plan.FinalizePath) != operation.Plan.ExpectedSize)
                {
                    throw new IOException("The staged move and expected size could not be proven complete.");
                }
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterStageTransfer);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staged, owner);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.ApplyingDatabase, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.BeforeDatabaseTransaction);
            CommitDatabaseDelete(operation, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterDatabaseCommit);
            operation = _repository.GetById(operation.Id);
            CompleteDatabaseCommitted(operation, owner, movieFile);
        }

        private void CommitDatabaseDelete(RecoverableOperation operation, string owner)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = connection.QuerySingleOrDefault<MovieFileState>(@"SELECT ""Id"",""MovieId"",""MovieEditionSlotId"",""RelativePath"" FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = operation.MovieFileId }, transaction);
            var expectedRelativePath = operation.Plan.EventFacts[RelativePathFact];
            if (row == null || row.MovieId != operation.MovieId || row.MovieEditionSlotId != operation.MovieEditionSlotId || !string.Equals(row.RelativePath, expectedRelativePath, StringComparison.Ordinal))
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }

            var deleted = connection.Execute(@"DELETE FROM ""MovieFiles"" WHERE ""Id""=@id AND ""MovieId""=@movieId AND ((""MovieEditionSlotId"" IS NULL AND @slotId IS NULL) OR ""MovieEditionSlotId""=@slotId) AND ""RelativePath""=@relativePath", new { id = operation.MovieFileId, movieId = operation.MovieId, slotId = operation.MovieEditionSlotId, relativePath = expectedRelativePath }, transaction);
            if (deleted != 1)
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }

            connection.Execute(@"UPDATE ""Movies"" SET ""MovieFileId""=0 WHERE ""Id""=@movieId AND ""MovieFileId""=@fileId", new { movieId = operation.MovieId, fileId = operation.MovieFileId }, transaction);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterDatabaseMutation);
            var references = connection.QuerySingle<int>(@"SELECT (SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""Id""=@fileId) + (SELECT COUNT(*) FROM ""Movies"" WHERE ""MovieFileId""=@fileId)", new { fileId = operation.MovieFileId }, transaction);
            if (references != 0)
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterInvariantAudit);
            _repository.Transition(connection, transaction, operation.Id, operation.State, operation.Version, RecoverableOperationState.DatabaseCommitted, DateTime.UtcNow, owner);
            transaction.Commit();
        }

        private void CompleteDatabaseCommitted(RecoverableOperation operation, string owner, MovieFile movieFile = null)
        {
            if (operation.State == RecoverableOperationState.ApplyingDatabase)
            {
                operation = _repository.GetById(operation.Id);
            }

            if (operation.State == RecoverableOperationState.DatabaseCommitted)
            {
                operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Finalizing, owner);
            }

            if (operation.State != RecoverableOperationState.Finalizing)
            {
                MarkRecoveryRequired(operation, owner, "DB-after delete operation was not in a finalizable state.");
                return;
            }

            if (HasUncertainDispatch(operation))
            {
                MarkRecoveryRequired(operation, owner, "A compatibility event dispatch was interrupted after side effects may have begun; operator recovery is required and the event will not be replayed automatically.");
                return;
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.BeforeFinalize);
            if (_diskProvider.FileExists(operation.Plan.FinalizePath))
            {
                operation = RunFilesystemOperation(operation, owner, () => _recycleBinProvider.DeleteFile(operation.Plan.FinalizePath, operation.Plan.EventFacts[RecycleSubfolderFact]));
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterFinalize);
            DispatchEvents(operation, owner, movieFile);
            operation = _repository.GetById(operation.Id);
            _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Completed, owner);
        }

        private void DispatchEvents(RecoverableOperation operation, string owner, MovieFile movieFile)
        {
            _faultInjector.Check(RecoverableOperationFaultPoint.BeforeEventDispatch);
            movieFile ??= RehydrateMovieFile(operation);

            try
            {
                if (!HasMask(operation, RecoverableOperationEventDispatchMask.MovieFileDeleted))
                {
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.MovieFileDeleted, owner);
                    _eventAggregator.PublishEventStrict(new MovieFileDeletedEvent(movieFile, DeleteMediaFileReason.Manual));
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterMovieFileDeletedPublish);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.MovieFileDeleted, owner);
                }

                if (!HasMask(operation, RecoverableOperationEventDispatchMask.DeleteCompleted))
                {
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.DeleteCompleted, owner);
                    _eventAggregator.PublishEventStrict(new DeleteCompletedEvent());
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterDeleteCompletedPublish);
                    _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.DeleteCompleted, owner);
                }

                _faultInjector.Check(RecoverableOperationFaultPoint.AfterEventDispatch);
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var current = _repository.GetById(operation.Id);
                MarkRecoveryRequired(current, owner, "Compatibility event dispatch failed after side effects may have begun: " + exception.Message);
                throw;
            }
        }

        private MovieFile RehydrateMovieFile(RecoverableOperation operation)
        {
            var snapshot = operation.Plan.MovieFileEvent ?? throw new RecoverableOperationValidationException("Delete recovery file event evidence is missing.");
            var movieSnapshot = operation.Plan.MovieEvent ?? throw new RecoverableOperationValidationException("Delete recovery movie event evidence is missing.");
            var movie = new Movie
            {
                Id = movieSnapshot.Id,
                MovieMetadataId = movieSnapshot.MovieMetadataId,
                Path = movieSnapshot.Path,
                Tags = new HashSet<int>(movieSnapshot.Tags),
                Title = movieSnapshot.Title,
                Year = movieSnapshot.Year,
                TmdbId = movieSnapshot.TmdbId,
                ImdbId = movieSnapshot.ImdbId
            };
            movie.MovieMetadata.Value.InCinemas = movieSnapshot.InCinemas;
            movie.MovieMetadata.Value.PhysicalRelease = movieSnapshot.PhysicalRelease;
            movie.MovieMetadata.Value.DigitalRelease = movieSnapshot.DigitalRelease;
            movie.MovieMetadata.Value.Overview = movieSnapshot.Overview;
            movie.MovieMetadata.Value.Genres = new List<string>(movieSnapshot.Genres);
            movie.MovieMetadata.Value.Images = new List<NzbDrone.Core.MediaCover.MediaCover>(movieSnapshot.Images);
            movie.MovieMetadata.Value.OriginalLanguage = movieSnapshot.OriginalLanguage;
            return new MovieFile
            {
                Id = snapshot.Id,
                MovieId = snapshot.MovieId,
                MovieEditionSlotId = snapshot.MovieEditionSlotId,
                RelativePath = snapshot.RelativePath,
                Path = operation.Plan.SourcePath,
                Size = snapshot.Size,
                Quality = snapshot.Quality,
                Languages = snapshot.Languages,
                ReleaseGroup = snapshot.ReleaseGroup,
                SceneName = snapshot.SceneName,
                DateAdded = snapshot.DateAdded,
                MediaInfo = snapshot.MediaInfo,
                IndexerFlags = snapshot.IndexerFlags,
                Edition = snapshot.Edition,
                Movie = movie
            };
        }

        private void RollBackBeforeCommit(int operationId, string owner, Exception exception)
        {
            var operation = _repository.GetById(operationId);
            if (operation.State is RecoverableOperationState.DatabaseCommitted or RecoverableOperationState.Finalizing or RecoverableOperationState.Completed)
            {
                return;
            }

            try
            {
                var sourceExists = _diskProvider.FileExists(operation.Plan.SourcePath);
                var backupExists = _diskProvider.FileExists(operation.Plan.FinalizePath);
                if (backupExists && !sourceExists)
                {
                    if (_diskProvider.GetFileSize(operation.Plan.FinalizePath) != operation.Plan.ExpectedSize)
                    {
                        MarkRecoveryRequired(operation, owner, "Rollback backup size does not match the durable expected size; evidence was preserved.");
                        return;
                    }

                    operation = MoveToRollingBack(operation, owner);
                    operation = RunFilesystemOperation(operation, owner, () => _diskTransferService.TransferFile(operation.Plan.FinalizePath, operation.Plan.SourcePath, TransferMode.Move));
                    if (!_diskProvider.FileExists(operation.Plan.SourcePath) ||
                        _diskProvider.FileExists(operation.Plan.FinalizePath) ||
                        _diskProvider.GetFileSize(operation.Plan.SourcePath) != operation.Plan.ExpectedSize)
                    {
                        throw new IOException("Rollback transfer could not prove one exact-size source copy.");
                    }

                    _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
                    return;
                }

                if (!backupExists && (sourceExists || SourceWasMissing(operation)))
                {
                    operation = MoveToRollingBack(operation, owner);
                    _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
                    return;
                }

                MarkRecoveryRequired(operation, owner, "Rollback could not prove a single safe copy after: " + exception.Message);
            }
            catch (Exception rollbackException) when (rollbackException is not RecoverableOperationConcurrencyException)
            {
                operation = _repository.GetById(operationId);
                MarkRecoveryRequired(operation, owner, "Rollback failed while preserving evidence: " + rollbackException.Message);
            }
        }

        private RecoverableOperation MoveToRollingBack(RecoverableOperation operation, string owner)
        {
            if (operation.State == RecoverableOperationState.RollingBack)
            {
                return operation;
            }

            return _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RollingBack, owner);
        }

        private void MarkRecoveryRequired(RecoverableOperation operation, string owner, string error)
        {
            if (operation.State != RecoverableOperationState.RecoveryRequired)
            {
                _repository.MarkRecoveryRequired(operation.Id, operation.State, operation.Version, error, owner);
            }
        }

        private RecoverableOperation Acquire(RecoverableOperation operation, string owner)
        {
            var now = DateTime.UtcNow;
            return _repository.AcquireLease(operation.Id, operation.Version, owner, now, now.Add(_leasePolicy.LeaseDuration));
        }

        private RecoverableOperation RunFilesystemOperation(RecoverableOperation operation, string owner, Action filesystemOperation)
        {
            return _leaseHeartbeat.Run(operation, owner, filesystemOperation);
        }

        private static bool HasMask(RecoverableOperation operation, RecoverableOperationEventDispatchMask mask)
        {
            return (((RecoverableOperationEventDispatchMask)operation.EventDispatchMask) & mask) != 0;
        }

        private static bool HasUncertainDispatch(RecoverableOperation operation)
        {
            const RecoverableOperationEventDispatchMask uncertain = RecoverableOperationEventDispatchMask.MovieFileDeletedInProgress | RecoverableOperationEventDispatchMask.DeleteCompletedInProgress;
            return HasMask(operation, uncertain);
        }

        private void QuarantineAfterLeaseFailure(int operationId, string owner, Exception exception)
        {
            var operation = _repository.GetById(operationId);
            MarkRecoveryRequired(operation, owner, "Lease heartbeat failed during filesystem work; evidence was preserved and operator recovery is required: " + exception.Message);
        }

        private void QuarantineRecoveryFailure(int operationId, string owner, Exception exception)
        {
            try
            {
                var operation = _repository.GetById(operationId);
                if (operation.State != RecoverableOperationState.RecoveryRequired)
                {
                    MarkRecoveryRequired(operation, owner, "Recovery handler failed; evidence was preserved: " + exception.Message);
                }
            }
            catch (RecoverableOperationConcurrencyException)
            {
                // The recovery service will reload and quarantine safely when this owner was not retained.
            }
        }

        private DeleteDatabaseState InspectDatabase(RecoverableOperation operation)
        {
            using var connection = _database.OpenConnection();
            var row = connection.QuerySingleOrDefault<MovieFileState>(@"SELECT ""Id"",""MovieId"",""MovieEditionSlotId"",""RelativePath"" FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = operation.MovieFileId });
            var mainReferences = connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""Movies"" WHERE ""MovieFileId""=@id", new { id = operation.MovieFileId });
            if (row != null && row.MovieId == operation.MovieId && row.MovieEditionSlotId == operation.MovieEditionSlotId && string.Equals(row.RelativePath, operation.Plan.EventFacts[RelativePathFact], StringComparison.Ordinal))
            {
                return mainReferences <= 1 ? DeleteDatabaseState.Before : DeleteDatabaseState.Mixed;
            }

            if (row == null && mainReferences == 0)
            {
                return DeleteDatabaseState.After;
            }

            return DeleteDatabaseState.Mixed;
        }

        private static bool SourceWasMissing(RecoverableOperation operation)
        {
            return operation.Plan.EventFacts.TryGetValue(MissingSourceFact, out var value) && bool.TryParse(value, out var missing) && missing;
        }

        private static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private static bool Contained(string path, string root)
        {
            return Path.GetFullPath(path).StartsWith(NormalizeDirectory(root), StringComparison.Ordinal);
        }

        private sealed class MovieFileState
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public int? MovieEditionSlotId { get; set; }
            public string RelativePath { get; set; }
        }

        private enum DeleteDatabaseState
        {
            Before,
            After,
            Mixed
        }
    }
}
