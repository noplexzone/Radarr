using System;
using System.Data;
using System.Data.Common;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(244)]
    public class movie_edition_slots_compat : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Ensure MovieEditionSlots table exists.  An earlier prototype of migration 243
            // added MovieEdition/EditionSearchTerm columns to the Movies table instead;
            // those DBs saw 243 as "done" and skipped the current 243 that creates this table.
            if (!Schema.Table("MovieEditionSlots").Exists())
            {
                Create.Table("MovieEditionSlots")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("MovieId").AsInt32().NotNullable()
                    .WithColumn("EditionName").AsString().NotNullable()
                    .WithColumn("SearchTerm").AsString().Nullable()
                    .WithColumn("Monitored").AsBoolean().NotNullable().WithDefaultValue(true)
                    .WithColumn("MovieFileId").AsInt32().Nullable()
                    .WithColumn("QualityProfileId").AsInt32().Nullable()
                    .WithColumn("MinimumCustomFormatScore").AsInt32().Nullable()
                    .WithColumn("LastSearchTime").AsDateTime().Nullable()
                    .WithColumn("DateAdded").AsDateTime().NotNullable();

                Create.Index("IX_MovieEditionSlots_MovieId_EditionName")
                    .OnTable("MovieEditionSlots")
                    .OnColumn("MovieId").Ascending()
                    .OnColumn("EditionName").Ascending()
                    .WithOptions().Unique();
            }

            // Queue the data copy as a runtime operation. Migration tests and restored
            // prototype databases can change shape immediately before this migration runs;
            // evaluating Schema here would observe the pre-expression shape and lose data.
            Execute.WithConnection(PreservePrototypeEditionValues);

            if (Schema.Table("Movies").Column("MovieEdition").Exists())
            {
                // The prototype also dropped IX_Movies_MovieMetadataId and replaced it with a
                // composite index; restore the original and remove the prototype index first
                // so the column can be safely dropped.
                Delete.Index("IX_Movies_MovieMetadataId_MovieEdition").OnTable("Movies");
                Create.Index("IX_Movies_MovieMetadataId")
                    .OnTable("Movies")
                    .OnColumn("MovieMetadataId").Ascending()
                    .WithOptions().Unique();

                Delete.Column("MovieEdition").FromTable("Movies");
            }

            if (Schema.Table("Movies").Column("EditionSearchTerm").Exists())
            {
                Delete.Column("EditionSearchTerm").FromTable("Movies");
            }
        }

        private static void PreservePrototypeEditionValues(IDbConnection connection, IDbTransaction transaction)
        {
            if (!HasColumn(connection, "Movies", "MovieEdition"))
            {
                return;
            }

            var searchTermExpression = HasColumn(connection, "Movies", "EditionSearchTerm")
                ? "NULLIF(TRIM(m.\"EditionSearchTerm\"), '')"
                : "NULL";

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
                    INSERT INTO ""MovieEditionSlots""
                        (""MovieId"", ""EditionName"", ""SearchTerm"", ""Monitored"", ""MovieFileId"", ""QualityProfileId"", ""MinimumCustomFormatScore"", ""LastSearchTime"", ""DateAdded"")
                    SELECT m.""Id"", TRIM(m.""MovieEdition""), {searchTermExpression}, 1, NULL, NULL, NULL, NULL, m.""Added""
                    FROM ""Movies"" m
                    WHERE m.""MovieEdition"" IS NOT NULL
                      AND TRIM(m.""MovieEdition"") <> ''
                      AND NOT EXISTS (
                          SELECT 1
                          FROM ""MovieEditionSlots"" s
                          WHERE s.""MovieId"" = m.""Id""
                            AND LOWER(TRIM(s.""EditionName"")) = LOWER(TRIM(m.""MovieEdition""))
                      )";
                command.ExecuteNonQuery();
            }
        }

        private static bool HasColumn(IDbConnection connection, string tableName, string columnName)
        {
            if (!(connection is DbConnection dbConnection))
            {
                return false;
            }

            var columns = dbConnection.GetSchema("Columns");
            foreach (DataRow row in columns.Rows)
            {
                if (string.Equals(Convert.ToString(row["TABLE_NAME"]), tableName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(row["COLUMN_NAME"]), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
