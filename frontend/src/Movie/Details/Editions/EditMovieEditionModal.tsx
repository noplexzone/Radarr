import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import { inputTypes, kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/Editions/MovieEdition';
import { SaveMovieEdition } from 'Movie/Editions/useMovieEditions';
import useMovie from 'Movie/useMovie';
import { InputChanged } from 'typings/inputs';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

interface EditMovieEditionModalProps {
  isOpen: boolean;
  movieId: number;

  // Adds a new edition when not given
  edition?: MovieEdition;
  onSave(edition: SaveMovieEdition): Promise<void>;
  onModalClose(): void;
}

function EditMovieEditionModal({
  isOpen,
  movieId,
  edition,
  onSave,
  onModalClose,
}: EditMovieEditionModalProps) {
  const movie = useMovie(movieId);
  const qualityProfiles = useSelector(
    (state: AppState) => state.settings.qualityProfiles.items
  );

  const [title, setTitle] = useState('');
  const [monitored, setMonitored] = useState(true);
  const [qualityProfileId, setQualityProfileId] = useState(0);
  const [searchForEdition, setSearchForEdition] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (isOpen) {
      setTitle(edition?.title ?? '');
      setMonitored(edition?.monitored ?? true);
      setQualityProfileId(edition?.qualityProfileId ?? 0);
      setSearchForEdition(false);
      setError(null);
    }
  }, [isOpen, edition]);

  const profileOptions = useMemo(() => {
    const movieProfile = qualityProfiles.find(
      (p) => p.id === movie?.qualityProfileId
    );

    return [
      {
        key: 0,
        value: translate('EditionUseMovieQualityProfile', {
          name: movieProfile?.name ?? '',
        }),
      },
      ...qualityProfiles.map((p) => ({ key: p.id, value: p.name })),
    ];
  }, [qualityProfiles, movie]);

  const handleInputChange = useCallback(({ name, value }: InputChanged) => {
    switch (name) {
      case 'title':
        setTitle(value as string);
        break;
      case 'monitored':
        setMonitored(value as boolean);
        break;
      case 'qualityProfileId':
        setQualityProfileId(Number(value));
        break;
      case 'searchForEdition':
        setSearchForEdition(value as boolean);
        break;
      default:
        break;
    }
  }, []);

  const handleSavePress = useCallback(async () => {
    setIsSaving(true);
    setError(null);

    try {
      await onSave({
        id: edition?.id,
        movieId,
        title,
        monitored,
        qualityProfileId,
        searchForEdition: edition ? undefined : searchForEdition,
      });

      onModalClose();
    } catch (e) {
      setError(getErrorMessage(e as ApiError, translate('EditionSaveError')));
    } finally {
      setIsSaving(false);
    }
  }, [
    edition,
    movieId,
    title,
    monitored,
    qualityProfileId,
    searchForEdition,
    onSave,
    onModalClose,
  ]);

  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {edition ? translate('EditEdition') : translate('AddEdition')}
        </ModalHeader>

        <ModalBody>
          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

          <Form>
            <FormGroup>
              <FormLabel>{translate('Name')}</FormLabel>

              <FormInputGroup
                type={inputTypes.TEXT}
                name="title"
                value={title}
                helpText={translate('EditionNameHelpText')}
                onChange={handleInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('Monitored')}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="monitored"
                value={monitored}
                helpText={translate('EditionMonitoredHelpText')}
                onChange={handleInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('QualityProfile')}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="qualityProfileId"
                value={qualityProfileId}
                values={profileOptions}
                onChange={handleInputChange}
              />
            </FormGroup>

            {edition ? null : (
              <FormGroup>
                <FormLabel>{translate('StartSearchForMissingMovie')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="searchForEdition"
                  value={searchForEdition}
                  onChange={handleInputChange}
                />
              </FormGroup>
            )}
          </Form>
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>{translate('Cancel')}</Button>

          <SpinnerButton isSpinning={isSaving} onPress={handleSavePress}>
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

export default EditMovieEditionModal;
