package main

import (
	"os"
	"runtime"
	"runtime/metrics"
)

type RuntimeSnapshot struct {
	PID                int    `json:"pid"`
	GoVersion          string `json:"goVersion"`
	Goroutines         uint64 `json:"goroutines"`
	GOMAXPROCS         uint64 `json:"gomaxprocs"`
	HeapObjectsBytes   uint64 `json:"heapObjectsBytes"`
	GCHeapLiveBytes    uint64 `json:"gcHeapLiveBytes"`
	GCHeapAllocBytes   uint64 `json:"gcHeapAllocBytes"`
	GCHeapAllocObjects uint64 `json:"gcHeapAllocObjects"`
	GCCycles           uint64 `json:"gcCycles"`
}

func currentRuntimeSnapshot() RuntimeSnapshot {
	samples := []metrics.Sample{
		{Name: "/sched/goroutines:goroutines"},
		{Name: "/sched/gomaxprocs:threads"},
		{Name: "/memory/classes/heap/objects:bytes"},
		{Name: "/gc/heap/live:bytes"},
		{Name: "/gc/heap/allocs:bytes"},
		{Name: "/gc/heap/allocs:objects"},
		{Name: "/gc/cycles/total:gc-cycles"},
	}
	metrics.Read(samples)

	return RuntimeSnapshot{
		PID:                os.Getpid(),
		GoVersion:          runtime.Version(),
		Goroutines:         runtimeMetricUint64(samples[0]),
		GOMAXPROCS:         runtimeMetricUint64(samples[1]),
		HeapObjectsBytes:   runtimeMetricUint64(samples[2]),
		GCHeapLiveBytes:    runtimeMetricUint64(samples[3]),
		GCHeapAllocBytes:   runtimeMetricUint64(samples[4]),
		GCHeapAllocObjects: runtimeMetricUint64(samples[5]),
		GCCycles:           runtimeMetricUint64(samples[6]),
	}
}

func runtimeMetricUint64(sample metrics.Sample) uint64 {
	if sample.Value.Kind() != metrics.KindUint64 {
		return 0
	}
	return sample.Value.Uint64()
}
