package incus

import "testing"

func fixtureDiscovery() Discovery {
	return Discovery{Instances: []InstanceInfo{{Name: "existing"}}, Images: []ImageInfo{{Fingerprint: "abcdef123456"}}, Pools: []PoolInfo{{Name: "pool1", Driver: "zfs", Status: "Created"}}, Networks: []NetworkInfo{{Name: "br0"}}}
}
func fixtureSpec() InstanceSpec {
	return InstanceSpec{Name: "new1", ImageFingerprint: "abcdef123456", CPU: 2, MemoryMiB: 512, DiskGiB: 8, StoragePool: "pool1", Network: "br0"}
}

func TestPreflightFailsClosedWithoutVerifiedCapacityAndQuota(t *testing.T) {
	result := Preflight(fixtureSpec(), fixtureDiscovery(), PreflightEvidence{})
	if result.Allowed || len(result.Reasons) == 0 {
		t.Fatalf("result=%+v", result)
	}
}

func TestPreflightAcceptsOnlyDiscoveredResourcesAndVerifiedEvidence(t *testing.T) {
	e := PreflightEvidence{CPUAvailable: 4, MemoryAvailableMiB: 2048, DiskAvailableGiB: 50, QuotaVerifiedForPool: "pool1", Verified: true}
	if got := Preflight(fixtureSpec(), fixtureDiscovery(), e); !got.Allowed {
		t.Fatalf("rejected valid request: %+v", got)
	}
	for _, change := range []func(*InstanceSpec){
		func(s *InstanceSpec) { s.Name = "existing" }, func(s *InstanceSpec) { s.ImageFingerprint = "999999999999" }, func(s *InstanceSpec) { s.StoragePool = "other" }, func(s *InstanceSpec) { s.Network = "unknown" }, func(s *InstanceSpec) { s.CPU = 5 }, func(s *InstanceSpec) { s.MemoryMiB = 4096 }, func(s *InstanceSpec) { s.DiskGiB = 100 },
	} {
		s := fixtureSpec()
		change(&s)
		if got := Preflight(s, fixtureDiscovery(), e); got.Allowed {
			t.Fatalf("accepted %+v", s)
		}
	}
}

func TestPreflightRequiresExactImageFingerprintAndHealthyPool(t *testing.T) {
	d := fixtureDiscovery()
	d.Images[0].Fingerprint = "abcdef1234567890"
	d.Pools[0].Status = "Errored"
	e := PreflightEvidence{CPUAvailable: 4, MemoryAvailableMiB: 2048, DiskAvailableGiB: 50, QuotaVerifiedForPool: "pool1", Verified: true}
	if result := Preflight(fixtureSpec(), d, e); result.Allowed {
		t.Fatalf("result=%+v", result)
	}
}
