using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableOperationRecoveryService
    {
        IReadOnlyList<int> Classify(int maximumOperations);
    }

    public sealed class RecoverableOperationRecoveryService : IRecoverableOperationRecoveryService,
                                                              IHandle<ApplicationStartedEvent>,
                                                              IExecute<RecoverRecoverableOperationsCommand>
    {
        private const int StartupRecoveryLimit = 100;
        private const string ImportLeasePrefix = "recover-import-";
        private readonly IRecoverableOperationRepository _repository;
        private readonly IRecoverableMovieFileDeletionRecoveryHandler _deleteRecoveryHandler;
        private readonly IRecoverableMovieFileImportCoordinator _importRecoveryHandler;
        private readonly IRecoverableOperationLeasePolicy _leasePolicy;

        // Retained for the Task 5A repository fixtures and safe unsupported-operation classification.
        public RecoverableOperationRecoveryService(IRecoverableOperationRepository repository)
            : this(repository, null, null, null)
        {
        }

        public RecoverableOperationRecoveryService(IRecoverableOperationRepository repository,
                                                    IRecoverableMovieFileDeletionRecoveryHandler deleteRecoveryHandler)
            : this(repository, deleteRecoveryHandler, null, null)
        {
        }

        public RecoverableOperationRecoveryService(IRecoverableOperationRepository repository,
                                                    IRecoverableMovieFileDeletionRecoveryHandler deleteRecoveryHandler,
                                                    IRecoverableMovieFileImportCoordinator importRecoveryHandler,
                                                    IRecoverableOperationLeasePolicy leasePolicy)
        {
            _repository = repository;
            _deleteRecoveryHandler = deleteRecoveryHandler;
            _importRecoveryHandler = importRecoveryHandler;
            _leasePolicy = leasePolicy;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            Classify(StartupRecoveryLimit);
        }

        public void Execute(RecoverRecoverableOperationsCommand message)
        {
            Classify(StartupRecoveryLimit);
        }

        public IReadOnlyList<int> Classify(int maximumOperations)
        {
            var rows = _repository.ListRecoverable(maximumOperations, DateTime.UtcNow);
            foreach (var row in rows)
            {
                try
                {
                    if (row.OperationType == RecoverableOperationType.Delete && _deleteRecoveryHandler != null)
                    {
                        _deleteRecoveryHandler.Recover(row);
                    }
                    else if (row.OperationType == RecoverableOperationType.Import && _importRecoveryHandler != null && _leasePolicy != null)
                    {
                        var owner = ImportLeasePrefix + Guid.NewGuid().ToString("N");
                        var now = DateTime.UtcNow;
                        var leased = _repository.AcquireLease(row.Id, row.Version, owner, now, now.Add(_leasePolicy.LeaseDuration));
                        _importRecoveryHandler.Recover(leased, owner);
                    }
                    else
                    {
                        _repository.MarkRecoveryRequired(row.Id, row.State, row.Version, "No recovery handler is registered for this operation type.");
                    }
                }
                catch (Exception exception)
                {
                    QuarantineSafelyUnleased(row.Id, exception);
                }
            }

            return rows.Select(x => x.Id).ToList();
        }

        private void QuarantineSafelyUnleased(int id, Exception exception)
        {
            try
            {
                var current = _repository.GetById(id);
                var now = DateTime.UtcNow;
                if (current.State != RecoverableOperationState.RecoveryRequired &&
                    (current.LeaseOwner == null || current.LeaseExpiresAt <= now))
                {
                    _repository.MarkRecoveryRequired(current.Id, current.State, current.Version, "Recovery failed before a handler could safely quarantine the row: " + exception.Message, now: now);
                }
            }
            catch (RecoverableOperationException)
            {
                // A concurrent owner changed the row; never steal or overwrite its evidence.
            }
        }
    }
}
