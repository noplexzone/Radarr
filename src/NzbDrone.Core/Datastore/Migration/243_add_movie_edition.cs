using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(243)]
    public class add_movie_edition : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
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
    }
}
