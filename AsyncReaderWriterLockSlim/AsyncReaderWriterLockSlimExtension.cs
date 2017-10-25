using System;
using System.Threading;
using System.Threading.Tasks;

namespace KPreisser
{
    /// <summary>
    /// Contains extension methods for <see cref="AsyncReaderWriterLockSlim"/>.
    /// </summary>
    public static class AsyncReaderWriterLockSlimExtension
    {
        /// <summary>
        /// Enters the lock in read mode.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="IDisposableLock"/> that will release the lock when disposed.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static IDisposableLock GetReadLock(
                this AsyncReaderWriterLockSlim lockInstance,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            lockInstance.EnterReadLock(cancellationToken);

            return new ActionDisposableLock(lockInstance.ExitReadLock, lockInstance, false);
        }

        /// <summary>
        /// Asynchronously enters the lock in read mode and returns a <see cref="IDisposableLock"/> that
        /// will release the lock when disposed.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete with a <see cref="IDisposableLock"/> when the lock has been entered,
        /// which will release the lock when disposed.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static async Task<IDisposableLock> GetReadLockAsync(
                this AsyncReaderWriterLockSlim lockInstance,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await lockInstance.EnterReadLockAsync(cancellationToken);

            return new ActionDisposableLock(lockInstance.ExitReadLock, lockInstance, false);
        }

        /// <summary>
        /// Tries to enter the lock in read mode, with an optional integer time-out.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="IDisposableLock"/> that will release the lock when disposed if the lock
        /// could be entered, or <c>null</c> otherwise.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static IDisposableLock TryGetReadLock(
                this AsyncReaderWriterLockSlim lockInstance,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {

            bool returnValue = lockInstance.TryEnterReadLock(
                    millisecondsTimeout, cancellationToken);

            if (returnValue)
                return new ActionDisposableLock(lockInstance.ExitReadLock, lockInstance, false);
            else
                return null;
        }

        /// <summary>
        /// Tries to asynchronously enter the lock in read mode, with an optional integer time-out.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete with a <see cref="IDisposableLock"/> that will release the lock
        /// when disposed if the lock could be entered, or with <c>null</c> otherwise.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static async Task<IDisposableLock> TryGetReadLockAsync(
                this AsyncReaderWriterLockSlim lockInstance,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            bool returnValue = await lockInstance.TryEnterReadLockAsync(
                    millisecondsTimeout, cancellationToken);

            if (returnValue)
                return new ActionDisposableLock(lockInstance.ExitReadLock, lockInstance, false);
            else
                return null;
        }

        /// <summary>
        /// Enters the lock in write mode.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="IDisposableLock"/> that will release the lock when disposed.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static IDisposableLock GetWriteLock(
                this AsyncReaderWriterLockSlim lockInstance,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            lockInstance.EnterWriteLock(cancellationToken);

            return new ActionDisposableLock(lockInstance.ExitWriteLock, lockInstance, true);
        }

        /// <summary>
        /// Asynchronously enters the lock in write mode.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>>A task that will complete with a <see cref="IDisposableLock"/> when the lock has been entered,
        /// which will release the lock when disposed.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static async Task<IDisposableLock> GetWriteLockAsync(
                this AsyncReaderWriterLockSlim lockInstance,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await lockInstance.EnterWriteLockAsync(cancellationToken);

            return new ActionDisposableLock(lockInstance.ExitWriteLock, lockInstance, true);
        }

        /// <summary>
        /// Tries to enter the lock in write mode, with an optional integer time-out.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="IDisposableLock"/> that will release the lock when disposed if the lock
        /// could be entered, or <c>null</c> otherwise.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static IDisposableLock TryGetWriteLock(
                this AsyncReaderWriterLockSlim lockInstance,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {

            bool returnValue = lockInstance.TryEnterWriteLock(
                    millisecondsTimeout, cancellationToken);

            if (returnValue)
                return new ActionDisposableLock(lockInstance.ExitWriteLock, lockInstance, true);
            else
                return null;
        }

        /// <summary>
        /// Tries to asynchronously enter the lock in write mode, with an optional integer time-out.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>>A task that will complete with a <see cref="IDisposableLock"/> that will release the lock
        /// when disposed if the lock could be entered, or with <c>null</c> otherwise.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static async Task<IDisposableLock> TryGetWriteLockAsync(
                this AsyncReaderWriterLockSlim lockInstance,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            bool returnValue = await lockInstance.TryEnterWriteLockAsync(
                    millisecondsTimeout, cancellationToken);

            if (returnValue)
                return new ActionDisposableLock(lockInstance.ExitWriteLock, lockInstance, true);
            else
                return null;
        }

        /// <summary>
        /// Downgrades the lock from write mode to read mode.
        /// </summary>
        /// <param name="lockInstance">The <see cref="AsyncReaderWriterLockSlim"/> instance.</param>
        /// <param name="readLock">The <see cref="IDisposableLock"/> which should be downgraded.</param>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public static void DowngradeWriteLockToReadLock(
                this AsyncReaderWriterLockSlim lockInstance,
                IDisposableLock readLock)
        {
            if (readLock == null)
                throw new ArgumentNullException(nameof(readLock));

            var myReadLock = readLock as ActionDisposableLock;
            if (myReadLock == null || myReadLock.LockOrigin != lockInstance ||
                    !myReadLock.IsWriteLock || myReadLock.IsDisposed)
                throw new ArgumentException();

            // Downgrade the lock.
            lockInstance.DowngradeWriteLockToReadLock();

            // Now mark the lock as being a read lock.
            myReadLock.IsWriteLock = false;
        }


        /// <summary>
        /// 
        /// </summary>
        public interface IDisposableLock : IDisposable
        {
        }


        private class ActionDisposableLock : IDisposableLock
        {
            private readonly Action action;

            private readonly AsyncReaderWriterLockSlim lockOrigin;

            private bool isWriteLock;

            private bool isDisposed;


            public ActionDisposableLock(
                    Action action,
                    AsyncReaderWriterLockSlim lockOrigin,
                    bool isWriteLock)
            {
                this.action = action;
                this.lockOrigin = lockOrigin;
                this.isWriteLock = isWriteLock;
            }

            ~ActionDisposableLock()
            {
                Dispose(false);
            }


            public AsyncReaderWriterLockSlim LockOrigin => this.lockOrigin;

            public bool IsWriteLock
            {
                get => this.isWriteLock;
                set => this.isWriteLock = value;
            }

            public bool IsDisposed => this.isDisposed;


            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }


            protected virtual void Dispose(bool disposing)
            {
                if (!this.isDisposed)
                {
                    if (disposing)
                    {
                        this.action();
                    }

                    this.isDisposed = true;
                }
            }
        }
    }
}
