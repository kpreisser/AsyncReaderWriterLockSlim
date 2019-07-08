using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KPreisser
{
    /// <summary>
    /// An alternative to <see cref="ReaderWriterLockSlim"/> which can be used in async methods.
    /// </summary>
    /// <remarks>
    /// This implementation has the following differences to <see cref="ReaderWriterLockSlim"/>:
    /// - The lock is not thread-affine, which means one thread can enter the lock,
    ///   and a different thread can release it. This allows you to use the lock in an async
    ///   method with a await call between entering and releasing the lock.
    /// - Additionally to synchronous methods like <see cref="EnterReadLock(CancellationToken)"/>,
    ///   it has asynchronous methods like <see cref="EnterReadLockAsync(CancellationToken)"/> which
    ///   can be called in async methods, so that the current thread is not blocked while waiting
    ///   for the lock.
    /// - Because this class doesn't have thread affinity, recursive locks are not supported (which
    ///   also means they cannot be detected). In order for the lock to work correctly, you must not
    ///   recursively enter the lock from the same execution flow.
    /// - The lock does not support upgradeable read mode locks that can be upgraded to a write mode
    ///   lock, due to the complexity this would add.
    ///   
    /// The lock can have different modes:
    /// - Read mode: One or more 'read mode' locks can be active at a time while no 'write mode' lock
    ///   is active.
    /// - Write Mode: One 'write mode' lock can be active at a time while no other
    ///   'write mode' locks and no other 'read mode' locks are active.
    /// 
    /// When a task or thread ("execution flow") tries to enter a 'write mode' lock while at least one
    /// 'read mode' lock is active, it is blocked until the last 'read mode' lock is released.
    /// 
    /// When a task or thread tries to enter a 'read mode' lock while a 'write mode' lock is active,
    /// it is blocked until the 'write mode' lock is released.
    /// 
    /// If, while other 'read mode' locks are active and the current task or thread waits to enter
    /// the 'write mode' lock, another task or thread tries
    /// to enter a 'read mode' lock, it is blocked until
    /// the current task or thread released the 'write mode' lock (or canceled the wait operation), 
    /// which means writers are favored in this case.
    /// 
    /// Also, when a 'write mode' lock is released while there are one or more execution flows
    /// trying to enter a *write mode* lock and also one or more execution flows trying to enter a
    /// 'read mode' lock, writers are favored.
    /// 
    /// The lock internally uses <see cref="SemaphoreSlim"/>s to implement wait functionality.
    /// </remarks>
    public class AsyncReaderWriterLockSlim : IDisposable
    {
        private readonly object syncRoot = new object();

        private bool isDisposed;

        /// <summary>
        /// A <see cref="SemaphoreSlim"/> which is used to manage the write lock.
        /// </summary>
        private readonly SemaphoreSlim writeLockSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// A <see cref="SemaphoreSlim"/> which a write lock uses to wait until the last
        /// active read lock is released.
        /// </summary>
        private readonly SemaphoreSlim readLockReleaseSemaphore = new SemaphoreSlim(0, 1);

        /// <summary>
        /// If not <c>null</c>, contains the <see cref="WriteLockState"/> that represents the
        /// state of the current write lock. This field may be set even if
        /// <see cref="currentReadLockCount"/> is not yet 0, in which case the task or thread
        /// trying to get the write lock needs to wait until the existing read locks are left.
        /// However, while this field is set, no new read locks can be acquired.
        /// </summary>
        private WriteLockState currentWriteLockState;

        /// <summary>
        /// The number of currently held read locks (when ignoring the MSB).
        /// The MSB will be set when a write lock state is present.
        /// </summary>
        private int currentReadLockCount;

        /// <summary>
        /// The number of tasks or threads that intend to wait on the <see cref="writeLockSemaphore"/>.
        /// This is used to check if the <see cref="currentWriteLockState"/> should already be
        /// cleaned-up when the write lock is released.
        /// </summary>
        private long currentWaitingWriteLockCount;


        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncReaderWriterLockSlim"/> class.
        /// </summary>
        public AsyncReaderWriterLockSlim()
            : base()
        {
        }


        private static int GetRemainingTimeout(int millisecondsTimeout, long initialTicks)
        {
            return millisecondsTimeout == Timeout.Infinite ? Timeout.Infinite :
                    (int)Math.Max(0, millisecondsTimeout -
                        (TimeUtils.GetTimestampTicks(true) - initialTicks) / 10000);
        }


        /// <summary>
        /// Releases all resources used by the <see cref="AsyncReaderWriterLockSlim"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Enters the lock in read mode.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void EnterReadLock(
                CancellationToken cancellationToken = default)
        {
            TryEnterReadLock(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Asynchronously enters the lock in read mode.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete when the lock has been entered.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public Task EnterReadLockAsync(
                CancellationToken cancellationToken = default)
        {
            return TryEnterReadLockAsync(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Tries to enter the lock in read mode, with an optional integer time-out.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns><c>true</c> if the lock has been entered, otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public bool TryEnterReadLock(
                int millisecondsTimeout = 0,
                CancellationToken cancellationToken = default)
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            // Check if we can enter the lock directly.
            if (EnterReadLockPreface(out var existingWriteLockState))
                return true;

            bool waitResult = false;
            try
            {
                // Need to wait until the existing write lock is released.
                // This may throw an OperationCanceledException.
                waitResult = existingWriteLockState.WaitingReadLocksSemaphore.Wait(
                        millisecondsTimeout,
                        cancellationToken);
            }
            finally
            {
                EnterReadLockPostface(existingWriteLockState, waitResult);
            }

            return waitResult;
        }

        /// <summary>
        /// Tries to asynchronously enter the lock in read mode, with an optional integer time-out.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete with a result of <c>true</c> if the lock has been entered,
        /// otherwise with a result of <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public async Task<bool> TryEnterReadLockAsync(
                int millisecondsTimeout = 0,
                CancellationToken cancellationToken = default)
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            // Check if we can enter the lock directly.
            if (EnterReadLockPreface(out var existingWriteLockState))
                return true;

            bool waitResult = false;
            try
            {
                // Need to wait until the existing write lock is released.
                // This may throw an OperationCanceledException.
                waitResult = await existingWriteLockState.WaitingReadLocksSemaphore.WaitAsync(
                        millisecondsTimeout,
                        cancellationToken);
            }
            finally
            {
                EnterReadLockPostface(existingWriteLockState, waitResult);
            }

            return waitResult;
        }

        /// <summary>
        /// Enters the lock in write mode.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void EnterWriteLock(
                CancellationToken cancellationToken = default)
        {
            TryEnterWriteLock(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Asynchronously enters the lock in write mode.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete when the lock has been entered.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public Task EnterWriteLockAsync(
                CancellationToken cancellationToken = default)
        {
            return TryEnterWriteLockAsync(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Tries to enter the lock in write mode, with an optional integer time-out.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns><c>true</c> if the lock has been entered, otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public bool TryEnterWriteLock(
                int millisecondsTimeout = 0,
                CancellationToken cancellationToken = default)
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            long initialTicks = millisecondsTimeout == Timeout.Infinite ? 0 :
                    TimeUtils.GetTimestampTicks(true);

            // Enter the write lock semaphore before doing anything else.
            if (!EnterWriteLockPreface(out bool waitForReadLocks))
            {
                bool writeLockWaitResult = false;
                try
                {
                    writeLockWaitResult = this.writeLockSemaphore.Wait(
                            millisecondsTimeout,
                            cancellationToken);
                }
                finally
                {
                    EnterWriteLockPostface(writeLockWaitResult, out waitForReadLocks);
                }
                if (!writeLockWaitResult)
                    return false;
            }

            // After we set the write lock state, we might need to wait for existing read
            // locks to be released.
            // In this state, no new read locks can be entered until we release the write
            // lock state.
            // We only wait one time since only the last active read lock will release
            // the semaphore.
            if (waitForReadLocks)
            {
                bool waitResult = false;
                try
                {
                    // This may throw an OperationCanceledException.
                    waitResult = this.readLockReleaseSemaphore.Wait(
                            GetRemainingTimeout(millisecondsTimeout, initialTicks),
                            cancellationToken);
                }
                finally
                {
                    if (!waitResult)
                    {
                        // Timeout has been exceeded or a OperationCancelledException has
                        // been thrown.
                        HandleEnterWriteLockWaitFailure();
                    }
                }

                if (!waitResult)
                    return false; // Timeout exceeded
            }

            return true;
        }

        /// <summary>
        /// Tries to asynchronously enter the lock in write mode, with an optional integer time-out.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete with a result of <c>true</c> if the lock has been entered,
        /// otherwise with a result of <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public async Task<bool> TryEnterWriteLockAsync(
                int millisecondsTimeout = 0,
                CancellationToken cancellationToken = default)
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            long initialTicks = millisecondsTimeout == Timeout.Infinite ? 0 :
                    TimeUtils.GetTimestampTicks(true);

            // Enter the write lock semaphore before doing anything else.
            if (!EnterWriteLockPreface(out bool waitForReadLocks))
            {
                bool writeLockWaitResult = false;
                try
                {
                    writeLockWaitResult = await this.writeLockSemaphore.WaitAsync(
                            millisecondsTimeout,
                            cancellationToken);
                }
                finally
                {
                    EnterWriteLockPostface(writeLockWaitResult, out waitForReadLocks);
                }
                if (!writeLockWaitResult)
                    return false;
            }

            // After we set the write lock state, we might need to wait for existing read
            // locks to be released.
            // In this state, no new read locks can be entered until we release the write
            // lock state.
            // We only wait one time since only the last active read lock will release
            // the semaphore.  
            if (waitForReadLocks)
            {
                bool waitResult = false;
                try
                {
                    // This may throw an OperationCanceledException.
                    waitResult = await this.readLockReleaseSemaphore.WaitAsync(
                            GetRemainingTimeout(millisecondsTimeout, initialTicks),
                            cancellationToken);
                }
                finally
                {
                    if (!waitResult)
                    {
                        // Timeout has been exceeded or a OperationCancelledException has
                        // been thrown.
                        HandleEnterWriteLockWaitFailure();
                    }
                }

                if (!waitResult)
                    return false; // Timeout exceeded
            }

            return true;
        }

        /// <summary>
        /// Downgrades the lock from write mode to read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void DowngradeWriteLockToReadLock()
        {
            ExitWriteLockInternal(downgradeLock: true);
        }

        /// <summary>
        /// Exits read mode.
        /// </summary>
        /// <remarks>
        /// You must call this method only as often as you entered the lock in read mode;
        /// otherwise, undefined behavior will occur.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void ExitReadLock()
        {
            DenyIfDisposed();

            ExitReadLockCore(getLock: true);
        }

        /// <summary>
        /// Exits write mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void ExitWriteLock()
        {
            ExitWriteLockInternal(downgradeLock: false);
        }


        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="AsyncReaderWriterLockSlim"/> and
        /// optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (this.syncRoot)
                {
                    if (this.currentWriteLockState != null)
                        throw new InvalidOperationException(
                                $"A write lock was still active while trying to " +
                                $"dispose the {nameof(AsyncReaderWriterLockSlim)}.");
                    else if ((Volatile.Read(ref this.currentReadLockCount) & 0x7FFFFFFF) > 0)
                        throw new InvalidOperationException(
                                $"At least one read lock was still active while trying to " +
                                $"dispose the {nameof(AsyncReaderWriterLockSlim)}.");
                }

                this.writeLockSemaphore.Dispose();
                this.readLockReleaseSemaphore.Dispose();
            }

            // The access to isDisposed is not volatile because that might be
            // expensive; however we still call MemoryBarrier to ensure the value is
            // now actually written.
            this.isDisposed = true;
            Thread.MemoryBarrier();
        }


        private void DenyIfDisposed()
        {
            if (this.isDisposed)
                throw new ObjectDisposedException(nameof(AsyncReaderWriterLockSlim));
        }

        private bool EnterReadLockPreface(out WriteLockState existingWriteLockState)
        {
            existingWriteLockState = null;

            // Increment the read lock count. If the MSB is not set, no write lock is
            // currently held and we can return immediately without the need to lock
            // on syncRoot.
            int readLockResult = Interlocked.Increment(ref this.currentReadLockCount);
            if (readLockResult >= 0)
                return true;

            // A write lock state might be present, so we need to lock and check that.
            lock (this.syncRoot)
            {
                existingWriteLockState = this.currentWriteLockState;
                if (existingWriteLockState == null)
                {
                    // There was a write lock state but it has already been released,
                    // so we don't need to do anything.
                    return true;
                }
                else
                {
                    // There is already another write lock, so we need to decrement
                    // the read lock count and then wait until the write lock's
                    // WaitingReadLocksSemaphore is released.
                    ExitReadLockCore(getLock: false);

                    // Ensure that there exists a semaphore on which we can wait.
                    if (existingWriteLockState.WaitingReadLocksSemaphore == null)
                        existingWriteLockState.WaitingReadLocksSemaphore = new SemaphoreSlim(0);

                    // Announce that we will wait on the semaphore.
                    existingWriteLockState.WaitingReadLocksCount++;

                    return false;
                }
            }
        }

        private void EnterReadLockPostface(WriteLockState existingLockState, bool waitResult)
        {
            lock (this.syncRoot)
            {
                // Check if we need to dispose the semaphore after the write lock state
                // has already been released.
                existingLockState.WaitingReadLocksCount--;
                if (existingLockState.StateIsReleased &&
                        existingLockState.WaitingReadLocksCount == 0)
                    existingLockState.WaitingReadLocksSemaphore.Dispose();

                if (waitResult)
                {
                    // The write lock has already incremented the currentReadLockCount
                    // field, so we can simply return.
                    Debug.Assert(existingLockState.StateIsReleased);
                }
                else if (existingLockState.StateIsReleased)
                {
                    // Need to release the read lock since we do not want to take it
                    // (because a OperationCanceledException might have been thrown).
                    ExitReadLockCore(getLock: false);
                }
            }
        }

        private void ExitReadLockCore(bool getLock)
        {
            int readLockResult = Interlocked.Decrement(ref this.currentReadLockCount);

            // If we are the last read lock and there's an active write lock waiting,
            // we need to release the read lock release semaphore.
            if (readLockResult == -0x80000000)
            {
                if (getLock)
                    Monitor.Enter(this.syncRoot);
                try
                {
                    var lockState = this.currentWriteLockState;
                    if (lockState != null && !lockState.ReadLockReleaseSemaphoreReleased)
                    {
                        this.readLockReleaseSemaphore.Release();
                        lockState.ReadLockReleaseSemaphoreReleased = true;
                    }
                }
                finally
                {
                    if (getLock)
                        Monitor.Exit(this.syncRoot);
                }
            }
        }

        private bool EnterWriteLockPreface(out bool waitForReadLocks)
        {
            waitForReadLocks = false;

            lock (this.syncRoot)
            {
                this.currentWaitingWriteLockCount++;

                // Check if we can immediately acquire the write lock semaphore without
                // releasing the lock on syncroot.
                if (this.writeLockSemaphore.CurrentCount > 0 && this.writeLockSemaphore.Wait(0))
                {
                    // Directly call the postface method.
                    EnterWriteLockPostface(
                            writeLockWaitResult: true,
                            out waitForReadLocks,
                            getLock: false);

                    return true;
                }
            }

            return false;
        }

        private void EnterWriteLockPostface(
                bool writeLockWaitResult,
                out bool waitForReadLocks,
                bool getLock = true)
        {
            waitForReadLocks = false;

            if (getLock)
                Monitor.Enter(this.syncRoot);
            try
            {
                this.currentWaitingWriteLockCount--;

                if (writeLockWaitResult)
                {
                    // If there's already a write lock state from a previous write lock,
                    // we simply use it. Otherwise, create a new one.
                    if (this.currentWriteLockState == null)
                    {
                        this.currentWriteLockState = new WriteLockState();

                        // Set the MSB on the current read lock count, so that other
                        // threads that want to enter the lock know that they need to
                        // wait until the write lock is released.
                        int readLockCount = Interlocked.Add(
                                ref this.currentReadLockCount,
                                -0x80000000) &
                                0x7FFFFFFF;

                        // Check if the write lock will need to wait for existing read
                        // locks to be released.
                        waitForReadLocks = readLockCount > 0;
                        this.currentWriteLockState.WaitForReadLocks = waitForReadLocks;

                        if (!waitForReadLocks)
                            this.currentWriteLockState.ReadLockReleaseSemaphoreReleased = true;
                    }
                    else
                    {
                        waitForReadLocks = this.currentWriteLockState.WaitForReadLocks;
                    }

                    this.currentWriteLockState.StateIsActive = true;
                }
                else if (this.currentWriteLockState?.StateIsActive == false &&
                        this.currentWaitingWriteLockCount == 0)
                {
                    // We were the last write lock and a previous (inactive) write lock
                    // state is still set, so we need to release it.
                    // This could happen e.g. if a write lock downgrades to a read lock
                    // and then the wait on the writeLockSemaphore times out.
                    ReleaseWriteLockState();
                }
            }
            finally
            {
                if (getLock)
                    Monitor.Exit(this.syncRoot);
            }
        }

        private void HandleEnterWriteLockWaitFailure()
        {
            lock (this.syncRoot)
            {
                ExitWriteLockCore( 
                        downgradeLock: false,
                        waitFailure: true);
            }
        }

        private void ExitWriteLockInternal(bool downgradeLock)
        {
            DenyIfDisposed();

            lock (this.syncRoot)
            {
                if (this.currentWriteLockState == null)
                    throw new InvalidOperationException();

                ExitWriteLockCore(
                        downgradeLock);
            }
        }

        private void ExitWriteLockCore(bool downgradeLock, bool waitFailure = false)
        {
            if (downgradeLock)
            {
                // Enter the read lock while releasing the write lock.
                Interlocked.Increment(ref this.currentReadLockCount);
            }

            // If currently no other write lock is waiting, we release the current
            // write lock state. Otherwise, we set it to incative to priorize waiting
            // writers over waiting readers.
            if (this.currentWaitingWriteLockCount == 0)
            {
                // Reset the read lock release semaphore if it has been released in
                // the meanwhile. It is OK to check this here since the semaphore can
                // only be released within the lock on syncRoot.
                if (this.currentWriteLockState.WaitForReadLocks &&
                        waitFailure &&
                        this.readLockReleaseSemaphore.CurrentCount > 0)
                    this.readLockReleaseSemaphore.Wait();

                ReleaseWriteLockState();
            }
            else
            {
                this.currentWriteLockState.StateIsActive = false;

                // If we exit the write lock normally, we have already waited for the
                // read locks to exit, so the next write lock mustn't do that again.
                if (!waitFailure)
                {
                    Debug.Assert(this.currentWriteLockState.ReadLockReleaseSemaphoreReleased);
                    this.currentWriteLockState.WaitForReadLocks = false;
                }
            }

            // Finally, release the write lock semaphore.
            this.writeLockSemaphore.Release();
        }

        private void ReleaseWriteLockState()
        {
            var writeLockState = this.currentWriteLockState;

            writeLockState.StateIsReleased = true;

            // Clear the MSB on the read lock count.
            Interlocked.Add(ref this.currentReadLockCount, -0x80000000);

            if (writeLockState.WaitingReadLocksSemaphore != null)
            {
                // If there is currently no other task or thread waiting on the semaphore, we can
                // dispose it here. Otherwise, the last waiting task or thread must dispose the
                // semaphore by checking the WriteLockReleased property.
                if (writeLockState.WaitingReadLocksCount == 0)
                {
                    writeLockState.WaitingReadLocksSemaphore.Dispose();
                }
                else
                {
                    // Directly mark the read locks as entered.
                    Interlocked.Add(
                            ref this.currentReadLockCount,
                            writeLockState.WaitingReadLocksCount);
                    // Release the waiting read locks semaphore as often as needed to ensure
                    // all other waiting tasks or threads are released and get the read lock.
                    // The semaphore however will only have been created if there actually was at
                    // least one other task or thread trying to get a read lock.
                    writeLockState.WaitingReadLocksSemaphore.Release(
                            writeLockState.WaitingReadLocksCount);
                }
            }

            // Clear the write lock state.
            this.currentWriteLockState = null;
        }


        private class WriteLockState
        {
            public WriteLockState()
                : base()
            {
            }

            /// <summary>
            /// Gets or sets a value that indicates if the state is active. Only when <c>true</c>, the
            /// <see cref="readLockReleaseSemaphore"/> will be released once the last read lock exits.
            /// </summary>
            public bool StateIsActive { get; set; }

            /// <summary>
            /// Gets or sets a value that indicates if the write lock associated with this
            /// <see cref="WriteLockState"/> has already been released. This is also used
            /// to indicate if the the task or thread that waits on the
            /// <see cref="WaitingReadLocksSemaphore"/> semaphore and then decrements
            /// <see cref="WaitingReadLocksCount"/> to zero (0) must dispose the
            /// <see cref="WaitingReadLocksSemaphore"/> semaphore.
            /// </summary>
            public bool StateIsReleased { get; set; }

            /// <summary>
            /// Gets or sets a value that indicates if a write lock that uses an existing
            /// <see cref="WriteLockState"/> must wait until the
            /// <see cref="readLockReleaseSemaphore"/> is released.
            /// </summary>
            public bool WaitForReadLocks { get; set; }

            /// <summary>
            /// Gets or sets a value that indicates if a read lock that is exited when
            /// there is a write lock present should not release the
            /// <see cref="readLockReleaseSemaphore"/> as it has already been released
            /// (or there were no read locks present when the write lock was initially
            /// entered).
            /// </summary>
            public bool ReadLockReleaseSemaphoreReleased { get; set; }

            /// <summary>
            /// Gets or sets a <see cref="SemaphoreSlim"/> on which new read locks need
            /// to wait until the existing write lock is released. The <see cref="SemaphoreSlim"/>
            /// will be created only if there is at least on additional task or thread that wants
            /// to enter a read lock.
            /// </summary>
            public SemaphoreSlim WaitingReadLocksSemaphore { get; set; }

            /// <summary>
            /// Gets or sets a value that indicates the number of tasks or threads which intend
            /// to wait on the <see cref="WaitingReadLocksSemaphore"/> semaphore. This
            /// is used to determine which task or thread is responsible to dispose the 
            /// <see cref="WaitingReadLocksSemaphore"/> if
            /// <see cref="StateIsReleased"/> is <c>true</c>.
            /// </summary>
            public int WaitingReadLocksCount { get; set; }
        }
    }
}
