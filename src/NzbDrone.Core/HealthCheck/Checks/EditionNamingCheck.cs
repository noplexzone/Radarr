using System.Text.RegularExpressions;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ModelEvent<NamingConfig>))]
    [CheckOn(typeof(ModelEvent<MovieEdition>))]
    public class EditionNamingCheck : HealthCheckBase, IProvideHealthCheck
    {
        private static readonly Regex EditionTokenRegex = new Regex(@"\{[^{}]*Edition[- ._]Tags[^{}]*\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly INamingConfigService _namingConfigService;
        private readonly IMovieEditionService _movieEditionService;

        public EditionNamingCheck(INamingConfigService namingConfigService, IMovieEditionService movieEditionService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _namingConfigService = namingConfigService;
            _movieEditionService = movieEditionService;
        }

        public override HealthCheck Check()
        {
            var namingConfig = _namingConfigService.GetConfig();

            // Without the edition in the file name, several editions in one movie folder can't be told apart
            if (!namingConfig.RenameMovies || EditionTokenRegex.IsMatch(namingConfig.StandardMovieFormat ?? string.Empty))
            {
                return new HealthCheck(GetType());
            }

            if (!_movieEditionService.AnyMovieHasMultipleEditions())
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(),
                HealthCheckResult.Warning,
                HealthCheckReason.EditionNamingFormat,
                _localizationService.GetLocalizedString("EditionNamingCheckMessage"));
        }
    }
}
