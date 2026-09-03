using System;
using System.Collections.Generic;
using System.IO;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using Newtonsoft.Json;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableMovieFileImportCompletionService
    {
        RecoverableOperation Complete(RecoverableOperation operation, string owner);
    }

    public sealed class RecoverableMovieFileImportCompletionService : IRecoverableMovieFileImportCompletionService
    {
        private const RecoverableOperationEventDispatchMask UncertainMask =
            RecoverableOperationEventDispatchMask.ImportMovieFileDeletedInProgress |
            RecoverableOperationEventDispatchMask.ImportMovieFileAddedInProgress |
            RecoverableOperationEventDispatchMask.MovieFileImportedInProgress |
            RecoverableOperationEventDispatchMask.ImportFolderCreatedInProgress |
            RecoverableOperationEventDispatchMask.ImportFileAttributesInProgress |
            RecoverableOperationEventDispatchMask.ImportExtrasInProgress;

        private readonly IRecoverableOperationRepository _repository;
        private readonly IMainDatabase _database;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRecoverableOperationFaultInjector _faultInjector;
        private readonly IMovieService _movieService;
        private readonly IExtraService _extraService;
        private readonly IUpdateMovieFileService _updateMovieFileService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly Logger _logger;

        public RecoverableMovieFileImportCompletionService(IRecoverableOperationRepository repository,
                                                            IMainDatabase database,
                                                            IEventAggregator eventAggregator,
                                                            IRecoverableOperationFaultInjector faultInjector,
                                                            IMovieService movieService,
                                                            IExtraService extraService,
                                                            IUpdateMovieFileService updateMovieFileService,
                                                            IMediaFileAttributeService mediaFileAttributeService,
                                                            Logger logger)
        {
            _repository = repository;
            _database = database;
            _eventAggregator = eventAggregator;
            _faultInjector = faultInjector;
            _movieService = movieService;
            _extraService = extraService;
            _updateMovieFileService = updateMovieFileService;
            _mediaFileAttributeService = mediaFileAttributeService;
            _logger = logger;
        }

        public RecoverableOperation Complete(RecoverableOperation operation, string owner)
        {
            if (operation == null || operation.OperationType != RecoverableOperationType.Import)
            {
                throw new RecoverableOperationValidationException("Import completion only accepts Import operations.");
            }

            var suppliedVersion = operation.Version;
            operation = _repository.GetById(operation.Id);
            if (operation.State is RecoverableOperationState.Completed or RecoverableOperationState.RolledBack or RecoverableOperationState.RecoveryRequired)
            {
                return operation;
            }

            if (operation.Version != suppliedVersion)
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }

            RequireLiveLease(operation, owner);
            if (operation.State == RecoverableOperationState.DatabaseCommitted)
            {
                operation = _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Finalizing, owner);
            }
            else if (operation.State != RecoverableOperationState.Finalizing)
            {
                throw new RecoverableOperationValidationException("Only database-committed imports can dispatch compatibility events.");
            }

            if (HasMask(operation, UncertainMask))
            {
                return _repository.MarkRecoveryRequired(operation.Id,
                                                        operation.State,
                                                        operation.Version,
                                                        "An import compatibility event dispatch was interrupted after handlers may have begun; it will not be replayed automatically.",
                                                        owner);
            }

            var payload = Rehydrate(operation);
            try
            {
                if ((operation.Plan.ImportEvent.MovieFolderCreated || operation.Plan.ImportEvent.MovieFileFolderCreated != null) &&
                    !HasMask(operation, RecoverableOperationEventDispatchMask.ImportFolderCreated))
                {
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportFolderCreated, owner);
                    var folderEvent = new MovieFolderCreatedEvent(payload.LocalMovie.Movie, payload.Incoming)
                    {
                        MovieFolder = operation.Plan.ImportEvent.MovieFolderCreated ? payload.LocalMovie.Movie.Path : null,
                        MovieFileFolder = operation.Plan.ImportEvent.MovieFileFolderCreated
                    };
                    _eventAggregator.PublishEventStrict(folderEvent);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportFolderCreated, owner);
                }

                if (!HasMask(operation, RecoverableOperationEventDispatchMask.ImportFileAttributes))
                {
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportFileAttributes, owner);
                    _updateMovieFileService.ChangeFileDateForFile(payload.Incoming, payload.LocalMovie.Movie);
                    try
                    {
                        _mediaFileAttributeService.SetFolderLastWriteTime(payload.LocalMovie.Movie.Path, payload.Incoming.DateAdded);
                    }
                    catch (Exception exception)
                    {
                        _logger.Warn(exception, "Unable to set last write time");
                    }

                    _mediaFileAttributeService.SetFilePermissions(payload.Incoming.Path);
                    if (operation.Plan.ImportEvent.MovieFolderCreated)
                    {
                        _mediaFileAttributeService.SetFolderPermissions(payload.LocalMovie.Movie.Path);
                    }

                    if (operation.Plan.ImportEvent.MovieFileFolderCreated != null)
                    {
                        _mediaFileAttributeService.SetFolderPermissions(operation.Plan.ImportEvent.MovieFileFolderCreated);
                    }

                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportFileAttributes, owner);
                }

                if (payload.Outgoing != null && !HasMask(operation, RecoverableOperationEventDispatchMask.ImportMovieFileDeleted))
                {
                    _faultInjector.Check(RecoverableOperationFaultPoint.BeforeImportMovieFileDeletedDispatch);
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportMovieFileDeleted, owner);
                    _eventAggregator.PublishEventStrict(new MovieFileDeletedEvent(payload.Outgoing, DeleteMediaFileReason.Upgrade));
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportMovieFileDeletedPublish);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportMovieFileDeleted, owner);
                }

                if (!HasMask(operation, RecoverableOperationEventDispatchMask.ImportMovieFileAdded))
                {
                    _faultInjector.Check(RecoverableOperationFaultPoint.BeforeImportMovieFileAddedDispatch);
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportMovieFileAdded, owner);
                    _eventAggregator.PublishEventStrict(new MovieFileAddedEvent(payload.Incoming));
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterImportMovieFileAddedPublish);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportMovieFileAdded, owner);
                }

                if (!HasMask(operation, RecoverableOperationEventDispatchMask.ImportExtras))
                {
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportExtras, owner);
                    _extraService.ImportMovie(payload.LocalMovie, payload.Incoming, operation.Plan.ImportEvent.CopyOnly);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.ImportExtras, owner);
                }

                if (!HasMask(operation, RecoverableOperationEventDispatchMask.MovieFileImported))
                {
                    _faultInjector.Check(RecoverableOperationFaultPoint.BeforeMovieFileImportedDispatch);
                    operation = _repository.BeginEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.MovieFileImported, owner);
                    var oldFiles = payload.Outgoing == null ? new List<DeletedMovieFile>() : new List<DeletedMovieFile> { new(payload.Outgoing, operation.Plan.ImportRecycleBinPath) };
                    _eventAggregator.PublishEventStrict(new MovieFileImportedEvent(payload.LocalMovie, payload.Incoming, oldFiles, true, payload.DownloadClientItem));
                    _faultInjector.Check(RecoverableOperationFaultPoint.AfterMovieFileImportedPublish);
                    operation = _repository.CompleteEventDispatch(operation.Id, operation.Version, RecoverableOperationEventDispatchMask.MovieFileImported, owner);
                }
            }
            catch (RecoverableOperationProcessDeathException)
            {
                throw;
            }
            catch
            {
                var current = _repository.GetById(operation.Id);
                if (HasMask(current, UncertainMask) && current.State == RecoverableOperationState.Finalizing)
                {
                    _repository.MarkRecoveryRequired(current.Id,
                                                     current.State,
                                                     current.Version,
                                                     "Import compatibility event dispatch failed after handlers may have begun; it will not be replayed automatically.",
                                                     owner);
                }

                throw;
            }

            var required = RecoverableOperationEventDispatchMask.ImportMovieFileAdded |
                           RecoverableOperationEventDispatchMask.ImportFileAttributes |
                           RecoverableOperationEventDispatchMask.ImportExtras |
                           RecoverableOperationEventDispatchMask.MovieFileImported;
            if (operation.Plan.ImportEvent.MovieFolderCreated || operation.Plan.ImportEvent.MovieFileFolderCreated != null)
            {
                required |= RecoverableOperationEventDispatchMask.ImportFolderCreated;
            }
            if (payload.Outgoing != null)
            {
                required |= RecoverableOperationEventDispatchMask.ImportMovieFileDeleted;
            }

            if ((((RecoverableOperationEventDispatchMask)operation.EventDispatchMask) & required) != required || HasMask(operation, UncertainMask))
            {
                throw new RecoverableOperationValidationException("All required import compatibility events must be durably final before completion.");
            }

            return _repository.Transition(operation.Id, operation.State, operation.Version, RecoverableOperationState.Completed, owner);
        }

        private Payload Rehydrate(RecoverableOperation operation)
        {
            var plan = operation.Plan;
            var context = plan?.ImportEvent;
            var movieSnapshot = plan?.MovieEvent;
            var desired = plan?.DesiredIncomingMovieFile;
            if (!operation.ResultMovieFileId.HasValue || operation.ResultMovieFileId.Value <= 0 || context == null || movieSnapshot == null || desired == null ||
                plan.Expected == null || plan.Desired == null || context.Quality == null || context.Languages == null || context.MediaInfo == null ||
                string.IsNullOrWhiteSpace(context.SourcePath) || !Path.IsPathRooted(context.SourcePath) ||
                string.IsNullOrWhiteSpace(plan.SourcePath) || !Path.IsPathRooted(plan.SourcePath) ||
                context.ImportTarget != plan.ImportTarget || context.AcquisitionTarget == null || context.Size != desired.Size || plan.ExpectedSize != desired.Size ||
                !Path.GetFullPath(context.SourcePath).PathEquals(Path.GetFullPath(plan.SourcePath)))
            {
                throw new RecoverableOperationValidationException("Durable import compatibility-event evidence is incomplete.");
            }

            var expectedTarget = plan.ImportTarget switch
            {
                MovieFileImportTarget.Main => MovieAcquisitionTarget.Main,
                MovieFileImportTarget.EditionSlot when desired.MovieEditionSlotId > 0 => MovieAcquisitionTarget.ForEditionSlot(desired.MovieEditionSlotId.Value),
                MovieFileImportTarget.Unassigned => MovieAcquisitionTarget.Unknown,
                _ => throw new RecoverableOperationValidationException("The durable import target is invalid.")
            };
            if (!context.AcquisitionTarget.Equals(expectedTarget))
            {
                throw new RecoverableOperationValidationException("The durable import acquisition target and slot are inconsistent.");
            }

            using var connection = _database.OpenConnection();
            var persisted = connection.QuerySingleOrDefault<MovieFile>(@"SELECT * FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = operation.ResultMovieFileId });
            if (!SameIdentity(persisted, desired, operation.ResultMovieFileId.Value))
            {
                throw new RecoverableOperationValidationException("The persisted imported movie-file row does not match its durable snapshot.");
            }

            var movie = _movieService.GetMovie(operation.MovieId);
            if (movie.Id != operation.MovieId || !IsUnderMovieRoot(movieSnapshot.Path, plan.DestinationPath) ||
                string.IsNullOrWhiteSpace(desired.RelativePath) || Path.IsPathRooted(desired.RelativePath) ||
                string.IsNullOrWhiteSpace(plan.Desired.DestinationPath) || !Path.IsPathRooted(plan.Desired.DestinationPath) ||
                !Path.GetFullPath(desired.RelativePath, movie.Path).PathEquals(Path.GetFullPath(plan.DestinationPath)) ||
                !Path.GetFullPath(plan.Desired.DestinationPath).PathEquals(Path.GetFullPath(plan.DestinationPath)) ||
                plan.ImportTarget == MovieFileImportTarget.Main && movie.MovieFileId != operation.ResultMovieFileId.Value)
            {
                throw new RecoverableOperationValidationException("The durable movie snapshot and destination are inconsistent.");
            }

            ValidateTarget(operation, desired);
            var incoming = RehydrateFile(desired, operation.ResultMovieFileId.Value, movie, plan.DestinationPath, plan.ImportTarget);
            MovieFile outgoing = null;
            if (plan.ExpectedOutgoingMovieFile != null)
            {
                var old = plan.ExpectedOutgoingMovieFile;
                if (old.Id <= 0 || old.MovieId != operation.MovieId || old.Id != operation.MovieFileId ||
                    old.MovieEditionSlotId != desired.MovieEditionSlotId || string.IsNullOrWhiteSpace(old.RelativePath) ||
                    string.IsNullOrWhiteSpace(plan.Expected?.Path) ||
                    !Path.GetFullPath(plan.Expected.Path).PathEquals(Path.GetFullPath(old.RelativePath, movie.Path)))
                {
                    throw new RecoverableOperationValidationException("The durable outgoing movie-file snapshot is invalid.");
                }

                outgoing = RehydrateFile(old, old.Id, movie, plan.Expected.Path, plan.ImportTarget);
            }

            var localMovie = new LocalMovie
            {
                Path = context.SourcePath,
                Size = context.Size,
                FileMovieInfo = context.FileMovieInfo,
                FolderMovieInfo = context.FolderMovieInfo,
                Movie = movie,
                Quality = context.Quality,
                Languages = new List<NzbDrone.Core.Languages.Language>(context.Languages),
                MediaInfo = context.MediaInfo,
                IndexerFlags = context.IndexerFlags,
                ExistingFile = context.ExistingFile,
                SceneSource = context.SceneSource,
                ReleaseGroup = context.ReleaseGroup,
                Edition = context.Edition,
                SceneName = context.SceneName,
                OtherVideoFiles = context.OtherVideoFiles,
                CustomFormats = RehydrateCustomFormats(context.CustomFormats),
                CustomFormatScore = context.CustomFormatScore,
                ImportTarget = context.ImportTarget,
                AcquisitionTarget = context.AcquisitionTarget,
                HasExactTargetContext = context.HasExactTargetContext,
                ScriptImported = context.ScriptImported,
                ShouldImportExtras = context.ShouldImportExtras,
                PossibleExtraFiles = new List<string>(context.PossibleExtraFiles ?? new List<string>())
            };
            localMovie.Release = RehydrateRelease(context.Release);
            localMovie.OldFiles = outgoing == null ? new List<DeletedMovieFile>() : new List<DeletedMovieFile> { new(outgoing, operation.Plan.ImportRecycleBinPath) };

            return new Payload(incoming, outgoing, localMovie, RehydrateDownloadClientItem(context.DownloadClientItem));
        }

        private static Movie RehydrateMovie(RecoverableMovieEventSnapshot snapshot)
        {
            if (snapshot.Id <= 0 || string.IsNullOrWhiteSpace(snapshot.Path) || !Path.IsPathRooted(snapshot.Path))
            {
                throw new RecoverableOperationValidationException("The durable movie event snapshot is invalid.");
            }

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
                MovieFileId = snapshot.MovieFileId,
                Tags = new HashSet<int>(snapshot.Tags ?? new HashSet<int>()),
                Title = snapshot.Title,
                Year = snapshot.Year,
                TmdbId = snapshot.TmdbId,
                ImdbId = snapshot.ImdbId
            };
            movie.MovieMetadata.Value.InCinemas = snapshot.InCinemas;
            movie.MovieMetadata.Value.PhysicalRelease = snapshot.PhysicalRelease;
            movie.MovieMetadata.Value.DigitalRelease = snapshot.DigitalRelease;
            movie.MovieMetadata.Value.Overview = snapshot.Overview;
            movie.MovieMetadata.Value.Genres = new List<string>(snapshot.Genres ?? new List<string>());
            movie.MovieMetadata.Value.Images = new List<NzbDrone.Core.MediaCover.MediaCover>(snapshot.Images ?? new List<NzbDrone.Core.MediaCover.MediaCover>());
            movie.MovieMetadata.Value.OriginalLanguage = snapshot.OriginalLanguage;
            return movie;
        }

        private static MovieFile RehydrateFile(RecoverableMovieFileRowSnapshot snapshot, int id, Movie movie, string path, MovieFileImportTarget target)
        {
            return new MovieFile
            {
                Id = id,
                MovieId = snapshot.MovieId,
                MovieEditionSlotId = snapshot.MovieEditionSlotId,
                ImportTarget = target,
                RelativePath = snapshot.RelativePath,
                Path = path,
                Size = snapshot.Size,
                Quality = snapshot.Quality,
                Languages = new List<NzbDrone.Core.Languages.Language>(snapshot.Languages ?? new List<NzbDrone.Core.Languages.Language>()),
                DateAdded = snapshot.DateAdded,
                SceneName = snapshot.SceneName,
                ReleaseGroup = snapshot.ReleaseGroup,
                MediaInfo = snapshot.MediaInfo,
                OriginalFilePath = snapshot.OriginalFilePath,
                IndexerFlags = snapshot.IndexerFlags,
                Edition = snapshot.Edition,
                Movie = movie
            };
        }

        private static List<CustomFormat> RehydrateCustomFormats(List<RecoverableCustomFormatSnapshot> snapshots)
        {
            var result = new List<CustomFormat>();
            foreach (var snapshot in snapshots ?? new List<RecoverableCustomFormatSnapshot>())
            {
                result.Add(new CustomFormat
                {
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    IncludeCustomFormatWhenRenaming = snapshot.IncludeCustomFormatWhenRenaming,
                    Specifications = new List<ICustomFormatSpecification>()
                });
            }

            return result;
        }

        private static GrabbedReleaseInfo RehydrateRelease(RecoverableGrabbedReleaseSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            var history = new DownloadHistory
            {
                MovieId = snapshot.MovieIds?.Count > 0 ? snapshot.MovieIds[0] : 0,
                SourceTitle = snapshot.Title,
                Release = new ReleaseInfo { Title = snapshot.Title, Indexer = snapshot.Indexer, Size = snapshot.Size, IndexerFlags = snapshot.IndexerFlags }
            };
            MovieAcquisitionTargetSerializer.Write(history.Data, snapshot.AcquisitionTarget ?? MovieAcquisitionTarget.Unknown);
            return new GrabbedReleaseInfo(history);
        }

        private static DownloadClientItem RehydrateDownloadClientItem(RecoverableDownloadClientItemSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            return new DownloadClientItem
            {
                DownloadId = snapshot.DownloadId,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = snapshot.Protocol,
                    Type = snapshot.Type,
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    RemoveCompletedDownloads = snapshot.RemoveCompletedDownloads,
                    HasPostImportCategory = snapshot.HasPostImportCategory
                }
            };
        }

        private static bool HasMask(RecoverableOperation operation, RecoverableOperationEventDispatchMask mask)
        {
            return (((RecoverableOperationEventDispatchMask)operation.EventDispatchMask) & mask) != 0;
        }

        private static void ValidateTarget(RecoverableOperation operation, RecoverableMovieFileRowSnapshot desired)
        {
            var plan = operation.Plan;
            var outgoing = plan.ExpectedOutgoingMovieFile;
            var valid = plan.ImportTarget switch
            {
                MovieFileImportTarget.Main => operation.MovieEditionSlotId == null && desired.MovieEditionSlotId == null &&
                                              plan.Desired.MovieEditionSlotId == null &&
                                              (outgoing == null
                                                  ? operation.MovieFileId == null && plan.ExpectedMovieFileId == 0
                                                  : operation.MovieFileId == outgoing.Id && plan.ExpectedMovieFileId == outgoing.Id && outgoing.MovieEditionSlotId == null),
                MovieFileImportTarget.EditionSlot => operation.MovieEditionSlotId > 0 && desired.MovieEditionSlotId == operation.MovieEditionSlotId &&
                                                     plan.Desired.MovieEditionSlotId == operation.MovieEditionSlotId &&
                                                     (outgoing == null
                                                         ? operation.MovieFileId == null
                                                         : operation.MovieFileId == outgoing.Id && outgoing.MovieEditionSlotId == operation.MovieEditionSlotId),
                MovieFileImportTarget.Unassigned => operation.MovieEditionSlotId == null && operation.MovieFileId == null &&
                                                    desired.MovieEditionSlotId == null && plan.Desired.MovieEditionSlotId == null && outgoing == null,
                _ => false
            };
            if (!valid)
            {
                throw new RecoverableOperationValidationException("The durable import target and outgoing snapshot are inconsistent.");
            }
        }

        private static bool IsUnderMovieRoot(string moviePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(moviePath) || string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathRooted(moviePath) || !Path.IsPathRooted(destinationPath))
            {
                return false;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(moviePath));
            var destination = Path.GetFullPath(destinationPath);
            var relative = Path.GetRelativePath(root, destination);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static bool SameIdentity(MovieFile actual, RecoverableMovieFileRowSnapshot expected, int expectedId)
        {
            return actual != null && expected != null && actual.Id == expectedId && actual.MovieId == expected.MovieId &&
                   actual.MovieEditionSlotId == expected.MovieEditionSlotId && string.Equals(actual.RelativePath, expected.RelativePath, StringComparison.Ordinal) &&
                   actual.Size == expected.Size && actual.DateAdded == expected.DateAdded && string.Equals(actual.SceneName, expected.SceneName, StringComparison.Ordinal) &&
                   string.Equals(actual.ReleaseGroup, expected.ReleaseGroup, StringComparison.Ordinal) && string.Equals(actual.OriginalFilePath, expected.OriginalFilePath, StringComparison.Ordinal) &&
                   actual.IndexerFlags == expected.IndexerFlags && string.Equals(actual.Edition, expected.Edition, StringComparison.Ordinal) &&
                   JsonConvert.SerializeObject(actual.Quality) == JsonConvert.SerializeObject(expected.Quality) &&
                   JsonConvert.SerializeObject(actual.Languages) == JsonConvert.SerializeObject(expected.Languages) &&
                   JsonConvert.SerializeObject(actual.MediaInfo) == JsonConvert.SerializeObject(expected.MediaInfo);
        }

        private static void RequireLiveLease(RecoverableOperation operation, string owner)
        {
            if (string.IsNullOrWhiteSpace(owner) || operation.LeaseOwner != owner || operation.LeaseExpiresAt <= DateTime.UtcNow)
            {
                throw new RecoverableOperationConcurrencyException(operation.Id);
            }
        }

        private sealed record Payload(MovieFile incoming, MovieFile outgoing, LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            public MovieFile Incoming { get; } = incoming;
            public MovieFile Outgoing { get; } = outgoing;
            public LocalMovie LocalMovie { get; } = localMovie;
            public DownloadClientItem DownloadClientItem { get; } = downloadClientItem;
        }
    }
}
