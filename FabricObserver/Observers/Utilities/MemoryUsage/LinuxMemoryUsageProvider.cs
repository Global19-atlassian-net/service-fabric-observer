﻿using System.Collections.Generic;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxMemoryUsageProvider : MemoryUsageProvider
    {
        public LinuxMemoryUsageProvider()
        {
        }

        internal override ulong GetCommittedBytes()
        {
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();

            ulong memTotal = memInfo[MemInfoConstants.MemTotal];
            ulong memFree = memInfo[MemInfoConstants.MemFree];
            ulong memAvail = memInfo[MemInfoConstants.MemAvailable];
            ulong swapTotal = memInfo[MemInfoConstants.SwapTotal];
            ulong swapFree = memInfo[MemInfoConstants.SwapFree];

            return (memTotal - memAvail - memFree + (swapTotal - swapFree)) * 1024;
        }
    }
}