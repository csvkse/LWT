package incus

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// ResourceSample is a point-in-time observation, not an admission guarantee.
type ResourceSample struct {
	CPUThreads       uint64    `json:"cpuThreads"`
	MemoryTotalBytes uint64    `json:"memoryTotalBytes"`
	MemoryUsedBytes  uint64    `json:"memoryUsedBytes"`
	PoolTotalBytes   uint64    `json:"poolTotalBytes"`
	PoolUsedBytes    uint64    `json:"poolUsedBytes"`
	ObservedAt       time.Time `json:"observedAt"`
}

func (c Client) getResource(ctx context.Context, path string, out any) error {
	if c.HTTP == nil {
		return errors.New("missing HTTP client")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.Endpoint, "/")+path, nil)
	if err != nil {
		return err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("incus resource query failed (HTTP %d)", resp.StatusCode)
	}
	var envelope struct {
		Type       string          `json:"type"`
		StatusCode int             `json:"status_code"`
		Metadata   json.RawMessage `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(&envelope) != nil || envelope.Type != "sync" || envelope.StatusCode != 200 || len(envelope.Metadata) == 0 || string(envelope.Metadata) == "null" {
		return errors.New("invalid resource response")
	}
	if json.Unmarshal(envelope.Metadata, out) != nil {
		return errors.New("invalid resource metadata")
	}
	return nil
}

// SampleResources reads trusted local Incus data. It must not be treated as
// reserved capacity, hard quota evidence, or a cluster-wide snapshot.
func (c Client) SampleResources(ctx context.Context, pool string) (ResourceSample, error) {
	if !resourceName.MatchString(pool) {
		return ResourceSample{}, errors.New("invalid pool name")
	}
	var host struct {
		CPU struct {
			Total *uint64 `json:"total"`
		} `json:"cpu"`
		Memory struct {
			Total *uint64 `json:"total"`
			Used  *uint64 `json:"used"`
		} `json:"memory"`
	}
	if err := c.getResource(ctx, "/1.0/resources", &host); err != nil {
		return ResourceSample{}, err
	}
	if host.CPU.Total == nil || *host.CPU.Total == 0 || host.Memory.Total == nil || *host.Memory.Total == 0 || host.Memory.Used == nil || *host.Memory.Used > *host.Memory.Total {
		return ResourceSample{}, errors.New("invalid host resource values")
	}
	var storage struct {
		Space struct {
			Total *uint64 `json:"total"`
			Used  *uint64 `json:"used"`
		} `json:"space"`
	}
	if err := c.getResource(ctx, "/1.0/storage-pools/"+pool+"/resources", &storage); err != nil {
		return ResourceSample{}, err
	}
	if storage.Space.Total == nil || *storage.Space.Total == 0 || storage.Space.Used == nil || *storage.Space.Used > *storage.Space.Total {
		return ResourceSample{}, errors.New("invalid pool resource values")
	}
	return ResourceSample{CPUThreads: *host.CPU.Total, MemoryTotalBytes: *host.Memory.Total, MemoryUsedBytes: *host.Memory.Used, PoolTotalBytes: *storage.Space.Total, PoolUsedBytes: *storage.Space.Used, ObservedAt: time.Now().UTC()}, nil
}
