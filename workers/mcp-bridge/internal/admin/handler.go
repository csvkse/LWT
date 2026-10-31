package admin

import (
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

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

	// Embedded Web UI routes for generic Ingress (/w/mcp-bridge/*)
	webHandler := web.Handler()
	mux.Handle("/", webHandler)
	mux.Handle("/ui", webHandler)
	mux.Handle("/ui/", webHandler)
	mux.Handle("/index.html", webHandler)
}

func (h *Handler) checkAuth(w http.ResponseWriter, r *http.Request) bool {
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

func (h *Handler) handleHandshake(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var req HandshakeRequest
	if err := json.NewDecoder(io.LimitReader(r.Body, 64*1024)).Decode(&req); err != nil {
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

	action := strings.TrimPrefix(r.URL.Path, "/control/invoke/")
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
	case "servers.list":
		h.actionServersList(w, r)
	case "servers.save":
		h.actionServersSave(w, r)
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
	dto := StatusDTO{
		WorkerRunID:      health.WorkerRunID,
		ProcessAlive:     health.WorkerAlive,
		AdminReady:       health.AdminReady,
		TunnelState:      health.TunnelState,
		BackendCount:     health.BackendCounts.Total,
		DraftRevision:    health.DraftRevision,
		DesiredRevision:  health.DesiredRevision,
		ActiveRevision:   health.ActiveRevision,
		RecoveryRequired: health.RecoveryRequired,
	}
	_ = json.NewEncoder(w).Encode(dto)
}

func (h *Handler) actionTunnelSave(w http.ResponseWriter, r *http.Request) {
	var req SaveTunnelRequestDTO
	if err := json.NewDecoder(io.LimitReader(r.Body, 1024*1024)).Decode(&req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body"})
		return
	}

	secretAction := req.SecretAction
	if secretAction == "" {
		secretAction = "keep"
	}

	mut := config.SecretMutation{Action: config.SecretAction(secretAction), Value: req.TunnelKey}
	if err := config.ValidateSecretMutation(mut); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: err.Error()})
		return
	}

	rev, err := h.rt.Store().SaveDraft(r.Context(), 0, func(s *config.State) error {
		if req.TunnelID != "" {
			s.Tunnel.TunnelID = req.TunnelID
		}
		if mut.Action == config.SecretActionReplace {
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

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev})
}

func (h *Handler) actionTunnelStart(w http.ResponseWriter, r *http.Request) {
	st, err := h.rt.Store().Read(r.Context())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(OperationResultDTO{Success: false, Message: err.Error()})
		return
	}

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

func (h *Handler) actionServersList(w http.ResponseWriter, r *http.Request) {
	st, err := h.rt.Store().Read(r.Context())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		_ = json.NewEncoder(w).Encode(ServersResultDTO{})
		return
	}

	var list []ServerDTO
	for id, srv := range st.Servers {
		sel := st.Selections[id]
		toolCount := 0
		if sel != nil {
			toolCount = len(sel.SelectedTools)
		}

		status := "idle"
		if !srv.Enabled {
			status = "disabled"
		}

		list = append(list, ServerDTO{
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

	_ = json.NewEncoder(w).Encode(ServersResultDTO{Servers: list})
}

func (h *Handler) actionServersSave(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	if id == "" {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "missing query parameter 'id'"})
		return
	}

	var req SaveServerRequestDTO
	if err := json.NewDecoder(io.LimitReader(r.Body, 1024*1024)).Decode(&req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body"})
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

		if mut.Action == config.SecretActionReplace {
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

	_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: true, Revision: rev})
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

	selectedMap := make(map[string]bool)
	if sel := st.Selections[id]; sel != nil {
		for _, name := range sel.SelectedTools {
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

		dtoList = append(dtoList, ToolDTO{
			ExportedName: exported,
			ServerID:     srv.ID,
			OriginalName: tool.Name,
			Description:  tool.Description,
			InputSchema:  schemaRaw,
			Selected:     selectedMap[tool.Name],
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
	if err := json.NewDecoder(io.LimitReader(r.Body, 1024*1024)).Decode(&req); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		_ = json.NewEncoder(w).Encode(MutationResultDTO{Success: false, Message: "invalid request body"})
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

func (h *Handler) actionConfigApply(w http.ResponseWriter, r *http.Request) {
	var req ApplyConfigRequestDTO
	_ = json.NewDecoder(io.LimitReader(r.Body, 64*1024)).Decode(&req)

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

	_ = json.NewEncoder(w).Encode(ApplyResultDTO{
		Success:         true,
		ActiveRevision:  res.ActiveRevision,
		ActiveToolCount: res.ExposedToolCount,
		Message:         "configuration applied successfully",
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
