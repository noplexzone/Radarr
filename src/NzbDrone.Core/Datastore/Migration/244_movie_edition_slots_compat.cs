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

            // Remove prototype columns if the old migration 243 added them.
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
    }
}
