package incus

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
)

type Client struct {
	HTTP     *http.Client
	Endpoint string
}

type InstanceSpec struct {
	Name             string `json:"name"`
	ImageFingerprint string `json:"imageFingerprint"`
	CPU              int    `json:"cpu"`
	MemoryMiB        int    `json:"memoryMiB"`
	DiskGiB          int    `json:"diskGiB"`
	StoragePool      string `json:"storagePool"`
	Network          string `json:"network"`
}

var resourceName = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$`)
var fingerprint = regexp.MustCompile(`^[a-f0-9]{12,64}$`)
var operationPath = regexp.MustCompile(`^/1\.0/operations/[a-zA-Z0-9-]+$`)

func (s InstanceSpec) validate() error {
	if !resourceName.MatchString(s.Name) || !resourceName.MatchString(s.StoragePool) || !resourceName.MatchString(s.Network) || !fingerprint.MatchString(s.ImageFingerprint) {
		return errors.New("invalid instance name, image fingerprint, storage pool or network")
	}
	if s.CPU < 1 || s.CPU > 1024 || s.MemoryMiB < 64 || s.MemoryMiB > 16777216 || s.DiskGiB < 1 || s.DiskGiB > 1048576 {
		return errors.New("resource value out of range")
	}
	return nil
}

// CreateOwned submits an asynchronous request. Completion and effective limits must
// be checked separately; an accepted operation is not a ready instance.
func (c Client) CreateOwned(ctx context.Context, s InstanceSpec, jobID string) (string, error) {
	if err := s.validate(); err != nil {
		return "", err
	}
	if !jobIDPattern.MatchString(jobID) {
		return "", errors.New("invalid job ownership marker")
	}
	if c.HTTP == nil {
		return "", errors.New("missing HTTP client")
	}
	body := map[string]any{
		"name": s.Name, "type": "container", "profiles": []string{},
		"source":  map[string]string{"type": "image", "fingerprint": s.ImageFingerprint},
		"config":  map[string]string{"user.lwt.job": jobID, "security.privileged": "false", "security.nesting": "false", "limits.cpu": fmt.Sprint(s.CPU), "limits.memory": fmt.Sprintf("%dMiB", s.MemoryMiB)},
		"devices": map[string]map[string]string{"root": {"type": "disk", "path": "/", "pool": s.StoragePool, "size": fmt.Sprintf("%dGiB", s.DiskGiB)}, "eth0": {"type": "nic", "network": s.Network}},
	}
	return c.submit(ctx, http.MethodPost, "/1.0/instances", body)
}

func (c Client) submit(ctx context.Context, method, path string, body any) (string, error) {
	if c.HTTP == nil {
		return "", errors.New("missing HTTP client")
	}
	data, err := json.Marshal(body)
	if err != nil {
		return "", err
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(c.Endpoint, "/")+path, bytes.NewReader(data))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusAccepted {
		return "", fmt.Errorf("incus create rejected (HTTP %d)", resp.StatusCode)
	}
	var result struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Operation  string `json:"operation"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result); err != nil || result.Type != "async" || result.StatusCode != 100 || !operationPath.MatchString(result.Operation) {
		return "", errors.New("invalid incus operation response")
	}
	return result.Operation, nil
}

// ChangeState permits only explicitly supported lifecycle actions.
func (c Client) ChangeState(ctx context.Context, name, action string) (string, error) {
	if !resourceName.MatchString(name) || (action != "start" && action != "stop" && action != "restart") {
		return "", errors.New("invalid instance or action")
	}
	return c.submit(ctx, http.MethodPut, "/1.0/instances/"+name+"/state", map[string]any{"action": action, "force": false})
}

type OperationStatus string

const (
	OperationPending OperationStatus = "Pending"
	OperationSuccess OperationStatus = "Success"
	OperationFailure OperationStatus = "Failure"
)

// Operation distinguishes an accepted request from its eventual outcome.
func (c Client) Operation(ctx context.Context, path string) (OperationStatus, error) {
	if c.HTTP == nil || !operationPath.MatchString(path) {
		return "", errors.New("invalid operation")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.Endpoint, "/")+path, nil)
	if err != nil {
		return "", err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("incus operation query failed (HTTP %d)", resp.StatusCode)
	}
	var result struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Metadata   struct {
			StatusCode int    `json:"status_code"`
			Error      string `json:"err"`
		} `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result) != nil || result.Type != "sync" || result.StatusCode != 200 {
		return "", errors.New("invalid incus operation response")
	}
	switch {
	case result.Metadata.StatusCode == 200 && result.Metadata.Error == "":
		return OperationSuccess, nil
	case result.Metadata.StatusCode >= 100 && result.Metadata.StatusCode < 200 && result.Metadata.Error == "":
		return OperationPending, nil
	case result.Metadata.StatusCode >= 400 || result.Metadata.Error != "":
		return OperationFailure, nil
	default:
		return "", errors.New("unknown incus operation status")
	}
}
