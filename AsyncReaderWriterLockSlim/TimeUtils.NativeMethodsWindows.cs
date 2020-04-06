using System.Runtime.InteropServices;

namespace KPreisser
{
    internal static partial class TimeUtils
    {
        private static class NativeMethodsWindows
        {
            [DllImport("kernel32.dll", EntryPoint = "QueryUnbiasedInterruptTime", ExactSpelling = true)]
            public static extern bool QueryUnbiasedInterruptTime(out long value);
        }
    }
}
