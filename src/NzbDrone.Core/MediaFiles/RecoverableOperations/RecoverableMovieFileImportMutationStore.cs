using System;
using System.Data;
using System.Data.SQLite;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableMovieFileImportMutationStore
    {
        RecoverableOperation Apply(RecoverableOperation operation, string leaseOwner);
    }

    public enum RecoverableMovieFileImportMutationStep
    {
        AfterOutgoingDelete,
        AfterIncomingInsert,
        AfterMainPointerUpdate,
        AfterInvariantAudit,
        BeforeCommit
    }

    public interface IRecoverableMovieFileImportMutationFaultInjector
    {
        void Check(RecoverableMovieFileImportMutationStep step);
    }

    public sealed class RecoverableMovieFileImportMutationFaultInjector : IRecoverableMovieFileImportMutationFaultInjector
    {
        public void Check(RecoverableMovieFileImportMutationStep step)
        {
        }
    }

    public sealed class RecoverableMovieFileImportMutationStore : IRecoverableMovieFileImportMutationStore
    {
        private const string OperationColumns = @"""Id"",""OperationKey"",""ResourceKey"",""ActiveResourceKey"",""OperationType"",""State"",""MovieId"",""MovieFileId"",""MovieEditionSlotId"",""Plan"",""StagingRoot"",""Version"",""AttemptCount"",""LastAttemptAt"",""LastError"",""LeaseOwner"",""LeaseExpiresAt"",""CreatedAt"",""UpdatedAt"",""DatabaseCommittedAt"",""CompletedAt"",""EventDispatchMask"",""ResultMovieFileId""";
        private readonly IMainDatabase _database;
        private readonly IRecoverableMovieFileImportMutationFaultInjector _faultInjector;

        public RecoverableMovieFileImportMutationStore(IMainDatabase database, IRecoverableMovieFileImportMutationFaultInjector faultInjector)
        {
            _database = database;
            _faultInjector = faultInjector;
        }

        public RecoverableOperation Apply(RecoverableOperation operation, string leaseOwner)
        {
            if (operation == null || operation.Id <= 0 || string.IsNullOrWhiteSpace(leaseOwner))
            {
                throw new RecoverableOperationValidationException("An import operation and lease owner are required.");
            }

            var now = DateTime.UtcNow;
            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var persisted = LockOperation(connection, transaction, operation.Id, operation.Version, leaseOwner, now);
                var movie = LockMovie(connection, transaction, persisted.MovieId);
                if (movie == null || movie.MovieFileId != persisted.Plan.ExpectedMovieFileId)
                {
                    throw Conflict(persisted.Id);
                }

                ValidatePlan(persisted, movie.Path);

                var outgoing = ReadExpectedTarget(connection, transaction, persisted);
                if (outgoing != null)
                {
                    var deleted = connection.Execute(@"DELETE FROM ""MovieFiles"" WHERE ""Id""=@Id AND ""MovieId""=@MovieId AND ((""MovieEditionSlotId"" IS NULL AND @MovieEditionSlotId IS NULL) OR ""MovieEditionSlotId""=@MovieEditionSlotId) AND ""RelativePath""=@RelativePath AND ""Size""=@Size", outgoing, transaction);
                    if (deleted != 1)
                    {
                        throw Conflict(persisted.Id);
                    }
                }

                _faultInjector.Check(RecoverableMovieFileImportMutationStep.AfterOutgoingDelete);
                var incomingId = connection.QuerySingle<int>(IncomingInsertSql(connection is NpgsqlConnection), persisted.Plan.DesiredIncomingMovieFile, transaction);
                _faultInjector.Check(RecoverableMovieFileImportMutationStep.AfterIncomingInsert);

                if (persisted.Plan.ImportTarget == MovieFileImportTarget.Main)
                {
                    var updated = connection.Execute(@"UPDATE ""Movies"" SET ""MovieFileId""=@incomingId WHERE ""Id""=@movieId AND ""MovieFileId""=@expectedMovieFileId",
                        new { incomingId, movieId = persisted.MovieId, expectedMovieFileId = persisted.Plan.ExpectedMovieFileId }, transaction);
                    if (updated != 1)
                    {
                        throw Conflict(persisted.Id);
                    }
                }

                _faultInjector.Check(RecoverableMovieFileImportMutationStep.AfterMainPointerUpdate);
                Audit(connection, transaction, persisted, outgoing, incomingId);
                _faultInjector.Check(RecoverableMovieFileImportMutationStep.AfterInvariantAudit);

                var transitioned = connection.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""ResultMovieFileId""=@incomingId,""State""=@committed,""Version""=""Version""+1,""UpdatedAt""=@now,""DatabaseCommittedAt""=@now WHERE ""Id""=@id AND ""OperationType""=@import AND ""State""=@applying AND ""Version""=@version AND ""LeaseOwner""=@leaseOwner AND ""LeaseExpiresAt"">@now AND ""ResultMovieFileId"" IS NULL",
                    new { incomingId, committed = RecoverableOperationState.DatabaseCommitted, now, id = persisted.Id, import = RecoverableOperationType.Import, applying = RecoverableOperationState.ApplyingDatabase, version = persisted.Version, leaseOwner }, transaction);
                if (transitioned != 1)
                {
                    throw Conflict(persisted.Id);
                }

                var result = connection.QuerySingle<RecoverableOperation>($@"SELECT {OperationColumns} FROM ""MovieEditionFileOperations"" WHERE ""Id""=@id", new { id = persisted.Id }, transaction);
                _faultInjector.Check(RecoverableMovieFileImportMutationStep.BeforeCommit);
                transaction.Commit();
                return result;
            }
            catch (Exception exception) when (IsUniqueViolation(exception))
            {
                throw Conflict(operation.Id);
            }
        }

        internal static string IncomingInsertSql(bool postgres)
        {
            const string insert = @"INSERT INTO ""MovieFiles"" (""MovieId"",""MovieEditionSlotId"",""RelativePath"",""Size"",""Quality"",""Languages"",""DateAdded"",""SceneName"",""ReleaseGroup"",""MediaInfo"",""OriginalFilePath"",""IndexerFlags"",""Edition"") VALUES (@MovieId,@MovieEditionSlotId,@RelativePath,@Size,@Quality,@Languages,@DateAdded,@SceneName,@ReleaseGroup,@MediaInfo,@OriginalFilePath,@IndexerFlags,@Edition)";
            return postgres ? insert + " RETURNING \"Id\"" : insert + "; SELECT last_insert_rowid();";
        }

        private static RecoverableOperation LockOperation(IDbConnection connection, IDbTransaction transaction, int id, int version, string leaseOwner, DateTime now)
        {
            RecoverableOperation operation;
            if (connection is NpgsqlConnection)
            {
                operation = connection.QuerySingleOrDefault<RecoverableOperation>($@"SELECT {OperationColumns} FROM ""MovieEditionFileOperations"" WHERE ""Id""=@id AND ""Version""=@version AND ""OperationType""=@import AND ""State""=@applying AND ""LeaseOwner""=@leaseOwner AND ""LeaseExpiresAt"">@now FOR UPDATE",
                    new { id, version, import = RecoverableOperationType.Import, applying = RecoverableOperationState.ApplyingDatabase, leaseOwner, now }, transaction);
            }
            else
            {
                var locked = connection.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""Id""=""Id"" WHERE ""Id""=@id AND ""Version""=@version AND ""OperationType""=@import AND ""State""=@applying AND ""LeaseOwner""=@leaseOwner AND ""LeaseExpiresAt"">@now",
                    new { id, version, import = RecoverableOperationType.Import, applying = RecoverableOperationState.ApplyingDatabase, leaseOwner, now }, transaction);
                operation = locked == 1
                    ? connection.QuerySingle<RecoverableOperation>($@"SELECT {OperationColumns} FROM ""MovieEditionFileOperations"" WHERE ""Id""=@id", new { id }, transaction)
                    : null;
            }

            return operation ?? throw Conflict(id);
        }

        private static LockedMovie LockMovie(IDbConnection connection, IDbTransaction transaction, int movieId)
        {
            if (connection is NpgsqlConnection)
            {
                return connection.QuerySingleOrDefault<LockedMovie>(@"SELECT ""MovieFileId"",""Path"" FROM ""Movies"" WHERE ""Id""=@movieId FOR UPDATE", new { movieId }, transaction);
            }

            var locked = connection.Execute(@"UPDATE ""Movies"" SET ""Id""=""Id"" WHERE ""Id""=@movieId", new { movieId }, transaction);
            return locked == 1 ? connection.QuerySingle<LockedMovie>(@"SELECT ""MovieFileId"",""Path"" FROM ""Movies"" WHERE ""Id""=@movieId", new { movieId }, transaction) : null;
        }

        private static RecoverableMovieFileRowSnapshot ReadExpectedTarget(IDbConnection connection, IDbTransaction transaction, RecoverableOperation operation)
        {
            var expected = operation.Plan.ExpectedOutgoingMovieFile;
            RecoverableMovieFileRowSnapshot actual = null;
            switch (operation.Plan.ImportTarget)
            {
                case MovieFileImportTarget.Main:
                    if (operation.Plan.ExpectedMovieFileId > 0)
                    {
                        actual = connection.QuerySingleOrDefault<RecoverableMovieFileRowSnapshot>($@"SELECT ""Id"",""MovieId"",""MovieEditionSlotId"",""RelativePath"",""Size"",""Quality"",""Languages"",""DateAdded"",""SceneName"",""ReleaseGroup"",""MediaInfo"",""OriginalFilePath"",""IndexerFlags"",""Edition"" FROM ""MovieFiles"" WHERE ""Id""=@id" + (connection is NpgsqlConnection ? " FOR UPDATE" : string.Empty), new { id = operation.Plan.ExpectedMovieFileId }, transaction);
                    }
                    break;
                case MovieFileImportTarget.EditionSlot:
                    LockSlot(connection, transaction, operation);
                    actual = connection.QuerySingleOrDefault<RecoverableMovieFileRowSnapshot>($@"SELECT ""Id"",""MovieId"",""MovieEditionSlotId"",""RelativePath"",""Size"",""Quality"",""Languages"",""DateAdded"",""SceneName"",""ReleaseGroup"",""MediaInfo"",""OriginalFilePath"",""IndexerFlags"",""Edition"" FROM ""MovieFiles"" WHERE ""MovieEditionSlotId""=@slotId" + (connection is NpgsqlConnection ? " FOR UPDATE" : string.Empty), new { slotId = operation.MovieEditionSlotId }, transaction);
                    break;
                case MovieFileImportTarget.Unassigned:
                    break;
            }

            if (!SameIdentity(actual, expected))
            {
                throw Conflict(operation.Id);
            }

            return actual;
        }

        private static void LockSlot(IDbConnection connection, IDbTransaction transaction, RecoverableOperation operation)
        {
            int? movieId;
            if (connection is NpgsqlConnection)
            {
                movieId = connection.QuerySingleOrDefault<int?>(@"SELECT ""MovieId"" FROM ""MovieEditionSlots"" WHERE ""Id""=@slotId FOR UPDATE", new { slotId = operation.MovieEditionSlotId }, transaction);
            }
            else
            {
                var locked = connection.Execute(@"UPDATE ""MovieEditionSlots"" SET ""Id""=""Id"" WHERE ""Id""=@slotId", new { slotId = operation.MovieEditionSlotId }, transaction);
                movieId = locked == 1 ? connection.QuerySingle<int>(@"SELECT ""MovieId"" FROM ""MovieEditionSlots"" WHERE ""Id""=@slotId", new { slotId = operation.MovieEditionSlotId }, transaction) : null;
            }

            if (movieId != operation.MovieId)
            {
                throw Conflict(operation.Id);
            }
        }

        private static void ValidatePlan(RecoverableOperation operation, string moviePath)
        {
            var plan = operation.Plan;
            var desired = plan?.DesiredIncomingMovieFile;
            if (plan == null || desired == null || plan.Desired == null || plan.ExpectedMovieFileId < 0 || desired.MovieId != operation.MovieId || string.IsNullOrWhiteSpace(desired.RelativePath) || desired.Size < 0 || desired.Quality == null || desired.Languages == null || desired.DateAdded == default ||
                plan.ExpectedSize != desired.Size || plan.Desired.MovieId != operation.MovieId || plan.Desired.MovieEditionSlotId != desired.MovieEditionSlotId || plan.Desired.Size != desired.Size || !DestinationMatches(moviePath, plan.DestinationPath, desired.RelativePath) || !DestinationMatches(moviePath, plan.Desired.DestinationPath, desired.RelativePath))
            {
                throw new RecoverableOperationValidationException("The durable import row snapshot is incomplete or inconsistent.");
            }

            var expected = plan.ExpectedOutgoingMovieFile;
            if (expected != null && (expected.Id <= 0 || expected.MovieId != operation.MovieId || string.IsNullOrWhiteSpace(expected.RelativePath) || expected.Size < 0 || operation.MovieFileId != expected.Id))
            {
                throw new RecoverableOperationValidationException("The expected outgoing movie file snapshot is inconsistent.");
            }

            switch (plan.ImportTarget)
            {
                case MovieFileImportTarget.Main when desired.MovieEditionSlotId == null && operation.MovieEditionSlotId == null &&
                                                    (expected == null || expected.MovieEditionSlotId == null) &&
                                                    ((plan.ExpectedMovieFileId == 0 && expected == null && operation.MovieFileId == null) ||
                                                     (plan.ExpectedMovieFileId > 0 && expected?.Id == plan.ExpectedMovieFileId)):
                    return;
                case MovieFileImportTarget.EditionSlot when operation.MovieEditionSlotId > 0 && desired.MovieEditionSlotId == operation.MovieEditionSlotId &&
                                                           (expected == null || expected.MovieEditionSlotId == operation.MovieEditionSlotId):
                    return;
                case MovieFileImportTarget.Unassigned when operation.MovieEditionSlotId == null && operation.MovieFileId == null && desired.MovieEditionSlotId == null && expected == null:
                    return;
                default:
                    throw new RecoverableOperationValidationException("The import target and durable row snapshots are inconsistent.");
            }
        }

        private static void Audit(IDbConnection connection, IDbTransaction transaction, RecoverableOperation operation, RecoverableMovieFileRowSnapshot outgoing, int incomingId)
        {
            if (outgoing != null && (connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""Id""=@id", new { id = outgoing.Id }, transaction) != 0 ||
                                     connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""Movies"" WHERE ""MovieFileId""=@id", new { id = outgoing.Id }, transaction) != 0))
            {
                throw Conflict(operation.Id);
            }

            var desired = operation.Plan.DesiredIncomingMovieFile;
            var incomingMatches = connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""Id""=@incomingId AND ""MovieId""=@movieId AND ((""MovieEditionSlotId"" IS NULL AND @slotId IS NULL) OR ""MovieEditionSlotId""=@slotId) AND ""RelativePath""=@relativePath",
                new { incomingId, movieId = operation.MovieId, slotId = desired.MovieEditionSlotId, relativePath = desired.RelativePath }, transaction);
            if (incomingMatches != 1)
            {
                throw Conflict(operation.Id);
            }

            var pointer = connection.QuerySingle<int>(@"SELECT ""MovieFileId"" FROM ""Movies"" WHERE ""Id""=@movieId", new { movieId = operation.MovieId }, transaction);
            var expectedPointer = operation.Plan.ImportTarget == MovieFileImportTarget.Main ? incomingId : operation.Plan.ExpectedMovieFileId;
            if (pointer != expectedPointer)
            {
                throw Conflict(operation.Id);
            }

            if (operation.Plan.ImportTarget == MovieFileImportTarget.EditionSlot && connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""MovieEditionSlotId""=@slotId", new { slotId = operation.MovieEditionSlotId }, transaction) != 1)
            {
                throw Conflict(operation.Id);
            }

            if (connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""Movies"" WHERE ""MovieFileId""=@incomingId AND ""Id""<>@movieId", new { incomingId, movieId = operation.MovieId }, transaction) != 0)
            {
                throw Conflict(operation.Id);
            }
        }

        private static bool SameIdentity(RecoverableMovieFileRowSnapshot actual, RecoverableMovieFileRowSnapshot expected)
        {
            if (actual == null || expected == null)
            {
                return actual == null && expected == null;
            }

            return actual.Id == expected.Id && actual.MovieId == expected.MovieId && actual.MovieEditionSlotId == expected.MovieEditionSlotId &&
                   string.Equals(actual.RelativePath, expected.RelativePath, StringComparison.Ordinal) && actual.Size == expected.Size &&
                   actual.DateAdded == expected.DateAdded && string.Equals(actual.SceneName, expected.SceneName, StringComparison.Ordinal) &&
                   string.Equals(actual.ReleaseGroup, expected.ReleaseGroup, StringComparison.Ordinal) && string.Equals(actual.OriginalFilePath, expected.OriginalFilePath, StringComparison.Ordinal) &&
                   actual.IndexerFlags == expected.IndexerFlags && string.Equals(actual.Edition, expected.Edition, StringComparison.Ordinal) &&
                   JsonConvert.SerializeObject(actual.Quality) == JsonConvert.SerializeObject(expected.Quality) &&
                   JsonConvert.SerializeObject(actual.Languages) == JsonConvert.SerializeObject(expected.Languages) &&
                   JsonConvert.SerializeObject(actual.MediaInfo) == JsonConvert.SerializeObject(expected.MediaInfo);
        }

        private static bool DestinationMatches(string moviePath, string destinationPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(moviePath) || string.IsNullOrWhiteSpace(destinationPath) || HasDotSegment(destinationPath) || HasDotSegment(relativePath))
            {
                return false;
            }

            try
            {
                var plannedRelativePath = moviePath.GetRelativePath(destinationPath).Replace('\\', '/');
                return string.Equals(plannedRelativePath, relativePath.Replace('\\', '/'), DiskProviderBase.PathStringComparison);
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

        private sealed class LockedMovie
        {
            public int MovieFileId { get; init; }
            public string Path { get; init; }
        }

        private static RecoverableOperationConcurrencyException Conflict(int operationId) => new(operationId);

        private static bool IsUniqueViolation(Exception exception)
        {
            return exception is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation ||
                   exception is SQLiteException sqlite && sqlite.ResultCode == SQLiteErrorCode.Constraint;
        }
    }
}
