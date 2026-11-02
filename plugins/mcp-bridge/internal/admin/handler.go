package admin

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/runtime"
	"linuxwebtool.local/mcp-bridge/internal/state"
	"linuxwebtool.local/mcp-bridge/internal/web"
)

// Handler serves standard Resident Control endpoints.
type Handler struct {
	rt         *runtime.Runtime
	token      string
	runID      string
	audit      *AuditLogger
	jobs       *JobTracker
	onShutdown func()
}

// NewHandler creates a new Resident Control handler.
func NewHandler(rt *runtime.Runtime, token string, runID string, audit *AuditLogger, onShutdown func()) *Handler {
	return &Handler{
		rt:         rt,
		token:      token,
		runID:      runID,
		audit:      audit,
		jobs:       NewJobTracker(),
		onShutdown: onShutdown,
	}
}

// RegisterRoutes registers standard control plane endpoints onto an http.ServeMux.
func (h *Handler) RegisterRoutes(mux *http.ServeMux) {
	mux.HandleFunc("/control/handshake", h.handleHandshake)
	mux.HandleFunc("/control/health", h.handleHealth)
	mux.HandleFunc("/control/invoke", h.handleInvoke)
	mux.HandleFunc("/control/invoke/", h.handleInvoke)
	mux.HandleFunc("/control/shutdown", h.handleShutdown)

	// Embedded Web UI routes with token injection and SPA fallback guard
	webHandler := web.HandlerWithToken(h.token)
	wrapFallback := func(next http.Handler) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if strings.Contains(r.URL.Path, "/control/invoke/") || strings.Contains(r.URL.Path, "/control/invoke") {
				h.handleInvoke(w, r)
				return
			}
			if r.URL.Path != "/" && r.Method != http.MethodGet && r.Method != http.MethodHead {
				w.Header().Set("Content-Type", "application/json; charset=utf-8")
				w.WriteHeader(http.StatusNotFound)
				_ = json.NewEncoder(w).Encode(map[string]any{"success": false, "message": "not found"})
				return
			}
			next.ServeHTTP(w, r)
		}
	}
	mux.Handle("/ui", webHandler)
	mux.Handle("/ui/", webHandler)
	mux.Handle("/index.html", webHandler)
	mux.HandleFunc("/", wrapFallback(webHandler))
}

func (h *Handler) checkAuth(w http.ResponseWriter, r *http.Request) bool {
	// 1. Trust host reverse proxy forward header
	if r.Header.Get("X-LWT-Plugin-Id") == "mcp-bridge" {
		return true
	}

	var provided string
	authHeader := r.Header.Get("Authorization")
	const prefix = "Bearer "
	if strings.HasPrefix(authHeader, prefix) {
		provided = strings.TrimPrefix(authHeader, prefix)
	} else if qToken := r.URL.Query().Get("token"); qToken != "" {
		provided = qToken
	} else if xToken := r.Header.Get("X-Control-Token"); xToken != "" {
		provided = xToken
	}

	if provided == "" {
		http.Error(w, "Unauthorized: missing Bearer token", http.StatusUnauthorized)
		return false
	}

	if subtle.ConstantTimeCompare([]byte(provided), []byte(h.token)) != 1 {
		http.Error(w, "Unauthorized: invalid token", http.StatusUnauthorized)
		return false
	}

	return true
}

func decodeJSONBody(r io.Reader, limit int64, v any) error {
	raw, err := io.ReadAll(io.LimitReader(r, limit))
	if err != nil {
		return err
	}
	raw = bytes.TrimPrefix(raw, []byte("\xef\xbb\xbf"))
	raw = bytes.TrimSpace(raw)
	return json.Unmarshal(raw, v)
}

func (h *Handler) handleHandshake(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var req HandshakeRequest
	if err := decodeJSONBody(r.Body, 64*1024, &req); err != nil {
		http.Error(w, "Invalid handshake payload", http.StatusBadRequest)
		return
	}

	if req.ProtocolVersion != 1 {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(HandshakeResponse{
			Success: false,
			Message: fmt.Sprintf("unsupported protocolVersion %d, expected 1", req.ProtocolVersion),
		})
		return
	}

	if req.WorkerID != "" && req.WorkerID != "mcp-bridge" {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(HandshakeResponse{
			Success: false,
			Message: fmt.Sprintf("workerId mismatch: %q != 'mcp-bridge'", req.WorkerID),
		})
		return
	}

	if req.RunID != "" && req.RunID != h.runID {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(HandshakeResponse{
			Success: false,
			Message: fmt.Sprintf("runId mismatch: expected %q, got %q", h.runID, req.RunID),
		})
		return
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(HandshakeResponse{
		Success:         true,
		ProtocolVersion: 1,
		WorkerID:        "mcp-bridge",
		WorkerRunID:     h.runID,
	})
}

func (h *Handler) handleHealth(w http.ResponseWriter, r *http.Request) {
	if !h.checkAuth(w, r) {
		return
	}

	health := h.rt.GetHealth(r.Context())
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(health)
}

func (h *Handler) handleShutdown(w http.ResponseWriter, r *http.Request) {
	if !h.checkAuth(w, r) {
		return
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"success": true,
		"message": "mcp-bridge shutdown initiated",
	})

	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}

	if h.onShutdown != nil {
		go func() {
			time.Sleep(50 * time.Millisecond)
			h.onShutdown()
		}()
	}
}

