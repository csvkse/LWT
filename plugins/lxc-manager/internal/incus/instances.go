package incus

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

type Client struct {
	HTTP     *http.Client
	Endpoint string
	Project  string
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
var networkPattern = regexp.MustCompile(`^(?:[a-zA-Z0-9][a-zA-Z0-9-]{0,62}|macvlan:[a-zA-Z0-9][a-zA-Z0-9._-]{0,62}|bridged:[a-zA-Z0-9][a-zA-Z0-9._-]{0,62})$`)
var fingerprint = regexp.MustCompile(`^[a-f0-9]{12,64}$`)
var operationPath = regexp.MustCompile(`^/1\.0/operations/[a-zA-Z0-9-]+$`)

func (s InstanceSpec) validate() error {
	if !resourceName.MatchString(s.Name) || !resourceName.MatchString(s.StoragePool) || !networkPattern.MatchString(s.Network) || !fingerprint.MatchString(s.ImageFingerprint) {
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

	ethDev := map[string]string{"type": "nic"}
	if strings.HasPrefix(s.Network, "macvlan:") {
		ethDev["nictype"] = "macvlan"
		ethDev["parent"] = strings.TrimPrefix(s.Network, "macvlan:")
	} else if strings.HasPrefix(s.Network, "bridged:") {
		ethDev["nictype"] = "bridged"
		ethDev["parent"] = strings.TrimPrefix(s.Network, "bridged:")
	} else {
		ethDev["network"] = s.Network
	}

	body := map[string]any{
		"name": s.Name, "type": "container", "profiles": []string{},
		"source":  map[string]string{"type": "image", "fingerprint": s.ImageFingerprint},
		"config":  map[string]string{"user.lwt.job": jobID, "security.privileged": "false", "security.nesting": "false", "limits.cpu": fmt.Sprint(s.CPU), "limits.memory": fmt.Sprintf("%dMiB", s.MemoryMiB)},
		"devices": map[string]map[string]string{"root": {"type": "disk", "path": "/", "pool": s.StoragePool, "size": fmt.Sprintf("%dGiB", s.DiskGiB)}, "eth0": ethDev},
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
	req, err := http.NewRequestWithContext(ctx, method, c.apiURL(path, nil), bytes.NewReader(data))
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
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL(path, nil), nil)
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

// InstanceDetail represents low-level container instance configuration and devices.
type InstanceDetail struct {
	Name    string                       `json:"name"`
	Status  string                       `json:"status"`
	Config  map[string]string            `json:"config"`
	Devices map[string]map[string]string `json:"devices"`
}

// GetInstance fetches current instance definition including devices and config.
func (c Client) GetInstance(ctx context.Context, name string) (*InstanceDetail, error) {
	if c.HTTP == nil {
		return nil, errors.New("missing HTTP client")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL("/1.0/instances/"+name, nil), nil)
	if err != nil {
		return nil, err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("failed to get instance %s (HTTP %d)", name, resp.StatusCode)
	}
	var res struct {
		Type       string         `json:"type"`
		StatusCode int            `json:"status_code"`
		Metadata   InstanceDetail `json:"metadata"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(&res); err != nil {
		return nil, err
	}
	return &res.Metadata, nil
}

// SubmitPatch sends a PATCH request to update instance configuration or devices.
func (c Client) SubmitPatch(ctx context.Context, path string, body any) error {
	if c.HTTP == nil {
		return errors.New("missing HTTP client")
	}
	data, err := json.Marshal(body)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPatch, c.apiURL(path, nil), bytes.NewReader(data))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		return nil
	}
	if resp.StatusCode == http.StatusAccepted {
		var result struct {
			Operation string `json:"operation"`
		}
		if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result); err == nil && result.Operation != "" {
			return c.WaitOperation(ctx, result.Operation, 15*time.Second)
		}
		return nil
	}
	respBody, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<10))
	return fmt.Errorf("incus patch rejected (HTTP %d): %s", resp.StatusCode, string(respBody))
}

// DoRequest executes a configured HTTP request using the Client's HTTP transport.
func (c Client) DoRequest(req *http.Request) (*http.Response, error) {
	if c.HTTP == nil {
		return nil, errors.New("missing HTTP client")
	}
	return c.HTTP.Do(req)
}

// ApiURL exposes apiURL resolution with project scoping.
func (c Client) ApiURL(path string, query url.Values) string {
	return c.apiURL(path, query)
}

// WaitOperation polls an Incus operation path until completion or timeout.
func (c Client) WaitOperation(ctx context.Context, path string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		status, err := c.Operation(ctx, path)
		if err != nil {
			return err
		}
		if status == OperationSuccess {
			return nil
		}
		if status == OperationFailure {
			return errors.New("incus operation failed")
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
	return errors.New("incus operation timed out")
}

// Exec runs a command synchronously inside an instance and captures stdout/stderr.
func (c Client) Exec(ctx context.Context, name string, cmd []string) (string, string, int, error) {
	if c.HTTP == nil {
		return "", "", -1, errors.New("missing HTTP client")
	}
	body := map[string]any{
		"command":            cmd,
		"wait-for-websocket": false,
		"record-output":      true,
		"interactive":        false,
	}
	data, err := json.Marshal(body)
	if err != nil {
		return "", "", -1, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.apiURL("/1.0/instances/"+name+"/exec", nil), bytes.NewReader(data))
	if err != nil {
		return "", "", -1, err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return "", "", -1, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusAccepted {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<10))
		return "", "", -1, fmt.Errorf("exec rejected (HTTP %d): %s", resp.StatusCode, string(b))
	}
	var res struct {
		Operation string `json:"operation"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&res); err != nil || res.Operation == "" {
		return "", "", -1, errors.New("invalid exec operation response")
	}

	waitReq, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL(res.Operation+"/wait", nil), nil)
	if err != nil {
		return "", "", -1, err
	}
	waitResp, err := c.HTTP.Do(waitReq)
	if err != nil {
		return "", "", -1, err
	}
	defer waitResp.Body.Close()

	var waitResult struct {
		Metadata struct {
			Metadata struct {
				Output map[string]string `json:"output"`
				Return int               `json:"return"`
			} `json:"metadata"`
		} `json:"metadata"`
	}
	if err := json.NewDecoder(io.LimitReader(waitResp.Body, 1<<20)).Decode(&waitResult); err != nil {
		return "", "", -1, err
	}

	stdout := ""
	stderr := ""
	meta := waitResult.Metadata.Metadata
	if path, ok := meta.Output["1"]; ok && path != "" {
		if outReq, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL(path, nil), nil); err == nil {
			if outResp, err := c.HTTP.Do(outReq); err == nil {
				outBytes, _ := io.ReadAll(io.LimitReader(outResp.Body, 64<<10))
				outResp.Body.Close()
				stdout = string(outBytes)
			}
		}
	}
	if path, ok := meta.Output["2"]; ok && path != "" {
		if errReq, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL(path, nil), nil); err == nil {
			if errResp, err := c.HTTP.Do(errReq); err == nil {
				errBytes, _ := io.ReadAll(io.LimitReader(errResp.Body, 64<<10))
				errResp.Body.Close()
				stderr = string(errBytes)
			}
		}
	}
	return stdout, stderr, meta.Return, nil
}
// Incus remains the authorization boundary: the server-side TLS certificate
// must independently be restricted to this project.
func (c Client) apiURL(path string, query url.Values) string {
	if query == nil {
		query = make(url.Values)
	}
	if c.Project != "" {
		query.Set("project", c.Project)
	}
	endpoint := strings.TrimRight(c.Endpoint, "/") + path
	if encoded := query.Encode(); encoded != "" {
		return endpoint + "?" + encoded
	}
	return endpoint
}
