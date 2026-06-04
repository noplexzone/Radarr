using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(243)]
    public class add_movie_edition : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("Movies").AddColumn("MovieEdition").AsString().WithDefaultValue("");

            // Optional freeform terms appended to indexer search queries for this edition.
            // Populated into MovieSearchCriteria.EditionSearchTerm; see ReleaseSearchService.
            Alter.Table("Movies").AddColumn("EditionSearchTerm").AsString().Nullable();

            // Replace the 1:1 unique constraint on MovieMetadataId with a composite
            // unique on (MovieMetadataId, MovieEdition) so multiple editions of the
            // same TMDB movie can coexist as distinct Movie rows sharing one metadata row.
            Delete.Index("IX_Movies_MovieMetadataId").OnTable("Movies");

            Create.Index("IX_Movies_MovieMetadataId_MovieEdition")
                .OnTable("Movies")
                .OnColumn("MovieMetadataId").Ascending()
                .OnColumn("MovieEdition").Ascending()
                .WithOptions().Unique();
        }
    }
}
