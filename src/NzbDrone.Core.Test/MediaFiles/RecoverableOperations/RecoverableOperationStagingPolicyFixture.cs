using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.Test.Framework;
namespace NzbDrone.Core.Test.MediaFiles.RecoverableOperations
{
    [TestFixture]
    public class RecoverableOperationStagingPolicyFixture : CoreTest
    {
        [Test] public void should_return_contained_paths_without_touching_disk() { var movie = Path.Combine(TempFolder, "Movie"); var p = RecoverableOperationStagingPolicy.GetPaths(movie, "operation-01"); p.Root.Should().Be(Path.Combine(Path.GetFullPath(movie), ".radarr-recovery", "operation-01") + Path.DirectorySeparatorChar); p.Source.Should().Be(Path.Combine(p.Root, "source")); p.Backup.Should().Be(Path.Combine(p.Root, "backup")); p.Candidate.Should().Be(Path.Combine(p.Root, "candidate")); Directory.Exists(movie).Should().BeFalse(); Directory.Exists(p.Root).Should().BeFalse(); }
        [TestCase("../escape")][TestCase("a/b")][TestCase("a\\b")][TestCase(".")][TestCase("")] public void should_reject_unsafe_keys(string key) { Action a = () => RecoverableOperationStagingPolicy.GetPaths(Path.Combine(TempFolder, "Movie"), key); a.Should().Throw<ArgumentException>(); }
        [Test] public void should_reject_non_rooted_movie_paths() { Action a = () => RecoverableOperationStagingPolicy.GetPaths("relative/movie", "safe-key"); a.Should().Throw<ArgumentException>(); }
    }
}
