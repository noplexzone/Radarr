import React from 'react';
import { useSelector } from 'react-redux';
import ProgressBar from 'Components/ProgressBar';
import { sizes } from 'Helpers/Props';
import { MovieEditionSummary } from 'Movie/Editions/MovieEdition';
import createMovieQueueItemsDetailsSelector, {
  MovieQueueDetails,
} from 'Movie/Index/createMovieQueueDetailsSelector';
import { MovieStatus } from 'Movie/Movie';
import { MovieFile } from 'MovieFile/MovieFile';
import getProgressBarKind from 'Utilities/Movie/getProgressBarKind';
import translate from 'Utilities/String/translate';
import styles from './MovieIndexProgressBar.css';

interface MovieIndexProgressBarProps {
  movieId: number;
  movieFile?: MovieFile;
  editions?: MovieEditionSummary[];
  monitored: boolean;
  status: MovieStatus;
  hasFile: boolean;
  isAvailable: boolean;
  width: number;
  detailedProgressBar: boolean;
  bottomRadius?: boolean;
  isStandAlone?: boolean;
}

function MovieIndexProgressBar({
  movieId,
  movieFile,
  editions,
  monitored,
  status,
  hasFile,
  isAvailable,
  width,
  detailedProgressBar,
  bottomRadius,
  isStandAlone,
}: MovieIndexProgressBarProps) {
  const queueDetails: MovieQueueDetails = useSelector(
    createMovieQueueItemsDetailsSelector(movieId)
  );

  // With several editions the bar counts monitored editions that have a file, e.g. 1/2
  const monitoredEditions =
    editions && editions.length > 1 ? editions.filter((e) => e.monitored) : [];
  const editionCount = monitoredEditions.length;
  const editionFileCount = monitoredEditions.filter((e) => e.hasFile).length;
  const showEditions = editionCount > 0;
  const allFiles = showEditions ? editionFileCount === editionCount : hasFile;

  const progress = showEditions ? (editionFileCount / editionCount) * 100 : 100;
  const queueStatusText =
    queueDetails.count > 0 ? translate('Downloading') : null;

  let movieStatus = translate('NotAvailable');
  if (showEditions) {
    movieStatus = `${editionFileCount}/${editionCount}`;
  } else if (hasFile) {
    movieStatus = movieFile?.quality?.quality.name ?? translate('Downloaded');
  } else if (status === 'deleted') {
    movieStatus = translate('Deleted');
  } else if (isAvailable && !hasFile) {
    movieStatus = translate('Missing');
  }

  const attachedClassName = bottomRadius
    ? styles.progressRadius
    : styles.progress;
  const containerClassName = isStandAlone ? undefined : attachedClassName;

  return (
    <ProgressBar
      className={styles.progressBar}
      containerClassName={containerClassName}
      progress={progress}
      kind={getProgressBarKind(
        status,
        monitored,
        allFiles,
        isAvailable,
        queueDetails.count > 0
      )}
      size={detailedProgressBar ? sizes.MEDIUM : sizes.SMALL}
      showText={detailedProgressBar}
      width={width}
      text={queueStatusText ? queueStatusText : movieStatus}
    />
  );
}

export default MovieIndexProgressBar;
