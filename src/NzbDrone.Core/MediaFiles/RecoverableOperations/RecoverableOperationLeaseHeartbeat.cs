using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.MediaFiles.RecoverableOperations
{
    public interface IRecoverableOperationLeasePolicy
    {
        TimeSpan LeaseDuration { get; }
        TimeSpan HeartbeatInterval { get; }
    }

    public sealed class RecoverableOperationLeasePolicy : IRecoverableOperationLeasePolicy
    {
        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
        public TimeSpan HeartbeatInterval => TimeSpan.FromMinutes(1);
    }

    public interface IRecoverableOperationLeaseHeartbeat
    {
        RecoverableOperation Run(RecoverableOperation operation, string owner, Action blockingOperation);
    }

    public sealed class RecoverableOperationLeaseHeartbeat : IRecoverableOperationLeaseHeartbeat
    {
        private readonly IRecoverableOperationRepository _repository;
        private readonly IRecoverableOperationLeasePolicy _policy;

        public RecoverableOperationLeaseHeartbeat(IRecoverableOperationRepository repository,
                                                   IRecoverableOperationLeasePolicy policy)
        {
            _repository = repository;
            _policy = policy;
        }

        public RecoverableOperation Run(RecoverableOperation operation, string owner, Action blockingOperation)
        {
            var gate = new object();
            var renewedAt = DateTime.UtcNow;
            var latest = _repository.RenewLease(operation.Id, operation.Version, owner, renewedAt, renewedAt.Add(_policy.LeaseDuration));
            Exception heartbeatFailure = null;
            using var stopped = new CancellationTokenSource();
            var heartbeat = Task.Run(() =>
            {
                while (!stopped.Token.WaitHandle.WaitOne(_policy.HeartbeatInterval))
                {
                    try
                    {
                        lock (gate)
                        {
                            var now = DateTime.UtcNow;
                            latest = _repository.RenewLease(latest.Id, latest.Version, owner, now, now.Add(_policy.LeaseDuration));
                        }
                    }
                    catch (Exception exception)
                    {
                        heartbeatFailure = exception;
                        return;
                    }
                }
            });

            Exception operationFailure = null;
            try
            {
                blockingOperation();
            }
            catch (Exception exception)
            {
                operationFailure = exception;
            }
            finally
            {
                stopped.Cancel();
                heartbeat.GetAwaiter().GetResult();
            }

            if (heartbeatFailure != null)
            {
                throw new RecoverableOperationLeaseHeartbeatException("The recoverable operation lease heartbeat failed while filesystem work was in progress.", heartbeatFailure);
            }

            if (operationFailure != null)
            {
                ExceptionDispatchInfo.Capture(operationFailure).Throw();
            }

            lock (gate)
            {
                return latest;
            }
        }
    }

    public sealed class RecoverableOperationLeaseHeartbeatException : RecoverableOperationException
    {
        public RecoverableOperationLeaseHeartbeatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