func (h *Handler) handleInvoke(w http.ResponseWriter, r *http.Request) {
	if !h.checkAuth(w, r) {
		return
	}

	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}

	path := r.URL.Path
	if idx := strings.Index(path, "/control/invoke/"); idx != -1 {
		path = path[idx:]
	} else if idx := strings.Index(path, "/control/invoke"); idx != -1 {
		path = path[idx:]
	}

	action := strings.TrimPrefix(path, "/control/invoke/")
	action = strings.TrimPrefix(action, "/control/invoke")
	action = strings.TrimPrefix(action, "/")
	if action == "" {
		action = r.URL.Query().Get("action")
	}
	if action == "" {
		action = r.Header.Get("X-Control-Action")
	}

	w.Header().Set("Content-Type", "application/json")

	switch action {
	case "status.get":
		h.actionStatusGet(w, r)
	case "tunnel.save":
		h.actionTunnelSave(w, r)
	case "tunnel.start":
		h.actionTunnelStart(w, r)
	case "tunnel.stop":
		h.actionTunnelStop(w, r)
	case "tunnel.logs":
		h.actionTunnelLogs(w, r)
	case "tunnel.diagnose":
		h.actionTunnelDiagnose(w, r)
	case "servers.list":
		h.actionServersList(w, r)
	case "servers.save":
		h.actionServersSave(w, r)
	case "servers.toggle":
		h.actionServersToggle(w, r)
	case "servers.import":
		h.actionServersImport(w, r)
	case "servers.delete":
		h.actionServersDelete(w, r)
	case "servers.probe":
		h.actionServersProbe(w, r)
	case "servers.discover":
		h.actionServersDiscover(w, r)
	case "tools.list":
		h.actionToolsList(w, r)
	case "tools.select":
		h.actionToolsSelect(w, r)
	case "tools.call":
		h.actionToolsCall(w, r)
	case "config.apply":
		h.actionConfigApply(w, r)
	case "audit.list":
		h.actionAuditList(w, r)
	default:
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": false,
			"message": fmt.Sprintf("unsupported action %q", action),
		})
	}
}

func (h *Handler) actionStatusGet(w http.ResponseWriter, r *http.Request) {
	health := h.rt.GetHealth(r.Context())
	st, err := h.rt.Store().Read(r.Context())
	var tunnelID string
	var hasTunnelKey bool
	var configuredProxy string
	backendCount := health.BackendCounts.Total
	if err == nil {
		tunnelID = st.Tunnel.TunnelID
		hasTunnelKey = st.Tunnel.EncryptedKey != ""
		configuredProxy = st.Tunnel.ProxyURL
		if len(st.Servers) > 0 {
			backendCount = len(st.Servers)
		}
	}

	var activeToolCount int
	if gw := h.rt.Gateway(); gw != nil {
		if snap := gw.CurrentSnapshot(); snap != nil && len(snap.Tools) > 0 {
			activeToolCount = len(snap.Tools)
		}
	}
	if activeToolCount == 0 && err == nil && st.ActiveRevision > 0 && len(st.Selections) > 0 {
		for srvID, sel := range st.Selections {
			if srv, ok := st.Servers[srvID]; ok && srv.Enabled && sel != nil {
				activeToolCount += len(sel.SelectedTools)
			}
		}
	}

	tunStatus := h.rt.Tunnel().Status()
	effectiveProxy := tunStatus.EffectiveProxy
	if effectiveProxy == "" && configuredProxy != "" {
		effectiveProxy = configuredProxy
	}
	lastError := tunStatus.ErrorMessage
	if lastError == "" {
		lastError = tunStatus.LastPollError
	}

	dto := StatusDTO{
		WorkerRunID:      health.WorkerRunID,
		ProcessAlive:     health.WorkerAlive,
		AdminReady:       health.AdminReady,
		TunnelState:      health.TunnelState,
		TunnelID:         tunnelID,
		ProxyURL:         configuredProxy,
		HasTunnelKey:     hasTunnelKey,
		AutoConnect:      st.Tunnel.ShouldAutoConnect(),
		UserPaused:       h.rt.Tunnel().IsUserPaused(),
		Hydrating:        health.Hydrating,
		ReadyForChatGPT:  tunStatus.ReadyForChatGPT,
		InitialPollMs:    tunStatus.InitialPollMs,
		LastPollStatus:   tunStatus.LastPollStatus,
		LastPollError:    tunStatus.LastPollError,
		EffectiveProxy:   effectiveProxy,
		BackendCount:     backendCount,
		ActiveToolCount:  activeToolCount,
		DraftRevision:    health.DraftRevision,
		DesiredRevision:  health.DesiredRevision,
		ActiveRevision:   health.ActiveRevision,
		RecoveryRequired: health.RecoveryRequired,
		LastError:        lastError,
	}
	_ = json.NewEncoder(w).Encode(dto)
}

