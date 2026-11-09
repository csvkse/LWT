package evidence

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"lwt/lxc-manager/internal/incus"
)

func TestHostEvidenceProvider_CollectEvidence(t *testing.T) {
	// 1. Create a dummy /proc/meminfo
	tmpDir := t.TempDir()
	meminfoPath := filepath.Join(tmpDir, "meminfo")
	fakeMem := "MemTotal:       16384000 kB\nMemFree:         4096000 kB\nMemAvailable:    8192000 kB\nBuffers:          500000 kB\n"
	if err := os.WriteFile(meminfoPath, []byte(fakeMem), 0644); err != nil {
		t.Fatalf("failed to write fake meminfo: %v", err)
	}

	p := DefaultHostEvidenceProvider()
	p.MemInfoPath = meminfoPath

	discovery := incus.Discovery{
		Pools: []incus.PoolInfo{
			{Name: "default", Driver: "dir", Status: "Created"},
		},
	}

	evidence := p.CollectEvidence(context.Background(), "default", discovery)

	if !evidence.Verified {
		t.Errorf("expected Verified=true, got false")
	}
	if evidence.QuotaVerifiedForPool != "default" {
		t.Errorf("expected QuotaVerifiedForPool='default', got %s", evidence.QuotaVerifiedForPool)
	}
	// 8192000 kB = 8000 MiB, minus 512 MiB reserved = 7488 MiB
	expectedMem := (8192000 / 1024) - 512
	if evidence.MemoryAvailableMiB != expectedMem {
		t.Errorf("expected memory %d MiB, got %d MiB", expectedMem, evidence.MemoryAvailableMiB)
	}
	if evidence.CPUAvailable < 1 {
		t.Errorf("expected cpu >= 1, got %d", evidence.CPUAvailable)
	}
	if evidence.DiskAvailableGiB <= 0 {
		t.Errorf("expected disk > 0, got %d", evidence.DiskAvailableGiB)
	}
}

func TestHostEvidenceProvider_Fallback(t *testing.T) {
	p := DefaultHostEvidenceProvider()
	p.MemInfoPath = "/non-existent/file"

	evidence := p.CollectEvidence(context.Background(), "mypool", incus.Discovery{})

	if !evidence.Verified {
		t.Errorf("expected Verified=true")
	}
	if evidence.MemoryAvailableMiB <= 0 {
		t.Errorf("expected positive fallback memory")
	}
}
