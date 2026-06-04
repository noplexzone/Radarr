using NUnit.Framework;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Movies;

[Parallelizable(ParallelScope.All)]
public class MovieResourceMapperFixture
{
    [Test]
    public void ToModel_without_movieEdition_should_produce_empty_string_not_null()
    {
        var resource = new MovieResource
        {
            TmdbId = 123,
            Title = "Test Movie",
            // MovieEdition intentionally omitted (simulates normal UI/API payload)
        };

        var model = resource.ToModel();

        Assert.That(model.MovieEdition, Is.EqualTo(string.Empty),
            "MovieEdition must default to empty string to satisfy the NOT NULL DB constraint");
    }

    [Test]
    public void ToModel_with_explicit_movieEdition_should_preserve_value()
    {
        var resource = new MovieResource
        {
            TmdbId = 456,
            Title = "Extended Movie",
            MovieEdition = "Extended Cut",
        };

        var model = resource.ToModel();

        Assert.That(model.MovieEdition, Is.EqualTo("Extended Cut"));
    }

    [Test]
    public void ToModel_with_empty_movieEdition_should_produce_empty_string()
    {
        var resource = new MovieResource
        {
            TmdbId = 789,
            Title = "Normal Movie",
            MovieEdition = "",
        };

        var model = resource.ToModel();

        Assert.That(model.MovieEdition, Is.EqualTo(string.Empty));
    }
}