func (h *Handler) actionTunnelSave(w http.ResponseWriter, r *http.Request) {
	var req SaveTunnelRequestDTO
	if err := decodeJSONBody(r.Body, 1024*1024, &req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body: " + err.Error()})
		return
	}

	secretAction := req.SecretAction
	if secretAction == "" {
		secretAction = "keep"
	}

	tunnelKey := req.TunnelKey
	if tunnelKey == "" && req.PresharedKey != "" {
		tunnelKey = req.PresharedKey
	}

	mut := config.SecretMutation{Action: config.SecretAction(secretAction), Value: tunnelKey}
	if err := config.ValidateSecretMutation(mut); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	rev, err := h.rt.Store().SaveDraft(r.Context(), req.ExpectedRevision, func(s *config.State) error {
		if req.TunnelID != "" {
			s.Tunnel.TunnelID = req.TunnelID
		}
		if req.AutoConnect != nil {
			s.Tunnel.AutoConnect = req.AutoConnect
		}
		if req.ProxyURL != nil {
			s.Tunnel.ProxyURL = strings.TrimSpace(*req.ProxyURL)
		}
		if mut.Action == config.SecretActionReplace || mut.Action == config.SecretActionUpdate {
			encrypted, err := h.rt.Store().Vault().Encrypt(mut.Value)
			if err != nil {
				return err
			}
			s.Tunnel.EncryptedKey = encrypted
		} else if mut.Action == config.SecretActionClear {
			s.Tunnel.EncryptedKey = ""
		}
		return nil
	})

	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	msg := "tunnel configuration saved as draft"
	if req.ApplyNow {
		committedRev, commitErr := h.rt.Store().Commit(r.Context(), rev, func(s *config.State) error {
			return nil
		})
		if commitErr != nil {
			w.WriteHeader(http.StatusInternalServerError)
			_ = json.NewEncoder(w).Encode(MutationResultDTO{
				Success:  false,
				Revision: rev,
				Message:  "saved as draft, but failed to apply: " + commitErr.Error(),
			})
			return
		}
		rev = committedRev
		msg = "tunnel configuration saved and applied"
		if latestState, readErr := h.rt.Store().Read(context.Background()); readErr == nil {
			if latestState.Tunnel.ShouldAutoConnect() && latestState.Tunnel.TunnelID != "" {
				h.rt.Tunnel().SetUserPaused(false)
				go func(cfg config.TunnelConfig) {
					_ = h.rt.Tunnel().Reload(context.Background(), cfg)
				}(latestState.Tunnel)
			}
		}
	}

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev, Message: msg})
}

func (h *Handler) actionTunnelStart(w http.ResponseWriter, r *http.Request) {
	st, err := h.rt.Store().Read(r.Context())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: false, Message: err.Error()})
		return
	}

	h.rt.Tunnel().SetUserPaused(false)
	if err := h.rt.Tunnel().Start(r.Context(), st.Tunnel); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: false, Message: err.Error()})
		return
	}

	_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: true, Message: "tunnel started"})
}

func (h *Handler) actionTunnelStop(w http.ResponseWriter, r *http.Request) {
	if err := h.rt.Tunnel().Stop(r.Context()); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: false, Message: err.Error()})
		return
	}
	_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: true, Message: "tunnel stopped"})
}

func (h *Handler) actionTunnelLogs(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodDelete || r.URL.Query().Get("clear") == "1" {
		h.rt.Tunnel().ClearLogs()
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": true,
			"message": "wire logs cleared",
		})
		return
	}

	limit := 50
	if lStr := r.URL.Query().Get("limit"); lStr != "" {
		if l, err := strconv.Atoi(lStr); err == nil && l > 0 {
			limit = l
		}
	}
	if limit > 200 {
		limit = 200
	}

	entries := h.rt.Tunnel().Logs(limit)
	dtos := make([]ChannelLogEntryDTO, len(entries))
	for i, e := range entries {
		dtos[i] = ChannelLogEntryDTO{
			Timestamp: e.Timestamp,
			Level:     e.Level,
			Message:   e.Message,
			Attrs:     e.Attrs,
		}
	}

	_ = json.NewEncoder(w).Encode(map[string]any{
		"success": true,
		"logs":    dtos,
		"count":   len(dtos),
	})
}

