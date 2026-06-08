using System.Runtime;

namespace DiskSpaceAnalyzer.Services;

public static class MemoryReleaseService
{
    public static void ReleaseUnusedMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        SystemInterop.TrimWorkingSet();
    }
}
