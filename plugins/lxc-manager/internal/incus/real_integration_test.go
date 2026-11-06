//go:build linux

package incus

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

func TestCleanupTestInstanceRefusesForeignOwner(t *testing.T) {
	writes := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" {
			writes++
		}
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"name":"lwt-test-0123456789abcdef","type":"container","expanded_config":{"user.lwt.job":"ffffffffffffffffffffffffffffffff"}}}`))
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	err := cleanupTestInstance(context.Background(), c, "lwt-test-0123456789abcdef", "0123456789abcdef0123456789abcdef")
	if err == nil || writes != 0 {
		t.Fatalf("foreign cleanup err=%v writes=%d", err, writes)
	}
}

func TestCleanupTestInstanceRefusesSnapshots(t *testing.T) {
	deletes := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/1.0/instances/lwt-test-0123456789abcdef":
			if r.Method != http.MethodGet {
				deletes++
				w.WriteHeader(http.StatusMethodNotAllowed)
				return
			}
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"name":"lwt-test-0123456789abcdef","type":"container","expanded_config":{"user.lwt.job":"0123456789abcdef0123456789abcdef"}}}`))
		case "/1.0/instances/lwt-test-0123456789abcdef/state":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":102}}`))
		case "/1.0/instances/lwt-test-0123456789abcdef/snapshots":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":["/1.0/instances/lwt-test-0123456789abcdef/snapshots/before-cleanup"]}`))
		default:
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	err := cleanupTestInstance(context.Background(), c, "lwt-test-0123456789abcdef", "0123456789abcdef0123456789abcdef")
	if err == nil || !strings.Contains(err.Error(), "snapshots") || deletes != 0 {
		t.Fatalf("cleanup err=%v deletes=%d; want snapshot refusal and no delete", err, deletes)
	}
}

// TestRealIncusLifecycle must be run only against a disposable, isolated Incus
// environment explicitly selected by the operator. It never installs Incus or
// alters network/pool configuration. It does not certify quota enforcement.
func TestRealIncusLifecycle(t *testing.T) {
	if os.Getenv("LWT_INCUS_TEST_ENABLE") != "1" {
		t.Skip("set LWT_INCUS_TEST_ENABLE=1 on an isolated test host")
	}
	if os.Getenv("LWT_INCUS_TEST_CONFIRM_DISPOSABLE_HOST") != "I_UNDERSTAND_THIS_IS_A_DISPOSABLE_HOST" {
		t.Fatal("real Incus lifecycle test requires explicit disposable-host confirmation")
	}
	socket, pool, network, image := os.Getenv("LWT_INCUS_TEST_SOCKET"), os.Getenv("LWT_INCUS_TEST_POOL"), os.Getenv("LWT_INCUS_TEST_NETWORK"), os.Getenv("LWT_INCUS_TEST_IMAGE")
	if socket == "" || pool == "" || network == "" || image == "" {
		t.Fatal("test socket, dedicated pool, managed network and local image fingerprint are required")
	}
	idBytes := make([]byte, 16)
	if _, err := rand.Read(idBytes); err != nil {
		t.Fatal(err)
	}
	owner := hex.EncodeToString(idBytes)
	spec := InstanceSpec{Name: "lwt-test-" + owner[:16], ImageFingerprint: image, CPU: 1, MemoryMiB: 256, DiskGiB: 2, StoragePool: pool, Network: network}
	if err := spec.validate(); err != nil {
		t.Fatal(err)
	}
	transport := &http.Transport{DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, "unix", socket)
	}}
	client := &http.Client{Transport: transport, Timeout: 10 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	c := Client{HTTP: client, Endpoint: "http://incus"}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if cap := Probe(ctx, client, c.Endpoint); cap.Status != Ready {
		t.Fatalf("Incus is not trusted: %s", cap.Status)
	}
	t.Logf("temporary instance=%s owner=%s; inspect and clean manually if the process is interrupted", spec.Name, owner)
	defer func() {
		cleanupCtx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
		if err := cleanupTestInstance(cleanupCtx, c, spec.Name, owner); err != nil {
			t.Errorf("test cleanup requires attention for %s: %v", spec.Name, err)
		}
	}()
	op, err := c.CreateOwned(ctx, spec, owner)
	if err != nil {
		t.Fatal(err)
	}
	if err = waitTestOperation(ctx, c, op); err != nil {
		t.Fatal(err)
	}
	ok, err := c.VerifyCreated(ctx, spec, owner)
	if err != nil || !ok {
		t.Fatalf("effective instance configuration mismatch: %v", err)
	}
	op, err = c.ChangeState(ctx, spec.Name, "start")
	if err != nil {
		t.Fatal(err)
	}
	if err = waitTestOperation(ctx, c, op); err != nil {
		t.Fatal(err)
	}
	running, err := c.IsRunning(ctx, spec.Name)
	if err != nil || !running {
		t.Fatalf("instance not running: %v", err)
	}
	op, err = c.ChangeState(ctx, spec.Name, "stop")
	if err != nil {
		t.Fatal(err)
	}
	if err = waitTestOperation(ctx, c, op); err != nil {
		t.Fatal(err)
	}
	state, err := c.InstanceState(ctx, spec.Name)
	if err != nil || state != "Stopped" {
		t.Fatalf("instance not stopped: %s %v", state, err)
	}
}