func (h *Handler) actionTunnelDiagnose(w http.ResponseWriter, r *http.Request) {
	var customProxy string
	if r.Method == http.MethodPost {
		var req struct {
			ProxyURL string `json:"proxyUrl,omitempty"`
		}
		_ = decodeJSONBody(r.Body, 64*1024, &req)
		customProxy = req.ProxyURL
	}
	if customProxy == "" {
		customProxy = r.URL.Query().Get("proxy")
	}

	res := h.rt.Tunnel().Diagnose(r.Context(), customProxy)
	_ = json.NewEncoder(w).Encode(map[string]any{
		"success": res.TlsHandshakeOk,
		"result":  res,
	})
}

func (h *Handler) actionServersList(w http.ResponseWriter, r *http.Request) {
	st, err := h.rt.Store().Read(r.Context())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(ServersResultDTO{})
		return
	}

	var dtoList []ServerDTO
	for _, srv := range st.Servers {
		sel := st.Selections[srv.ID]
		toolCount := 0
		if sel != nil {
			toolCount = len(sel.SelectedTools)
		} else if activeSnap := h.rt.Gateway().CurrentSnapshot(); activeSnap != nil {
			for _, b := range activeSnap.Bindings {
				if b.ServerID == srv.ID {
					toolCount++
				}
			}
		}

		status := "idle"
		if !srv.Enabled {
			status = "disabled"
		}

		dtoList = append(dtoList, ServerDTO{
			ID:               srv.ID,
			Name:             srv.Name,
			Transport:        string(srv.Transport),
			Command:          srv.Command,
			Arguments:        srv.Args,
			WorkingDirectory: srv.Cwd,
			URL:              srv.URL,
			HasSecret:        srv.EncryptedAuth != "",
			Enabled:          srv.Enabled,
			ToolCount:        toolCount,
			Status:           status,
		})
	}

	_ = json.NewEncoder(w).Encode(ServersResultDTO{
		Servers: dtoList,
	})
}

func (h *Handler) actionServersSave(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	if id == "" {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "missing query parameter 'id'"})
		return
	}

	var req SaveServerRequestDTO
	if err := decodeJSONBody(r.Body, 1024*1024, &req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body: " + err.Error()})
		return
	}

	secretAction := req.SecretAction
	if secretAction == "" {
		secretAction = "keep"
	}

	mut := config.SecretMutation{Action: config.SecretAction(secretAction), Value: req.SecretValue}
	if err := config.ValidateSecretMutation(mut); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	transport := config.TransportType(req.Transport)
	if req.Transport == "http" {
		transport = config.TransportStreamableHTTP
	}

	rev, err := h.rt.Store().SaveDraft(r.Context(), 0, func(s *config.State) error {
		existing := s.Servers[id]
		var encryptedAuth string
		var backendRev uint64 = 1

		if existing != nil {
			encryptedAuth = existing.EncryptedAuth
			backendRev = existing.Revision + 1
		}

		if mut.Action == config.SecretActionReplace || mut.Action == config.SecretActionUpdate {
			enc, err := h.rt.Store().Vault().Encrypt(mut.Value)
			if err != nil {
				return err
			}
			encryptedAuth = enc
		} else if mut.Action == config.SecretActionClear {
			encryptedAuth = ""
		}

		spec := &config.BackendSpec{
			ID:             id,
			Name:           req.Name,
			Transport:      transport,
			Command:        req.Command,
			Args:           req.Arguments,
			Cwd:            req.WorkingDirectory,
			URL:            req.URL,
			EncryptedAuth:  encryptedAuth,
			Enabled:        req.Enabled,
			Env:            req.Env,
			Headers:        req.Headers,
			Revision:       backendRev,
			UpdatedAt:      time.Now(),
		}

		s.Servers[id] = spec
		return nil
	})

	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	if req.Enabled {
		go func() {
			discCtx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
			defer cancel()
			st, err := h.rt.Store().Read(discCtx)
			if err != nil || st.Servers[id] == nil {
				return
			}
			savedSpec := st.Servers[id]
			tools, err := h.rt.Manager().Discover(discCtx, savedSpec)
			if err == nil && len(tools) > 0 {
				_, _ = h.rt.Store().SaveDraft(context.Background(), 0, func(s *config.State) error {
					if s.Selections == nil {
						s.Selections = make(map[string]*config.ServerSelection)
					}
					if s.Selections[id] == nil || len(s.Selections[id].SelectedTools) == 0 {
						var toolNames []string
						digests := make(map[string]string)
						for _, t := range tools {
							toolNames = append(toolNames, t.Name)
							digests[t.Name] = catalog.ComputeSchemaDigest(t)
						}
						s.Selections[id] = &config.ServerSelection{
							ServerID:            id,
							SelectedTools:       toolNames,
							ApprovedToolDigests: digests,
							ReviewedAt:          time.Now(),
						}
					}
					return nil
				})
			}
		}()
	}

	msg := "server saved as draft"
	if req.ApplyNow {
		committedRev, commitErr := h.rt.Store().Commit(r.Context(), rev, func(s *config.State) error {
			return nil
		})
		if commitErr == nil {
			rev = committedRev
			msg = "server saved and applied"
			if gw := h.rt.Gateway(); gw != nil {
				_, _ = gw.Apply(context.Background(), committedRev)
			}
		}
	}

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev, Message: msg})
}

