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
using NzbDrone.Core.Download;
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
                                                 MovieFileImportTarget importTarget,
                                                 DownloadClientItem downloadClientItem = null);
        RecoverableOperation Recover(RecoverableOperation operation, string owner);
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
        private readonly IRecoverableMovieFileImportCompletionService _completionService;

        public RecoverableMovieFileImportCoordinator(IRecoverableOperationRepository repository,
                                                      IRecoverableMovieFileImportMutationStore mutationStore,
                                                      IMainDatabase database,
                                                      IDiskProvider diskProvider,
                                                      IDiskTransferService diskTransferService,
                                                      IRecycleBinProvider recycleBinProvider,
                                                      IRecoverableOperationFaultInjector faultInjector,
                                                      IRecoverableOperationLeaseHeartbeat leaseHeartbeat,
                                                      IRecoverableOperationLeasePolicy leasePolicy,
                                                      IRecoverableMovieFileImportCompletionService completionService)
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
            _completionService = completionService;
        }

        public RecoverableMovieFileImportResult Import(MovieFile desiredMovieFile,
                                                        LocalMovie localMovie,
                                                        TransferMode transferMode,
                                                        MovieFile outgoingMovieFile,
                                                        int expectedMainMovieFileId,
                                                        MovieFileImportTarget importTarget,
                                                        DownloadClientItem downloadClientItem = null)
        {
            var request = BuildRequest(desiredMovieFile, localMovie, transferMode, outgoingMovieFile, expectedMainMovieFileId, importTarget, downloadClientItem ?? localMovie?.DownloadItem);
            var operation = _repository.CreatePending(request);
            var owner = LeasePrefix + Guid.NewGuid().ToString("N");

            var destinationOwned = false;
            try
            {
                _faultInjector.Check(RecoverableOperationFaultPoint.AfterPendingCommit);
                operation = Acquire(operation, owner);
                operation = Execute(operation, owner, () => destinationOwned = true);
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

                RollBackBeforeCommit(current, owner, exception, destinationOwned);
                throw;
            }
        }

        public RecoverableOperation Recover(RecoverableOperation operation, string owner)
        {
            if (operation == null || operation.OperationType != RecoverableOperationType.Import)
            {
                throw new RecoverableOperationValidationException("The movie-file import recovery handler only accepts Import operations.");
            }

            operation = _repository.GetById(operation.Id);
            if (operation.State is RecoverableOperationState.Completed or RecoverableOperationState.RolledBack or RecoverableOperationState.RecoveryRequired)
            {
                return operation;
            }

            if (string.IsNullOrWhiteSpace(owner) || operation.LeaseOwner != owner || operation.LeaseExpiresAt <= DateTime.UtcNow)
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }

            try
            {
                ValidateRecoveryPlan(operation);
                while (true)
                {
                    operation = _repository.GetById(operation.Id);
                    switch (operation.State)
                    {
                        case RecoverableOperationState.Pending:
                            return Execute(operation, owner);
                        case RecoverableOperationState.Staging:
                            operation = ResumeStaging(operation, owner);
                            break;
                        case RecoverableOperationState.Staged:
                            operation = ApplyDatabase(operation, owner);
                            break;
                        case RecoverableOperationState.ApplyingDatabase:
                            operation = _mutationStore.Apply(operation, owner);
                            break;
                        case RecoverableOperationState.DatabaseCommitted:
                        case RecoverableOperationState.Finalizing:
                            return Finalize(operation, owner);
                        case RecoverableOperationState.RollingBack:
                            RollBackBeforeCommit(operation, owner, new IOException("Restart resumed an interrupted rollback."));
                            return _repository.GetById(operation.Id);
                        case RecoverableOperationState.Completed:
                        case RecoverableOperationState.RolledBack:
                        case RecoverableOperationState.RecoveryRequired:
                            return operation;
                        default:
                            throw new RecoverableOperationValidationException("The import operation has an unsupported recovery state.");
                    }
                }
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch (RecoverableOperationLeaseHeartbeatException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or RecoverableOperationValidationException or RecoverableOperationConcurrencyException)
            {
                var current = _repository.GetById(operation.Id);
                if (IsDatabaseCommitted(current))
                {
                    return RecordPostCommitFailure(current, owner, "Import recovery finalization is pending: " + exception.Message);
                }

                return MarkRecoveryRequiredSafely(current, owner, "Import recovery found ambiguous or changed evidence and preserved it: " + exception.Message);
            }
        }

        private RecoverableOperation Execute(RecoverableOperation operation, string owner, Action destinationOwned = null)
        {
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staging, owner);
            operation = ValidatePersistedEvidence(operation, owner, true);
            return ResumeStaging(operation, owner, destinationOwned);
        }

        private RecoverableOperation ResumeStaging(RecoverableOperation operation, string owner, Action destinationOwned = null)
        {
            ValidateRecoveryPlan(operation);
            ValidateDatabaseEvidence(operation.MovieId, operation.Plan.EventFacts["moviePath"], operation.Plan.ExpectedMovieFileId, operation.Plan.ImportTarget, operation.Plan.DesiredIncomingMovieFile.MovieEditionSlotId, operation.Plan.ExpectedOutgoingMovieFile);
            var destinationFolder = Path.GetDirectoryName(operation.Plan.DestinationPath);
            if (!_diskProvider.FolderExists(destinationFolder))
            {
                operation = Run(operation, owner, () => _diskProvider.CreateFolder(destinationFolder));
            }

            if (!_diskProvider.FolderExists(operation.StagingRoot))
            {
                operation = Run(operation, owner, () => _diskProvider.CreateFolder(operation.StagingRoot));
            }

            var plan = operation.Plan;
            operation = HeartbeatExactHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, out var source);
            operation = HeartbeatExactHash(operation, owner, plan.StagingPath, plan.ExpectedSize.Value, plan.IncomingSha256, out var candidate);
            operation = HeartbeatExactHash(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, out var destination);
            if (plan.ActualTransferMode == null)
            {
                if (candidate)
                {
                    var inferred = plan.TransferMode switch
                    {
                        RecoverableTransferMode.Move when !source => RecoverableTransferMode.Move,
                        RecoverableTransferMode.Copy when source => RecoverableTransferMode.Copy,
                        RecoverableTransferMode.HardLink when source => RecoverableTransferMode.Copy,
                        _ => throw new IOException("The completed incoming transfer mode cannot be inferred unambiguously.")
                    };
                    operation = _repository.UpdateActualTransferMode(operation.Id, operation.Version, owner, inferred);
                    plan = operation.Plan;
                }
                else if (!destination)
                {
                    if (!source || _diskProvider.FileExists(plan.StagingPath))
                    {
                        throw new IOException("The incoming transfer has no exact, uniquely owned source evidence.");
                    }

                    var actualMode = TransferMode.None;
                    operation = Run(operation, owner, () =>
                    {
                        RejectReparseAncestors(plan.SourcePath, plan.StagingPath);
                        actualMode = _diskTransferService.TransferFile(plan.SourcePath, plan.StagingPath, ToDiskTransferMode(plan.TransferMode));
                    });
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportSourceTransfer);
                    operation = _repository.UpdateActualTransferMode(operation.Id, operation.Version, owner, ToRecoverableTransferMode(actualMode));
                    plan = operation.Plan;
                    candidate = true;
                    operation = HeartbeatExactHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, out source);
                }
            }

            if ((plan.ActualTransferMode ?? plan.TransferMode) == RecoverableTransferMode.Move && source && (candidate || destination))
            {
                throw new IOException("Move recovery found an unexpected duplicate incoming file.");
            }

            operation = HeartbeatVerifySourceSemantics(operation, owner);
            if (!candidate && !destination)
            {
                throw new IOException("No exact incoming candidate or destination evidence exists.");
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportSourceToCandidate);
            var outgoing = plan.ExpectedOutgoingMovieFile;
            if (outgoing != null)
            {
                operation = HeartbeatExactHash(operation, owner, plan.Expected.Path, outgoing.Size, plan.OutgoingSha256, out var original);
                operation = HeartbeatExactHash(operation, owner, plan.FinalizePath, outgoing.Size, plan.OutgoingSha256, out var backup);
                if (original && backup)
                {
                    throw new IOException("Outgoing recovery found duplicate original and backup evidence.");
                }

                if (!backup)
                {
                    if (!original || _diskProvider.FileExists(plan.FinalizePath))
                    {
                        throw new IOException("Outgoing recovery cannot prove a safe backup transfer.");
                    }

                    operation = Run(operation, owner, () =>
                    {
                        RejectReparseAncestors(plan.Expected.Path, plan.FinalizePath);
                        _diskTransferService.TransferFile(plan.Expected.Path, plan.FinalizePath, TransferMode.Move);
                    });
                }

                if (!string.Equals(plan.Expected.Path, plan.DestinationPath, PathComparison) || !destination)
                {
                    RequireAbsent(plan.Expected.Path, "outgoing movie file");
                }

                operation = HeartbeatVerifyHash(operation, owner, plan.FinalizePath, outgoing.Size, plan.OutgoingSha256, "outgoing backup");
                _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportOutgoingToBackup);
            }

            operation = HeartbeatExactHash(operation, owner, plan.StagingPath, plan.ExpectedSize.Value, plan.IncomingSha256, out candidate);
            operation = HeartbeatExactHash(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, out destination);
            if (candidate && destination)
            {
                throw new IOException("Incoming recovery found duplicate candidate and destination evidence.");
            }

            if (!destination)
            {
                if (!candidate || _diskProvider.FileExists(plan.DestinationPath))
                {
                    throw new IOException("Destination recovery cannot prove a safe candidate transfer.");
                }

                operation = Run(operation, owner, () =>
                {
                    RejectReparseAncestors(plan.StagingPath, plan.DestinationPath);
                    _diskTransferService.TransferFile(plan.StagingPath, plan.DestinationPath, TransferMode.Move);
                });
                destinationOwned?.Invoke();
                _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportDestinationTransfer);
            }

            RequireAbsent(plan.StagingPath, "incoming candidate");
            operation = HeartbeatVerifyHash(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, "destination");
            operation = HeartbeatVerifySourceSemantics(operation, owner);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Staged, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportCandidateToDestination);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportStaged);
            return ApplyDatabase(operation, owner);
        }

        private RecoverableOperation ApplyDatabase(RecoverableOperation operation, string owner)
        {
            operation = ValidatePersistedEvidence(operation, owner, false);
            operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.ApplyingDatabase, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportApplyingDatabase);
            operation = _mutationStore.Apply(operation, owner);
            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportDatabaseCommit);
            return Finalize(operation, owner);
        }

        private RecoverableOperation Finalize(RecoverableOperation operation, string owner)
        {
            if (operation.ResultMovieFileId == null)
            {
                throw new RecoverableOperationValidationException("A committed import result id is required for finalization.");
            }

            if (operation.State == RecoverableOperationState.DatabaseCommitted)
            {
                operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Finalizing, owner);
            }
            else if (operation.State != RecoverableOperationState.Finalizing)
            {
                throw new RecoverableOperationValidationException("Only committed imports can be finalized.");
            }

            var outgoing = operation.Plan.ExpectedOutgoingMovieFile;
            if (outgoing != null)
            {
                var recycleClaim = Path.Combine(operation.StagingRoot, "recycle-" + Path.GetFileName(operation.Plan.FinalizePath));
                operation = HeartbeatExactHash(operation, owner, operation.Plan.FinalizePath, outgoing.Size, operation.Plan.OutgoingSha256, out var backup);
                operation = HeartbeatExactHash(operation, owner, recycleClaim, outgoing.Size, operation.Plan.OutgoingSha256, out var claim);
                if (backup && claim)
                {
                    throw new IOException("Finalization found duplicate backup and recycle-claim evidence.");
                }

                if (!backup && _diskProvider.FileExists(operation.Plan.FinalizePath) || !claim && _diskProvider.FileExists(recycleClaim))
                {
                    throw new IOException("Finalization found unexpected outgoing evidence and preserved it.");
                }

                if (backup)
                {
                    RejectReparseAncestors(operation.Plan.FinalizePath, recycleClaim);
                    operation = Run(operation, owner, () => _diskTransferService.TransferFile(operation.Plan.FinalizePath, recycleClaim, TransferMode.Move));
                    claim = true;
                }

                if (operation.Plan.ImportRecycleStarted && !operation.Plan.ImportRecycleCompleted)
                {
                    throw new RecoverableOperationValidationException("The outgoing recycle action may have begun without durable completion evidence.");
                }

                if (claim && !operation.Plan.ImportRecycleCompleted)
                {
                    operation = HeartbeatVerifyHash(operation, owner, recycleClaim, outgoing.Size, operation.Plan.OutgoingSha256, "claimed outgoing backup");
                    operation = _repository.UpdateImportRecycleEvidence(operation.Id, operation.Version, owner, false, null);
                    var recycleSubfolder = Path.GetFileName(NormalizeDirectory(operation.Plan.EventFacts["moviePath"]).TrimEnd(Path.DirectorySeparatorChar));
                    string recycleBinPath = null;
                    operation = Run(operation, owner, () => recycleBinPath = _recycleBinProvider.DeleteFile(recycleClaim, recycleSubfolder));
                    RequireAbsent(recycleClaim, "recycle claim");
                    operation = _repository.UpdateImportRecycleEvidence(operation.Id, operation.Version, owner, true, recycleBinPath);
                    claim = false;
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportRecycleAction);
                }

                if (operation.Plan.ImportRecycleCompleted && claim)
                {
                    throw new IOException("Finalization found a recycle claim after durable recycle completion.");
                }
            }

            _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportRecycle);
            RequireAbsent(operation.Plan.StagingPath, "incoming candidate");
            if (_diskProvider.FolderExists(operation.StagingRoot))
            {
                operation = Run(operation, owner, () => _diskProvider.DeleteFolder(operation.StagingRoot, false));
            }

            return _completionService.Complete(operation, owner);
        }

        private RecoverableOperationCreateRequest BuildRequest(MovieFile desired,
                                                                LocalMovie localMovie,
                                                                TransferMode transferMode,
                                                                MovieFile outgoing,
                                                                int expectedMainMovieFileId,
                                                                MovieFileImportTarget target,
                                                                DownloadClientItem downloadClientItem)
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
                    EventFacts = new Dictionary<string, string> { ["moviePath"] = moviePath },
                    MovieEvent = SnapshotMovie(localMovie.Movie),
                    ImportEvent = SnapshotImportEvent(localMovie,
                                                      desired,
                                                      target,
                                                      downloadClientItem,
                                                      plannedMode != RecoverableTransferMode.Move,
                                                      !_diskProvider.FolderExists(moviePath),
                                                      !Path.GetDirectoryName(destinationPath).PathEquals(moviePath) && !_diskProvider.FolderExists(Path.GetDirectoryName(destinationPath))
                                                          ? Path.GetDirectoryName(destinationPath)
                                                          : null)
                }
            };
        }

        private void ValidateRecoveryPlan(RecoverableOperation operation)
        {
            var plan = operation.Plan;
            if (plan?.Expected == null || plan.Desired == null || plan.DesiredIncomingMovieFile == null || plan.ExpectedSize < 0 ||
                string.IsNullOrWhiteSpace(plan.IncomingSha256) || plan.IncomingSha256.Length != 64 ||
                plan.TransferMode is not RecoverableTransferMode.Move and not RecoverableTransferMode.Copy and not RecoverableTransferMode.HardLink ||
                plan.ActualTransferMode is RecoverableTransferMode.None or RecoverableTransferMode.Reflink ||
                plan.EventFacts == null || !plan.EventFacts.TryGetValue("moviePath", out var moviePath))
            {
                throw new RecoverableOperationValidationException("The durable import recovery plan is incomplete or unsupported.");
            }

            var expectedRoot = RecoverableOperationStagingPolicy.GetPaths(moviePath, operation.OperationKey).Root;
            if (!string.Equals(CanonicalDirectory(operation.StagingRoot, "staging root"), CanonicalDirectory(expectedRoot, "expected staging root"), PathComparison) ||
                !Contained(CanonicalPath(plan.StagingPath, "candidate path"), CanonicalDirectory(expectedRoot, "expected staging root")) ||
                !Contained(CanonicalPath(plan.FinalizePath, "finalize path"), CanonicalDirectory(expectedRoot, "expected staging root")) ||
                !Contained(CanonicalPath(plan.DestinationPath, "destination path"), CanonicalDirectory(moviePath, "movie path")))
            {
                throw new RecoverableOperationValidationException("The durable import paths violate the operation-private recovery trust boundary.");
            }

            RejectReparseAncestors(moviePath, plan.SourcePath, plan.DestinationPath, plan.Expected.Path, operation.StagingRoot, plan.StagingPath, plan.FinalizePath);
        }

        private RecoverableOperation ValidatePersistedEvidence(RecoverableOperation operation, string owner, bool beforeFilesystemMutation)
        {
            var plan = operation.Plan;
            RejectReparseAncestors(plan.EventFacts["moviePath"], plan.SourcePath, plan.DestinationPath, plan.Expected.Path, operation.StagingRoot, plan.StagingPath, plan.FinalizePath);
            ValidateDatabaseEvidence(operation.MovieId, plan.EventFacts["moviePath"], plan.ExpectedMovieFileId, plan.ImportTarget, plan.DesiredIncomingMovieFile.MovieEditionSlotId, plan.ExpectedOutgoingMovieFile);
            if (beforeFilesystemMutation)
            {
                operation = HeartbeatVerifyHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "source");
            }
            else
            {
                operation = HeartbeatVerifySourceSemantics(operation, owner);
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
                operation = HeartbeatVerifyHash(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, "destination");
                RequireAbsent(plan.StagingPath, "incoming candidate");
                if (plan.ExpectedOutgoingMovieFile != null)
                {
                    if (!string.Equals(plan.Expected.Path, plan.DestinationPath, PathComparison))
                    {
                        RequireAbsent(plan.Expected.Path, "outgoing movie file");
                    }

                    operation = HeartbeatVerifyHash(operation, owner, plan.FinalizePath, plan.ExpectedOutgoingMovieFile.Size, plan.OutgoingSha256, "outgoing backup");
                }
            }

            return operation;
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

        private void RollBackBeforeCommit(RecoverableOperation operation, string owner, Exception cause, bool destinationOwnedHint = false)
        {
            try
            {
                if (IsDatabaseCommitted(operation))
                {
                    return;
                }

                if (operation.State != RecoverableOperationState.RollingBack)
                {
                    var destinationOwned = destinationOwnedHint || operation.State is RecoverableOperationState.Staged or RecoverableOperationState.ApplyingDatabase;
                    operation = _repository.BeginImportRollback(operation.Id, operation.State, operation.Version, owner, destinationOwned);
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportRollbackBegin);
                }

                if (operation.Plan.RollbackDestinationOwned == null)
                {
                    throw new IOException("Rollback destination ownership evidence is missing from the durable import plan.");
                }

                var plan = operation.Plan;
                var destinationOwnedByOperation = plan.RollbackDestinationOwned.Value;
                operation = HeartbeatExactHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, out var sourceExists);
                operation = HeartbeatExactHash(operation, owner, plan.StagingPath, plan.ExpectedSize.Value, plan.IncomingSha256, out var candidateExists);
                var destinationExists = _diskProvider.FileExists(plan.DestinationPath);
                operation = HeartbeatExactHash(operation, owner, plan.DestinationPath, plan.ExpectedSize.Value, plan.IncomingSha256, out var exactIncomingDestination);
                var destinationIncoming = destinationOwnedByOperation && exactIncomingDestination &&
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
                        operation = HeartbeatVerifyHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "restored source");
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
                    operation = HeartbeatExactHash(operation, owner, plan.Expected.Path, outgoing.Size, plan.OutgoingSha256, out var originalExists);
                    operation = HeartbeatExactHash(operation, owner, plan.FinalizePath, outgoing.Size, plan.OutgoingSha256, out var backupExists);
                    if (originalExists && backupExists || !originalExists && !backupExists)
                    {
                        throw new IOException("Rollback could not prove exactly one outgoing movie-file copy.");
                    }

                    if (backupExists)
                    {
                        operation = Run(operation, owner, () => _diskTransferService.TransferFile(plan.FinalizePath, plan.Expected.Path, TransferMode.Move));
                    }

                    operation = HeartbeatVerifyHash(operation, owner, plan.Expected.Path, outgoing.Size, plan.OutgoingSha256, "restored outgoing movie file");
                    RequireAbsent(plan.FinalizePath, "outgoing backup");
                }
                else
                {
                    RequireAbsent(plan.DestinationPath, "destination");
                }

                operation = HeartbeatVerifyHash(operation, owner, plan.SourcePath, plan.ExpectedSize.Value, plan.IncomingSha256, "restored source");
                if (unexpectedDestination)
                {
                    throw new IOException("Rollback found an unexpected destination file and preserved it.");
                }

                RequireAbsent(plan.StagingPath, "incoming candidate");
                CleanupStaging(operation, owner);
                operation = _repository.GetById(operation.Id);
                _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.RolledBack, owner);
            }
            catch (Exception rollbackException) when (rollbackException is not RecoverableOperationConcurrencyException and not RecoverableOperationProcessDeathException and not RecoverableOperationLeaseHeartbeatException)
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
                operation = HeartbeatVerifyHash(operation, owner, claim, size, sha256, "claimed coordinator-created copy");
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
            if (operation.State == RecoverableOperationState.Completed)
            {
                return Result(operation, outgoing, false);
            }

            operation = RecordPostCommitFailure(operation, owner, error);
            return Result(operation, outgoing, true);
        }

        private RecoverableOperation RecordPostCommitFailure(RecoverableOperation operation, string owner, string error)
        {
            const RecoverableOperationEventDispatchMask importInProgress =
                RecoverableOperationEventDispatchMask.ImportMovieFileDeletedInProgress |
                RecoverableOperationEventDispatchMask.ImportMovieFileAddedInProgress |
                RecoverableOperationEventDispatchMask.MovieFileImportedInProgress;
            if ((((RecoverableOperationEventDispatchMask)operation.EventDispatchMask) & importInProgress) != 0 ||
                operation.Plan?.ImportRecycleStarted == true && operation.Plan.ImportRecycleCompleted == false)
            {
                return MarkRecoveryRequiredSafely(operation, owner, error);
            }

            try
            {
                return _repository.RecordErrorAttempt(operation.Id, operation.Version, error, DateTime.UtcNow, owner);
            }
            catch (RecoverableOperationException)
            {
                return _repository.GetById(operation.Id);
            }
        }

        private RecoverableMovieFileImportResult Result(RecoverableOperation operation, MovieFile outgoing, bool finalizationPending)
        {
            using var connection = _database.OpenConnection();
            var imported = connection.QuerySingle<MovieFile>(@"SELECT * FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = operation.ResultMovieFileId });
            imported.Path = operation.Plan.DestinationPath;
            imported.ImportTarget = operation.Plan.ImportTarget;
            imported.Movie = RehydrateResultMovie(operation.Plan.MovieEvent, operation.ResultMovieFileId.Value, operation.Plan.ImportTarget);
            return new RecoverableMovieFileImportResult { ImportedMovieFile = imported, OutgoingMovieFile = outgoing, Operation = operation, IsImported = true, FinalizationPending = finalizationPending };
        }

        private static Movie RehydrateResultMovie(RecoverableMovieEventSnapshot snapshot, int resultMovieFileId, MovieFileImportTarget target)
        {
            var movie = new Movie
            {
                Id = snapshot.Id,
                MovieMetadataId = snapshot.MovieMetadataId,
                Monitored = snapshot.Monitored,
                MinimumAvailability = snapshot.MinimumAvailability,
                QualityProfileId = snapshot.QualityProfileId,
                Path = snapshot.Path,
                RootFolderPath = snapshot.RootFolderPath,
                Added = snapshot.Added,
                AddOptions = snapshot.AddOptions,
                LastSearchTime = snapshot.LastSearchTime,
                MovieFileId = target == MovieFileImportTarget.Main ? resultMovieFileId : snapshot.MovieFileId,
                Tags = new HashSet<int>(snapshot.Tags ?? new HashSet<int>())
            };
            movie.MovieMetadata.Value.Title = snapshot.Title;
            movie.MovieMetadata.Value.Year = snapshot.Year;
            movie.MovieMetadata.Value.TmdbId = snapshot.TmdbId;
            movie.MovieMetadata.Value.ImdbId = snapshot.ImdbId;
            return movie;
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

        private RecoverableOperation HeartbeatVerifySourceSemantics(RecoverableOperation operation, string owner)
        {
            return Run(operation, owner, () => VerifySourceSemantics(operation));
        }

        private RecoverableOperation HeartbeatVerifyHash(RecoverableOperation operation, string owner, string path, long size, string expectedSha256, string label)
        {
            return Run(operation, owner, () => VerifyHash(path, size, expectedSha256, label));
        }

        private RecoverableOperation HeartbeatExactHash(RecoverableOperation operation, string owner, string path, long size, string expectedSha256, out bool exact)
        {
            var result = false;
            operation = Run(operation, owner, () => result = ExactHash(path, size, expectedSha256));
            exact = result;
            return operation;
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

        private static RecoverableMovieEventSnapshot SnapshotMovie(Movie movie)
        {
            return new RecoverableMovieEventSnapshot
            {
                Id = movie.Id,
                MovieMetadataId = movie.MovieMetadataId,
                Monitored = movie.Monitored,
                MinimumAvailability = movie.MinimumAvailability,
                QualityProfileId = movie.QualityProfileId,
                Path = Path.GetFullPath(movie.Path),
                RootFolderPath = movie.RootFolderPath,
                Added = movie.Added,
                AddOptions = movie.AddOptions,
                LastSearchTime = movie.LastSearchTime,
                MovieFileId = movie.MovieFileId,
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
            };
        }

        private static RecoverableMovieFileImportEventSnapshot SnapshotImportEvent(LocalMovie localMovie,
                                                                                    MovieFile desired,
                                                                                    MovieFileImportTarget target,
                                                                                    DownloadClientItem downloadClientItem,
                                                                                    bool copyOnly,
                                                                                    bool movieFolderCreated,
                                                                                    string movieFileFolderCreated)
        {
            var acquisitionTarget = target switch
            {
                MovieFileImportTarget.Main => MovieAcquisitionTarget.Main,
                MovieFileImportTarget.EditionSlot => MovieAcquisitionTarget.ForEditionSlot(desired.MovieEditionSlotId.Value),
                _ => MovieAcquisitionTarget.Unknown
            };
            var customFormats = new List<RecoverableCustomFormatSnapshot>();
            foreach (var format in localMovie.CustomFormats ?? new List<NzbDrone.Core.CustomFormats.CustomFormat>())
            {
                customFormats.Add(new RecoverableCustomFormatSnapshot
                {
                    Id = format.Id,
                    Name = format.Name,
                    IncludeCustomFormatWhenRenaming = format.IncludeCustomFormatWhenRenaming
                });
            }

            RecoverableGrabbedReleaseSnapshot release = null;
            if (localMovie.Release != null)
            {
                release = new RecoverableGrabbedReleaseSnapshot
                {
                    Title = localMovie.Release.Title,
                    Indexer = localMovie.Release.Indexer,
                    Size = localMovie.Release.Size,
                    IndexerFlags = localMovie.Release.IndexerFlags,
                    MovieIds = new List<int>(localMovie.Release.MovieIds ?? new List<int>()),
                    AcquisitionTarget = localMovie.Release.AcquisitionTarget
                };
            }

            RecoverableDownloadClientItemSnapshot download = null;
            if (downloadClientItem != null)
            {
                download = new RecoverableDownloadClientItemSnapshot
                {
                    DownloadId = downloadClientItem.DownloadId,
                    Protocol = downloadClientItem.DownloadClientInfo?.Protocol ?? default,
                    Type = downloadClientItem.DownloadClientInfo?.Type,
                    Id = downloadClientItem.DownloadClientInfo?.Id ?? 0,
                    Name = downloadClientItem.DownloadClientInfo?.Name,
                    RemoveCompletedDownloads = downloadClientItem.DownloadClientInfo?.RemoveCompletedDownloads ?? false,
                    HasPostImportCategory = downloadClientItem.DownloadClientInfo?.HasPostImportCategory ?? false
                };
            }

            return new RecoverableMovieFileImportEventSnapshot
            {
                SourcePath = Path.GetFullPath(localMovie.Path),
                Size = localMovie.Size,
                FileMovieInfo = localMovie.FileMovieInfo,
                FolderMovieInfo = localMovie.FolderMovieInfo,
                Quality = localMovie.Quality ?? desired.Quality,
                Languages = new List<NzbDrone.Core.Languages.Language>(localMovie.Languages ?? desired.Languages),
                MediaInfo = localMovie.MediaInfo ?? desired.MediaInfo,
                IndexerFlags = localMovie.IndexerFlags != default ? localMovie.IndexerFlags : desired.IndexerFlags,
                ExistingFile = localMovie.ExistingFile,
                SceneSource = localMovie.SceneSource,
                ReleaseGroup = localMovie.ReleaseGroup ?? desired.ReleaseGroup,
                Edition = localMovie.Edition ?? desired.Edition,
                SceneName = localMovie.SceneName ?? desired.SceneName,
                OtherVideoFiles = localMovie.OtherVideoFiles,
                CustomFormats = customFormats,
                CustomFormatScore = localMovie.CustomFormatScore,
                ImportTarget = target,
                AcquisitionTarget = acquisitionTarget,
                HasExactTargetContext = localMovie.HasExactTargetContext,
                Release = release,
                ScriptImported = localMovie.ScriptImported,
                ShouldImportExtras = localMovie.ShouldImportExtras,
                PossibleExtraFiles = new List<string>(localMovie.PossibleExtraFiles ?? new List<string>()),
                DownloadClientItem = download,
                CopyOnly = copyOnly,
                MovieFolderCreated = movieFolderCreated,
                MovieFileFolderCreated = movieFileFolderCreated
            };
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
