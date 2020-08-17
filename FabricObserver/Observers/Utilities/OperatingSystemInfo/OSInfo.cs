﻿namespace FabricObserver.Observers.Utilities
{
    internal struct OSInfo
    {
        // General Info
        public string Name;
        public string Version;
        public string Status;
        public string Language;
        public int NumberOfProcesses;
        public string InstallDate;
        public string LastBootUpTime;

        // Mem
        public ulong AvailableMemoryKB;
        public ulong FreePhysicalMemoryKB;
        public ulong FreeVirtualMemoryKB;
        public ulong TotalVirtualMemorySizeKB;
        public ulong TotalVisibleMemorySizeKB;
    }
}
