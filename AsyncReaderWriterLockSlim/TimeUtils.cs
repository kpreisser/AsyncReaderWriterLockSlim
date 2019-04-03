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
                    if (NativeMethodsWindows.QueryUnbiasedInterruptTime(out _)) {
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
                    // This is also done in .NET Core:
                    // https://source.dot.net/#System.Private.CoreLib/src/System/Threading/Timer.cs,67
                    if (!NativeMethodsWindows.QueryUnbiasedInterruptTime(out long timestamp))
                        throw new InvalidOperationException(); // Should not happen

                    return timestamp;
                }
                else {
                    //// TODO: Check how to correctly get an unbiased timestamp on UNIX.
                    //// Currently we simply use the Stopwatch.
                }
            }

            // Note: On Windows, Stopwatch.GetTimestamp() uses QueryPerformanceCounter (QPC)
            // which doesn't overflow in less than 100 years, according to the documentation.
            // See:
            // https://docs.microsoft.com/en-us/windows/desktop/SysInfo/acquiring-high-resolution-time-stamps
            // We need to calculate with doubles so that it is accurate, because when using long the
            // result would either be inaccurate (when you first divide frequency by 1000, the decimal
            // places would be cut off), or an overflow could occur (if you multiply first
            // GetTimestamp() with 1000).
            // This aligns with the implementation of Stopwatch.ElapsedMilliseconds.
            return (long)(10000000d * unchecked((ulong)Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
        }
    }
}
