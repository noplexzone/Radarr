using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public enum RecoverableOperationType { Delete = 1, Import = 2, Rename = 3, Move = 4, Promote = 5 }
    public enum RecoverableOperationState { Pending = 1, Staging = 2, Staged = 3, ApplyingDatabase = 4, DatabaseCommitted = 5, Finalizing = 6, Completed = 7, RollingBack = 8, RolledBack = 9, RecoveryRequired = 10 }
    public enum RecoverableTransferMode { None = 0, Move = 1, Copy = 2, HardLink = 3, Reflink = 4 }

    [Flags]
    public enum RecoverableOperationEventDispatchMask : long
    {
        None = 0,
        MovieFileDeleted = 1,
        DeleteCompleted = 2,
        MovieFileDeletedInProgress = 4,
        DeleteCompletedInProgress = 8
    }

    public sealed class RecoverableOperationSnapshot
    {
        public int MovieId { get; init; }
        public int? MovieFileId { get; init; }
        public int? MovieEditionSlotId { get; init; }
        public string Path { get; init; }
        public string DestinationPath { get; init; }
        public long? Size { get; init; }
    }

    public sealed class RecoverableMovieEventSnapshot : IEmbeddedDocument
    {
        public int Id { get; init; }
        public int MovieMetadataId { get; init; }
        public string Path { get; init; }
        public string Title { get; init; }
        public int Year { get; init; }
        public int TmdbId { get; init; }
        public string ImdbId { get; init; }
        public DateTime? InCinemas { get; init; }
        public DateTime? PhysicalRelease { get; init; }
        public DateTime? DigitalRelease { get; init; }
        public string Overview { get; init; }
        public List<string> Genres { get; init; } = new();
        public List<NzbDrone.Core.MediaCover.MediaCover> Images { get; init; } = new();
        public Language OriginalLanguage { get; init; }
        public HashSet<int> Tags { get; init; } = new();
    }

    public sealed class RecoverableMovieFileEventSnapshot : IEmbeddedDocument
    {
        public int Id { get; init; }
        public int MovieId { get; init; }
        public int? MovieEditionSlotId { get; init; }
        public string RelativePath { get; init; }
        public long Size { get; init; }
        public QualityModel Quality { get; init; }
        public List<Language> Languages { get; init; }
        public string ReleaseGroup { get; init; }
        public string SceneName { get; init; }
        public DateTime DateAdded { get; init; }
        public MediaInfoModel MediaInfo { get; init; }
        public IndexerFlags IndexerFlags { get; init; }
        public string Edition { get; init; }
    }

    public sealed class RecoverableOperationPlan : IEmbeddedDocument
    {
        public RecoverableOperationSnapshot Expected { get; init; }
        public RecoverableOperationSnapshot Desired { get; init; }
        public string SourcePath { get; init; }
        public string StagingPath { get; init; }
        public string DestinationPath { get; init; }
        public string FinalizePath { get; init; }
        public long? ExpectedSize { get; init; }
        public RecoverableTransferMode TransferMode { get; init; }
        public Dictionary<string, string> EventFacts { get; init; } = new();
        public RecoverableMovieFileEventSnapshot MovieFileEvent { get; init; }
        public RecoverableMovieEventSnapshot MovieEvent { get; init; }
    }

    public sealed class RecoverableOperation : ModelBase
    {
        public string OperationKey { get; set; }
        public string ResourceKey { get; set; }
        public string ActiveResourceKey { get; set; }
        public RecoverableOperationType OperationType { get; set; }
        public RecoverableOperationState State { get; set; }
        public int MovieId { get; set; }
        public int? MovieFileId { get; set; }
        public int? MovieEditionSlotId { get; set; }
        public RecoverableOperationPlan Plan { get; set; }
        public string StagingRoot { get; set; }
        public int Version { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public string LastError { get; set; }
        public string LeaseOwner { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DatabaseCommittedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long EventDispatchMask { get; set; }
    }

    public sealed class RecoverableOperationResource : ModelBase
    {
        public int OperationId { get; set; }
        public string ResourceKey { get; set; }
    }

    public sealed class RecoverableOperationCreateRequest
    {
        public string OperationKey { get; init; }
        public string ResourceKey { get; init; }
        public IReadOnlyCollection<string> ResourceKeys { get; init; }
        public RecoverableOperationType OperationType { get; init; }
        public int MovieId { get; init; }
        public int? MovieFileId { get; init; }
        public int? MovieEditionSlotId { get; init; }
        public RecoverableOperationPlan Plan { get; init; }
        public string StagingRoot { get; init; }
    }

    public class RecoverableOperationException : Exception
    {
        public RecoverableOperationException(string message)
            : base(message)
        {
        }

        public RecoverableOperationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class RecoverableOperationValidationException : RecoverableOperationException { public RecoverableOperationValidationException(string message) : base(message) { } }
    public sealed class RecoverableOperationResourceConflictException : RecoverableOperationException { public RecoverableOperationResourceConflictException(string resource) : base($"An active recoverable operation already owns resource '{resource}'.") { } }
    public sealed class RecoverableOperationTransitionException : RecoverableOperationException { public RecoverableOperationTransitionException(RecoverableOperationState from, RecoverableOperationState to) : base($"Recoverable operation transition from {from} to {to} is not legal.") { } }
    public sealed class RecoverableOperationConcurrencyException : RecoverableOperationException { public RecoverableOperationConcurrencyException(int id) : base($"Recoverable operation {id} changed or its lease is unavailable.") { } }
}
