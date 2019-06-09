using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KPreisser.LockTests
{
    [TestClass]
    public class AsyncReaderWriterLockSlimTests
    {
        public AsyncReaderWriterLockSlimTests()
            : base()
        {
        }


        [TestMethod]
        public void CanEnterLocksSync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            Assert.IsTrue(myLock.TryEnterReadLock(0));
            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            myLock.ExitReadLock();
            myLock.ExitReadLock();

            myLock.EnterWriteLock();
            Assert.IsFalse(myLock.TryEnterReadLock(0));
            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        public async Task CanEnterLocksAync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            await myLock.EnterReadLockAsync();
            Assert.IsTrue(await myLock.TryEnterReadLockAsync(0));
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(0));
            myLock.ExitReadLock();
            myLock.ExitReadLock();

            await myLock.EnterWriteLockAsync();
            Assert.IsFalse(await myLock.TryEnterReadLockAsync(0));
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(0));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void ThrowsOnIncorrectWriteLockRelease()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterWriteLock();

            myLock.ExitWriteLock();
            myLock.ExitWriteLock(); // should throw
        }

        [TestMethod]
        public async Task MixedSyncAndAsync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            await myLock.EnterReadLockAsync();

            myLock.ExitReadLock();
            myLock.ExitReadLock();

            myLock.EnterWriteLock();
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(10));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public void ThrowsOperationCanceledException()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            try
            {
                // Should throw without entering the lock.
                myLock.EnterReadLock(new CancellationToken(true));
            }
            catch (OperationCanceledException)
            {
                // Check that the read lock actually was not entered.
                Assert.IsTrue(myLock.TryEnterWriteLock(0));

                throw;
            }
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public void ThrowsOperationCanceledExceptionAfterWait()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();

            try
            {
                using (var cts = new CancellationTokenSource(100))
                {
                    myLock.EnterWriteLock(cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Check that we can now enter another read lock.
                Assert.IsTrue(myLock.TryEnterReadLock(0));

                throw;
            }
        }

        [TestMethod]
        public async Task WriteTakesPriorityOverRead1()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            // Check that no new read lock can be entered when another thread/task wants
            // to enter the write lock while at least one read lock is active.
            myLock.EnterReadLock();

            using (var cts = new CancellationTokenSource())
            {
                var enterWriteLockTask = myLock.EnterWriteLockAsync(cts.Token);

                Assert.IsFalse(myLock.TryEnterReadLock(0));

                cts.Cancel();
                try
                {
                    await enterWriteLockTask;
                    Assert.Fail();
                }
                catch (OperationCanceledException)
                {
                }
            }

            // Verify that now a new read lock can be entered.
            Assert.IsTrue(await myLock.TryEnterReadLockAsync(0));
        }

        [TestMethod]
        public async Task WriteTakesPriorityOverRead2()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            // Check that when at least one thread/task wants to enter the read lock and at least
            // one thread/task wants to enter the write lock while another write lock is active,
            // writers always get precedence over readers when the existing write lock is released.
            for (int i = 0; i < 1000; i++)
            {
                myLock.EnterWriteLock();

                var readLockTask = myLock.EnterReadLockAsync();
                var writeLockTask = myLock.EnterWriteLockAsync();

                // Release the current write lock. Now the writeLockTask should complete.
                myLock.ExitWriteLock();
                await writeLockTask;

                // After releasing the second write lock, the readLockTask should complete.
                myLock.ExitWriteLock();
                await readLockTask;
                myLock.ExitReadLock();
            }
        }

        [TestMethod]
        public async Task ReleasingOneOfTwoReadLocksDoesNotReleaseWaitingWriteAndReadLocks()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            await myLock.EnterReadLockAsync();
            await myLock.EnterReadLockAsync();

            var writeLockTask = myLock.TryEnterWriteLockAsync(500);
            var readLockTask = myLock.TryEnterReadLockAsync(300);

            // When releasing one of the two read locks, the waiting write lock should not
            // be released, and also the waiting read lock should not be released because of
            // the waiting write lock.
            myLock.ExitReadLock();

            Assert.IsFalse(await readLockTask);
            Assert.IsFalse(await writeLockTask);
        }

        [TestMethod]
        public void DowngradeLockAllowsReadLock()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterWriteLock();

            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            Assert.IsFalse(myLock.TryEnterReadLock(0));

            // After downgrading the lock and after the try to get the write lock is canceled,
            // it should be possible to enter another read lock.
            myLock.DowngradeWriteLockToReadLock();

            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            Assert.IsTrue(myLock.TryEnterReadLock(0));

            myLock.ExitReadLock();
            myLock.ExitReadLock();
        }

        [TestMethod]
        public void MultipleThreads()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            bool enteredWriteLock = false;
            var t1 = new Thread(() =>
            {
                myLock.EnterWriteLock();
                Volatile.Write(ref enteredWriteLock, true);
                myLock.ExitWriteLock();
            });
            t1.Start();

            // Wait a bit, then release the read lock, so that the other thread can enter
            // the write lock.
            Thread.Sleep(200);
            Assert.IsFalse(Volatile.Read(ref enteredWriteLock));
            myLock.ExitReadLock();

            t1.Join();
            Assert.IsTrue(Volatile.Read(ref enteredWriteLock));
        }

        [TestMethod]
        public void MultipleThreadsAndTasks()
        {
            var myLock = new AsyncReaderWriterLockSlim();
            int readLocksEntered = 0;

            var threads = new Thread[5];
            var tasks = new Task[5];

            myLock.EnterWriteLock();
            for (int i = 0; i < threads.Length; i++)
            {
                (threads[i] = new Thread(() =>
                {
                    myLock.EnterReadLock();
                    Interlocked.Increment(ref readLocksEntered);
                })).Start();
            }
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    await myLock.EnterReadLockAsync();
                    Interlocked.Increment(ref readLocksEntered);
                });
            }

            // Wait a bit, then release the read lock, so that the other thread can enter
            // the write lock.
            Thread.Sleep(200);
            Assert.AreEqual(0, Volatile.Read(ref readLocksEntered));
            myLock.ExitWriteLock();

            // Wait for the tasks and threads.
            foreach (var thread in threads)
                thread.Join();
            foreach (var task in tasks)
                task.GetAwaiter().GetResult();

            Assert.AreEqual(threads.Length + tasks.Length, Volatile.Read(ref readLocksEntered));
        }

        [TestMethod]
        [Timeout(5000)]
        public void WaitingReaderIsReleasedAfterWaitingWriterCanceled()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            // Thread A enters a read lock.
            myLock.EnterReadLock();

            // Thread B wants to enter a write lock within 2 seconds. Because Thread A holds
            // a read lock, Thread B will not get the write lock.
            bool threadBResult = true;
            var threadB = new Thread(() =>
            {
                bool result = myLock.TryEnterWriteLock(600);
                Volatile.Write(ref threadBResult, result);
            });
            threadB.Start();

            // Wait a bit before starting the next thread, to ensure Thread B is already
            // in the TryEnter...() call.
            Thread.Sleep(200);

            // Thread C wants to enter a read lock. It should get the lock after
            // 2 seconds because Thread B cancels its try to get the write lock after
            // that time.
            var threadC = new Thread(() =>
            {
                myLock.EnterReadLock();
            });
            threadC.Start();

            threadB.Join();
            threadC.Join();

            Assert.IsFalse(Volatile.Read(ref threadBResult));

            myLock.EnterReadLock();
        }

        [TestMethod]
        public void LoadTest()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            object lockCountSyncRoot = new object();
            int readLockCount = 0, writeLockCount = 0;

            bool incorrectLockCount = false;

            void checkLockCount()
            {
                Debug.WriteLine($"ReadLocks = {readLockCount}, WriteLocks = {writeLockCount}");

                bool countIsCorrect = readLockCount == 0 && writeLockCount == 0 ||
                        readLockCount > 0 && writeLockCount == 0 ||
                        readLockCount == 0 && writeLockCount == 1;

                if (!countIsCorrect)
                    Volatile.Write(ref incorrectLockCount, true);
            }

            bool cancel = false;

            var threads = new Thread[20];
            var tasks = new Task[20];

            var masterRandom = new Random();

            for (int i = 0; i < threads.Length; i++)
            {
                var random = new Random(masterRandom.Next());
                (threads[i] = new Thread(() =>
                {
                    bool isRead = random.Next(100) < 70;
                    if (isRead)
                        myLock.EnterReadLock();
                    else
                        myLock.EnterWriteLock();

                    lock (lockCountSyncRoot)
                    {
                        if (isRead)
                            readLockCount++;
                        else
                            writeLockCount++;
                    }

                    // Simulate work.
                    Thread.Sleep(10);

                    lock (lockCountSyncRoot)
                    {
                        if (isRead)
                        {
                            myLock.ExitReadLock();
                            readLockCount--;
                        }
                        else
                        {
                            myLock.ExitWriteLock();
                            writeLockCount--;
                        }
                    }
                })).Start();
            }
            for (int i = 0; i < tasks.Length; i++)
            {
                var random = new Random(masterRandom.Next());
                tasks[i] = Task.Run(async () =>
                {
                    while (!Volatile.Read(ref cancel))
                    {
                        bool isRead = random.Next(10) < 7;
                        if (isRead)
                            await myLock.EnterReadLockAsync();
                        else
                            await myLock.EnterWriteLockAsync();

                        lock (lockCountSyncRoot)
                        {
                            if (isRead)
                                readLockCount++;
                            else
                                writeLockCount++;

                            checkLockCount();
                        }

                        // Simulate work.
                        await Task.Delay(10);

                        lock (lockCountSyncRoot)
                        {
                            if (isRead)
                            {
                                myLock.ExitReadLock();
                                readLockCount--;
                            }
                            else
                            {
                                myLock.ExitWriteLock();
                                writeLockCount--;
                            }

                            checkLockCount();
                        }
                    }
                });
            }

            // Run for 5 seconds, then stop the tasks and threads.
            Thread.Sleep(5000);

            Volatile.Write(ref cancel, true);
            foreach (var thread in threads)
                thread.Join();
            foreach (var task in tasks)
                task.GetAwaiter().GetResult();

            Assert.IsFalse(incorrectLockCount);
        }
    }
}