func (h *Handler) actionServersToggle(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	if id == "" {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "missing query parameter 'id'"})
		return
	}

	var newEnabled bool
	rev, err := h.rt.Store().SaveDraft(r.Context(), 0, func(s *config.State) error {
		existing := s.Servers[id]
		if existing == nil {
			return fmt.Errorf("server %q not found", id)
		}
		existing.Enabled = !existing.Enabled
		existing.Revision++
		existing.UpdatedAt = time.Now()
		newEnabled = existing.Enabled
		return nil
	})

	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	applyNow := r.URL.Query().Get("applyNow") == "true" || r.URL.Query().Get("applyNow") == "1" || r.URL.Query().Get("apply") == "1"
	if applyNow {
		committedRev, commitErr := h.rt.Store().Commit(r.Context(), rev, func(s *config.State) error {
			return nil
		})
		if commitErr == nil {
			rev = committedRev
			if gw := h.rt.Gateway(); gw != nil {
				_, _ = gw.Apply(context.Background(), committedRev)
			}
		}
	}

	_ = json.NewEncoder(w).Encode(map[string]any{
		"success":  true,
		"id":       id,
		"enabled":  newEnabled,
		"revision": rev,
		"message":  fmt.Sprintf("server %q is now %s", id, map[bool]string{true: "enabled", false: "disabled"}[newEnabled]),
	})
}

