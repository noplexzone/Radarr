using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(246)]
    public class movie_edition_identities : NzbDroneMigrationBase
    {
        private const int BatchSize = 500;
        private const int CanonicalName = 1;
        private const int SearchTerm = 2;
        private const int Alias = 3;
        private const int EmptyNormalizedTerm = 1;
        private const int CrossSlotCollision = 2;
        private const int OrphanAlias = 3;
        private const int MissingMovie = 4;
        private static readonly Regex NormalizeRegex = new Regex(@"[\p{P}\p{Z}\p{S}\s_]+", RegexOptions.Compiled);

        protected override void MainDbUpgrade()
        {
            Create.Table("MovieEditionIdentities")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieEditionSlotId").AsInt32().NotNullable()
                .WithColumn("NormalizedTerm").AsString().NotNullable()
                .WithColumn("IdentityType").AsInt32().NotNullable()
                .WithColumn("DisplayValue").AsString().NotNullable();

            Create.Table("MovieEditionIdentityFindings")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("FindingType").AsInt32().NotNullable()
                .WithColumn("MovieId").AsInt32().Nullable()
                .WithColumn("MovieEditionSlotId").AsInt32().Nullable()
                .WithColumn("NormalizedTerm").AsString().Nullable()
                .WithColumn("IdentityType").AsInt32().Nullable()
                .WithColumn("DisplayValue").AsString().Nullable()
                .WithColumn("Details").AsString().NotNullable();

            Create.Table("MovieEditionIdentityBackfillCandidates")
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieEditionSlotId").AsInt32().NotNullable()
                .WithColumn("NormalizedTerm").AsString().NotNullable()
                .WithColumn("IdentityType").AsInt32().NotNullable()
                .WithColumn("DisplayValue").AsString().NotNullable()
                .WithColumn("SourceId").AsInt32().NotNullable();

            Create.Table("MovieEditionIdentityBackfillWinners")
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieEditionSlotId").AsInt32().NotNullable()
                .WithColumn("NormalizedTerm").AsString().NotNullable()
                .WithColumn("IdentityType").AsInt32().NotNullable()
                .WithColumn("DisplayValue").AsString().NotNullable();

            Execute.WithConnection(BackfillIdentities);

            Delete.Table("MovieEditionIdentityBackfillWinners");
            Delete.Table("MovieEditionIdentityBackfillCandidates");

            Create.Index("UX_MovieEditionIdentities_MovieId_NormalizedTerm").OnTable("MovieEditionIdentities").OnColumn("MovieId").Ascending().OnColumn("NormalizedTerm").Ascending().WithOptions().Unique();
            Create.Index("IX_MovieEditionIdentities_MovieEditionSlotId").OnTable("MovieEditionIdentities").OnColumn("MovieEditionSlotId").Ascending();
            Create.Index("IX_MovieEditionIdentityFindings_MovieId").OnTable("MovieEditionIdentityFindings").OnColumn("MovieId").Ascending();
            Create.Index("IX_MovieEditionIdentityFindings_MovieEditionSlotId").OnTable("MovieEditionIdentityFindings").OnColumn("MovieEditionSlotId").Ascending();
            Create.Index("IX_MovieEditionIdentityFindings_NormalizedTerm").OnTable("MovieEditionIdentityFindings").OnColumn("NormalizedTerm").Ascending();
        }

        private static void BackfillIdentities(IDbConnection connection, IDbTransaction transaction)
        {
            BackfillSlots(connection, transaction);
            BackfillAliases(connection, transaction);

            ExecuteNonQuery(connection, transaction, @"
INSERT INTO ""MovieEditionIdentityBackfillWinners"" (""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"")
SELECT c.""MovieId"", c.""MovieEditionSlotId"", c.""NormalizedTerm"", c.""IdentityType"", c.""DisplayValue""
FROM ""MovieEditionIdentityBackfillCandidates"" c
WHERE NOT EXISTS (
    SELECT 1 FROM ""MovieEditionIdentityBackfillCandidates"" preferred
    WHERE preferred.""MovieId"" = c.""MovieId""
      AND preferred.""MovieEditionSlotId"" = c.""MovieEditionSlotId""
      AND preferred.""NormalizedTerm"" = c.""NormalizedTerm""
      AND (preferred.""IdentityType"" < c.""IdentityType""
           OR (preferred.""IdentityType"" = c.""IdentityType"" AND preferred.""SourceId"" < c.""SourceId"")))
ORDER BY c.""MovieId"", c.""MovieEditionSlotId"", c.""NormalizedTerm"", c.""IdentityType""");

            ExecuteNonQuery(connection, transaction, @"
INSERT INTO ""MovieEditionIdentityFindings"" (""FindingType"", ""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"", ""Details"")
SELECT @findingType, winner.""MovieId"", winner.""MovieEditionSlotId"", winner.""NormalizedTerm"", winner.""IdentityType"", winner.""DisplayValue"", @details
FROM ""MovieEditionIdentityBackfillWinners"" winner
WHERE EXISTS (
    SELECT 1 FROM ""MovieEditionIdentityBackfillWinners"" other
    WHERE other.""MovieId"" = winner.""MovieId""
      AND other.""NormalizedTerm"" = winner.""NormalizedTerm""
      AND other.""MovieEditionSlotId"" <> winner.""MovieEditionSlotId"")
ORDER BY winner.""MovieId"", winner.""NormalizedTerm"", winner.""MovieEditionSlotId""",
                ("@findingType", CrossSlotCollision),
                ("@details", "Multiple edition slots claim this normalized term; automatic ownership was excluded."));

            ExecuteNonQuery(connection, transaction, @"
INSERT INTO ""MovieEditionIdentities"" (""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"")
SELECT winner.""MovieId"", winner.""MovieEditionSlotId"", winner.""NormalizedTerm"", winner.""IdentityType"", winner.""DisplayValue""
FROM ""MovieEditionIdentityBackfillWinners"" winner
WHERE NOT EXISTS (
    SELECT 1 FROM ""MovieEditionIdentityBackfillWinners"" other
    WHERE other.""MovieId"" = winner.""MovieId""
      AND other.""NormalizedTerm"" = winner.""NormalizedTerm""
      AND other.""MovieEditionSlotId"" <> winner.""MovieEditionSlotId"")
ORDER BY winner.""MovieId"", winner.""MovieEditionSlotId"", winner.""NormalizedTerm""");
        }

        private static void BackfillSlots(IDbConnection connection, IDbTransaction transaction)
        {
            var lastId = 0;
            while (true)
            {
                var rows = new List<SlotRow>();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"SELECT s.""Id"", s.""MovieId"", s.""EditionName"", s.""SearchTerm"", CASE WHEN m.""Id"" IS NULL THEN 0 ELSE 1 END FROM ""MovieEditionSlots"" s LEFT JOIN ""Movies"" m ON m.""Id"" = s.""MovieId"" WHERE s.""Id"" > @lastId ORDER BY s.""Id"" LIMIT 500";
                    AddParameter(command, "@lastId", lastId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            rows.Add(new SlotRow(Convert.ToInt32(reader[0]), Convert.ToInt32(reader[1]), reader.IsDBNull(2) ? null : Convert.ToString(reader[2]), reader.IsDBNull(3) ? null : Convert.ToString(reader[3]), Convert.ToInt32(reader[4]) == 1));
                        }
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    lastId = row.Id;
                    if (!row.MovieExists)
                    {
                        InsertFinding(connection, transaction, MissingMovie, row.MovieId, row.Id, null, null, row.EditionName, "Edition slot references a movie that does not exist; all candidates were excluded.");
                        continue;
                    }

                    InsertCandidate(connection, transaction, row.MovieId, row.Id, row.EditionName, CanonicalName, row.Id);
                    if (row.SearchTerm != null)
                    {
                        InsertCandidate(connection, transaction, row.MovieId, row.Id, row.SearchTerm, SearchTerm, row.Id);
                    }
                }
            }
        }

        private static void BackfillAliases(IDbConnection connection, IDbTransaction transaction)
        {
            var lastId = 0;
            while (true)
            {
                var rows = new List<AliasRow>();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"SELECT a.""Id"", a.""MovieEditionSlotId"", a.""Alias"", s.""MovieId"", CASE WHEN s.""Id"" IS NULL THEN 0 ELSE 1 END, CASE WHEN m.""Id"" IS NULL THEN 0 ELSE 1 END FROM ""MovieEditionSlotAliases"" a LEFT JOIN ""MovieEditionSlots"" s ON s.""Id"" = a.""MovieEditionSlotId"" LEFT JOIN ""Movies"" m ON m.""Id"" = s.""MovieId"" WHERE a.""Id"" > @lastId ORDER BY a.""Id"" LIMIT 500";
                    AddParameter(command, "@lastId", lastId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            rows.Add(new AliasRow(Convert.ToInt32(reader[0]), Convert.ToInt32(reader[1]), reader.IsDBNull(2) ? null : Convert.ToString(reader[2]), reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader[3]), Convert.ToInt32(reader[4]) == 1, Convert.ToInt32(reader[5]) == 1));
                        }
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    lastId = row.Id;
                    var normalized = Normalize(row.DisplayValue);
                    if (!row.SlotExists)
                    {
                        InsertFinding(connection, transaction, OrphanAlias, null, row.SlotId, normalized, Alias, row.DisplayValue, "Alias references an edition slot that does not exist; candidate was excluded.");
                    }
                    else if (row.MovieExists)
                    {
                        InsertCandidate(connection, transaction, row.MovieId.Value, row.SlotId, row.DisplayValue, Alias, row.Id);
                    }
                }
            }
        }

        private static void InsertCandidate(IDbConnection connection, IDbTransaction transaction, int movieId, int slotId, string displayValue, int identityType, int sourceId)
        {
            var normalized = Normalize(displayValue);
            if (normalized.Length == 0)
            {
                InsertFinding(connection, transaction, EmptyNormalizedTerm, movieId, slotId, normalized, identityType, displayValue, "Candidate normalizes to an empty term; candidate was excluded.");
                return;
            }

            ExecuteNonQuery(connection, transaction, @"INSERT INTO ""MovieEditionIdentityBackfillCandidates"" (""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"", ""SourceId"") VALUES (@movieId, @slotId, @term, @type, @display, @sourceId)",
                ("@movieId", movieId), ("@slotId", slotId), ("@term", normalized), ("@type", identityType), ("@display", displayValue), ("@sourceId", sourceId));
        }

        private static void InsertFinding(IDbConnection connection, IDbTransaction transaction, int findingType, int? movieId, int? slotId, string normalizedTerm, int? identityType, string displayValue, string details)
        {
            ExecuteNonQuery(connection, transaction, @"INSERT INTO ""MovieEditionIdentityFindings"" (""FindingType"", ""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"", ""Details"") VALUES (@findingType, @movieId, @slotId, @term, @identityType, @display, @details)",
                ("@findingType", findingType), ("@movieId", movieId), ("@slotId", slotId), ("@term", normalizedTerm), ("@identityType", identityType), ("@display", displayValue), ("@details", details));
        }

        private static void ExecuteNonQuery(IDbConnection connection, IDbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var parameter in parameters)
                {
                    AddParameter(command, parameter.Name, parameter.Value);
                }

                command.ExecuteNonQuery();
            }
        }

        private static void AddParameter(IDbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeRegex.Replace(value, string.Empty).ToLowerInvariant();
        }

        private sealed record SlotRow(int Id, int MovieId, string EditionName, string SearchTerm, bool MovieExists);
        private sealed record AliasRow(int Id, int SlotId, string DisplayValue, int? MovieId, bool SlotExists, bool MovieExists);
    }
}
