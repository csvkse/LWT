package incus

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
)

var jobIDPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)

func (c Client) IsRunning(ctx context.Context, name string) (bool, error) {
	state, err := c.InstanceState(ctx, name)
	if err != nil {
		return false, err
	}
	return state == "Running", nil
}

// InstanceState recognizes terminal states explicitly; all other statuses
// remain transitioning and cannot be mistaken for a stopped instance.
func (c Client) InstanceState(ctx context.Context, name string) (string, error) {
	if !resourceName.MatchString(name) {
		return "", errors.New("invalid instance name")
	}
	var state struct {
		StatusCode *int `json:"status_code"`
	}
	if err := c.getResource(ctx, "/1.0/instances/"+name+"/state", &state); err != nil {
		return "", err
	}
	if state.StatusCode == nil {
		return "", errors.New("missing instance status")
	}
	switch *state.StatusCode {
	case 102:
		return "Stopped", nil
	case 103:
		return "Running", nil
	default:
		return "Transitioning", nil
	}
}

// VerifyCreated checks backend-reported effective config and an ownership
// marker. It does not prove runtime enforcement of storage quotas.
func (c Client) VerifyCreated(ctx context.Context, spec InstanceSpec, jobID string) (bool, error) {
	if c.HTTP == nil || spec.validate() != nil || !jobIDPattern.MatchString(jobID) {
		return false, errors.New("invalid verification request")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.Endpoint, "/")+"/1.0/instances/"+spec.Name, nil)
	if err != nil {
		return false, err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return false, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return false, fmt.Errorf("instance query failed (HTTP %d)", resp.StatusCode)
	}
	var result struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Metadata   struct {
			Name    string                       `json:"name"`
			Type    string                       `json:"type"`
			Config  map[string]string            `json:"expanded_config"`
			Devices map[string]map[string]string `json:"expanded_devices"`
		} `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result) != nil || result.Type != "sync" || result.StatusCode != 200 {
		return false, errors.New("invalid instance response")
	}
	m := result.Metadata
	config := m.Config
	root := m.Devices["root"]
	nic := m.Devices["eth0"]
	return m.Name == spec.Name && m.Type == "container" && config["user.lwt.job"] == jobID && config["security.privileged"] == "false" && config["security.nesting"] == "false" && config["limits.cpu"] == fmt.Sprint(spec.CPU) && config["limits.memory"] == fmt.Sprintf("%dMiB", spec.MemoryMiB) && config["volatile.base_image"] == spec.ImageFingerprint && root["type"] == "disk" && root["path"] == "/" && root["pool"] == spec.StoragePool && root["size"] == fmt.Sprintf("%dGiB", spec.DiskGiB) && nic["type"] == "nic" && nic["network"] == spec.Network, nil
}