func (h *Handler) actionServersImport(w http.ResponseWriter, r *http.Request) {
	bodyBytes, err := io.ReadAll(io.LimitReader(r.Body, 2*1024*1024))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(ImportServersResultDTO{Success: false, Message: "failed to read request body: " + err.Error()})
		return
	}
	bodyBytes = bytes.TrimPrefix(bodyBytes, []byte("\xef\xbb\xbf"))
	bodyBytes = bytes.TrimSpace(bodyBytes)

	var rawData []byte
	var conflictRes string
	var expectedRev uint64

	var wrapper ImportServersRequestDTO
	if err := json.Unmarshal(bodyBytes, &wrapper); err == nil && wrapper.RawJSON != "" {
		rawData = []byte(wrapper.RawJSON)
		conflictRes = wrapper.ConflictResolution
		expectedRev = wrapper.ExpectedRevision
	} else {
		rawData = bodyBytes
		conflictRes = r.URL.Query().Get("conflictResolution")
		if revStr := r.URL.Query().Get("expectedRevision"); revStr != "" {
			if parsed, err := strconv.ParseUint(revStr, 10, 64); err == nil {
				expectedRev = parsed
			}
		}
	}

	if conflictRes == "" {
		conflictRes = "overwrite"
	}

	specs, err := config.ParseStandardMCPConfig(rawData)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(ImportServersResultDTO{Success: false, Message: err.Error()})
		return
	}

	var importedIDs []string
	var skippedIDs []string

	rev, err := h.rt.Store().SaveDraft(r.Context(), expectedRev, func(s *config.State) error {
		if s.Servers == nil {
			s.Servers = make(map[string]*config.BackendSpec)
		}

		for _, spec := range specs {
			id := spec.ID
			existing := s.Servers[id]

			if existing != nil {
				switch conflictRes {
				case "skip":
					skippedIDs = append(skippedIDs, id)
					continue
				case "rename":
					suffix := 2
					newID := fmt.Sprintf("%s-%d", id, suffix)
					for s.Servers[newID] != nil {
						suffix++
						newID = fmt.Sprintf("%s-%d", id, suffix)
					}
					spec.ID = newID
					id = newID
				case "overwrite":
					spec.Revision = existing.Revision + 1
					if spec.EncryptedAuth == "" && existing.EncryptedAuth != "" {
						spec.EncryptedAuth = existing.EncryptedAuth
					}
				}
			}

			s.Servers[id] = spec
			importedIDs = append(importedIDs, id)
		}

		if len(s.Servers) > config.MaxServersCount {
			return fmt.Errorf("import would exceed maximum server limit (%d > %d)", len(s.Servers), config.MaxServersCount)
		}
		return nil
	})

	if err != nil {
		if errorsIs(err, state.ErrRevisionConflict) {
			w.WriteHeader(http.StatusConflict)
		} else {
			w.WriteHeader(http.StatusBadRequest)
		}
		_ = json.NewEncoder(w).Encode(ImportServersResultDTO{Success: false, Message: err.Error()})
		return
	}

	applyNow := wrapper.ApplyNow || r.URL.Query().Get("applyNow") == "true" || r.URL.Query().Get("applyNow") == "1" || r.URL.Query().Get("apply") == "1"
	if applyNow {
		committedRev, commitErr := h.rt.Store().Commit(r.Context(), rev, func(s *config.State) error {
			return nil
		})
		if commitErr != nil {
			w.WriteHeader(http.StatusInternalServerError)
			_ = json.NewEncoder(w).Encode(ImportServersResultDTO{
				Success:  false,
				Revision: rev,
				Message:  "servers imported into draft, but failed to apply: " + commitErr.Error(),
			})
			return
		}
		rev = committedRev
		if gw := h.rt.Gateway(); gw != nil {
			_, _ = gw.Apply(context.Background(), committedRev)
		}
	}

	if len(importedIDs) > 0 {
		go func(isApplied bool) {
			discCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
			defer cancel()
			st, err := h.rt.Store().Read(discCtx)
			if err != nil {
				return
			}
			var updated bool
			for _, id := range importedIDs {
				srv := st.Servers[id]
				if srv != nil && srv.Enabled {
					if tools, err := h.rt.Manager().Discover(discCtx, srv); err == nil && len(tools) > 0 {
						draftRev, draftErr := h.rt.Store().SaveDraft(context.Background(), 0, func(s *config.State) error {
							if s.Selections == nil {
								s.Selections = make(map[string]*config.ServerSelection)
							}
							if s.Selections[id] == nil || len(s.Selections[id].SelectedTools) == 0 {
								var toolNames []string
								digests := make(map[string]string)
								for _, t := range tools {
									toolNames = append(toolNames, t.Name)
									digests[t.Name] = catalog.ComputeSchemaDigest(t)
								}
								s.Selections[id] = &config.ServerSelection{
									ServerID:            id,
									SelectedTools:       toolNames,
									ApprovedToolDigests: digests,
									ReviewedAt:          time.Now(),
								}
								updated = true
							}
							return nil
						})
						// 若本次导入为实时生效，自动将工具选择提交为生效状态，防止残留“待生效草稿”
						if draftErr == nil && isApplied && updated {
							if cRev, cErr := h.rt.Store().Commit(context.Background(), draftRev, func(s *config.State) error {
								return nil
							}); cErr == nil {
								if gw := h.rt.Gateway(); gw != nil {
									_, _ = gw.Apply(context.Background(), cRev)
								}
							}
						}
					}
				}
			}
		}(applyNow)
	}

	_ = json.NewEncoder(w).Encode(ImportServersResultDTO{
		Success:     true,
		Revision:    rev,
		ImportedIDs: importedIDs,
		SkippedIDs:  skippedIDs,
		Message:     fmt.Sprintf("successfully imported %d server(s), skipped %d", len(importedIDs), len(skippedIDs)),
	})
}

func (h *Handler) actionServersDelete(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	if id == "" {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "missing query param id"})
		return
	}

	rev, err := h.rt.Store().SaveDraft(r.Context(), 0, func(s *config.State) error {
		delete(s.Servers, id)
		delete(s.Selections, id)
		return nil
	})

	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	applyNow := r.URL.Query().Get("applyNow") == "true" || r.URL.Query().Get("applyNow") == "1" || r.URL.Query().Get("apply") == "1"
	if applyNow {
		committedRev, commitErr := h.rt.Store().Commit(r.Context(), rev, func(s *config.State) error {
			return nil
		})
		if commitErr == nil {
			rev = committedRev
			if gw := h.rt.Gateway(); gw != nil {
				_, _ = gw.Apply(context.Background(), committedRev)
			}
		}
	}

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev})
}

