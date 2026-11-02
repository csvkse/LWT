package testkit

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"time"

	"github.com/openai/tunnel-client/pkg/controlplane/wiretypes"
)

// ControlPlaneFixture implements a mock OpenAI Secure MCP Tunnel control plane.
type ControlPlaneFixture struct {
	server       *httptest.Server
	tunnelID     string
	expectedKey  string
	statusCode   atomic.Int32
	pollCount    atomic.Int64
	respCount    atomic.Int64

	mu           sync.Mutex
	commandQueue []json.RawMessage
	responses    []*wiretypes.TunnelResponsePayload
	respWaiters  []chan *wiretypes.TunnelResponsePayload
}

// NewControlPlaneFixture creates a mock OpenAI Tunnel control-plane HTTP test server.
func NewControlPlaneFixture(tunnelID string, expectedKey string) *ControlPlaneFixture {
	f := &ControlPlaneFixture{
		tunnelID:    tunnelID,
		expectedKey: expectedKey,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/v1/tunnels/"+tunnelID, f.handleMetadata)
	mux.HandleFunc("/v1/tunnels/"+tunnelID+"/poll", f.handlePoll)
	mux.HandleFunc("/v1/tunnels/"+tunnelID+"/response", f.handleResponse)

	f.server = httptest.NewServer(mux)
	return f
}

// URL returns the loopback URL of the mock control plane.
func (f *ControlPlaneFixture) URL() string {
	return f.server.URL
}

// Close shuts down the mock server.
func (f *ControlPlaneFixture) Close() {
	f.server.Close()
}

// SetStatusCode allows forcing an HTTP status code (e.g. 401 or 403) for all requests.
func (f *ControlPlaneFixture) SetStatusCode(code int) {
	f.statusCode.Store(int32(code))
}

// PollCount returns how many poll requests have been received.
func (f *ControlPlaneFixture) PollCount() int64 {
	return f.pollCount.Load()
}

// ResponseCount returns how many responses have been received.
func (f *ControlPlaneFixture) ResponseCount() int64 {
	return f.respCount.Load()
}

// QueueCommand queues a raw JSON-RPC command for the next poll.
func (f *ControlPlaneFixture) QueueCommand(requestID string, jsonrpcPayload any) error {
	rawRPC, err := json.Marshal(jsonrpcPayload)
	if err != nil {
		return err
	}

	cmd := wiretypes.RawJSONRPCPolledCommand{
		BaseRawPolledCommand: wiretypes.BaseRawPolledCommand{
			RequestID:   requestID,
			ShardToken:  "test-shard-" + requestID,
			CommandType: wiretypes.CommandTypeJSONRPC,
			CreatedAt:   time.Now(),
			Headers:     http.Header{},
		},
		JSONRPC: rawRPC,
	}

	rawCmd, err := json.Marshal(cmd)
	if err != nil {
		return err
	}

	f.mu.Lock()
	defer f.mu.Unlock()
	f.commandQueue = append(f.commandQueue, rawCmd)
	return nil
}

// WaitForResponse waits for a response payload to arrive or returns nil on timeout.
func (f *ControlPlaneFixture) WaitForResponse(timeout time.Duration) *wiretypes.TunnelResponsePayload {
	f.mu.Lock()
	if len(f.responses) > 0 {
		res := f.responses[0]
		f.responses = f.responses[1:]
		f.mu.Unlock()
		return res
	}

	ch := make(chan *wiretypes.TunnelResponsePayload, 1)
	f.respWaiters = append(f.respWaiters, ch)
	f.mu.Unlock()

	select {
	case res := <-ch:
		return res
	case <-time.After(timeout):
		return nil
	}
}

func (f *ControlPlaneFixture) checkAuth(w http.ResponseWriter, r *http.Request) bool {
	if code := f.statusCode.Load(); code != 0 {
		http.Error(w, http.StatusText(int(code)), int(code))
		return false
	}

	auth := r.Header.Get("Authorization")
	expected := "Bearer " + f.expectedKey
	if f.expectedKey != "" && auth != expected {
		http.Error(w, "Unauthorized", http.StatusUnauthorized)
		return false
	}
	return true
}

func (f *ControlPlaneFixture) handleMetadata(w http.ResponseWriter, r *http.Request) {
	if !f.checkAuth(w, r) {
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"id":          f.tunnelID,
		"name":        "Test Tunnel",
		"description": "Mock tunnel for integration testing",
	})
}

func (f *ControlPlaneFixture) handlePoll(w http.ResponseWriter, r *http.Request) {
	if !f.checkAuth(w, r) {
		return
	}
	f.pollCount.Add(1)

	f.mu.Lock()
	var cmds []json.RawMessage
	if len(f.commandQueue) > 0 {
		cmds = f.commandQueue
		f.commandQueue = nil
	}
	f.mu.Unlock()

	envelope := wiretypes.PolledCommandEnvelope{
		Commands: cmds,
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(envelope)
}

func (f *ControlPlaneFixture) handleResponse(w http.ResponseWriter, r *http.Request) {
	if !f.checkAuth(w, r) {
		return
	}
	f.respCount.Add(1)

	var payload wiretypes.TunnelResponsePayload
	if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	f.mu.Lock()
	if len(f.respWaiters) > 0 {
		waiter := f.respWaiters[0]
		f.respWaiters = f.respWaiters[1:]
		waiter <- &payload
	} else {
		f.responses = append(f.responses, &payload)
	}
	f.mu.Unlock()

	w.WriteHeader(http.StatusOK)
}
