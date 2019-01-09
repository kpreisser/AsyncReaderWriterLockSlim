using System;
using System.Diagnostics;
#if !NET45
using System.Runtime.InteropServices;
#endif

namespace KPreisser
{
    internal static partial class TimeUtils
    {
        private static readonly bool isQueryUnbiasedInterruptTimeAvailable;


        static TimeUtils()
        {
            // If we are running on Windows, check if we can use the
            // QueryUnbiasedInterruptTime API.
#if !NET45
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
#else
            if (true) {
#endif
                try {
                    if (NativeMethodsWindows.QueryUnbiasedInterruptTime(out long unused)) {
                        // OK, API is useable.
                        isQueryUnbiasedInterruptTimeAvailable = true;
                    }
                }
                catch {
                    // Ignore.
                }
            }
        }


        /// <summary>
        /// Gets a timestamp in DateTime Ticks that contains the time elapsed since the
        /// system has started, or (if <paramref name="unbiased"/> is <c>true</c>) the time
        /// the system has spent in the working state.
        /// </summary>
        /// <returns></returns>
        public static long GetTimestampTicks(bool unbiased = false)
        {
            if (unbiased) {
#if !NET45
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                        isQueryUnbiasedInterruptTimeAvailable) {
#else
                if (isQueryUnbiasedInterruptTimeAvailable) {
#endif
                    // On Windows, we need to use QueryUnbiasedInterruptTime, because
                    // Environment.TickCount/GetTickCount64 and QueryPerformanceCounter will
                    // return the biased time (including the time the system has spent
                    // in standby/hibernation).
                    if (!NativeMethodsWindows.QueryUnbiasedInterruptTime(out long timestamp))
                        throw new InvalidOperationException(); // Should not happen

                    return timestamp;
                }
                else {
                    //// TODO: Check how to correctly get an unbiased timestamp on UNIX.
                    //// Currently we simply use the Stopwatch.
                }
            }

            return (long)(10000000d * unchecked((ulong)Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
        }
    }
}
