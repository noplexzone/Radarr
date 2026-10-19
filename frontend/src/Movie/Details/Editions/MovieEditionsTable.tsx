import React, { useCallback, useEffect, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import * as commandNames from 'Commands/commandNames';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Column from 'Components/Table/Column';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import usePrevious from 'Helpers/Hooks/usePrevious';
import { icons, kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/Editions/MovieEdition';
import {
  useMovieEditionActions,
  useMovieEditions,
} from 'Movie/Editions/useMovieEditions';
import useMovie from 'Movie/useMovie';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchMovieFiles } from 'Store/Actions/movieFileActions';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import EditMovieEditionModal from './EditMovieEditionModal';
import MovieEditionRow from './MovieEditionRow';
import styles from './MovieEditionsTable.css';

const columns: Column[] = [
  {
    name: 'monitored',
    label: '',
    isVisible: true,
  },
  {
    name: 'title',
    label: () => translate('Edition'),
    isVisible: true,
  },
  {
    name: 'qualityProfileId',
    label: () => translate('QualityProfile'),
    isVisible: true,
  },
  {
    name: 'quality',
    label: () => translate('Quality'),
    isVisible: true,
  },
  {
    name: 'fileCount',
    label: () => translate('Files'),
    isVisible: true,
  },
  {
    name: 'actions',
    label: '',
    isVisible: true,
  },
];

interface MovieEditionsTableProps {
  movieId: number;
}

function MovieEditionsTable({ movieId }: MovieEditionsTableProps) {
  const dispatch = useDispatch();
  const movie = useMovie(movieId);
  const { data, isFetching, error, refetch } = useMovieEditions(movieId);
  const { saveEdition, deleteEdition } = useMovieEditionActions();
  const commands = useSelector((state: AppState) => state.commands.items);
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  // The movie is pushed again whenever its files or editions change, the file details below need to follow
  const previousMovie = usePrevious(movie);

  useEffect(() => {
    if (previousMovie && previousMovie !== movie) {
      refetch();
    }
  }, [movie, previousMovie, refetch]);

  const handleMonitorPress = useCallback(
    async (edition: MovieEdition, monitored: boolean) => {
      try {
        setActionError(null);
        await saveEdition({ ...edition, monitored });
      } catch (e) {
        setActionError(getErrorMessage(e as ApiError));
      }
    },
    [saveEdition]
  );

  const handleSearchPress = useCallback(
    (edition: MovieEdition) => {
      dispatch(
        executeCommand({
          name: commandNames.MOVIE_EDITION_SEARCH,
          movieId,
          movieEditionId: edition.id,
        })
      );
    },
    [movieId, dispatch]
  );

  const handleDeletePress = useCallback(
    async (edition: MovieEdition, deleteFiles: boolean) => {
      try {
        setActionError(null);
        await deleteEdition(edition.id, deleteFiles);
        dispatch(fetchMovieFiles({ movieId }));
      } catch (e) {
        setActionError(getErrorMessage(e as ApiError));
      }
    },
    [movieId, deleteEdition, dispatch]
  );

  const handleAddPress = useCallback(() => {
    setIsAddModalOpen(true);
  }, []);

  const handleAddModalClose = useCallback(() => {
    setIsAddModalOpen(false);
  }, []);

  if (!data && error) {
    return (
      <Alert kind={kinds.DANGER}>
        {getErrorMessage(error, translate('EditionsLoadError'))}
      </Alert>
    );
  }

  return (
    <div className={styles.container}>
      {!data && isFetching ? <LoadingIndicator /> : null}

      {actionError ? <Alert kind={kinds.DANGER}>{actionError}</Alert> : null}

      {data ? (
        <Table columns={columns}>
          <TableBody>
            {data.map((edition) => (
              <MovieEditionRow
                key={edition.id}
                edition={edition}
                movieQualityProfileId={movie?.qualityProfileId ?? 0}
                isSearching={isCommandExecuting(
                  findCommand(commands, {
                    name: commandNames.MOVIE_EDITION_SEARCH,
                    movieEditionId: edition.id,
                  })
                )}
                onMonitorPress={handleMonitorPress}
                onSearchPress={handleSearchPress}
                onDeletePress={handleDeletePress}
              />
            ))}
          </TableBody>
        </Table>
      ) : null}

      <div className={styles.addButtonContainer}>
        <Button onPress={handleAddPress}>
          <Icon name={icons.ADD} /> {translate('AddEdition')}
        </Button>
      </div>

      <EditMovieEditionModal
        isOpen={isAddModalOpen}
        movieId={movieId}
        onSave={saveEdition}
        onModalClose={handleAddModalClose}
      />
    </div>
  );
}

export default MovieEditionsTable;
