# AsyncReaderWriterLockSlim

This is an alternative to .NET's
[`ReaderWriterLockSlim`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.readerwriterlockslim)
with a similar functionality, but can be used in async methods. Due to its async-readiness, it
does **not support recursive locks** (see section Differences).


## Lock Modes

The lock can have different modes:
 * **Read mode:** One or more *read mode* locks can be active at a time while no *write mode* lock
   is active.
 * **Upgradeable read mode:** Initially like *read mode*, but only one lock in this mode can be active
   at a time. A lock in this mode can be upgraded to *upgraded write mode*.
 * **Write mode:** Only one *write mode* lock can be active at a time while no other
   *read mode* locks are active.
 * **Upgraded write mode:** Like *write mode*, but was upgraded from *upgradeable read mode*.

At a time, any number of *read mode* locks can be active and up to one *upgradeable read mode* lock 
can be active, while no *write mode* lock (or *upgraded write mode* lock) is active. <br>
If a *write mode* lock (or *upgraded write mode* lock) is active, no other *write mode* locks and
no other *read mode* locks (or *upgradeable read mode* locks) can be active.

When a task or thread ("execution flow") tries to enter a *write mode* lock while at least one
*read mode* lock is active, it is blocked until the last *read mode* lock is released.

When a task or thread tries to enter a *read mode* lock while a *write mode* lock is active,
it is blocked until the *write mode* lock is released.

If, while other *read mode* locks are active and the current task or thread waits to enter
the *write mode* lock, another task or thread tries
to enter a *read mode* lock, it is blocked until
the current task or thread released the *write mode* lock (or canceled the wait operation), 
which means writers are favored in this case.

Also, when a *write mode* lock is released while there are one or more execution flows
trying to enter a *write mode* lock and also one or more execution flows trying to enter a
*read mode* lock, writers are favored.

Just like with `ReaderWriterLockSlim`, only a lock that is in *upgradeable read mode*
can be upgraded to write mode, in order to prevent deadlocks.


## Lock Methods

The lock provides synchronous `Enter...()` methods for the different lock modes that block
until the lock has been acquired, and asynchronous `Enter...Async()` methods that
"block asynchronously" by returning a
[`Task`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.task) that
will complete once the lock has been acquired. 

For each `Enter...()` and `Enter...Async()` method there is also a `TryEnter...()` and
`TryEnter...Async()` method that allow you to specify an integer time-out, and return a `Boolean` 
that indicates if the lock could be acquired within that time-out.

You must make sure to call the corresponding `Exit...()` method to release the lock once you
don't need it anymore.


## Differences to ReaderWriterLockSlim

This implementation has the following differences to .NET's
[`ReaderWriterLockSlim`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.readerwriterlockslim):

 * The lock is not thread-affine, which means one thread can enter the lock, and a different
   thread can release it. This allows you to use the lock in an async method with a `await`
   operator between entering and releasing the lock.
 * Additionally to synchronous methods like `EnterReadLock`, it has asynchronous methods
   like `EnterReadLockAsync` which can be called in async methods, so that the current thread
   is not blocked while waiting for the lock.
 * You can specify a 
   [`CancellationToken`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken)
   when entering a lock to cancel the wait operation.
 * Because this lock is not thread-affine, **recursive locks are not supported** (which
   also means they cannot be detected). In order for the lock to work correctly, you must not
   recursively enter the lock from the same execution flow.
 * To upgrade and downgrade a lock between *upgradeable mode* and *write mode* (see below),
   you must call the `Upgrade...`and `Downgrade...` methods instead of the
   `Enter...` and `Exit...` methods. <br>
   This is the only supported lock upgrade. However, you can additionally downgrade the lock
   from *write mode* to *read mode* or from *upgradeable read mode* to *read mode*.


## Differences to Nito.AsyncEx.AsyncReaderWriterLock

