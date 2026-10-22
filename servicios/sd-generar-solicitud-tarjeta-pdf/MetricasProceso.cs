using System.Diagnostics;

namespace SdGenerarSolicitudTarjetaPdf;

/// <summary>Foto del proceso para la prueba de carga: hilos, ThreadPool, memoria y GC.</summary>
public static class MetricasProceso
{
    public static Dictionary<string, object> Capturar()
    {
        var proceso = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new()
        {
            ["hilosProceso"] = proceso.Threads.Count,
            ["hilosThreadPool"] = ThreadPool.ThreadCount,
            ["colaThreadPool"] = ThreadPool.PendingWorkItemCount,
            ["workingSetMB"] = proceso.WorkingSet64 / 1048576.0,
            ["heapGcMB"] = GC.GetTotalMemory(false) / 1048576.0,
            ["lohMB"] = gc.GenerationInfo[3].SizeAfterBytes / 1048576.0,
            ["asignadoTotalMB"] = GC.GetTotalAllocatedBytes(precise: true) / 1048576.0,
            ["gcGen0"] = GC.CollectionCount(0),
            ["gcGen1"] = GC.CollectionCount(1),
            ["gcGen2"] = GC.CollectionCount(2),
            ["pausaGcPct"] = GC.GetTotalPauseDuration().TotalMilliseconds
                / (DateTime.Now - proceso.StartTime).TotalMilliseconds * 100,
        };
    }
}
