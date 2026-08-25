using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Dapper;
using Newtonsoft.Json;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableMovieFileImportCoordinator
    {
        RecoverableMovieFileImportResult Import(MovieFile desiredMovieFile,
                                                 LocalMovie localMovie,
                                                 TransferMode transferMode,
                                                 MovieFile outgoingMovieFile,
                                                 int expectedMainMovieFileId,
                                                 MovieFileImportTarget importTarget);
    }

    public sealed class RecoverableMovieFileImportResult
    {
        public MovieFile ImportedMovieFile { get; init; }
        public MovieFile OutgoingMovieFile { get; init; }
        public RecoverableOperation Operation { get; init; }
        public bool IsImported { get; init; }
        public bool FinalizationPending { get; init; }
    }

    public sealed class RecoverableMovieFileImportCoordinator : IRecoverableMovieFileImportCoordinator
    {
        private const string LeasePrefix = "import-file-";
        private readonly IRecoverableOperationRepository _repository;
        private readonly IRecoverableMovieFileImportMutationStore _mutationStore;
        private readonly IMainDatabase _database;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IRecoverableOperationFaultInjector _faultInjector;
        private readonly IRecoverableOperationLeaseHeartbeat _leaseHeartbeat;
        private readonly IRecoverableOperationLeasePolicy _leasePolicy;

        public RecoverableMovieFileImportCoordinator(IRecoverableOperationRepository repository,
                                                      IRecoverableMovieFileImportMutationStore mutationStore,
                                                      IMainDatabase database,
                                                      IDiskProvider diskProvider,
                                                      IDiskTransferService diskTransferService,
                                                      IRecycleBinProvider recycleBinProvider,
                                                      IRecoverableOperationFaultInjector faultInjector,
                                                      IRecoverableOperationLeaseHeartbeat leaseHeartbeat,
                                                      IRecoverableOperationLeasePolicy leasePolicy)
        {
            _repository = repository;
            _mutationStore = mutationStore;
            _database = database;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _recycleBinProvider = recycleBinProvider;
            _faultInjector = faultInjector;
            _leaseHeartbeat = leaseHeartbeat;
            _leasePolicy = leasePolicy;
        }

        public RecoverableMovieFileImportResult Import(MovieFile desiredMovieFile,
                                                        LocalMovie localMovie,
                                                        TransferMode transferMode,
                                                        MovieFile outgoingMovieFile,
                                                        int expectedMainMovieFileId,
                                                        MovieFileImportTarget importTarget)
        {
            var request = BuildRequest(desiredMovieFile, localMovie, transferMode, outgoingMovieFile, expectedMainMovieFileId, importTarget);
            var operation = _repository.CreatePending(request);
            var owner = LeasePrefix + Guid.NewGuid().ToString("N");

            try
            {
                _faultInjector.Check(RecoverableOperationFaultPoint.AfterPendingCommit);
                operation = Acquire(operation, owner);
                operation = Execute(operation, owner);
                return Result(operation, outgoingMovieFile, false);
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch (RecoverableOperationLeaseHeartbeatException exception)
            {
                var current = _repository.GetById(operation.Id);
                if (IsDatabaseCommitted(current))
                {
                    return PostCommitResult(current, outgoingMovieFile, owner, "Lease heartbeat failed after import commit; finalization may be pending: " + exception.Message);
                }

                MarkRecoveryRequiredSafely(current, owner, "Lease heartbeat failed during import filesystem work; evidence was preserved: " + exception.Message);
                throw;
            }
            catch (Exception exception)
            {
                var current = _repository.GetById(operation.Id);
                if (IsDatabaseCommitted(current))
                {
                    return PostCommitResult(current, outgoingMovieFile, owner, "Import committed, but finalization is pending: " + exception.Message);
                }

                RollBackBeforeCommit(current, owner, exception);
                throw;
            }
        }

        private RecoverableOperation Execute(RecoverableOperation operation, string owner)
        {
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, owner);
            ValidatePersistedEvidence(operation, true);
            operation = Run(operation, owner, () => _diskProvider.CreateFolder(operation.StagingRoot));

            VerifyHash(operation.Plan.SourcePath, operation.Plan.ExpectedSize.Value, operation.Plan.IncomingSha256, "source");
            RequireAbsent(operation.Plan.StagingPath, "incoming candidate");
            var incomingMode = ToDiskTransferMode(operation.Plan.TransferMode);
            var actualMode = TransferMode.None;
            operation = Run(operation, owner, () => actualMode = _diskTransferService.TransferFile(operation.Plan.SourcePath, operation.Plan.StagingPath, incomingMode));
            operation = _repository.UpdateActualTransferMode(operation.Id, operation.Version, owner, ToRecoverableTransferMode(actualMode));
            VerifyHash(operation.Plan.StagingPath, operation.Plan.ExpectedSize.Value, operation.Plan.IncomingSha256, "incoming candidate");
            VerifySourceSemantics(operation);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate);

            if (operation.Plan.ExpectedOutgoingMovieFile != null)
            {
                VerifyHash(operation.Plan.Expected.Path, operation.Plan.ExpectedOutgoingMovieFile.Size, operation.Plan.OutgoingSha256, "outgoing movie file");
                RequireAbsent(operation.Plan.FinalizePath, "outgoing backup");
                operation = Run(operation, owner, () =>
                {
                    RejectReparseAncestors(operation.Plan.Expected.Path, operation.Plan.FinalizePath);
                    _diskTransferService.TransferFile(operation.Plan.Expected.Path, operation.Plan.FinalizePath, TransferMode.Move);
                });
                RequireAbsent(operation.Plan.Expected.Path, "outgoing movie file");
                VerifyHash(operation.Plan.FinalizePath, operation.Plan.ExpectedOutgoingMovieFile.Size, operation.Plan.OutgoingSha256, "outgoing backup");
                _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportOutgoingToBackup);
            }

            RequireAbsent(operation.Plan.DestinationPath, "destination");
            VerifyHash(operation.Plan.StagingPath, operation.Plan.ExpectedSize.Value, operation.Plan.IncomingSha256, "incoming candidate");
            operation = Run(operation, owner, () =>
            {
                RejectReparseAncestors(operation.Plan.StagingPath, operation.Plan.DestinationPath);
                _diskTransferService.TransferFile(operation.Plan.StagingPath, operation.Plan.DestinationPath, TransferMode.Move);
            });
            RequireAbsent(operation.Plan.StagingPath, "incoming candidate");
            VerifyHash(operation.Plan.DestinationPath, operation.Plan.ExpectedSize.Value, operation.Plan.IncomingSha256, "destination");
            VerifySourceSemantics(operation);
            if (operation.Plan.ExpectedOutgoingMovieFile != null)
            {
                VerifyHash(operation.Plan.FinalizePath, operation.Plan.ExpectedOutgoingMovieFile.Size, operation.Plan.OutgoingSha256, "outgoing backup");
            }

            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staged, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportCandidateToDestination);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportStaged);
            ValidatePersistedEvidence(operation, false);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.ApplyingDatabase, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportApplyingDatabase);
            operation = _mutationStore.Apply(operation, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportDatabaseCommit);
            return Finalize(operation, owner);
        }

        private RecoverableOperation Finalize(RecoverableOperation operation, string owner)
        {
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Finalizing, owner);
            var outgoing = operation.Plan.ExpectedOutgoingMovieFile;
            if (outgoing != null && _diskProvider.FileExists(operation.Plan.FinalizePath))
            {
                var recycleClaim = Path.Combine(operation.StagingRoot, "recycle-" + Path.GetFileName(operation.Plan.FinalizePath));
                RequireAbsent(recycleClaim, "recycle claim");
                RejectReparseAncestors(operation.Plan.FinalizePath, recycleClaim);
                operation = Run(operation, owner, () => _diskTransferService.TransferFile(operation.Plan.FinalizePath, recycleClaim, TransferMode.Move));
                VerifyHash(recycleClaim, outgoing.Size, operation.Plan.OutgoingSha256, "claimed outgoing backup");
                var recycleSubfolder = Path.GetFileName(NormalizeDirectory(operation.Plan.EventFacts["moviePath"]).TrimEnd(Path.DirectorySeparatorChar));
                operation = Run(operation, owner, () => _recycleBinProvider.DeleteFile(recycleClaim, recycleSubfolder));
                RequireAbsent(recycleClaim, "recycle claim");
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportRecycle);
            RequireAbsent(operation.Plan.StagingPath, "incoming candidate");
            if (_diskProvider.FolderExists(operation.StagingRoot))
            {
                operation = Run(operation, owner, () => _diskProvider.DeleteFolder(operation.StagingRoot, false));
            }

            return _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Completed, owner);
        }

        private RecoverableOperationCreateRequest BuildRequest(MovieFile desired,
                                                                LocalMovie localMovie,
                                                                TransferMode transferMode,
                                                                MovieFile outgoing,
                                                                int expectedMainMovieFileId,
                                                                MovieFileImportTarget target)
        {
            if (desired == null || localMovie?.Movie == null || localMovie.Movie.Id <= 0 || desired.MovieId != localMovie.Movie.Id || expectedMainMovieFileId < 0)
            {
                throw new RecoverableOperationValidationException("A durable desired movie file, persisted movie, and expected main pointer are required.");
            }

            if (target is not MovieFileImportTarget.Main and not MovieFileImportTarget.EditionSlot and not MovieFileImportTarget.Unassigned || localMovie.ImportTarget != target)
            {
                throw new RecoverableOperationValidationException("A known, consistent import target is required.");
            }

            var plannedMode = ToRecoverableTransferMode(transferMode);
            var moviePath = CanonicalDirectory(localMovie.Movie.Path, "movie path");
            var sourcePath = CanonicalPath(localMovie.Path, "source path");
            var destinationPath = CanonicalPath(desired.Path, "destination path");
            if (!Contained(destinationPath, moviePath) || destinationPath.PathEquals(moviePath))
            {
                throw new RecoverableOperationValidationException("The destination must be contained by the movie path.");
            }

            if (HasDotSegment(desired.RelativePath) || !RelativeMatches(moviePath, destinationPath, desired.RelativePath))
            {
                throw new RecoverableOperationValidationException("The desired relative path and canonical destination do not match.");
            }

            if (desired.Size < 0 || localMovie.Size != desired.Size || desired.Quality == null || desired.Languages == null || desired.MediaInfo == null || desired.DateAdded == default)
            {
                throw new RecoverableOperationValidationException("The desired incoming movie-file evidence is incomplete or inconsistent.");
            }

            ValidateTarget(desired, outgoing, expectedMainMovieFileId, target);
            if (outgoing != null && (Path.IsPathRooted(outgoing.RelativePath) || HasDotSegment(outgoing.RelativePath)))
            {
                throw new RecoverableOperationValidationException("The outgoing relative path must be relative and contain no dot traversal.");
            }

            var outgoingPath = outgoing == null ? destinationPath : CanonicalPath(Path.Combine(moviePath, outgoing.RelativePath), "outgoing path");
            if (outgoing != null && (!Contained(outgoingPath, moviePath) || !RelativeMatches(moviePath, outgoingPath, outgoing.RelativePath)))
            {
                throw new RecoverableOperationValidationException("The outgoing relative path must resolve beneath the movie path.");
            }

            if (string.Equals(sourcePath, destinationPath, PathComparison) || outgoing != null && string.Equals(sourcePath, outgoingPath, PathComparison))
            {
                throw new RecoverableOperationValidationException("The import source must be distinct from destination and outgoing paths.");
            }

            var operationKey = "import-" + localMovie.Movie.Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var paths = RecoverableOperationStagingPolicy.GetPaths(moviePath, operationKey);
            var candidatePath = CanonicalPath(Path.Combine(paths.Root, "candidate-" + Path.GetFileName(destinationPath)), "candidate path");
            var backupPath = CanonicalPath(Path.Combine(paths.Root, outgoing == null ? "outgoing" : Path.GetFileName(outgoingPath)), "outgoing backup path");
            RejectReparseAncestors(moviePath, sourcePath, destinationPath, outgoingPath, paths.Root, candidatePath, backupPath);
            ValidateInitialDisk(sourcePath, destinationPath, outgoingPath, desired.Size, outgoing == null ? null : Snapshot(outgoing));
            var incomingSha256 = HashExact(sourcePath, desired.Size, "source");
            var outgoingSha256 = outgoing == null ? null : HashExact(outgoingPath, outgoing.Size, "outgoing movie file");
            ValidateDatabaseEvidence(localMovie.Movie.Id, moviePath, expectedMainMovieFileId, target, desired.MovieEditionSlotId, outgoing == null ? null : Snapshot(outgoing));
            if (_diskProvider.FileExists(paths.Root) || _diskProvider.FolderExists(paths.Root) || _diskProvider.FileExists(candidatePath) || _diskProvider.FileExists(backupPath))
            {
                throw new RecoverableOperationValidationException("The deterministic import recovery staging path already exists.");
            }

            var desiredSnapshot = Snapshot(desired);
            desiredSnapshot = new RecoverableMovieFileRowSnapshot
            {
                MovieId = desiredSnapshot.MovieId,
                MovieEditionSlotId = desiredSnapshot.MovieEditionSlotId,
                RelativePath = desiredSnapshot.RelativePath,
                Size = desiredSnapshot.Size,
                Quality = desiredSnapshot.Quality,
                Languages = desiredSnapshot.Languages,
                DateAdded = desiredSnapshot.DateAdded,
                SceneName = desiredSnapshot.SceneName,
                ReleaseGroup = desiredSnapshot.ReleaseGroup,
                MediaInfo = desiredSnapshot.MediaInfo,
                OriginalFilePath = desiredSnapshot.OriginalFilePath,
                IndexerFlags = desiredSnapshot.IndexerFlags,
                Edition = desiredSnapshot.Edition
            };
            var resources = new HashSet<string>(StringComparer.Ordinal)
            {
                PathResource(sourcePath),
                PathResource(destinationPath),
                PathResource(CanonicalPath(paths.Root, "staging root resource")),
                PathResource(candidatePath),
                PathResource(backupPath),
                TargetResource(localMovie.Movie.Id, desired.MovieEditionSlotId, target)
            };
            if (outgoing != null)
            {
                resources.Add("movie-file:" + outgoing.Id.ToString(CultureInfo.InvariantCulture));
                resources.Add(PathResource(outgoingPath));
            }

            var sortedResources = new List<string>(resources);
            sortedResources.Sort(StringComparer.Ordinal);
            return new RecoverableOperationCreateRequest
            {
                OperationKey = operationKey,
                ResourceKeys = sortedResources,
                OperationType = RecoverableOperationType.Import,
                MovieId = localMovie.Movie.Id,
                MovieFileId = outgoing?.Id,
                MovieEditionSlotId = desired.MovieEditionSlotId,
                StagingRoot = CanonicalDirectory(paths.Root, "staging root"),
                Plan = new RecoverableOperationPlan
                {
                    Expected = new RecoverableOperationSnapshot { MovieId = localMovie.Movie.Id, MovieFileId = outgoing?.Id, MovieEditionSlotId = outgoing?.MovieEditionSlotId, Path = outgoingPath, DestinationPath = destinationPath, Size = outgoing?.Size },
                    Desired = new RecoverableOperationSnapshot { MovieId = localMovie.Movie.Id, MovieEditionSlotId = desired.MovieEditionSlotId, Path = sourcePath, DestinationPath = destinationPath, Size = desired.Size },
                    ImportTarget = target,
                    ExpectedMovieFileId = expectedMainMovieFileId,
                    ExpectedOutgoingMovieFile = outgoing == null ? null : Snapshot(outgoing),
                    DesiredIncomingMovieFile = desiredSnapshot,
                    SourcePath = sourcePath,
                    StagingPath = candidatePath,
                    DestinationPath = destinationPath,
                    FinalizePath = backupPath,
                    ExpectedSize = desired.Size,
                    TransferMode = plannedMode,
                    IncomingSha256 = incomingSha256,
                    OutgoingSha256 = outgoingSha256,
                    EventFacts = new Dictionary<string, string> { ["moviePath"] = moviePath }
                }
            };
        }

        private void ValidatePersistedEvidence(RecoverableOperation operation, bool beforeFilesystemMutation)
        {
            var plan = operation.Plan;
            RejectReparseAncestors(plan.EventFacts["moviePath"], plan.SourcePath, plan.DestinationPath, plan.Expected.Path, operation.StagingRoot, plan.StagingPath, plan.FinalizePath);
            ValidateDatabaseEvidence(operation.MovieId, plan.EventFacts["moviePath"], plan.ExpectedMovieFileId, plan.ImportTarget, plan.DesiredIncomingMovieFile.MovieEditionSlotId, plan.ExpectedOutgoingMovieFile);
            if (beforeFilesystemMutation)
            {
                VerifyHash(plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "source");
            }
            else
            {
                VerifySourceSemantics(operation);
            }

            if (beforeFilesystemMutation)
            {
                ValidateInitialDisk(plan.SourcePath, plan.DestinationPath, plan.Expected.Path, plan.ExpectedSize.Value, plan.ExpectedOutgoingMovieFile);
                if (_diskProvider.FileExists(operation.StagingRoot) || _diskProvider.FolderExists(operation.StagingRoot))
                {
                    throw new RecoverableOperationValidationException("The import staging root collided after Pending was committed.");
                }
            }
            else
            {
                VerifyHash(plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, "destination");
                RequireAbsent(plan.StagingPath, "incoming candidate");
                if (plan.ExpectedOutgoingMovieFile != null)
                {
                    if (!string.Equals(plan.Expected.Path, plan.DestinationPath, PathComparison))
                    {
                        RequireAbsent(plan.Expected.Path, "outgoing movie file");
                    }

                    VerifyHash(plan.FinalizePath, plan.ExpectedOutgoingMovieFile.Size, plan.OutgoingSha256, "outgoing backup");
                }
            }
        }

        private void ValidateDatabaseEvidence(int movieId, string moviePath, int expectedPointer, MovieFileImportTarget target, int? slotId, RecoverableMovieFileRowSnapshot outgoing)
        {
            using var connection = _database.OpenConnection();
            var movie = connection.QuerySingleOrDefault<MovieState>(@"SELECT ""Id"",""MovieFileId"",""Path"" FROM ""Movies"" WHERE ""Id""=@movieId", new { movieId });
            if (movie == null || movie.MovieFileId != expectedPointer || !string.Equals(CanonicalDirectory(movie.Path, "persisted movie path"), CanonicalDirectory(moviePath, "planned movie path"), PathComparison))
            {
                throw new RecoverableOperationConcurrencyException(movieId);
            }

            if (target == MovieFileImportTarget.EditionSlot &&
                connection.QuerySingleOrDefault<int?>(@"SELECT ""MovieId"" FROM ""MovieEditionSlots"" WHERE ""Id""=@slotId", new { slotId }) != movieId)
            {
                throw new RecoverableOperationConcurrencyException(movieId);
            }

            RecoverableMovieFileRowSnapshot actual = null;
            if (outgoing != null)
            {
                actual = connection.QuerySingleOrDefault<RecoverableMovieFileRowSnapshot>(@"SELECT * FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = outgoing.Id });
            }
            else if (target == MovieFileImportTarget.EditionSlot)
            {
                actual = connection.QuerySingleOrDefault<RecoverableMovieFileRowSnapshot>(@"SELECT * FROM ""MovieFiles"" WHERE ""MovieEditionSlotId""=@slotId", new { slotId });
            }

            if (!SameSnapshot(actual, outgoing))
            {
                throw new RecoverableOperationConcurrencyException(movieId);
            }
        }

        private void ValidateInitialDisk(string source, string destination, string outgoingPath, long incomingSize, RecoverableMovieFileRowSnapshot outgoing)
        {
            VerifyExact(source, incomingSize, "source");
            if (outgoing == null)
            {
                if (_diskProvider.FileExists(destination))
                {
                    throw new RecoverableOperationValidationException("The destination is occupied by an unexpected file.");
                }

                return;
            }

            VerifyExact(outgoingPath, outgoing.Size, "outgoing movie file");
            if (_diskProvider.FileExists(destination) && !string.Equals(destination, outgoingPath, PathComparison))
            {
                throw new RecoverableOperationValidationException("The destination is occupied by an unexpected file.");
            }
        }

        private void RollBackBeforeCommit(RecoverableOperation operation, string owner, Exception cause)
        {
            try
            {
                if (IsDatabaseCommitted(operation))
                {
                    return;
                }

                var destinationOwned = operation.State is RecoverableOperationState.Staged or RecoverableOperationState.ApplyingDatabase;
                if (operation.State != RecoverableOperationState.RollingBack)
                {
                    operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RollingBack, owner);
                }

                var plan = operation.Plan;
                var sourceExists = ExactHash(plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256);
                var candidateExists = ExactHash(plan.StagingPath, plan.ExpectedSize.Value, plan.IncomingSha256);
                var destinationExists = _diskProvider.FileExists(plan.DestinationPath);
                var destinationIncoming = destinationOwned && ExactHash(plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256) &&
                                          (plan.ExpectedOutgoingMovieFile == null || _diskProvider.FileExists(plan.FinalizePath) || !string.Equals(plan.DestinationPath, plan.Expected.Path, PathComparison));
                var unexpectedDestination = destinationExists && !destinationIncoming &&
                                            (plan.ExpectedOutgoingMovieFile == null || !string.Equals(plan.DestinationPath, plan.Expected.Path, PathComparison));
                if ((plan.ActualTransferMode ?? plan.TransferMode) == RecoverableTransferMode.Move)
                {
                    if (!sourceExists)
                    {
                        if (candidateExists == destinationIncoming)
                        {
                            throw new IOException("Rollback could not prove exactly one incoming file to restore.");
                        }

                        var incomingPath = candidateExists ? plan.StagingPath : plan.DestinationPath;
                        operation = Run(operation, owner, () => _diskTransferService.TransferFile(incomingPath, plan.SourcePath, TransferMode.Move));
                        VerifyHash(plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "restored source");
                        RequireAbsent(incomingPath, "restored incoming location");
                    }
                }
                else
                {
                    if (!sourceExists)
                    {
                        throw new IOException("Rollback could not prove the preserved source copy.");
                    }

                    if (candidateExists)
                    {
                        operation = DeleteExact(operation, owner, plan.StagingPath, plan.ExpectedSize.Value, plan.IncomingSha256);
                    }

                    if (destinationIncoming)
                    {
                        operation = DeleteExact(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256);
                    }
                }

                var outgoing = plan.ExpectedOutgoingMovieFile;
                if (outgoing != null)
                {
                    var originalExists = ExactHash(plan.Expected.Path, outgoing.Size, plan.OutgoingSha256);
                    var backupExists = ExactHash(plan.FinalizePath, outgoing.Size, plan.OutgoingSha256);
                    if (originalExists && backupExists || !originalExists && !backupExists)
                    {
                        throw new IOException("Rollback could not prove exactly one outgoing movie-file copy.");
                    }

                    if (backupExists)
                    {
                        operation = Run(operation, owner, () => _diskTransferService.TransferFile(plan.FinalizePath, plan.Expected.Path, TransferMode.Move));
                    }

                    VerifyHash(plan.Expected.Path, outgoing.Size, plan.OutgoingSha256, "restored outgoing movie file");
                    RequireAbsent(plan.FinalizePath, "outgoing backup");
                }
                else
                {
                    RequireAbsent(plan.DestinationPath, "destination");
                }

                VerifyHash(plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "restored source");
                if (unexpectedDestination)
                {
                    throw new IOException("Rollback found an unexpected destination file and preserved it.");
                }

                RequireAbsent(plan.StagingPath, "incoming candidate");
                CleanupStaging(operation, owner);
                operation = _repository.GetById(operation.Id);
                _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
            }
            catch (Exception rollbackException) when (rollbackException is not RecoverableOperationConcurrencyException)
            {
                MarkRecoveryRequired(operation.Id, owner, "Import rollback was ambiguous after '" + cause.Message + "'; evidence was preserved: " + rollbackException.Message);
            }
        }

        private RecoverableOperation DeleteExact(RecoverableOperation operation, string owner, string path, long size, string sha256)
        {
            var claim = Path.Combine(operation.StagingRoot, "rollback-delete-" + Path.GetFileName(path));
            RequireAbsent(claim, "rollback delete claim");
            RejectReparseAncestors(path, claim);
            operation = Run(operation, owner, () => _diskTransferService.TransferFile(path, claim, TransferMode.Move));
            try
            {
                VerifyHash(claim, size, sha256, "claimed coordinator-created copy");
            }
            catch
            {
                if (!_diskProvider.FileExists(path))
                {
                    Run(operation, owner, () => _diskTransferService.TransferFile(claim, path, TransferMode.Move));
                }

                throw;
            }

            operation = Run(operation, owner, () => _diskProvider.DeleteFile(claim));
            RequireAbsent(claim, "rollback delete claim");
            return operation;
        }

        private void CleanupStaging(RecoverableOperation operation, string owner)
        {
            if (_diskProvider.FolderExists(operation.StagingRoot) && !_diskProvider.FileExists(operation.Plan.StagingPath) && !_diskProvider.FileExists(operation.Plan.FinalizePath))
            {
                Run(operation, owner, () => _diskProvider.DeleteFolder(operation.StagingRoot, false));
            }
        }

        private RecoverableMovieFileImportResult PostCommitResult(RecoverableOperation operation, MovieFile outgoing, string owner, string error)
        {
            if (operation.State != RecoverableOperationState.Completed)
            {
                operation = MarkRecoveryRequiredSafely(operation, owner, error);
            }

            return Result(operation, outgoing, operation.State != RecoverableOperationState.Completed);
        }

        private RecoverableMovieFileImportResult Result(RecoverableOperation operation, MovieFile outgoing, bool finalizationPending)
        {
            using var connection = _database.OpenConnection();
            var imported = connection.QuerySingle<MovieFile>(@"SELECT * FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = operation.ResultMovieFileId });
            return new RecoverableMovieFileImportResult { ImportedMovieFile = imported, OutgoingMovieFile = outgoing, Operation = operation, IsImported = true, FinalizationPending = finalizationPending };
        }

        private RecoverableOperation Run(RecoverableOperation operation, string owner, Action action)
        {
            return _leaseHeartbeat.Run(operation, owner, action);
        }

        private RecoverableOperation Acquire(RecoverableOperation operation, string owner)
        {
            var now = DateTime.UtcNow;
            return _repository.AcquireLease(operation.Id, operation.Version, owner, now, now.Add(_leasePolicy.LeaseDuration));
        }

        private void MarkRecoveryRequired(int operationId, string owner, string error)
        {
            MarkRecoveryRequiredSafely(_repository.GetById(operationId), owner, error);
        }

        private RecoverableOperation MarkRecoveryRequiredSafely(RecoverableOperation current, string owner, string error)
        {
            if (current.State == RecoverableOperationState.RecoveryRequired || current.State == RecoverableOperationState.Completed)
            {
                return current;
            }

            try
            {
                return _repository.MarkRecoveryRequired(current.Id, current.State, current.Version, error, owner);
            }
            catch (RecoverableOperationException)
            {
                return _repository.GetById(current.Id);
            }
        }

        private void VerifySourceSemantics(RecoverableOperation operation)
        {
            if ((operation.Plan.ActualTransferMode ?? operation.Plan.TransferMode) == RecoverableTransferMode.Move)
            {
                RequireAbsent(operation.Plan.SourcePath, "consumed source");
            }
            else
            {
                VerifyHash(operation.Plan.SourcePath, operation.Plan.ExpectedSize.Value, operation.Plan.IncomingSha256, "preserved source");
            }
        }

        private void VerifyExact(string path, long size, string label)
        {
            if (!_diskProvider.FileExists(path) || _diskProvider.GetFileSize(path) != size)
            {
                throw new IOException("The " + label + " is absent or does not have the exact planned size.");
            }
        }

        private string HashExact(string path, long size, string label)
        {
            VerifyExact(path, size, label);
            using var stream = _diskProvider.OpenReadStream(path);
            using var sha256 = SHA256.Create();
            var hash = Convert.ToHexString(sha256.ComputeHash(stream));
            VerifyExact(path, size, label);
            return hash;
        }

        private void VerifyHash(string path, long size, string expectedSha256, string label)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || !string.Equals(HashExact(path, size, label), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The " + label + " does not have the exact planned SHA256 identity.");
            }
        }

        private bool ExactHash(string path, long size, string expectedSha256)
        {
            if (!_diskProvider.FileExists(path) || _diskProvider.GetFileSize(path) != size || string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            return string.Equals(HashExact(path, size, "filesystem evidence"), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }

        private void RequireAbsent(string path, string label)
        {
            if (_diskProvider.FileExists(path))
            {
                throw new IOException("The " + label + " unexpectedly exists.");
            }
        }

        private static void ValidateTarget(MovieFile desired, MovieFile outgoing, int expectedPointer, MovieFileImportTarget target)
        {
            var valid = target switch
            {
                MovieFileImportTarget.Main => desired.MovieEditionSlotId == null && (outgoing == null || outgoing.MovieEditionSlotId == null) && expectedPointer == (outgoing?.Id ?? 0),
                MovieFileImportTarget.EditionSlot => desired.MovieEditionSlotId > 0 && (outgoing == null || outgoing.MovieEditionSlotId == desired.MovieEditionSlotId),
                MovieFileImportTarget.Unassigned => desired.MovieEditionSlotId == null && outgoing == null,
                _ => false
            };
            if (!valid || outgoing != null && (outgoing.Id <= 0 || outgoing.MovieId != desired.MovieId || string.IsNullOrWhiteSpace(outgoing.RelativePath)))
            {
                throw new RecoverableOperationValidationException("The target, expected pointer, outgoing row, and desired row are inconsistent.");
            }
        }

        private static RecoverableMovieFileRowSnapshot Snapshot(MovieFile file)
        {
            return new RecoverableMovieFileRowSnapshot { Id = file.Id, MovieId = file.MovieId, MovieEditionSlotId = file.MovieEditionSlotId, RelativePath = file.RelativePath, Size = file.Size, Quality = file.Quality, Languages = file.Languages, DateAdded = file.DateAdded, SceneName = file.SceneName, ReleaseGroup = file.ReleaseGroup, MediaInfo = file.MediaInfo, OriginalFilePath = file.OriginalFilePath, IndexerFlags = file.IndexerFlags, Edition = file.Edition };
        }

        private static bool SameSnapshot(RecoverableMovieFileRowSnapshot actual, RecoverableMovieFileRowSnapshot expected)
        {
            return actual == null && expected == null || actual != null && expected != null && JsonConvert.SerializeObject(actual) == JsonConvert.SerializeObject(expected);
        }

        private static RecoverableTransferMode ToRecoverableTransferMode(TransferMode mode)
        {
            return mode switch
            {
                TransferMode.Move => RecoverableTransferMode.Move,
                TransferMode.Copy => RecoverableTransferMode.Copy,
                TransferMode.HardLink => RecoverableTransferMode.HardLink,
                TransferMode.HardLinkOrCopy => RecoverableTransferMode.HardLink,
                _ => throw new RecoverableOperationValidationException("Only Move, Copy, and Hardlink-or-copy imports are recoverable.")
            };
        }

        private static TransferMode ToDiskTransferMode(RecoverableTransferMode mode)
        {
            return mode switch
            {
                RecoverableTransferMode.Move => TransferMode.Move,
                RecoverableTransferMode.Copy => TransferMode.Copy,
                RecoverableTransferMode.HardLink => TransferMode.HardLinkOrCopy,
                _ => throw new RecoverableOperationValidationException("The durable import transfer mode is unsupported.")
            };
        }

        private static string TargetResource(int movieId, int? slotId, MovieFileImportTarget target)
        {
            return target switch
            {
                MovieFileImportTarget.Main => "target:main:" + movieId.ToString(CultureInfo.InvariantCulture),
                MovieFileImportTarget.EditionSlot => "target:slot:" + slotId.Value.ToString(CultureInfo.InvariantCulture),
                MovieFileImportTarget.Unassigned => "target:unassigned:" + movieId.ToString(CultureInfo.InvariantCulture),
                _ => throw new RecoverableOperationValidationException("Unknown import target.")
            };
        }

        private static string PathResource(string path) => "path:" + path;
        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static void RejectReparseAncestors(params string[] paths)
        {
            foreach (var rawPath in paths)
            {
                var path = Path.GetFullPath(rawPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                while (!string.IsNullOrEmpty(path))
                {
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new RecoverableOperationValidationException("Import paths must not traverse a symbolic link or reparse point.");
                        }
                    }

                    var parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, path, PathComparison))
                    {
                        break;
                    }

                    path = parent;
                }
            }
        }

        private static string CanonicalPath(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || HasDotSegment(path))
            {
                throw new RecoverableOperationValidationException("The " + label + " must be rooted and contain no dot traversal.");
            }

            var canonical = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
        }

        private static string CanonicalDirectory(string path, string label)
        {
            return CanonicalPath(path, label) + Path.DirectorySeparatorChar;
        }

        private static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private static bool Contained(string path, string root)
        {
            return path.StartsWith(root, PathComparison);
        }

        private static bool RelativeMatches(string moviePath, string destinationPath, string relativePath)
        {
            try
            {
                return string.Equals(moviePath.GetRelativePath(destinationPath).Replace('\\', '/'), relativePath.Replace('\\', '/'), PathComparison);
            }
            catch (NotParentException)
            {
                return false;
            }
        }

        private static bool HasDotSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            foreach (var segment in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDatabaseCommitted(RecoverableOperation operation)
        {
            return operation.ResultMovieFileId.HasValue || operation.State is RecoverableOperationState.DatabaseCommitted or RecoverableOperationState.Finalizing or RecoverableOperationState.Completed;
        }

        private sealed class MovieState
        {
            public int Id { get; init; }
            public int MovieFileId { get; init; }
            public string Path { get; init; }
        }
    }
}
