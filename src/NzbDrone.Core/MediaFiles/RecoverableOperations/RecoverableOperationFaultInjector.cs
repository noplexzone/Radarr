namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public enum RecoverableOperationFaultPoint
    {
        AfterPendingCommit = 1,
        AfterStageTransfer = 2,
        BeforeDatabaseTransaction = 3,
        AfterDatabaseMutation = 4,
        AfterInvariantAudit = 5,
        AfterDatabaseCommit = 6,
        BeforeFinalize = 7,
        AfterFinalize = 8,
        BeforeEventDispatch = 9,
        AfterEventDispatch = 10,
        AfterMovieFileDeletedPublish = 11,
        AfterDeleteCompletedPublish = 12,
        AfterImportSourceToCandidate = 13,
        AfterImportOutgoingToBackup = 14,
        AfterImportCandidateToDestination = 15,
        AfterImportStaged = 16,
        AfterImportApplyingDatabase = 17,
        AfterImportDatabaseCommit = 18,
        AfterImportRecycle = 19,
        AfterImportSourceTransfer = 20,
        AfterImportDestinationTransfer = 21,
        AfterImportRecycleAction = 22,
        AfterImportRollbackBegin = 23
    }

    public interface IRecoverableOperationFaultInjector
    {
        void Check(RecoverableOperationFaultPoint point);
    }

    public sealed class RecoverableOperationFaultInjector : IRecoverableOperationFaultInjector
    {
        public void Check(RecoverableOperationFaultPoint point)
        {
        }
    }
}
