﻿using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal abstract class OperatingSystemInfoProvider
    {
        private static OperatingSystemInfoProvider instance;
        private static object lockObj = new object();

        protected OperatingSystemInfoProvider()
        {
        }

        internal static OperatingSystemInfoProvider Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (lockObj)
                    {
                        if (instance == null)
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                instance = new WindowsInfoProvider();
                            }
                            else
                            {
                                instance = new LinuxInfoProvider();
                            }
                        }
                    }
                }

                return instance;
            }
        }

        internal abstract (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse();

        internal abstract int GetActivePortCount(int processId = -1);

        internal abstract int GetActiveEphemeralPortCount(int processId = -1);

        internal abstract (int LowPort, int HighPort) TupleGetDynamicPortRange();

        internal abstract Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken);
    }
}