func (h *Handler) actionServersProbe(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	st, err := h.rt.Store().Read(r.Context())
	if err != nil || st.Servers[id] == nil {
		w.WriteHeader(http.StatusNotFound)
		_ = json.NewEncoder(w).Encode(ProbeResultDTO{Success: false, Status: "not_found", Message: "server not found"})
		return
	}

	srv := st.Servers[id]
	probeRes, err := h.rt.Manager().Probe(r.Context(), srv)
	if err != nil {
		w.WriteHeader(http.StatusOK)
		_ = json.NewEncoder(w).Encode(ProbeResultDTO{Success: false, Status: "error", Message: err.Error()})
		return
	}

	_ = json.NewEncoder(w).Encode(ProbeResultDTO{
		Success:   probeRes.Connected,
		Status:    "connected",
		ToolCount: probeRes.ToolCount,
		Message:   probeRes.Error,
	})
}

func (h *Handler) actionServersDiscover(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	st, err := h.rt.Store().Read(r.Context())
	if err != nil || st.Servers[id] == nil {
		w.WriteHeader(http.StatusNotFound)
		_ = json.NewEncoder(w).Encode(DiscoveryResultDTO{Success: false, Message: "server not found"})
		return
	}

	srv := st.Servers[id]
	tools, err := h.rt.Manager().Discover(r.Context(), srv)
	if err != nil {
		w.WriteHeader(http.StatusOK)
		_ = json.NewEncoder(w).Encode(DiscoveryResultDTO{Success: false, Message: err.Error()})
		return
	}

	_ = json.NewEncoder(w).Encode(DiscoveryResultDTO{
		Success:         true,
		DiscoveredCount: len(tools),
		Message:         "discovery completed",
	})
}

func (h *Handler) actionToolsList(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	st, err := h.rt.Store().Read(r.Context())
	if err != nil || st.Servers[id] == nil {
		w.WriteHeader(http.StatusNotFound)
		_ = json.NewEncoder(w).Encode(ToolsPageDTO{})
		return
	}

	srv := st.Servers[id]
	tools, err := h.rt.Manager().Discover(r.Context(), srv)
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(ToolsPageDTO{})
		return
	}

	hasExplicitSelection := st.Selections[id] != nil
	selectedMap := make(map[string]bool)
	if hasExplicitSelection {
		for _, name := range st.Selections[id].SelectedTools {
			selectedMap[name] = true
		}
	}

	var dtoList []ToolDTO
	for _, tool := range tools {
		exported := catalog.ExportedToolName(srv.ID, tool.Name)
		digest := catalog.ComputeSchemaDigest(tool)

		var schemaRaw json.RawMessage
		if tool.InputSchema != nil {
			if bytes, err := json.Marshal(tool.InputSchema); err == nil {
				schemaRaw = bytes
			}
		}

		isSelected := true
		if hasExplicitSelection {
			isSelected = selectedMap[tool.Name]
		}

		dtoList = append(dtoList, ToolDTO{
			ExportedName: exported,
			ServerID:     srv.ID,
			OriginalName: tool.Name,
			Description:  tool.Description,
			InputSchema:  schemaRaw,
			Selected:     isSelected,
			SchemaDigest: digest,
		})
	}

	_ = json.NewEncoder(w).Encode(ToolsPageDTO{
		Tools:            dtoList,
		SnapshotRevision: st.DesiredRevision,
	})
}

func (h *Handler) actionToolsSelect(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	var req SaveSelectionRequestDTO
	if err := decodeJSONBody(r.Body, 1024*1024, &req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body: " + err.Error()})
		return
	}

	st, err := h.rt.Store().Read(r.Context())
	if err != nil || st.Servers[id] == nil {
		w.WriteHeader(http.StatusNotFound)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "server not found"})
		return
	}

	// Discover tools to compute schema digests for approved selections
	tools, err := h.rt.Manager().Discover(r.Context(), st.Servers[id])
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	toolDigestMap := make(map[string]string)
	exportedToOrig := make(map[string]string)
	for _, tool := range tools {
		exp := catalog.ExportedToolName(id, tool.Name)
		exportedToOrig[exp] = tool.Name
		toolDigestMap[tool.Name] = catalog.ComputeSchemaDigest(tool)
	}

	var selectedOrigNames []string
	approvedDigests := make(map[string]string)
	for _, exp := range req.SelectedExportedNames {
		if orig, ok := exportedToOrig[exp]; ok {
			selectedOrigNames = append(selectedOrigNames, orig)
			approvedDigests[orig] = toolDigestMap[orig]
		}
	}

	rev, err := h.rt.Store().SaveDraft(r.Context(), req.ExpectedRevision, func(s *config.State) error {
		s.Selections[id] = &config.ServerSelection{
			ServerID:            id,
			SelectedTools:       selectedOrigNames,
			ApprovedToolDigests: approvedDigests,
			ReviewedAt:          time.Now(),
		}
		return nil
	})

	if err != nil {
		if errorsIs(err, state.ErrRevisionConflict) {
			w.WriteHeader(http.StatusConflict)
		} else {
			w.WriteHeader(http.StatusBadRequest)
		}
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev})
}

