using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dapper;
using Npgsql;
using NzbDrone.Core.Datastore;
namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableOperationRepository { RecoverableOperation CreatePending(RecoverableOperationCreateRequest request); RecoverableOperation GetById(int id); RecoverableOperation GetByKey(string operationKey); IReadOnlyList<RecoverableOperation> ListRecoverableAfter(int afterId, int limit, DateTime? now = null); RecoverableOperation AcquireLease(int id, int expectedVersion, string owner, DateTime now, DateTime expiresAt); RecoverableOperation RenewLease(int id, int expectedVersion, string owner, DateTime now, DateTime expiresAt); RecoverableOperation UpdateActualTransferMode(int id, int expectedVersion, string owner, RecoverableTransferMode actualTransferMode); RecoverableOperation Transition(int id, RecoverableOperationState expectedState, int expectedVersion, RecoverableOperationState nextState, string leaseOwner = null); RecoverableOperation Transition(IDbConnection connection, IDbTransaction transaction, int id, RecoverableOperationState expectedState, int expectedVersion, RecoverableOperationState nextState, DateTime now, string leaseOwner = null); RecoverableOperation RecordErrorAttempt(int id, int expectedVersion, string error, DateTime now, string leaseOwner = null); RecoverableOperation BeginEventDispatch(int id, int expectedVersion, RecoverableOperationEventDispatchMask eventMask, string leaseOwner); RecoverableOperation CompleteEventDispatch(int id, int expectedVersion, RecoverableOperationEventDispatchMask eventMask, string leaseOwner); RecoverableOperation MarkRecoveryRequired(int id, RecoverableOperationState expectedState, int expectedVersion, string error, string leaseOwner = null, DateTime? now = null); }
    public sealed class RecoverableOperationRepository : IRecoverableOperationRepository
    {
        const string Columns = @"""Id"",""OperationKey"",""ResourceKey"",""ActiveResourceKey"",""OperationType"",""State"",""MovieId"",""MovieFileId"",""MovieEditionSlotId"",""Plan"",""StagingRoot"",""Version"",""AttemptCount"",""LastAttemptAt"",""LastError"",""LeaseOwner"",""LeaseExpiresAt"",""CreatedAt"",""UpdatedAt"",""DatabaseCommittedAt"",""CompletedAt"",""EventDispatchMask"",""ResultMovieFileId"""; readonly IMainDatabase _database; public RecoverableOperationRepository(IMainDatabase database) { _database = database; }
        public RecoverableOperation CreatePending(RecoverableOperationCreateRequest request)
        {
            var resources = Validate(request);
            var primaryResource = request.ResourceKey == null ? resources[0] : request.ResourceKey.Trim();
            var now = DateTime.UtcNow;
            var model = new RecoverableOperation { OperationKey = request.OperationKey, ResourceKey = primaryResource, ActiveResourceKey = primaryResource, OperationType = request.OperationType, State = RecoverableOperationState.Pending, MovieId = request.MovieId, MovieFileId = request.MovieFileId, MovieEditionSlotId = request.MovieEditionSlotId, Plan = request.Plan, StagingRoot = request.StagingRoot, Version = 1, AttemptCount = 0, CreatedAt = now, UpdatedAt = now, EventDispatchMask = 0 };

            using var c = _database.OpenConnection();
            using var tx = c.BeginTransaction(IsolationLevel.ReadCommitted);
            var suffix = c is NpgsqlConnection ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid();";
            try
            {
                model.Id = c.QuerySingle<int>(@"INSERT INTO ""MovieEditionFileOperations"" (""OperationKey"",""ResourceKey"",""ActiveResourceKey"",""OperationType"",""State"",""MovieId"",""MovieFileId"",""MovieEditionSlotId"",""Plan"",""StagingRoot"",""Version"",""AttemptCount"",""LastAttemptAt"",""LastError"",""LeaseOwner"",""LeaseExpiresAt"",""CreatedAt"",""UpdatedAt"",""DatabaseCommittedAt"",""CompletedAt"",""EventDispatchMask"") VALUES (@OperationKey,@ResourceKey,@ActiveResourceKey,@OperationType,@State,@MovieId,@MovieFileId,@MovieEditionSlotId,@Plan,@StagingRoot,@Version,@AttemptCount,@LastAttemptAt,@LastError,@LeaseOwner,@LeaseExpiresAt,@CreatedAt,@UpdatedAt,@DatabaseCommittedAt,@CompletedAt,@EventDispatchMask)" + suffix, model, tx);
            }
            catch (Exception ex) when (IsUnique(ex))
            {
                throw new RecoverableOperationResourceConflictException(primaryResource);
            }
            foreach (var resource in resources)
            {
                try
                {
                    c.Execute(@"INSERT INTO ""MovieEditionFileOperationResources"" (""OperationId"",""ResourceKey"") VALUES (@operationId,@resource)", new { operationId = model.Id, resource }, tx);
                }
                catch (Exception ex) when (IsUnique(ex))
                {
                    throw new RecoverableOperationResourceConflictException(resource);
                }
            }

            tx.Commit();
            return model;
        }
        public RecoverableOperation GetById(int id) { using var c = _database.OpenConnection(); return c.QuerySingleOrDefault<RecoverableOperation>($@"SELECT {Columns} FROM ""MovieEditionFileOperations"" WHERE ""Id""=@id", new { id }) ?? throw new RecoverableOperationConcurrencyException(id); }
        public RecoverableOperation GetByKey(string key) { using var c = _database.OpenConnection(); return c.QuerySingleOrDefault<RecoverableOperation>($@"SELECT {Columns} FROM ""MovieEditionFileOperations"" WHERE ""OperationKey""=@key", new { key }); }
        public IReadOnlyList<RecoverableOperation> ListRecoverableAfter(int afterId, int limit, DateTime? now = null) { if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit)); var availableAt = now ?? DateTime.UtcNow; using var c = _database.OpenConnection(); return c.Query<RecoverableOperation>($@"SELECT {Columns} FROM ""MovieEditionFileOperations"" WHERE ""Id"">@afterId AND ""State"" IN (@pending,@staging,@staged,@applying,@committed,@finalizing,@rollingBack) AND (""LeaseOwner"" IS NULL OR ""LeaseExpiresAt""<=@availableAt) ORDER BY ""Id"" LIMIT @limit", new { afterId, limit, availableAt, pending = RecoverableOperationState.Pending, staging = RecoverableOperationState.Staging, staged = RecoverableOperationState.Staged, applying = RecoverableOperationState.ApplyingDatabase, committed = RecoverableOperationState.DatabaseCommitted, finalizing = RecoverableOperationState.Finalizing, rollingBack = RecoverableOperationState.RollingBack }).ToList(); }
        public RecoverableOperation AcquireLease(int id, int version, string owner, DateTime now, DateTime expires) { ValidateLease(owner, now, expires); using var c = _database.OpenConnection(); var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""LeaseOwner""=@owner,""LeaseExpiresAt""=@expires,""Version""=""Version""+1,""UpdatedAt""=@now WHERE ""Id""=@id AND ""Version""=@version AND (""LeaseOwner"" IS NULL OR ""LeaseExpiresAt""<=@now OR ""LeaseOwner""=@owner) AND ""State"" IN (@pending,@staging,@staged,@applying,@committed,@finalizing,@rollingBack)", new { id, version, owner, now, expires, pending = RecoverableOperationState.Pending, staging = RecoverableOperationState.Staging, staged = RecoverableOperationState.Staged, applying = RecoverableOperationState.ApplyingDatabase, committed = RecoverableOperationState.DatabaseCommitted, finalizing = RecoverableOperationState.Finalizing, rollingBack = RecoverableOperationState.RollingBack }); return OneOrThrow(c, id, n); }
        public RecoverableOperation RenewLease(int id, int version, string owner, DateTime now, DateTime expires) { ValidateLease(owner, now, expires); using var c = _database.OpenConnection(); var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""LeaseExpiresAt""=@expires,""Version""=""Version""+1,""UpdatedAt""=@now WHERE ""Id""=@id AND ""Version""=@version AND ""LeaseOwner""=@owner AND ""LeaseExpiresAt"">@now AND ""State"" IN (@pending,@staging,@staged,@applying,@committed,@finalizing,@rollingBack)", new { id, version, owner, now, expires, pending = RecoverableOperationState.Pending, staging = RecoverableOperationState.Staging, staged = RecoverableOperationState.Staged, applying = RecoverableOperationState.ApplyingDatabase, committed = RecoverableOperationState.DatabaseCommitted, finalizing = RecoverableOperationState.Finalizing, rollingBack = RecoverableOperationState.RollingBack }); return OneOrThrow(c, id, n); }
        public RecoverableOperation UpdateActualTransferMode(int id, int version, string owner, RecoverableTransferMode actualTransferMode)
        {
            if (actualTransferMode is not RecoverableTransferMode.Move and not RecoverableTransferMode.Copy and not RecoverableTransferMode.HardLink)
            {
                throw new RecoverableOperationValidationException("A concrete actual transfer mode is required.");
            }

            var current = GetById(id);
            var now = DateTime.UtcNow;
            if (current.Version != version || current.State != RecoverableOperationState.Staging || current.LeaseOwner != owner || current.LeaseExpiresAt <= now)
            {
                throw new RecoverableOperationConcurrencyException(id);
            }

            current.Plan.ActualTransferMode = actualTransferMode;
            using var c = _database.OpenConnection();
            var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""Plan""=@plan,""Version""=""Version""+1,""UpdatedAt""=@now WHERE ""Id""=@id AND ""Version""=@version AND ""State""=@state AND ""LeaseOwner""=@owner AND ""LeaseExpiresAt"">@now", new { id, version, owner, now, state = RecoverableOperationState.Staging, plan = current.Plan });
            return OneOrThrow(c, id, n);
        }
        public RecoverableOperation Transition(int id, RecoverableOperationState state, int version, RecoverableOperationState next, string leaseOwner = null) { using var c = _database.OpenConnection(); using var tx = c.BeginTransaction(IsolationLevel.ReadCommitted); var result = Transition(c, tx, id, state, version, next, DateTime.UtcNow, leaseOwner); tx.Commit(); return result; }
        public RecoverableOperation Transition(IDbConnection c, IDbTransaction tx, int id, RecoverableOperationState state, int version, RecoverableOperationState next, DateTime now, string leaseOwner = null)
        {
            if (!Legal(state, next)) throw new RecoverableOperationTransitionException(state, next);
            var terminal = next is RecoverableOperationState.Completed or RecoverableOperationState.RolledBack;
            var committed = next == RecoverableOperationState.DatabaseCommitted;
            var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""State""=@next,""Version""=""Version""+1,""UpdatedAt""=@now,""DatabaseCommittedAt""=CASE WHEN @committed=1 THEN @now ELSE ""DatabaseCommittedAt"" END,""CompletedAt""=CASE WHEN @terminal=1 THEN @now ELSE ""CompletedAt"" END,""ActiveResourceKey""=CASE WHEN @terminal=1 THEN NULL ELSE ""ActiveResourceKey"" END,""LeaseOwner""=CASE WHEN @terminal=1 THEN NULL ELSE ""LeaseOwner"" END,""LeaseExpiresAt""=CASE WHEN @terminal=1 THEN NULL ELSE ""LeaseExpiresAt"" END WHERE ""Id""=@id AND ""State""=@state AND ""Version""=@version AND (""LeaseOwner"" IS NULL OR ""LeaseExpiresAt""<=@now OR ""LeaseOwner""=@leaseOwner)", new { id, state, version, next, now, leaseOwner, committed = committed ? 1 : 0, terminal = terminal ? 1 : 0 }, tx);
            if (n == 1 && terminal)
            {
                c.Execute(@"DELETE FROM ""MovieEditionFileOperationResources"" WHERE ""OperationId""=@id", new { id }, tx);
            }

            return OneOrThrow(c, id, n, tx);
        }
        public RecoverableOperation RecordErrorAttempt(int id, int version, string error, DateTime now, string leaseOwner = null) { using var c = _database.OpenConnection(); var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""AttemptCount""=""AttemptCount""+1,""LastAttemptAt""=@now,""LastError""=@error,""UpdatedAt""=@now,""Version""=""Version""+1 WHERE ""Id""=@id AND ""Version""=@version AND (""LeaseOwner"" IS NULL OR ""LeaseExpiresAt""<=@now OR ""LeaseOwner""=@leaseOwner)", new { id, version, error, now, leaseOwner }); return OneOrThrow(c, id, n); }
        public RecoverableOperation BeginEventDispatch(int id, int version, RecoverableOperationEventDispatchMask eventMask, string leaseOwner)
        {
            ValidateFinalEventMask(eventMask);
            var now = DateTime.UtcNow;
            using var c = _database.OpenConnection();
            var finalMask = (long)eventMask;
            var inProgressMask = (long)InProgressMask(eventMask);
            var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""EventDispatchMask""=""EventDispatchMask""|@inProgressMask,""Version""=""Version""+1,""UpdatedAt""=@now WHERE ""Id""=@id AND ""Version""=@version AND (""EventDispatchMask""&(@finalMask|@inProgressMask))=0 AND ""LeaseOwner""=@leaseOwner AND ""LeaseExpiresAt"">@now AND ""State""=@state", new { id, version, finalMask, inProgressMask, leaseOwner, now, state = RecoverableOperationState.Finalizing });
            return OneOrThrow(c, id, n);
        }

        public RecoverableOperation CompleteEventDispatch(int id, int version, RecoverableOperationEventDispatchMask eventMask, string leaseOwner)
        {
            ValidateFinalEventMask(eventMask);
            var now = DateTime.UtcNow;
            using var c = _database.OpenConnection();
            var finalMask = (long)eventMask;
            var inProgressMask = (long)InProgressMask(eventMask);
            var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""EventDispatchMask""=(""EventDispatchMask""|@finalMask)&~@inProgressMask,""Version""=""Version""+1,""UpdatedAt""=@now WHERE ""Id""=@id AND ""Version""=@version AND (""EventDispatchMask""&@finalMask)=0 AND (""EventDispatchMask""&@inProgressMask)=@inProgressMask AND ""LeaseOwner""=@leaseOwner AND ""LeaseExpiresAt"">@now AND ""State""=@state", new { id, version, finalMask, inProgressMask, leaseOwner, now, state = RecoverableOperationState.Finalizing });
            return OneOrThrow(c, id, n);
        }
        public RecoverableOperation MarkRecoveryRequired(int id, RecoverableOperationState state, int version, string error, string leaseOwner = null, DateTime? now = null) { if (!Enum.IsDefined(typeof(RecoverableOperationState), state) || !Legal(state, RecoverableOperationState.RecoveryRequired)) throw new RecoverableOperationTransitionException(state, RecoverableOperationState.RecoveryRequired); using var c = _database.OpenConnection(); var changedAt = now ?? DateTime.UtcNow; var n = c.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""State""=@next,""LastError""=@error,""LastAttemptAt""=@changedAt,""AttemptCount""=""AttemptCount""+1,""UpdatedAt""=@changedAt,""Version""=""Version""+1,""LeaseOwner""=NULL,""LeaseExpiresAt""=NULL WHERE ""Id""=@id AND ""State""=@state AND ""Version""=@version AND (""LeaseOwner"" IS NULL OR ""LeaseExpiresAt""<=@changedAt OR ""LeaseOwner""=@leaseOwner)", new { id, state, version, error, changedAt, leaseOwner, next = RecoverableOperationState.RecoveryRequired }); return OneOrThrow(c, id, n); }
        static void ValidateFinalEventMask(RecoverableOperationEventDispatchMask eventMask)
        {
            if (eventMask is not RecoverableOperationEventDispatchMask.MovieFileDeleted and not RecoverableOperationEventDispatchMask.DeleteCompleted)
            {
                throw new RecoverableOperationValidationException("A known final event dispatch bit is required.");
            }
        }

        static RecoverableOperationEventDispatchMask InProgressMask(RecoverableOperationEventDispatchMask eventMask)
        {
            return eventMask == RecoverableOperationEventDispatchMask.MovieFileDeleted ? RecoverableOperationEventDispatchMask.MovieFileDeletedInProgress : RecoverableOperationEventDispatchMask.DeleteCompletedInProgress;
        }

        static RecoverableOperation OneOrThrow(IDbConnection c, int id, int n, IDbTransaction tx = null) { if (n != 1) throw new RecoverableOperationConcurrencyException(id); return c.QuerySingle<RecoverableOperation>($@"SELECT {Columns} FROM ""MovieEditionFileOperations"" WHERE ""Id""=@id", new { id }, tx); }
        static IReadOnlyList<string> Validate(RecoverableOperationCreateRequest r)
        {
            if (r == null || !SafeOperationKey(r.OperationKey) || r.OperationKey.Length > 128) throw new RecoverableOperationValidationException("Operation key is required, ASCII path safe, and must be at most 128 characters.");
            if (!Enum.IsDefined(typeof(RecoverableOperationType), r.OperationType) || r.OperationType == 0 || r.MovieId <= 0 || r.Plan == null || r.Plan.Expected == null || r.Plan.Desired == null) throw new RecoverableOperationValidationException("A known operation type, movie, and immutable plan are required.");
            var resources = NormalizeResources(r);
            if (!Enum.IsDefined(typeof(RecoverableTransferMode), r.Plan.TransferMode) || r.Plan.TransferMode == RecoverableTransferMode.None) throw new RecoverableOperationValidationException("A known non-empty transfer mode is required.");
            if (r.Plan.Expected.MovieId != r.MovieId || r.Plan.Desired.MovieId != r.MovieId) throw new RecoverableOperationValidationException("Plan snapshots must belong to the requested movie.");
            var requiredPaths = new[] { r.Plan.Expected.Path, r.Plan.Desired.DestinationPath, r.Plan.SourcePath, r.Plan.StagingPath, r.Plan.DestinationPath, r.Plan.FinalizePath, r.StagingRoot };
            if (requiredPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))) throw new RecoverableOperationValidationException("Recovery plan paths must be present and rooted.");
            var moviePath = r.Plan.EventFacts != null && r.Plan.EventFacts.TryGetValue("moviePath", out var plannedMoviePath) ? plannedMoviePath : Path.GetDirectoryName(Path.GetFullPath(r.Plan.Expected.Path));
            if (string.IsNullOrWhiteSpace(moviePath) || !Path.IsPathRooted(moviePath) || !Contained(r.Plan.Expected.Path, moviePath, StringComparison.Ordinal)) throw new RecoverableOperationValidationException("Expected file path must be contained by a rooted movie directory.");
            RecoverableOperationStagingPaths expected;
            try { expected = RecoverableOperationStagingPolicy.GetPaths(moviePath, r.OperationKey); } catch (ArgumentException ex) { throw new RecoverableOperationValidationException(ex.Message); }
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(NormalizeDirectory(r.StagingRoot), NormalizeDirectory(expected.Root), comparer) || !Contained(r.Plan.StagingPath, expected.Root, comparer) || !Contained(r.Plan.FinalizePath, expected.Root, comparer)) throw new RecoverableOperationValidationException("Staging and finalize paths must be contained in the deterministic operation root.");
            return resources;
        }

        static IReadOnlyList<string> NormalizeResources(RecoverableOperationCreateRequest request)
        {
            var resources = new List<string>();
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (request.ResourceKeys != null)
            {
                foreach (var rawResource in request.ResourceKeys)
                {
                    var resource = NormalizeResource(rawResource);
                    if (!requested.Add(resource)) throw new RecoverableOperationValidationException($"Duplicate resource key '{resource}' was requested.");
                    resources.Add(resource);
                }
            }

            if (request.ResourceKey != null)
            {
                var legacyResource = NormalizeResource(request.ResourceKey);
                if (!requested.Add(legacyResource)) throw new RecoverableOperationValidationException($"Duplicate resource key '{legacyResource}' was requested.");
                resources.Add(legacyResource);
            }

            if (resources.Count == 0) throw new RecoverableOperationValidationException("At least one bounded resource key is required.");
            return resources.OrderBy(resource => resource, StringComparer.Ordinal).ToList();
        }

        static string NormalizeResource(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource)) throw new RecoverableOperationValidationException("Resource keys must be non-empty.");
            var normalized = resource.Trim();
            if (normalized.Length > 512) throw new RecoverableOperationValidationException("Resource keys must be at most 512 characters.");
            return normalized;
        }
        static bool SafeOperationKey(string key) => !string.IsNullOrWhiteSpace(key) && key != "." && key != ".." && key.All(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || ch is '-' or '_' or '.');
        static string NormalizeDirectory(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        static bool Contained(string path, string root, StringComparison comparison) { var normalizedRoot = NormalizeDirectory(root); var normalizedPath = Path.GetFullPath(path); return normalizedPath.StartsWith(normalizedRoot, comparison); }
        static void ValidateLease(string owner, DateTime now, DateTime expires) { if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || expires <= now) throw new RecoverableOperationValidationException("A bounded lease owner and future expiry are required."); }
        static bool Legal(RecoverableOperationState a, RecoverableOperationState b) => a switch { RecoverableOperationState.Pending => b is RecoverableOperationState.Staging or RecoverableOperationState.RollingBack or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.Staging => b is RecoverableOperationState.Staged or RecoverableOperationState.RollingBack or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.Staged => b is RecoverableOperationState.ApplyingDatabase or RecoverableOperationState.RollingBack or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.ApplyingDatabase => b is RecoverableOperationState.DatabaseCommitted or RecoverableOperationState.RollingBack or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.DatabaseCommitted => b is RecoverableOperationState.Finalizing or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.Finalizing => b is RecoverableOperationState.Completed or RecoverableOperationState.RecoveryRequired, RecoverableOperationState.RollingBack => b is RecoverableOperationState.RolledBack or RecoverableOperationState.RecoveryRequired, _ => false };
        static bool IsUnique(Exception ex) => ex is PostgresException p && p.SqlState == PostgresErrorCodes.UniqueViolation || ex is SQLiteException s && s.ResultCode == SQLiteErrorCode.Constraint;
    }
}
