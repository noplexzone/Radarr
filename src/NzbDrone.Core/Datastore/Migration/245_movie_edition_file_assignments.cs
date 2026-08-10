using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(245)]
    public class movie_edition_file_assignments : NzbDroneMigrationBase
    {
        private static readonly Regex NormalizeRegex = new Regex(@"[\s\-_.'""]+", RegexOptions.Compiled);

        protected override void MainDbUpgrade()
        {
            Alter.Table("MovieFiles").AddColumn("MovieEditionSlotId").AsInt32().Nullable();
            Alter.Table("MovieEditionSlots").AddColumn("CanonicalEditionKey").AsString().Nullable();

            Create.Table("MovieEditionSlotAliases")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieEditionSlotId").AsInt32().NotNullable()
                .WithColumn("Alias").AsString().NotNullable()
                .WithColumn("NormalizedAlias").AsString().NotNullable();

            Execute.WithConnection(BackfillAssignmentsAndKeys);

            Delete.Index("IX_MovieEditionSlots_MovieId_EditionName").OnTable("MovieEditionSlots");
            Delete.Column("MovieFileId").FromTable("MovieEditionSlots");

            Create.Index("IX_MovieFiles_MovieEditionSlotId").OnTable("MovieFiles").OnColumn("MovieEditionSlotId").Ascending();
            Execute.Sql(@"CREATE UNIQUE INDEX ""UX_MovieFiles_MovieEditionSlotId"" ON ""MovieFiles"" (""MovieEditionSlotId"") WHERE ""MovieEditionSlotId"" IS NOT NULL");
            Create.Index("UX_MovieEditionSlots_MovieId_CanonicalEditionKey").OnTable("MovieEditionSlots").OnColumn("MovieId").Ascending().OnColumn("CanonicalEditionKey").Ascending().WithOptions().Unique();
            Create.Index("IX_MovieEditionSlotAliases_MovieEditionSlotId").OnTable("MovieEditionSlotAliases").OnColumn("MovieEditionSlotId").Ascending();
            Create.Index("UX_MovieEditionSlotAliases_Slot_NormalizedAlias").OnTable("MovieEditionSlotAliases").OnColumn("MovieEditionSlotId").Ascending().OnColumn("NormalizedAlias").Ascending().WithOptions().Unique();
        }

        private void BackfillAssignmentsAndKeys(IDbConnection connection, IDbTransaction transaction)
        {
            var usedFiles = new HashSet<int>();
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            var slots = new List<(int Id, int MovieId, string EditionName, int? MovieFileId)>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT ""Id"", ""MovieId"", ""EditionName"", ""MovieFileId"" FROM ""MovieEditionSlots"" ORDER BY ""Id""";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        slots.Add((Convert.ToInt32(reader[0]), Convert.ToInt32(reader[1]), Convert.ToString(reader[2]), reader.IsDBNull(3) ? null : Convert.ToInt32(reader[3])));
                    }
                }
            }

            foreach (var slot in slots)
            {
                var key = Normalize(slot.EditionName);
                var uniqueKey = key;
                if (uniqueKey.Length == 0 || !usedKeys.Add(slot.MovieId + ":" + uniqueKey))
                {
                    uniqueKey = (key.Length == 0 ? "edition" : key) + "-legacy-" + slot.Id;
                    usedKeys.Add(slot.MovieId + ":" + uniqueKey);
                    _logger.Warn("Edition slot {0} for movie {1} had a conflicting normalized name; preserving it with canonical key '{2}'", slot.Id, slot.MovieId, uniqueKey);
                }

                ExecuteNonQuery(connection, transaction, "UPDATE \"MovieEditionSlots\" SET \"CanonicalEditionKey\" = @key WHERE \"Id\" = @id", ("@key", uniqueKey), ("@id", slot.Id));

                if (!slot.MovieFileId.HasValue)
                {
                    continue;
                }

                var movieFileId = slot.MovieFileId.Value;
                if (usedFiles.Contains(movieFileId))
                {
                    _logger.Warn("Edition slot {0} also referenced movie file {1}; preserving the file and leaving this slot missing", slot.Id, movieFileId);
                    continue;
                }

                var file = QueryFile(connection, transaction, movieFileId);
                if (file == null)
                {
                    _logger.Warn("Edition slot {0} referenced missing movie file {1}; leaving the slot missing", slot.Id, movieFileId);
                    continue;
                }

                if (file.Value.MovieId != slot.MovieId)
                {
                    _logger.Warn("Edition slot {0} belongs to movie {1} but referenced file {2} from movie {3}; preserving the file unassigned", slot.Id, slot.MovieId, movieFileId, file.Value.MovieId);
                    continue;
                }

                if (file.Value.IsMain)
                {
                    _logger.Warn("Edition slot {0} referenced main movie file {1}; preserving it as main and leaving the slot missing", slot.Id, movieFileId);
                    continue;
                }

                ExecuteNonQuery(connection, transaction, "UPDATE \"MovieFiles\" SET \"MovieEditionSlotId\" = @slotId WHERE \"Id\" = @fileId", ("@slotId", slot.Id), ("@fileId", movieFileId));
                usedFiles.Add(movieFileId);
            }
        }

        private static (int MovieId, bool IsMain)? QueryFile(IDbConnection connection, IDbTransaction transaction, int movieFileId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT f.""MovieId"", CASE WHEN m.""MovieFileId"" = f.""Id"" THEN 1 ELSE 0 END FROM ""MovieFiles"" f LEFT JOIN ""Movies"" m ON m.""Id"" = f.""MovieId"" WHERE f.""Id"" = @id";
                AddParameter(command, "@id", movieFileId);
                using (var reader = command.ExecuteReader())
                {
                    return reader.Read() ? (Convert.ToInt32(reader[0]), Convert.ToInt32(reader[1]) == 1) : null;
                }
            }
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
    }
}
