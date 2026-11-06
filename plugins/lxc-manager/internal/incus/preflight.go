package incus

// PreflightEvidence is populated by trusted server-side checks, never by a UI
// request. Available amounts must already account for reservations and host
// safety margins. Quota verification is specific to the selected pool.
type PreflightEvidence struct {
	Verified             bool
	CPUAvailable         int
	MemoryAvailableMiB   int
	DiskAvailableGiB     int
	QuotaVerifiedForPool string
}
type PreflightResult struct {
	Allowed bool     `json:"allowed"`
	Reasons []string `json:"reasons"`
}

func Preflight(s InstanceSpec, d Discovery, e PreflightEvidence) PreflightResult {
	reasons := []string{}
	if s.validate() != nil {
		reasons = append(reasons, "invalid instance specification")
	}
	for _, v := range d.Instances {
		if v.Name == s.Name {
			reasons = append(reasons, "instance name already exists")
			break
		}
	}
	for _, name := range d.OccupiedNames {
		if name == s.Name {
			reasons = append(reasons, "instance name already exists")
			break
		}
	}
	image := false
	for _, v := range d.Images {
		if v.Fingerprint == s.ImageFingerprint {
			image = true
			break
		}
	}
	if !image {
		reasons = append(reasons, "image fingerprint not found")
	}
	pool := false
	for _, v := range d.Pools {
		if v.Name == s.StoragePool && v.Status == "Created" {
			pool = true
			break
		}
	}
	if !pool {
		reasons = append(reasons, "storage pool unavailable")
	}
	network := false
	for _, v := range d.Networks {
		if v.Name == s.Network {
			network = true
			break
		}
	}
	if !network {
		reasons = append(reasons, "managed bridge unavailable")
	}
	if !e.Verified {
		reasons = append(reasons, "capacity evidence unavailable")
	}
	if e.QuotaVerifiedForPool != s.StoragePool || e.QuotaVerifiedForPool == "" {
		reasons = append(reasons, "hard storage quota unverified")
	}
	if s.CPU > e.CPUAvailable || s.MemoryMiB > e.MemoryAvailableMiB || s.DiskGiB > e.DiskAvailableGiB {
		reasons = append(reasons, "insufficient verified capacity")
	}
	return PreflightResult{Allowed: len(reasons) == 0, Reasons: reasons}
}
