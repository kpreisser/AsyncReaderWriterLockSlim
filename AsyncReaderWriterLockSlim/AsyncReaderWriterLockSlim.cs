using System;
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
    /// - To upgrade and downgrade a lock between upgradeable mode and write mode, you must call the
    ///   <see cref="UpgradeUpgradeableReadLockToUpgradedWriteLock(CancellationToken)"/> and
    ///   <see cref="DowngradeUpgradedWriteLockToUpgradeableReadLock"/> methods instead of the
    ///   Enter() and Exit() methods.
    ///   This is the only supported lock upgrade. However, you can additionally downgrade the lock
    ///   from write mode to read mode or from upgradeable read mode to read mode.
    ///   
    /// The lock can have different modes:
    /// - Read mode: Zero or more 'read mode' locks can be active at a time while no 'write mode' lock
    ///   is active.
    /// - Upgradeable read mode: Initially like 'read mode', but only one lock in this mode can be active
    ///   at a time. A lock in this mode can be upgraded to 'upgraded write mode'.
    /// - Write Mode: Only one 'write mode' lock can be active at a time while no other
    ///   'read mode' locks are active.
    /// - Upgraded write mode: Like 'write mode', but was upgraded from 'upgradeable read mode'.
    /// 
    /// At a time, any number of 'read mode' locks can be active and up to one 'upgradeable read mode' lock
    /// can be active, while no 'write mode' lock (or 'upgraded write mode' lock) is active.
    /// If a 'write mode' lock (or 'upgraded write mode' lock) is active, no other 'write mode' locks and
    /// no other 'read mode' locks (or 'upgradeable read mode' locks) can be active.
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
    /// Just like with <see cref="ReaderWriterLockSlim"/>, only a lock that is in 'upgradeable read mode'
    /// can be upgraded to write mode, in order to prevent deadlocks.
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
        /// A <see cref="SemaphoreSlim"/> which is used to manage the upgradeable lock.
        /// </summary>
        private readonly SemaphoreSlim upgradeableLockSemaphore = new SemaphoreSlim(1, 1);

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
        /// The number of currently held read locks.
        /// </summary>
        private long currentReadLockCount;

        /// <summary>
        /// The number of tasks or threads that intend to wait on the <see cref="writeLockSemaphore"/>.
        /// This is used to check if the <see cref="currentWriteLockState"/> should already be
        /// cleaned-up when the write lock is released.
        /// </summary>
        private long currentWaitingWriteLockCount;


        /// <summary>
        /// Initializes a new instance of the <see cref="ReaderWriterLockSlim"/> class.
        /// </summary>
        public AsyncReaderWriterLockSlim()
            : base()
        {
        }

        /// <summary>
        /// 
        /// </summary>
        ~AsyncReaderWriterLockSlim()
        {
            Dispose(false);
        }


        private static int GetRemainingTimeout(int millisecondsTimeout, int initialTickCount)
        {
            return millisecondsTimeout == Timeout.Infinite ? Timeout.Infinite :
                    (int)Math.Max(0, millisecondsTimeout - unchecked(
                        (uint)(Environment.TickCount - initialTickCount)));
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
                CancellationToken cancellationToken = default(CancellationToken))
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
        public async Task EnterReadLockAsync(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await TryEnterReadLockAsync(Timeout.Infinite, cancellationToken);
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
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            Monitor.Enter(this.syncRoot);
            try
            {
                // We use a loop so that we can try again after the current write lock
                // (if present) is released.
                while (true)
                {
                    // Note that EnterReadLockPreface() will exit the monitor if
                    // it doesn't return true.
                    WriteLockState existingWriteLockState;
                    if (EnterReadLockPreface(out existingWriteLockState))
                        return true;

                    bool waitResult;
                    try
                    {
                        // Need to wait until the existing write lock is released, then try again.
                        // This may throw an OperationCanceledException.
                        waitResult = existingWriteLockState.WaitingReadLocksSemaphore.Wait(
                                GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                                cancellationToken);
                    }
                    finally
                    {
                        // Note that EnterReadLockPostface() will re-enter the monitor.
                        EnterReadLockPostface(existingWriteLockState);
                    }

                    if (!waitResult)
                        return false; // Timeout exceeded
                }
            }
            finally
            {
                if (Monitor.IsEntered(this.syncRoot))
                    Monitor.Exit(this.syncRoot);
            }
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
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            Monitor.Enter(this.syncRoot);
            try
            {
                // We use a loop so that we can try again after the current write lock
                // (if present) is released.
                while (true)
                {
                    // Note that EnterReadLockPreface() will exit the monitor if
                    // it doesn't return true.
                    WriteLockState existingWriteLockState;
                    if (EnterReadLockPreface(out existingWriteLockState))
                        return true;

                    bool waitResult;
                    try
                    {
                        // Need to wait until the existing write lock is released, then try again.
                        // This may throw an OperationCanceledException.
                        waitResult = await existingWriteLockState.WaitingReadLocksSemaphore.WaitAsync(
                                GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                                cancellationToken);
                    }
                    finally
                    {
                        // Note that EnterReadLockPostface() will re-enter the monitor.
                        EnterReadLockPostface(existingWriteLockState);
                    }

                    if (!waitResult)
                        return false; // Timeout exceeded
                }
            }
            finally
            {
                if (Monitor.IsEntered(this.syncRoot))
                    Monitor.Exit(this.syncRoot);
            }
        }

        /// <summary>
        /// Enters the lock in upgradeable read mode.
        /// To upgrade the lock to write mode, you can call
        /// <see cref="UpgradeUpgradeableReadLockToUpgradedWriteLock(CancellationToken)"/>.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void EnterUpgradeableReadLock(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            TryEnterUpgradeableReadLock(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Asynchronously enters the lock in upgradeable read mode.
        /// To upgrade the lock to write mode, you can call
        /// <see cref="UpgradeUpgradeableReadLockToUpgradedWriteLockAsync(CancellationToken)"/>.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete when the lock has been entered.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public async Task EnterUpgradeableReadLockAsync(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await TryEnterUpgradeableReadLockAsync(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Tries to enter the lock in upgradeable read mode, with an optional integer time-out.
        /// To upgrade the lock to write mode, you can call
        /// <see cref="TryUpgradeUpgradeableReadLockToUpgradedWriteLock(int, CancellationToken)"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns><c>true</c> if the lock has been upgraded, otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public bool TryEnterUpgradeableReadLock(
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            // Enter the upgradeable lock semaphore before doing anything else.
            if (!this.upgradeableLockSemaphore.Wait(
                    millisecondsTimeout, cancellationToken))
                return false;

            // Now enter the read lock that is implied by the upgradeable lock.
            bool waitResult = false;
            try
            {
                // This may throw an OperationCanceledException.
                waitResult = TryEnterReadLock(
                        GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                        cancellationToken);
            }
            finally
            {
                if (!waitResult)
                {
                    // Timeout has been exceeded or a OperationCancelledException has
                    // been thrown.
                    this.upgradeableLockSemaphore.Release();
                }
            }

            return waitResult;
        }

        /// <summary>
        /// Tries to asynchronously enter the lock in upgradeable read mode, with an optional integer time-out.
        /// To upgrade the lock to write mode, you can call
        /// <see cref="TryUpgradeUpgradeableReadLockToUpgradedWriteLockAsync(int, CancellationToken)"/>.
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
        public async Task<bool> TryEnterUpgradeableReadLockAsync(
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            // Enter the upgradeable lock semaphore before doing anything else.
            if (!await this.upgradeableLockSemaphore.WaitAsync(
                    millisecondsTimeout, cancellationToken))
                return false;

            // Now enter the read lock that is implied by the upgradeable lock.
            bool waitResult = false;
            try
            {
                // This may throw an OperationCanceledException.
                waitResult = await TryEnterReadLockAsync(
                        GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                        cancellationToken);
            }
            finally
            {
                if (!waitResult)
                {
                    // Timeout has been exceeded or a OperationCancelledException has
                    // been thrown.
                    this.upgradeableLockSemaphore.Release();
                }
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
                CancellationToken cancellationToken = default(CancellationToken))
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
        public async Task EnterWriteLockAsync(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await TryEnterWriteLockAsync(Timeout.Infinite, cancellationToken);
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
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryEnterWriteLockInternal(false, millisecondsTimeout, cancellationToken);
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
        public Task<bool> TryEnterWriteLockAsync(
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryEnterWriteLockInternalAsync(false, millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Upgrades the lock from upgradeable read mode to upgraded write mode.
        /// You must call
        /// <see cref="DowngradeUpgradedWriteLockToUpgradeableReadLock"/> before calling
        /// <see cref="ExitUpgradeableReadLock"/>.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void UpgradeUpgradeableReadLockToUpgradedWriteLock(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            TryUpgradeUpgradeableReadLockToUpgradedWriteLock(
                    Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Asynchronously upgrades the lock from upgradeable read mode to upgraded write mode.
        /// You must call
        /// <see cref="DowngradeUpgradedWriteLockToUpgradeableReadLock"/> before calling
        /// <see cref="ExitUpgradeableReadLock"/>.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete when the lock has been upgraded.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public async Task UpgradeUpgradeableReadLockToUpgradedWriteLockAsync(
                CancellationToken cancellationToken = default(CancellationToken))
        {
            await TryUpgradeUpgradeableReadLockToUpgradedWriteLockAsync(
                    Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Tries to upgrade the lock from upgradeable read mode to upgraded write mode,
        /// with an optional integer time-out.
        /// You must call
        /// <see cref="DowngradeUpgradedWriteLockToUpgradeableReadLock"/> before calling
        /// <see cref="ExitUpgradeableReadLock"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns><c>true</c> if the lock has been upgraded, otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public bool TryUpgradeUpgradeableReadLockToUpgradedWriteLock(
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryEnterWriteLockInternal(true, millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Tries to asynchronously upgrade the lock from upgradeable read mode to upgraded write mode,
        /// with an optional integer time-out.
        /// You must call
        /// <see cref="DowngradeUpgradedWriteLockToUpgradeableReadLock"/> before calling
        /// <see cref="ExitUpgradeableReadLock"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1
        /// (<see cref="Timeout.Infinite"/>) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A task that will complete with a result of <c>true</c> if the lock has been upgraded,
        /// otherwise with a result of <c>false</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number
        /// other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public Task<bool> TryUpgradeUpgradeableReadLockToUpgradedWriteLockAsync(
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryEnterWriteLockInternalAsync(true, millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Downgrades the lock from upgraded write mode to upgradeable read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void DowngradeUpgradedWriteLockToUpgradeableReadLock()
        {
            ExitWriteLockInternal(true);
        }

        /// <summary>
        /// Downgrades the lock from write mode to read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void DowngradeWriteLockToReadLock()
        {
            // This has the same effect as DowngradeWriteLockToUpgradeableReadLock(),
            // but the current execution flow should not have the upgradeable lock.
            ExitWriteLockInternal(true);
        }

        /// <summary>
        /// Downgrades the lock from upgradeable read mode to read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void DowngradeUpgradeableReadLockToReadLock()
        {
            DenyIfDisposed();

            // Release the upgradeable lock semaphore without releasing the read lock.
            this.upgradeableLockSemaphore.Release();
        }

        /// <summary>
        /// Exits read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void ExitReadLock()
        {
            DenyIfDisposed();

            lock (this.syncRoot)
            {
                if (this.currentReadLockCount == 0)
                    throw new InvalidOperationException();

                this.currentReadLockCount--;

                // If we are the last read lock and there's an active write lock waiting, we need to
                // release the read lock release semaphore.
                if (this.currentReadLockCount == 0 &&
                        this.currentWriteLockState?.StateIsActive == true)
                    this.readLockReleaseSemaphore.Release();
            }
        }

        /// <summary>
        /// Exits upgradeable read mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void ExitUpgradeableReadLock()
        {
            // Downgrade to a read lock.
            DowngradeUpgradeableReadLockToReadLock();

            // Exit the read lock that is implied by the upgradeable lock.
            ExitReadLock();
        }

        /// <summary>
        /// Exits write mode.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
        public void ExitWriteLock()
        {
            ExitWriteLockInternal(false);
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
                        throw new InvalidOperationException($"A write lock was still active " +
                                $"while trying to dispose the {nameof(AsyncReaderWriterLockSlim)}.");
                    else if (this.currentReadLockCount > 0)
                        throw new InvalidOperationException($"At least one read lock was still active " +
                                $"while trying to dispose the {nameof(AsyncReaderWriterLockSlim)}.");
                }

                this.upgradeableLockSemaphore.Dispose();
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
            existingWriteLockState = this.currentWriteLockState;
            if (existingWriteLockState != null)
            {
                // There is already another write lock, so we need to wait until
                // its WaitingReadLocksSemaphore is released, and then try again.
                // Ensure that there exists a semaphore on which we can wait.
                if (existingWriteLockState.WaitingReadLocksSemaphore == null)
                    existingWriteLockState.WaitingReadLocksSemaphore = new SemaphoreSlim(0);

                // Announce that we will wait on the semaphore.
                existingWriteLockState.WaitingReadLocksCount++;
            }
            else
            {
                // No write lock is active, so we don't need to wait.                
                this.currentReadLockCount++;

                return true;
            }

            // Exit the monitor, and enter it again after waiting (so it is entered
            // while the loop starts again). This is so that we only need one instead of
            // two lock operations for one wait.
            Monitor.Exit(this.syncRoot);

            return false;
        }

        private void EnterReadLockPostface(WriteLockState existingLockState)
        {
            // Re-enter the monitor.
            Monitor.Enter(this.syncRoot);

            // Check if we need to dispose the semaphore after the write lock state has
            // already been cleared.
            existingLockState.WaitingReadLocksCount--;
            if (existingLockState.LastWaitingReadLockMustDisposeSemaphore &&
                    existingLockState.WaitingReadLocksCount == 0)
                existingLockState.WaitingReadLocksSemaphore.Dispose();
        }

        private bool TryEnterWriteLockInternal(
                bool upgradeLock,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            // Enter the write lock semaphore before doing anything else.
            lock (this.syncRoot)
            {
                this.currentWaitingWriteLockCount++;
            }

            bool writeLockWaitResult = false;
            bool waitForReadLocks;
            try
            {
                writeLockWaitResult = this.writeLockSemaphore.Wait(
                        millisecondsTimeout, cancellationToken);
            }
            finally
            {
                // This may throw if upgradeLock is true but no read lock is registered.
                // However since this results from an incorrect usage of this lock, we don't
                // handle this specially.
                EnterWriteLockPreface(writeLockWaitResult, upgradeLock, out waitForReadLocks);
            }
            if (!writeLockWaitResult)
                return false;

            // After we set the write lock state, we might need to wait for existing read locks
            // to be released.
            // In this state, no new read locks can be entered until we release the write lock state.
            // We only wait one time since only the last active read lock will release the semaphore.    
            if (waitForReadLocks)
            {
                bool waitResult = false;
                try
                {
                    // This may throw an OperationCanceledException.
                    waitResult = this.readLockReleaseSemaphore.Wait(
                            GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                            cancellationToken);
                }
                finally
                {
                    if (!waitResult)
                    {
                        // Timeout has been exceeded or a OperationCancelledException has
                        // been thrown.
                        HandleEnterWriteLockWaitFailure(upgradeLock);
                    }
                }

                if (!waitResult)
                    return false; // Timeout exceeded
            }

            return true;
        }

        private async Task<bool> TryEnterWriteLockInternalAsync(
                bool upgradeLock,
                int millisecondsTimeout,
                CancellationToken cancellationToken = default(CancellationToken))
        {
            DenyIfDisposed();
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            cancellationToken.ThrowIfCancellationRequested();

            int initialTickCount = millisecondsTimeout == Timeout.Infinite ? 0 :
                    Environment.TickCount;

            // Enter the write lock semaphore before doing anything else.
            lock (this.syncRoot)
            {
                this.currentWaitingWriteLockCount++;
            }

            bool writeLockWaitResult = false;
            bool waitForReadLocks;
            try
            {
                writeLockWaitResult = await this.writeLockSemaphore.WaitAsync(
                        millisecondsTimeout, cancellationToken);
            }
            finally
            {
                // This may throw if upgradeLock is true but no read lock is registered.
                // However since this results from an incorrect usage of this lock, we don't
                // handle this specially.
                EnterWriteLockPreface(writeLockWaitResult, upgradeLock, out waitForReadLocks);
            }
            if (!writeLockWaitResult)
                return false;

            // After we set the write lock state, we might need to wait for existing read locks
            // to be released.
            // In this state, no new read locks can be entered until we release the write lock state.
            // We only wait one time since only the last active read lock will release the semaphore.    
            if (waitForReadLocks)
            {
                bool waitResult = false;
                try
                {
                    // This may throw an OperationCanceledException.
                    waitResult = await this.readLockReleaseSemaphore.WaitAsync(
                            GetRemainingTimeout(millisecondsTimeout, initialTickCount),
                            cancellationToken);
                }
                finally
                {
                    if (!waitResult)
                    {
                        // Timeout has been exceeded or a OperationCancelledException has
                        // been thrown.
                        HandleEnterWriteLockWaitFailure(upgradeLock);
                    }
                }

                if (!waitResult)
                    return false; // Timeout exceeded
            }

            return true;
        }

        private void EnterWriteLockPreface(bool writeLockWaitResult, bool upgradeLock, out bool waitForReadLocks)
        {
            waitForReadLocks = false;

            lock (this.syncRoot)
            {
                this.currentWaitingWriteLockCount--;

                if (writeLockWaitResult)
                {
                    if (upgradeLock)
                    {
                        // Release the read lock that is implied by the upgradeable lock.
                        if (this.currentReadLockCount == 0)
                            throw new InvalidOperationException();
                        this.currentReadLockCount--;
                    }

                    // If there's already a write lock state from a previous write lock, we simply
                    // use it. Otherwise, create a new one.
                    if (this.currentWriteLockState == null)
                        this.currentWriteLockState = new WriteLockState();

                    this.currentWriteLockState.StateIsActive = true;

                    // Check if the write lock will need to wait for existing read locks to be
                    // released.
                    waitForReadLocks = this.currentReadLockCount > 0;
                }
                else if (this.currentWriteLockState?.StateIsActive == false &&
                        this.currentWaitingWriteLockCount == 0)
                {
                    // We were the last write lock and a previous (inactive) write lock state is
                    // still set, we need to clean it up.
                    // This could happen e.g. if a write lock downgrades to a read lock and then the
                    // wait on the writeLockSemaphore times out.
                    CleanUpWriteLockState();
                }
            }
        }

        private void HandleEnterWriteLockWaitFailure(bool downgradeLock)
        {
            lock (this.syncRoot)
            {
                // Reset the read lock release semaphore if it has been released in
                // the meanwhile. It is OK to check this here since the semaphore can
                // only be released within the lock on syncRoot.
                if (this.readLockReleaseSemaphore.CurrentCount > 0)
                    this.readLockReleaseSemaphore.Wait();

                ExitWriteLockCore(downgradeLock);
            }
        }

        private void ExitWriteLockInternal(bool downgradeLock)
        {
            DenyIfDisposed();

            lock (this.syncRoot)
            {
                if (this.currentWriteLockState == null)
                    throw new InvalidOperationException();

                ExitWriteLockCore(downgradeLock);
            }
        }

        private void ExitWriteLockCore(bool downgradeLock)
        {
            if (downgradeLock)
            {
                // Enter the read lock which is implied by the upgradeable lock.
                this.currentReadLockCount++;
            }

            // If currently no other write lock is waiting, we clean-up the current
            // write lock state. Otherwise, we set it to incative to priorize waiting writers
            // over waiting readers.
            if (this.currentWaitingWriteLockCount == 0)
                CleanUpWriteLockState();
            else
                this.currentWriteLockState.StateIsActive = false;

            // Finally, release the write lock semaphore.
            this.writeLockSemaphore.Release();
        }

        private void CleanUpWriteLockState()
        {
            var writeLockState = this.currentWriteLockState;

            if (writeLockState.WaitingReadLocksSemaphore != null)
            {
                // If there is currently no other task or thread waiting on the semaphore, we can
                // dispose it here. Otherwise, the last waiting task or thread must dispose the
                // semaphore.
                if (writeLockState.WaitingReadLocksCount == 0)
                {
                    writeLockState.WaitingReadLocksSemaphore.Dispose();
                }
                else
                {
                    // Release the waiting read locks semaphore as often as possible to ensure
                    // all other waiting tasks or threads are released and can start a new try to
                    // get a lock.
                    // The semaphore however will only have been created if there actually was at
                    // least one other task or thread trying to get a read lock.
                    writeLockState.WaitingReadLocksSemaphore.Release(int.MaxValue);

                    // Announce that the last waiting task or thread must dispose the semaphore.
                    // It cannot happen that some other code will wait on the semaphore after
                    // disposing it since we clear the current writer state inside the lock.
                    writeLockState.LastWaitingReadLockMustDisposeSemaphore = true;
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
            /// <see cref="LastWaitingReadLockMustDisposeSemaphore"/> is <c>true</c>.
            /// </summary>
            public int WaitingReadLocksCount { get; set; }

            /// <summary>
            /// Gets or sets a value that indicates if the the task or thread that waits on the
            /// <see cref="WaitingReadLocksSemaphore"/> semaphore and then decrements
            /// <see cref="WaitingReadLocksCount"/> to zero (0) must dispose the
            /// <see cref="WaitingReadLocksSemaphore"/> semaphore.
            /// </summary>
            public bool LastWaitingReadLockMustDisposeSemaphore { get; set; }
        }
    }
}
