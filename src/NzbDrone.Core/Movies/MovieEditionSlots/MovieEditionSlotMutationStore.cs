using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using Dapper;
using FluentValidation;
using FluentValidation.Results;
using Npgsql;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionSlotMutationStore
    {
        MovieEditionSlot Add(MovieEditionSlot slot);
        MovieEditionSlot Update(MovieEditionSlot slot);
        void Delete(int id);
        void DeleteForMovie(int id);
        void AssignFile(int movieId, int movieFileId, int slotId, int? expectedSlotId);
        void UnassignFile(int movieFileId, int slotId);
    }

    public enum MovieEditionSlotMutationStep
    {
        AfterSlotWrite,
        DuringAliases,
        DuringIdentities,
        AfterDelete,
        AfterAssignmentSlotLock,
        AfterAssignmentFileWrite
    }

    public interface IMovieEditionSlotMutationFaultInjector
    {
        void Check(MovieEditionSlotMutationStep step);
    }

    public class MovieEditionSlotMutationFaultInjector : IMovieEditionSlotMutationFaultInjector
    {
        public void Check(MovieEditionSlotMutationStep step)
        {
        }
    }

    public class MovieEditionSlotMutationStore : IMovieEditionSlotMutationStore
    {
        private const string SlotColumns = @"""Id"", ""MovieId"", ""EditionName"", ""CanonicalEditionKey"", ""SearchTerm"", ""Monitored"", ""QualityProfileId"", ""MinimumCustomFormatScore"", ""LastSearchTime"", ""DateAdded""";
        private readonly IMainDatabase _database;
        private readonly IMovieEditionSlotMutationFaultInjector _faultInjector;

        public MovieEditionSlotMutationStore(IMainDatabase database, IMovieEditionSlotMutationFaultInjector faultInjector)
        {
            _database = database;
            _faultInjector = faultInjector;
        }

        public MovieEditionSlot Add(MovieEditionSlot slot)
        {
            Normalize(slot);
            var aliases = BuildAliases(slot);
            var identities = BuildIdentities(slot);

            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var insertSql = connection is NpgsqlConnection
                    ? @"INSERT INTO ""MovieEditionSlots"" (""MovieId"", ""EditionName"", ""CanonicalEditionKey"", ""SearchTerm"", ""Monitored"", ""QualityProfileId"", ""MinimumCustomFormatScore"", ""LastSearchTime"", ""DateAdded"") VALUES (@MovieId, @EditionName, @CanonicalEditionKey, @SearchTerm, @Monitored, @QualityProfileId, @MinimumCustomFormatScore, @LastSearchTime, @DateAdded) RETURNING ""Id"""
                    : @"INSERT INTO ""MovieEditionSlots"" (""MovieId"", ""EditionName"", ""CanonicalEditionKey"", ""SearchTerm"", ""Monitored"", ""QualityProfileId"", ""MinimumCustomFormatScore"", ""LastSearchTime"", ""DateAdded"") VALUES (@MovieId, @EditionName, @CanonicalEditionKey, @SearchTerm, @Monitored, @QualityProfileId, @MinimumCustomFormatScore, @LastSearchTime, @DateAdded); SELECT last_insert_rowid();";
                slot.Id = connection.QuerySingle<int>(insertSql, slot, transaction);
                _faultInjector.Check(MovieEditionSlotMutationStep.AfterSlotWrite);
                InsertAliases(connection, transaction, slot.Id, aliases);
                InsertIdentities(connection, transaction, slot, identities);
                transaction.Commit();
                slot.Aliases = aliases.Select(x => x.Alias).ToList();
                return slot;
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                throw IdentityConflict();
            }
        }

        public MovieEditionSlot Update(MovieEditionSlot slot)
        {
            Normalize(slot);
            var aliases = BuildAliases(slot);
            var identities = BuildIdentities(slot);

            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var affected = connection.Execute(@"UPDATE ""MovieEditionSlots"" SET ""EditionName"" = @EditionName, ""CanonicalEditionKey"" = @CanonicalEditionKey, ""SearchTerm"" = @SearchTerm, ""Monitored"" = @Monitored, ""QualityProfileId"" = @QualityProfileId, ""MinimumCustomFormatScore"" = @MinimumCustomFormatScore WHERE ""Id"" = @Id AND ""MovieId"" = @MovieId", slot, transaction);
                if (affected != 1)
                {
                    throw new InvalidOperationException("Edition slot no longer exists or belongs to another movie.");
                }

                _faultInjector.Check(MovieEditionSlotMutationStep.AfterSlotWrite);
                connection.Execute(@"DELETE FROM ""MovieEditionSlotAliases"" WHERE ""MovieEditionSlotId"" = @id", new { id = slot.Id }, transaction);
                connection.Execute(@"DELETE FROM ""MovieEditionIdentities"" WHERE ""MovieEditionSlotId"" = @id", new { id = slot.Id }, transaction);
                InsertAliases(connection, transaction, slot.Id, aliases);
                InsertIdentities(connection, transaction, slot, identities);
                var persisted = connection.QuerySingle<MovieEditionSlot>($@"SELECT {SlotColumns} FROM ""MovieEditionSlots"" WHERE ""Id"" = @id", new { id = slot.Id }, transaction);
                transaction.Commit();
                persisted.Aliases = aliases.Select(x => x.Alias).ToList();
                return persisted;
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                throw IdentityConflict();
            }
        }

        public void AssignFile(int movieId, int movieFileId, int slotId, int? expectedSlotId)
        {
            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                int? slotMovieId;
                if (connection is NpgsqlConnection)
                {
                    slotMovieId = connection.QuerySingleOrDefault<int?>(@"SELECT ""MovieId"" FROM ""MovieEditionSlots"" WHERE ""Id"" = @slotId FOR UPDATE", new { slotId }, transaction);
                }
                else
                {
                    var locked = connection.Execute(@"UPDATE ""MovieEditionSlots"" SET ""Id"" = ""Id"" WHERE ""Id"" = @slotId", new { slotId }, transaction);
                    slotMovieId = locked == 1
                        ? connection.QuerySingle<int>(@"SELECT ""MovieId"" FROM ""MovieEditionSlots"" WHERE ""Id"" = @slotId", new { slotId }, transaction)
                        : null;
                }

                if (slotMovieId != movieId)
                {
                    throw new InvalidOperationException($"Edition slot {slotId} no longer exists or belongs to another movie.");
                }

                _faultInjector.Check(MovieEditionSlotMutationStep.AfterAssignmentSlotLock);
                var affected = connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionSlotId"" = @slotId WHERE ""Id"" = @movieFileId AND ""MovieId"" = @movieId AND ((""MovieEditionSlotId"" IS NULL AND @expectedSlotId IS NULL) OR ""MovieEditionSlotId"" = @expectedSlotId) AND NOT EXISTS (SELECT 1 FROM ""Movies"" WHERE ""MovieFileId"" = @movieFileId)",
                    new { movieId, movieFileId, slotId, expectedSlotId }, transaction);
                if (affected != 1)
                {
                    throw new InvalidOperationException("Movie file assignment changed or is no longer valid.");
                }

                _faultInjector.Check(MovieEditionSlotMutationStep.AfterAssignmentFileWrite);
                transaction.Commit();
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                throw new InvalidOperationException("Edition slot already has an assigned movie file.");
            }
        }

        public void UnassignFile(int movieFileId, int slotId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var affected = connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionSlotId"" = NULL WHERE ""Id"" = @movieFileId AND ""MovieEditionSlotId"" = @slotId", new { movieFileId, slotId }, transaction);
            if (affected != 1)
            {
                throw new InvalidOperationException("Movie file assignment changed or no longer exists.");
            }

            transaction.Commit();
        }

        public void Delete(int id)
        {
            Delete(id, requireUnassigned: true);
        }

        public void DeleteForMovie(int id)
        {
            Delete(id, requireUnassigned: false);
        }

        private void Delete(int id, bool requireUnassigned)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            bool exists;
            if (connection is NpgsqlConnection)
            {
                exists = connection.QuerySingleOrDefault<int?>(@"SELECT ""Id"" FROM ""MovieEditionSlots"" WHERE ""Id"" = @id FOR UPDATE", new { id }, transaction).HasValue;
            }
            else
            {
                exists = connection.Execute(@"UPDATE ""MovieEditionSlots"" SET ""Id"" = ""Id"" WHERE ""Id"" = @id", new { id }, transaction) == 1;
            }

            if (!exists)
            {
                throw new InvalidOperationException("Edition slot no longer exists.");
            }

            if (requireUnassigned && connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""MovieEditionSlotId"" = @id", new { id }, transaction) != 0)
            {
                throw new InvalidOperationException("Edition slot still has an assigned movie file.");
            }

            connection.Execute(@"DELETE FROM ""MovieEditionIdentities"" WHERE ""MovieEditionSlotId"" = @id", new { id }, transaction);
            connection.Execute(@"DELETE FROM ""MovieEditionSlotAliases"" WHERE ""MovieEditionSlotId"" = @id", new { id }, transaction);
            if (connection.Execute(@"DELETE FROM ""MovieEditionSlots"" WHERE ""Id"" = @id", new { id }, transaction) != 1)
            {
                throw new InvalidOperationException("Edition slot changed during deletion.");
            }

            _faultInjector.Check(MovieEditionSlotMutationStep.AfterDelete);
            transaction.Commit();
        }

        private void InsertAliases(IDbConnection connection, IDbTransaction transaction, int slotId, List<MovieEditionSlotAlias> aliases)
        {
            foreach (var alias in aliases)
            {
                _faultInjector.Check(MovieEditionSlotMutationStep.DuringAliases);
                alias.MovieEditionSlotId = slotId;
                connection.Execute(@"INSERT INTO ""MovieEditionSlotAliases"" (""MovieEditionSlotId"", ""Alias"", ""NormalizedAlias"") VALUES (@MovieEditionSlotId, @Alias, @NormalizedAlias)", alias, transaction);
            }
        }

        private void InsertIdentities(IDbConnection connection, IDbTransaction transaction, MovieEditionSlot slot, List<MovieEditionIdentity> identities)
        {
            foreach (var identity in identities)
            {
                _faultInjector.Check(MovieEditionSlotMutationStep.DuringIdentities);
                identity.MovieId = slot.MovieId;
                identity.MovieEditionSlotId = slot.Id;
                connection.Execute(@"INSERT INTO ""MovieEditionIdentities"" (""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"") VALUES (@MovieId, @MovieEditionSlotId, @NormalizedTerm, @IdentityType, @DisplayValue)", identity, transaction);
            }
        }

        private static List<MovieEditionSlotAlias> BuildAliases(MovieEditionSlot slot)
        {
            return slot.Aliases.Select(alias => new MovieEditionSlotAlias
            {
                Alias = alias,
                NormalizedAlias = EditionNormalizer.Normalize(alias)
            }).ToList();
        }

        private static List<MovieEditionIdentity> BuildIdentities(MovieEditionSlot slot)
        {
            var candidates = new List<MovieEditionIdentity>
            {
                NewIdentity(slot.EditionName, MovieEditionIdentityType.CanonicalName)
            };
            if (slot.SearchTerm != null)
            {
                candidates.Add(NewIdentity(slot.SearchTerm, MovieEditionIdentityType.SearchTerm));
            }

            candidates.AddRange(slot.Aliases.Select(alias => NewIdentity(alias, MovieEditionIdentityType.Alias)));
            return candidates
                .GroupBy(x => x.NormalizedTerm, StringComparer.Ordinal)
                .Select(group => group.OrderBy(x => x.IdentityType).ThenBy(x => x.DisplayValue, StringComparer.Ordinal).First())
                .ToList();
        }

        private static MovieEditionIdentity NewIdentity(string displayValue, MovieEditionIdentityType type)
        {
            return new MovieEditionIdentity
            {
                NormalizedTerm = EditionNormalizer.Normalize(displayValue),
                IdentityType = type,
                DisplayValue = displayValue
            };
        }

        internal static void Normalize(MovieEditionSlot slot)
        {
            slot.EditionName = slot.EditionName?.Trim() ?? string.Empty;
            slot.CanonicalEditionKey = EditionNormalizer.Normalize(slot.EditionName);
            slot.SearchTerm = slot.SearchTerm.IsNullOrWhiteSpace() ? null : slot.SearchTerm.Trim();
            slot.Aliases = (slot.Aliases ?? new List<string>())
                .Where(alias => alias.IsNotNullOrWhiteSpace())
                .Select(alias => alias.Trim())
                .ToList();

            var failures = new List<ValidationFailure>();
            if (slot.CanonicalEditionKey.IsNullOrWhiteSpace())
            {
                failures.Add(new ValidationFailure("EditionName", "Edition name must contain letters or numbers."));
            }

            if (slot.SearchTerm != null && EditionNormalizer.Normalize(slot.SearchTerm).IsNullOrWhiteSpace())
            {
                failures.Add(new ValidationFailure("SearchTerm", "Search term must contain letters or numbers."));
            }

            if (slot.Aliases.Any(alias => EditionNormalizer.Normalize(alias).IsNullOrWhiteSpace()))
            {
                failures.Add(new ValidationFailure("Aliases", "Aliases must contain letters or numbers."));
            }

            if (failures.Count != 0)
            {
                throw new ValidationException(failures);
            }

            slot.Aliases = slot.Aliases
                .GroupBy(EditionNormalizer.Normalize, StringComparer.Ordinal)
                .Select(group => group.OrderBy(alias => alias, StringComparer.Ordinal).First())
                .ToList();
        }

        private static bool IsUniqueViolation(Exception exception)
        {
            return exception is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation ||
                   exception is SQLiteException sqlite && sqlite.ResultCode == SQLiteErrorCode.Constraint;
        }

        private static ValidationException IdentityConflict()
        {
            return new ValidationException(new[]
            {
                new ValidationFailure("EditionIdentity", "An edition identity conflicts with another edition for this movie.")
            });
        }
    }
}
