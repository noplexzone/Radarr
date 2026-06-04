using NUnit.Framework;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Movies;

[Parallelizable(ParallelScope.All)]
public class MovieEditionSlotResourceMapperFixture
{
    [Test]
    public void ToModel_trims_edition_name()
    {
        var resource = new MovieEditionSlotResource
        {
            MovieId = 1,
            EditionName = "  Extended Cut  ",
            Monitored = true,
        };

        var model = resource.ToModel();

        Assert.That(model.EditionName, Is.EqualTo("Extended Cut"));
    }

    [Test]
    public void ToModel_null_edition_name_becomes_empty_string()
    {
        var resource = new MovieEditionSlotResource
        {
            MovieId = 1,
            EditionName = null,
            Monitored = true,
        };

        var model = resource.ToModel();

        Assert.That(model.EditionName, Is.EqualTo(string.Empty));
    }

    [Test]
    public void ToModel_whitespace_search_term_becomes_null()
    {
        var resource = new MovieEditionSlotResource
        {
            MovieId = 1,
            EditionName = "Director's Cut",
            SearchTerm = "   ",
            Monitored = true,
        };

        var model = resource.ToModel();

        Assert.That(model.SearchTerm, Is.Null);
    }

    [Test]
    public void ToModel_trims_search_term()
    {
        var resource = new MovieEditionSlotResource
        {
            MovieId = 1,
            EditionName = "Director's Cut",
            SearchTerm = "  director cut  ",
            Monitored = true,
        };

        var model = resource.ToModel();

        Assert.That(model.SearchTerm, Is.EqualTo("director cut"));
    }

    [Test]
    public void ToResource_round_trips_all_fields()
    {
        var resource = new MovieEditionSlotResource
        {
            Id = 7,
            MovieId = 42,
            EditionName = "IMAX",
            SearchTerm = "imax",
            Monitored = false,
            MovieFileId = 3,
            QualityProfileId = 1,
            MinimumCustomFormatScore = 10,
        };

        var model = resource.ToModel();
        var back = model.ToResource();

        Assert.That(back.MovieId, Is.EqualTo(42));
        Assert.That(back.EditionName, Is.EqualTo("IMAX"));
        Assert.That(back.SearchTerm, Is.EqualTo("imax"));
        Assert.That(back.Monitored, Is.False);
        Assert.That(back.MovieFileId, Is.EqualTo(3));
        Assert.That(back.QualityProfileId, Is.EqualTo(1));
        Assert.That(back.MinimumCustomFormatScore, Is.EqualTo(10));
    }
}