func (h *Handler) actionToolsCall(w http.ResponseWriter, r *http.Request) {
	var req CallToolRequestDTO
	if err := decodeJSONBody(r.Body, 1024*1024, &req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(CallToolResultDTO{Success: false, Message: "invalid request body: " + err.Error()})
		return
	}

	if req.Name == "" {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(CallToolResultDTO{Success: false, Message: "missing tool name"})
		return
	}

	start := time.Now()
	var callResult *mcp.CallToolResult
	var callErr error

	if req.ServerID != "" {
		st, err := h.rt.Store().Read(r.Context())
		if err != nil || st.Servers[req.ServerID] == nil {
			w.WriteHeader(http.StatusNotFound)
			_ = json.NewEncoder(w).Encode(CallToolResultDTO{Success: false, Message: fmt.Sprintf("server %q not found", req.ServerID)})
			return
		}
		srv := st.Servers[req.ServerID]
		lease, err := h.rt.Manager().Acquire(r.Context(), srv, backend.CallScope{
			RequestID: fmt.Sprintf("call-%d", time.Now().UnixNano()),
		})
		if err != nil {
			w.WriteHeader(http.StatusOK)
			_ = json.NewEncoder(w).Encode(CallToolResultDTO{
				Success:   false,
				IsError:   true,
				LatencyMs: time.Since(start).Milliseconds(),
				Message:   fmt.Sprintf("failed to acquire backend session: %v", err),
			})
			return
		}
		defer lease.Release()

		callResult, callErr = lease.Session().CallTool(r.Context(), req.Name, req.Arguments)
	} else {
		callResult, callErr = h.rt.Gateway().Call(r.Context(), req.Name, req.Arguments, backend.CallScope{
			RequestID: fmt.Sprintf("call-%d", time.Now().UnixNano()),
		})
	}

	latency := time.Since(start).Milliseconds()

	if callErr != nil {
		w.WriteHeader(http.StatusOK)
		_ = json.NewEncoder(w).Encode(CallToolResultDTO{
			Success:   false,
			IsError:   true,
			LatencyMs: latency,
			Message:   callErr.Error(),
		})
		return
	}

	var contentAny any
	isError := false
	if callResult != nil {
		contentAny = callResult.Content
		isError = callResult.IsError
	}

	_ = json.NewEncoder(w).Encode(CallToolResultDTO{
		Success:   !isError,
		IsError:   isError,
		Content:   contentAny,
		LatencyMs: latency,
		Message:   "tool execution completed",
	})
}

func (h *Handler) actionConfigApply(w http.ResponseWriter, r *http.Request) {
	var req ApplyConfigRequestDTO
	_ = decodeJSONBody(r.Body, 64*1024, &req)

	res, err := h.rt.Gateway().Apply(r.Context(), req.ExpectedRevision)
	if err != nil {
		if errorsIs(err, state.ErrRevisionConflict) {
			w.WriteHeader(http.StatusConflict)
		} else {
			w.WriteHeader(http.StatusBadRequest)
		}
		_ = json.NewEncoder(w).Encode(ApplyResultDTO{Success: false, Message: err.Error()})
		return
	}

	// 同步热重载 OpenAI Secure Tunnel 渠道
	if latestState, readErr := h.rt.Store().Read(r.Context()); readErr == nil {
		if latestState.Tunnel.ShouldAutoConnect() && latestState.Tunnel.TunnelID != "" {
			_ = h.rt.Tunnel().Reload(r.Context(), latestState.Tunnel)
		}
	}

	msg := "configuration applied successfully"
	if len(res.DegradedServers) > 0 {
		msg = fmt.Sprintf("configuration applied successfully with %d degraded server(s): %s",
			len(res.DegradedServers), strings.Join(res.DegradedServers, ", "))
	}

	_ = json.NewEncoder(w).Encode(ApplyResultDTO{
		Success:         true,
		ActiveRevision:  res.ActiveRevision,
		ActiveToolCount: res.ExposedToolCount,
		DegradedServers: res.DegradedServers,
		Message:         msg,
	})
}

func (h *Handler) actionAuditList(w http.ResponseWriter, r *http.Request) {
	cursor := r.URL.Query().Get("cursor")
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))

	items, next := h.audit.List(cursor, limit)
	_ = json.NewEncoder(w).Encode(AuditPageDTO{
		Items:      items,
		NextCursor: next,
	})
}

func errorsIs(err, target error) bool {
	return strings.Contains(err.Error(), target.Error())
}