This implementation has the following differences to Nito.AsyncEx'
[`AsyncReaderWriterLock`](https://github.com/StephenCleary/AsyncEx/blob/master/src/Nito.AsyncEx.Coordination/AsyncReaderWriterLock.cs):

  * Instead of methods that return a `IDisposable`, it has `Enter...()` and `Exit...()` methods
    similar to .NET's `ReaderWriterLockSlim`. However, you can add this functionality by using
	extension methods.
  * Additionally to a *read mode* and *write mode* locks, a *upgradeable read lock* is supported
    that can be upgraded to a *write mode* lock.
  * Additionally to providing a `CancellationToken` that allows you to cancel the wait operation,
    you can supply an integer time-out to the `TryEnter...()` methods.
  * When calling one of the `Enter...()` methods with an already canceled
    [`CancellationToken`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken),
    the method does not try to acquire the lock, but instead throws a `OperationCanceledException`,
	which matches the behavior of `SemaphoreSlim`. <br>
	To try to acquire the lock without blocking, you can call one of the `Try...` methods and
	specify a timeout of `0`.
  * Non-async methods do not require a ThreadPool thread to run the unblock logic; instead all
    code is executed in the thread that called the synchronous method.



## Additional Infos

The lock internally uses
[`SemaphoreSlim`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim)s
to implement wait functionality.

The code of the `AsyncReaderWriterLockSlim` class has been tested in various scenarios 
to ensure it works correctly.


## API Surface

Method                                                                              | Description
------------------------------------------------------------------------------------|------------
`Dispose ()`                                                                        | Releases all resources used by the `AsyncReaderWriterLockSlim`.
`EnterReadLock (CancellationToken)`                                                 | Enters the lock in read mode.
`EnterReadLockAsync (CancellationToken)`                                            | Asynchronously enters the lock in read mode.
`TryEnterReadLock (Int32, CancellationToken)`                                       | Tries to enter the lock in read mode, with an optional integer time-out.
`TryEnterReadLockAsync (Int32, CancellationToken)`                                  | Tries to asynchronously enter the lock in read mode, with an optional integer time-out.
`EnterUpgradeableReadLock (CancellationToken)`                                      | Enters the lock in upgradeable read mode.
`EnterUpgradeableReadLockAsync (CancellationToken)`                                 | Asynchronously enters the lock in upgradeable read mode.
`TryEnterUpgradeableReadLock (Int32, CancellationToken)`                            | Tries to enter the lock in upgradeable read mode, with an optional integer time-out.
`TryEnterUpgradeableReadLockAsync (Int32, CancellationToken)`                       | Tries to asynchronously enter the lock in upgradeable read mode, with an optional integer time-out.
`EnterWriteLock (CancellationToken)`                                                | Enters the lock in write mode.
`EnterWriteLockAsync (CancellationToken)`                                           | Asynchronously enters the lock in write mode.
`TryEnterWriteLock (Int32, CancellationToken)`                                      | Tries to enter the lock in write mode, with an optional integer time-out.
`TryEnterWriteLockAsync (Int32, CancellationToken)`                                 | Tries to asynchronously enter the lock in write mode, with an optional integer time-out.
`UpgradeUpgradeableReadLockToUpgradedWriteLock (CancellationToken)`                 | Upgrades the lock from upgradeable read mode to upgraded write mode.
`UpgradeUpgradeableReadLockToUpgradedWriteLockAsync (CancellationToken)`            | Asynchronously upgrades the lock from upgradeable read mode to upgraded write mode.
`TryUpgradeUpgradeableReadLockToUpgradedWriteLock (Int32, CancellationToken)`       | Tries to upgrade the lock from upgradeable read mode to upgraded write mode, with an optional integer time-out.
`TryUpgradeUpgradeableReadLockToUpgradedWriteLockAsync (Int32, CancellationToken)`  | Tries to asynchronously upgrade the lock from upgradeable read mode to upgraded write mode, with an optional integer time-out.
`DowngradeUpgradedWriteLockToUpgradeableReadLock ()`                                | Downgrades the lock from upgraded write mode to upgradeable read mode.
`DowngradeWriteLockToReadLock ()`                                                   | Downgrades the lock from write mode to read mode.
`DowngradeUpgradeableReadLockToReadLock ()`                                         | Downgrades the lock from upgradeable read mode to read mode.
`ExitReadLock ()`                                                                   | Exits read mode.
`ExitUpgradeableReadLock ()`                                                        | Exits upgradeable read mode.
`ExitWriteLock ()`                                                                  | Exits write mode.


## Examples

Enter the lock in read mode within an async method:

```c#
private async Task TestReadModeAsync(AsyncReaderWriterLockSlim asyncLock)
{
    // Asynchronously enter the lock in read mode. The task completes after the lock
    // has been acquired.
    await asyncLock.EnterReadLockAsync();
    try
    {
        // Use Task.Delay to simulate asynchronous work, which means a different thread
        // might continue execution after this point.
        await Task.Delay(200);
    }
    finally
    {
        asyncLock.ExitReadLock();
    }
}
```

Enter the lock in write mode in a synchronous method, using a timeout:

```c#
private void TestWriteMode(AsyncReaderWriterLockSlim asyncLock)
{
    // Try to enter the lock within 2 seconds.
    if (asyncLock.TryEnterWriteLock(2000))
    {
        try
        {
            // Simulate some work...
            Thread.Sleep(200);
        }
        finally
        {
            asyncLock.ExitWriteLock();
        }
    }
    else
    {
        // We could not enter the lock within the timeout...
    }
}
```

Enter the lock in upgradeable read mode and later upgrade it to write mode:

```c#
private async Task TestUpgradeableModeAsync(AsyncReaderWriterLockSlim asyncLock)
{
    // First, enter the lock in upgradeable read mode. This allows other execution flows to
    // enter read mode at the same time.
    await asyncLock.EnterUpgradeableReadLockAsync();
    try
    {
        // Simulate some work...
        await Task.Delay(200);

        // Now, upgrade to "upgraded write mode". This will block until all other execution
        // flows left read mode. Also, once we called this method, no execution flows can
        // enter read mode until we downgraded the lock.
        await asyncLock.UpgradeUpgradeableReadLockToUpgradedWriteLockAsync();
        try
        {
            // Simulate other work...
            await Task.Delay(200);
        }
        finally
        {
            // Note: You MUST downgrade the lock before exiting upgradeable read mode.
            asyncLock.DowngradeUpgradedWriteLockToUpgradeableReadLock();
        }
    }
    finally
    {
        asyncLock.ExitUpgradeableReadLock();
    }
}
```
