import React, { useCallback } from 'react';
import { useDispatch } from 'react-redux';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { scrollDirections, sizes } from 'Helpers/Props';
import InteractiveSearch from 'InteractiveSearch/InteractiveSearch';
import Movie from 'Movie/Movie';
import useMovie from 'Movie/useMovie';
import { clearMovieBlocklist } from 'Store/Actions/movieBlocklistActions';
import { clearMovieHistory } from 'Store/Actions/movieHistoryActions';
import {
  cancelFetchReleases,
  clearReleases,
} from 'Store/Actions/releaseActions';
import translate from 'Utilities/String/translate';

interface MovieEditionSlotInteractiveSearchModalProps {
  isOpen: boolean;
  movieId: number;
  movieEditionSlotId: number;
  editionName: string;
  onModalClose(): void;
}

function MovieEditionSlotInteractiveSearchModal({
  isOpen,
  movieId,
  movieEditionSlotId,
  editionName,
  onModalClose,
}: MovieEditionSlotInteractiveSearchModalProps) {
  const dispatch = useDispatch();

  const { title, year } = useMovie(movieId) as Movie;

  const handleModalClose = useCallback(() => {
    dispatch(cancelFetchReleases());
    dispatch(clearReleases());
    dispatch(clearMovieBlocklist());
    dispatch(clearMovieHistory());
    onModalClose();
  }, [dispatch, onModalClose]);

  const movieTitle = `${title}${year > 0 ? ` (${year})` : ''}`;
  const headerTitle = editionName
    ? `${movieTitle} — ${editionName}`
    : movieTitle;

  return (
    <Modal
      isOpen={isOpen}
      closeOnBackgroundClick={false}
      size={sizes.EXTRA_EXTRA_LARGE}
      onModalClose={handleModalClose}
    >
      <ModalContent onModalClose={handleModalClose}>
        <ModalHeader>
          {translate('InteractiveSearchModalHeaderTitle', {
            title: headerTitle,
          })}
        </ModalHeader>

        <ModalBody scrollDirection={scrollDirections.BOTH}>
          <InteractiveSearch searchPayload={{ movieId, movieEditionSlotId }} />
        </ModalBody>

        <ModalFooter>
          <Button onPress={handleModalClose}>{translate('Close')}</Button>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

export default MovieEditionSlotInteractiveSearchModal;
