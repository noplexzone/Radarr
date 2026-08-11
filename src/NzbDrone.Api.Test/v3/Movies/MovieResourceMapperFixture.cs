using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Movies.MovieEditionSlots;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Movies;

[Parallelizable(ParallelScope.All)]
public class MovieEditionSlotResourceMapperFixture
{
    [Test]
    public void create_request_trims_edition_name()
    {
        var request = new CreateMovieEditionRequest
        {
            MovieId = 1,
            EditionName = "  Extended Cut  ",
            Monitored = true,
        };

        var model = request.ToModel();

        Assert.That(model.EditionName, Is.EqualTo("Extended Cut"));
    }

    [Test]
    public void create_request_null_edition_name_becomes_empty_string()
    {
        var request = new CreateMovieEditionRequest
        {
            MovieId = 1,
            EditionName = null,
            Monitored = true,
        };

        var model = request.ToModel();

        Assert.That(model.EditionName, Is.EqualTo(string.Empty));
    }

    [Test]
    public void create_request_whitespace_search_term_becomes_null()
    {
        var request = new CreateMovieEditionRequest
        {
            MovieId = 1,
            EditionName = "Director's Cut",
            SearchTerm = "   ",
            Monitored = true,
        };

        var model = request.ToModel();

        Assert.That(model.SearchTerm, Is.Null);
    }

    [Test]
    public void create_request_trims_search_term()
    {
        var request = new CreateMovieEditionRequest
        {
            MovieId = 1,
            EditionName = "Director's Cut",
            SearchTerm = "  director cut  ",
            Monitored = true,
        };

        var model = request.ToModel();

        Assert.That(model.SearchTerm, Is.EqualTo("director cut"));
    }

    [Test]
    public void create_request_maps_only_create_fields()
    {
        var request = new CreateMovieEditionRequest
        {
            MovieId = 42,
            EditionName = "IMAX",
            SearchTerm = "imax",
            Aliases = new List<string> { "IMAX Enhanced" },
            Monitored = true,
            QualityProfileId = 3,
            MinimumCustomFormatScore = 25
        };

        var model = request.ToModel();

        Assert.That(model.Id, Is.EqualTo(0));
        Assert.That(model.MovieId, Is.EqualTo(42));
        Assert.That(model.DateAdded, Is.EqualTo(default(DateTime)));
        Assert.That(model.LastSearchTime, Is.Null);
        Assert.That(model.EditionName, Is.EqualTo("IMAX"));
        Assert.That(model.SearchTerm, Is.EqualTo("imax"));
        Assert.That(model.Aliases, Is.EqualTo(new[] { "IMAX Enhanced" }));
        Assert.That(model.Monitored, Is.True);
        Assert.That(model.QualityProfileId, Is.EqualTo(3));
        Assert.That(model.MinimumCustomFormatScore, Is.EqualTo(25));
    }

    [Test]
    public void update_request_preserves_server_managed_identity_and_search_state()
    {
        var persisted = new MovieEditionSlot
        {
            Id = 7,
            MovieId = 42,
            EditionName = "IMAX",
            SearchTerm = "imax",
            Aliases = new List<string> { "old" },
            Monitored = false,
            QualityProfileId = 1,
            MinimumCustomFormatScore = 10,
            LastSearchTime = DateTime.UtcNow.AddDays(-1),
            DateAdded = DateTime.UtcNow.AddDays(-10)
        };
        var request = new UpdateMovieEditionRequest
        {
            EditionName = "  Director's Cut  ",
            SearchTerm = "  directors  ",
            Aliases = new List<string> { "DC" },
            Monitored = true,
            QualityProfileId = 3,
            MinimumCustomFormatScore = 25
        };

        var model = request.ToModel(persisted);

        Assert.That(model.Id, Is.EqualTo(persisted.Id));
        Assert.That(model.MovieId, Is.EqualTo(persisted.MovieId));
        Assert.That(model.LastSearchTime, Is.EqualTo(persisted.LastSearchTime));
        Assert.That(model.DateAdded, Is.EqualTo(persisted.DateAdded));
        Assert.That(model.EditionName, Is.EqualTo("Director's Cut"));
        Assert.That(model.SearchTerm, Is.EqualTo("directors"));
        Assert.That(model.Aliases, Is.EqualTo(new[] { "DC" }));
        Assert.That(model.Monitored, Is.True);
        Assert.That(model.QualityProfileId, Is.EqualTo(3));
        Assert.That(model.MinimumCustomFormatScore, Is.EqualTo(25));
    }

    [Test]
    public void remove_request_file_action_is_nullable_so_empty_body_preserves_default_compatibility()
    {
        var request = new RemoveMovieEditionRequest();

        Assert.That(request.AttachedFileAction, Is.Null);
        Assert.That(request.ExistingMainFileAction, Is.Null);
    }

    [Test]
    public void convert_to_main_request_requires_explicit_existing_main_action()
    {
        var request = new ConvertMovieEditionFileToMainRequest();

        Assert.That(request.ExistingMainFileAction, Is.Null);
    }

    [Test]
    public void response_resource_does_not_map_body_fields_back_to_model()
    {
        Assert.That(typeof(MovieEditionSlotResourceMapper).GetMethod("ToModel", new[] { typeof(MovieEditionSlotResource) }), Is.Null);
    }

    [Test]
    public void to_resource_exposes_read_model_fields()
    {
        var model = new MovieEditionSlot
        {
            Id = 7,
            MovieId = 42,
            EditionName = "IMAX",
            SearchTerm = "imax",
            Aliases = new List<string> { "IMAX Enhanced" },
            Monitored = false,
            QualityProfileId = 1,
            MinimumCustomFormatScore = 10,
            LastSearchTime = DateTime.UtcNow.AddHours(-2),
            DateAdded = DateTime.UtcNow.AddDays(-3)
        };

        var resource = model.ToResource();

        Assert.That(resource.Id, Is.EqualTo(7));
        Assert.That(resource.MovieId, Is.EqualTo(42));
        Assert.That(resource.EditionName, Is.EqualTo("IMAX"));
        Assert.That(resource.SearchTerm, Is.EqualTo("imax"));
        Assert.That(resource.Aliases, Is.EqualTo(new[] { "IMAX Enhanced" }));
        Assert.That(resource.Monitored, Is.False);
        Assert.That(resource.QualityProfileId, Is.EqualTo(1));
        Assert.That(resource.MinimumCustomFormatScore, Is.EqualTo(10));
        Assert.That(resource.LastSearchTime, Is.EqualTo(model.LastSearchTime));
        Assert.That(resource.DateAdded, Is.EqualTo(model.DateAdded));
    }
}