func waitTestOperation(ctx context.Context, c Client, op string) error {
	for {
		status, err := c.Operation(ctx, op)
		if err != nil {
			return err
		}
		switch status {
		case OperationSuccess:
			return nil
		case OperationFailure:
			return fmt.Errorf("operation failed: %s", op)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

// Cleanup only removes a stopped, generated test instance bearing the exact
// generated ownership marker. Unknown ownership or status fails closed.
func cleanupTestInstance(ctx context.Context, c Client, name, owner string) error {
	if !strings.HasPrefix(name, "lwt-test-") || !resourceName.MatchString(name) || !jobIDPattern.MatchString(owner) {
		return fmt.Errorf("invalid cleanup identity")
	}
	req, err := http.NewRequestWithContext(ctx, "GET", c.Endpoint+"/1.0/instances/"+name, nil)
	if err != nil {
		return err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusNotFound {
		return nil
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("ownership query HTTP %d", resp.StatusCode)
	}
	var body struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Metadata   struct {
			Name   string            `json:"name"`
			Type   string            `json:"type"`
			Config map[string]string `json:"expanded_config"`
		} `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&body) != nil || body.Type != "sync" || body.StatusCode != 200 || body.Metadata.Name != name || body.Metadata.Type != "container" || body.Metadata.Config["user.lwt.job"] != owner {
		return fmt.Errorf("ownership mismatch; refusing cleanup")
	}
	state, err := c.InstanceState(ctx, name)
	if err != nil {
		return err
	}
	if state == "Running" {
		op, err := c.ChangeState(ctx, name, "stop")
		if err != nil {
			return err
		}
		if err = waitTestOperation(ctx, c, op); err != nil {
			return err
		}
		state, err = c.InstanceState(ctx, name)
		if err != nil {
			return err
		}
	}
	if state != "Stopped" {
		return fmt.Errorf("instance not confirmed stopped")
	}
	var snapshots []json.RawMessage
	if err := c.getResource(ctx, "/1.0/instances/"+name+"/snapshots", &snapshots); err != nil {
		return fmt.Errorf("cannot verify snapshots: %w", err)
	}
	if len(snapshots) != 0 {
		return fmt.Errorf("instance has snapshots; refusing cleanup")
	}
	req, err = http.NewRequestWithContext(ctx, "DELETE", c.Endpoint+"/1.0/instances/"+name, nil)
	if err != nil {
		return err
	}
	resp, err = c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusAccepted {
		return fmt.Errorf("delete HTTP %d", resp.StatusCode)
	}
	var result struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Operation  string `json:"operation"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result) != nil || result.Type != "async" || result.StatusCode != 100 || !operationPath.MatchString(result.Operation) {
		return fmt.Errorf("invalid delete response")
	}
	return waitTestOperation(ctx, c, result.Operation)
}
