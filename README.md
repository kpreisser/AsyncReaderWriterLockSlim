# AsyncReaderWriterLockSlim

This is an alternative to .NET's
[`ReaderWriterLockSlim`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.readerwriterlockslim)
with a similar functionality, but can be used in async methods. Due to its async-readiness, it
does **not support recursive locks** (see section [Differences](#differences-to-readerwriterlockslim)).


## Lock Modes

The lock can have different modes:
 * **Read mode:** One or more *read mode* locks can be active at a time while no *write mode* lock
   is active.
 * **Write mode:** One *write mode* lock can be active at a time while no other
   *write mode* locks and no other *read mode* locks are active.

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

Additionally, the `AsyncReaderWriterLockSlimExtension` class contains extension methods
that return an `IDisposable` so that the lock can be used with a `using` block.


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
 * The lock does not support upgradeable read mode locks that can be upgraded to a write mode
   lock, due to the complexity this would add.


## Differences to Nito.AsyncEx.AsyncReaderWriterLock

This implementation has the following differences to Nito.AsyncEx'
[`AsyncReaderWriterLock`](https://github.com/StephenCleary/AsyncEx/blob/master/src/Nito.AsyncEx.Coordination/AsyncReaderWriterLock.cs):

  * Instead of methods that return an `IDisposable`, it has `Enter...()` and `Exit...()` methods
    similar to .NET's `ReaderWriterLockSlim`. However, the class `AsyncReaderWriterLockSlimExtension`
	provides extension methods that return an `IDisposable`.
  * Additionally to providing a `CancellationToken` that allows you to cancel the wait operation,
    you can supply an integer time-out to the `TryEnter...()` methods.
  * When calling one of the `Enter...()` methods with an already canceled
    [`CancellationToken`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken),
    the method does not try to acquire the lock, but instead throws a `OperationCanceledException`,
	which matches the behavior of `SemaphoreSlim`. <br>
	To try to acquire the lock without blocking, you can call one of the `Try...` methods without
	specifying a timeout (or specify a timeout of `0`).
  * You can downgrade a *write mode* lock to a *read mode* lock by calling
    `DowngradeWriteLockToReadLock()`.
  * Non-async methods do not require a ThreadPool thread to run the unblock logic; instead all
    code is executed in the thread that called the synchronous method.
  * Consider the following scenario: Thread A holds a read lock. During that time, Thread B tries to
    get a write lock but cancels the wait operation after a specific time (e.g. by specifying a
	timeout or using a `CancellationToken`). Before Thread B cancels the wait operation, Thread C tries to
	enter a read lock (which has to wait until no other write lock is active or waiting to be acquired).
	Now, after Thread B cancels the wait operation, Thread C will correctly enter the read lock. <br>
	In contrast, with Nito.AsyncEx' `AsyncReaderWriterLock`, Thread C will not enter the read lock in this situation.
	Such an issue also existed in .NET's `ReaderWriterLockSlim`
	[prior to .NET Framework 4.7.1](https://github.com/Microsoft/dotnet/blob/master/releases/net471/dotnet471-changes.md#bcl).



## Additional Infos

The lock internally uses
[`SemaphoreSlim`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim)
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
`EnterWriteLock (CancellationToken)`                                                | Enters the lock in write mode.
`EnterWriteLockAsync (CancellationToken)`                                           | Asynchronously enters the lock in write mode.
`TryEnterWriteLock (Int32, CancellationToken)`                                      | Tries to enter the lock in write mode, with an optional integer time-out.
`TryEnterWriteLockAsync (Int32, CancellationToken)`                                 | Tries to asynchronously enter the lock in write mode, with an optional integer time-out.
`DowngradeWriteLockToReadLock ()`                                                   | Downgrades the lock from write mode to read mode.
`ExitReadLock ()`                                                                   | Exits read mode.
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

Enter the lock in read mode within an async method within an `using` block:

```c#
private async Task TestReadModeAsync(AsyncReaderWriterLockSlim asyncLock)
{
    // Asynchronously enter the lock in read mode. The task completes after the lock
    // has been acquired.
    // As the Get...() methods return a IDisposeable, you can use the lock within an
    // "using" block.
    using (var myLock = await asyncLock.GetReadLockAsync())
    {
        // Use Task.Delay to simulate asynchronous work, which means a different thread
        // might continue execution after this point.
        await Task.Delay(200);
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
