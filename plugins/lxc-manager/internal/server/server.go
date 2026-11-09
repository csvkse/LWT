package server

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"regexp"
	"strings"

	"lwt/lxc-manager/internal/driver"
	"lwt/lxc-manager/internal/evidence"
	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
	"lwt/lxc-manager/internal/network"
	"lwt/lxc-manager/internal/templates"
	"lwt/lxc-manager/internal/terminal"
	"lwt/lxc-manager/internal/workflow"
)

type Probe func(context.Context) incus.Capability
type Discover func(context.Context) (incus.Discovery, error)
type SampleResources func(context.Context, string) (incus.ResourceSample, error)

type CreateFlow interface {
	Submit(context.Context, string, incus.InstanceSpec) (jobs.Result, error)
	Reconcile(context.Context, string, incus.InstanceSpec) (jobs.Result, error)
}

type ManagementFlow interface {
	Managed(context.Context) ([]workflow.ManagedInstance, error)
	Pending(context.Context) ([]jobs.PendingTask, error)
	Submit(context.Context, string, string, incus.InstanceSpec, string) (jobs.Result, error)
	Reconcile(context.Context, string, string, incus.InstanceSpec, string) (jobs.Result, error)
}

type ServerOptions struct {
	Secret     string
	Probe      Probe
	Discover   Discover
	Sample     SampleResources
	Evidence   evidence.EvidenceProvider
	Terminal   *terminal.SessionManager
	Network    *network.Store
	Management ManagementFlow
	Control    func(context.Context, string, string) (string, error)
	Flows      []CreateFlow
	Driver     driver.ILxcEngineDriver
}

var keyPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)
var poolPattern = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$`)

func writeTaskError(w http.ResponseWriter, err error, isSubmit bool) {
	if isSubmit && errors.Is(err, workflow.ErrPreflight) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusConflict)
		_ = json.NewEncoder(w).Encode(map[string]string{"code": "not_submitted", "message": "preflight rejected before submission"})
		return
	}
	if errors.Is(err, workflow.ErrPreflight) || errors.Is(err, jobs.ErrAdmissionBusy) || errors.Is(err, jobs.ErrConflict) {
		http.Error(w, "request rejected by safety checks", http.StatusConflict)
		return
	}
	http.Error(w, "task unavailable", http.StatusBadGateway)
}

func New(secret string, probe Probe, discover Discover, flows ...CreateFlow) http.Handler {
	return NewWithResources(secret, probe, discover, nil, flows...)
}

func NewWithResources(secret string, probe Probe, discover Discover, sample SampleResources, flows ...CreateFlow) http.Handler {
	return NewWithManagement(secret, probe, discover, sample, nil, flows...)
}

func NewWithManagement(secret string, probe Probe, discover Discover, sample SampleResources, management ManagementFlow, flows ...CreateFlow) http.Handler {
	return NewServer(ServerOptions{
		Secret:     secret,
		Probe:      probe,
		Discover:   discover,
		Sample:     sample,
		Management: management,
		Flows:      flows,
	})
}

func NewServer(opts ServerOptions) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		provided := r.Header.Get("X-LWT-Internal-Secret")
		if opts.Secret == "" || provided == "" || subtle.ConstantTimeCompare([]byte(provided), []byte(opts.Secret)) != 1 {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		if r.Header.Get("X-LWT-Auth-Type") != "AdminSession" {
			http.Error(w, "administrator required", http.StatusForbidden)
			return
		}
		w.Header().Set("Cache-Control", "no-store")

		// 0. Direct state control (fast path)
		if r.URL.Path == "/api/instances/control" && r.Method == http.MethodPost && opts.Control != nil {
			var req struct {
				Name   string `json:"name"`
				Action string `json:"action"`
			}
			if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil || (req.Action != "start" && req.Action != "stop" && req.Action != "restart") {
				http.Error(w, "invalid request", http.StatusBadRequest)
				return
			}
			op, err := opts.Control(r.Context(), req.Name, req.Action)
			if err != nil {
				http.Error(w, err.Error(), http.StatusConflict)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(map[string]any{"operation": op, "status": "accepted"})
			return
		}

		// 0.1 Host Network Interfaces Probe
		if r.URL.Path == "/api/network/host-interfaces" && r.Method == http.MethodGet {
			if opts.Driver == nil {
				http.Error(w, "driver unavailable", http.StatusServiceUnavailable)
				return
			}
			nics, err := opts.Driver.GetHostInterfaces(r.Context())
			if err != nil {
				http.Error(w, err.Error(), http.StatusInternalServerError)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(map[string]any{"interfaces": nics})
			return
		}

		// 0.2 Host Storage Pools Query
		if r.URL.Path == "/api/storage/pools" && r.Method == http.MethodGet {
			if opts.Driver == nil {
				http.Error(w, "driver unavailable", http.StatusServiceUnavailable)
				return
			}
			pools, err := opts.Driver.GetStoragePools(r.Context())
			if err != nil {
				http.Error(w, err.Error(), http.StatusInternalServerError)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(map[string]any{"pools": pools})
			return
		}

		// 0.3 Fine-grained Hardware Limits Hot-Tuning
		if (r.URL.Path == "/api/instances/hardware-limits" || (strings.HasPrefix(r.URL.Path, "/api/instances/") && strings.HasSuffix(r.URL.Path, "/hardware-limits"))) && (r.Method == http.MethodPatch || r.Method == http.MethodPost) {
			var req struct {
				Name string                   `json:"name"`
				Spec driver.HardwareLimitsSpec `json:"spec"`
			}
			if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10)).Decode(&req); err != nil {
				http.Error(w, "invalid request", http.StatusBadRequest)
				return
			}
			if req.Name == "" && strings.HasPrefix(r.URL.Path, "/api/instances/") {
				parts := strings.Split(r.URL.Path, "/")
				if len(parts) >= 4 {
					req.Name = parts[3]
				}
			}
			if req.Name == "" || opts.Driver == nil {
				http.Error(w, "missing instance or driver", http.StatusBadRequest)
				return
			}
			if err := opts.Driver.UpdateHardwareLimits(r.Context(), req.Name, req.Spec); err != nil {
				http.Error(w, err.Error(), http.StatusConflict)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "name": req.Name})
			return
		}

		// 0.4 Stream Export Container Tarball
		if (r.URL.Path == "/api/instances/export" || (strings.HasPrefix(r.URL.Path, "/api/instances/") && strings.HasSuffix(r.URL.Path, "/export"))) && r.Method == http.MethodGet {
			name := r.URL.Query().Get("name")
			if name == "" && strings.HasPrefix(r.URL.Path, "/api/instances/") {
				parts := strings.Split(r.URL.Path, "/")
				if len(parts) >= 4 {
					name = parts[3]
				}
			}
			if name == "" || opts.Driver == nil {
				http.Error(w, "missing instance or driver", http.StatusBadRequest)
				return
			}
			filename := fmt.Sprintf("%s-backup.tar.gz", name)
			w.Header().Set("Content-Type", "application/gzip")
			w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s"`, filename))
			w.Header().Set("Transfer-Encoding", "chunked")

			_ = opts.Driver.ExportInstance(r.Context(), name, w, driver.ExportOptions{InstanceOnly: true})
			return
		}

		// 0.5 Stream Import Container Archive
		if r.URL.Path == "/api/instances/import" && r.Method == http.MethodPost {
			if opts.Driver == nil {
				http.Error(w, "driver unavailable", http.StatusServiceUnavailable)
				return
			}
			mr, err := r.MultipartReader()
			if err != nil {
				http.Error(w, "multipart request required", http.StatusBadRequest)
				return
			}

			var req driver.ImportRequest
			req.ResetMacAddress = true

			for {
				part, pErr := mr.NextPart()
				if pErr == io.EOF {
					break
				}
				if pErr != nil {
					http.Error(w, "error reading multipart: "+pErr.Error(), http.StatusBadRequest)
					return
				}
				formName := part.FormName()
				if formName == "name" {
					b, _ := io.ReadAll(io.LimitReader(part, 1<<10))
					req.NewName = strings.TrimSpace(string(b))
				} else if formName == "pool" || formName == "storagePool" {
					b, _ := io.ReadAll(io.LimitReader(part, 1<<10))
					req.StoragePool = strings.TrimSpace(string(b))
				} else if formName == "resetMac" {
					b, _ := io.ReadAll(io.LimitReader(part, 1<<10))
					req.ResetMacAddress = strings.TrimSpace(string(b)) != "false"
				} else if formName == "file" || formName == "archive" {
					inst, iErr := opts.Driver.ImportInstance(r.Context(), req, part)
					if iErr != nil {
						http.Error(w, iErr.Error(), http.StatusInternalServerError)
						return
					}
					w.Header().Set("Content-Type", "application/json")
					w.WriteHeader(http.StatusCreated)
					_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "instance": inst})
					return
				}
			}
			http.Error(w, "no archive file provided", http.StatusBadRequest)
			return
		}

		// 1. Lifecycle actions
		if (r.URL.Path == "/api/lifecycle" || r.URL.Path == "/api/lifecycle/reconcile") && r.Method == http.MethodPost && opts.Management != nil {
			contentType, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
			if err != nil || contentType != "application/json" {
				http.Error(w, "application/json required", http.StatusUnsupportedMediaType)
				return
			}
			var req struct {
				Key    string             `json:"key"`
				Owner  string             `json:"owner"`
				Spec   incus.InstanceSpec `json:"spec"`
				Action string             `json:"action"`
			}
			decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10))
			decoder.DisallowUnknownFields()
			if decoder.Decode(&req) != nil || decoder.Decode(new(any)) != io.EOF || !keyPattern.MatchString(req.Key) || !keyPattern.MatchString(req.Owner) || (req.Action != "start" && req.Action != "stop" && req.Action != "restart") {
				http.Error(w, "invalid request", http.StatusBadRequest)
				return
			}
			var result jobs.Result
			if r.URL.Path == "/api/lifecycle" {
				result, err = opts.Management.Submit(r.Context(), req.Key, req.Owner, req.Spec, req.Action)
			} else {
				result, err = opts.Management.Reconcile(r.Context(), req.Key, req.Owner, req.Spec, req.Action)
			}
			if err != nil {
				writeTaskError(w, err, r.URL.Path == "/api/lifecycle")
				return
			}
			w.Header().Set("Content-Type", "application/json")
			if r.URL.Path == "/api/lifecycle" {
				w.WriteHeader(http.StatusAccepted)
			}
			_ = json.NewEncoder(w).Encode(result)
			return
		}

		// 2. Instance creation and reconcile
		if (r.URL.Path == "/api/instances" || r.URL.Path == "/api/instances/reconcile") && r.Method == http.MethodPost && len(opts.Flows) > 0 && opts.Flows[0] != nil {
			contentType, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
			if err != nil || contentType != "application/json" {
				http.Error(w, "application/json required", http.StatusUnsupportedMediaType)
				return
			}
			var request struct {
				Key  string             `json:"key"`
				Spec incus.InstanceSpec `json:"spec"`
			}
			decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10))
			decoder.DisallowUnknownFields()
			if decoder.Decode(&request) != nil || decoder.Decode(new(any)) != io.EOF || !keyPattern.MatchString(request.Key) {
				http.Error(w, "invalid request", http.StatusBadRequest)
				return
			}
			var result jobs.Result
			if r.URL.Path == "/api/instances" {
				result, err = opts.Flows[0].Submit(r.Context(), request.Key, request.Spec)
			} else {
				result, err = opts.Flows[0].Reconcile(r.Context(), request.Key, request.Spec)
			}
			if err != nil {
				writeTaskError(w, err, r.URL.Path == "/api/instances")
				return
			}
			w.Header().Set("Content-Type", "application/json")
			if r.URL.Path == "/api/instances" {
				w.WriteHeader(http.StatusAccepted)
			}
			_ = json.NewEncoder(w).Encode(result)
			return
		}

		// 2.1 Instance deletion
		if (r.URL.Path == "/api/instances" || strings.HasPrefix(r.URL.Path, "/api/instances/")) && r.Method == http.MethodDelete {
			name := r.URL.Query().Get("name")
			if name == "" && strings.HasPrefix(r.URL.Path, "/api/instances/") {
				parts := strings.Split(r.URL.Path, "/")
				if len(parts) >= 4 {
					name = parts[3]
				}
			}
			if name == "" {
				http.Error(w, "missing instance name", http.StatusBadRequest)
				return
			}
			force := r.URL.Query().Get("force") == "true"
			if opts.Driver == nil {
				http.Error(w, "driver unavailable", http.StatusServiceUnavailable)
				return
			}
			if err := opts.Driver.DeleteInstance(r.Context(), name, force); err != nil {
				http.Error(w, err.Error(), http.StatusConflict)
				return
			}
			// Cascade cleanup: remove any port mappings for this container
			if opts.Network != nil {
				for _, m := range opts.Network.List(name) {
					_ = opts.Network.Delete(m.ID)
				}
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "name": name})
			return
		}

		// 3. Preflight check
		if r.URL.Path == "/api/preflight" && r.Method == http.MethodPost {
			contentType, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
			if err != nil || contentType != "application/json" {
				http.Error(w, "application/json required", http.StatusUnsupportedMediaType)
				return
			}
			var spec incus.InstanceSpec
			decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10))
			decoder.DisallowUnknownFields()
			if decoder.Decode(&spec) != nil || decoder.Decode(new(any)) != io.EOF {
				http.Error(w, "invalid request", http.StatusBadRequest)
				return
			}
			if opts.Discover == nil {
				http.Error(w, "discovery unavailable", http.StatusServiceUnavailable)
				return
			}
			resources, err := opts.Discover(r.Context())
			if err != nil {
				http.Error(w, "incus discovery unavailable", http.StatusBadGateway)
				return
			}

			// Collect trusted capacity evidence if provider is available
			var ev incus.PreflightEvidence
			if opts.Evidence != nil {
				ev = opts.Evidence.CollectEvidence(r.Context(), spec.StoragePool, resources)
			}

			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(incus.Preflight(spec, resources, ev))
			return
		}

		// 4. Console execution
		if r.URL.Path == "/api/console/exec" && r.Method == http.MethodPost {
			if opts.Terminal == nil {
				http.Error(w, "terminal service unavailable", http.StatusServiceUnavailable)
				return
			}
			var req struct {
				Container string `json:"container"`
				Command   string `json:"command"`
			}
			if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&req); err != nil {
				http.Error(w, "invalid request body", http.StatusBadRequest)
				return
			}
			res := opts.Terminal.ExecuteCommand(r.Context(), req.Container, req.Command)
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(res)
			return
		}

		// 5. Network port mappings
		if r.URL.Path == "/api/network/mappings" {
			if opts.Network == nil {
				http.Error(w, "network store unavailable", http.StatusServiceUnavailable)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			switch r.Method {
			case http.MethodGet:
				ct := r.URL.Query().Get("container")
				_ = json.NewEncoder(w).Encode(opts.Network.List(ct))
			case http.MethodPost:
				var m network.PortMapping
				if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&m); err != nil {
					http.Error(w, "invalid port mapping", http.StatusBadRequest)
					return
				}
				if err := opts.Network.Upsert(m); err != nil {
					http.Error(w, err.Error(), http.StatusConflict)
					return
				}
				w.WriteHeader(http.StatusOK)
				_ = json.NewEncoder(w).Encode(map[string]bool{"success": true})
			case http.MethodDelete:
				id := r.URL.Query().Get("id")
				if id == "" {
					http.Error(w, "missing mapping id", http.StatusBadRequest)
					return
				}
				if err := opts.Network.Delete(id); err != nil {
					http.Error(w, err.Error(), http.StatusInternalServerError)
					return
				}
				w.WriteHeader(http.StatusOK)
				_ = json.NewEncoder(w).Encode(map[string]bool{"success": true})
			default:
				http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			}
			return
		}

		// 6. GET queries
		if r.Method != http.MethodGet {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/templates":
			_ = json.NewEncoder(w).Encode(map[string]any{
				"templates": templates.BuiltinTemplates(),
				"mirrors":   templates.BuiltinMirrors(),
			})
		case "/api/tasks":
			if opts.Management == nil {
				http.Error(w, "tasks unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := opts.Management.Pending(r.Context())
			if err != nil {
				http.Error(w, "tasks unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/managed":
			if opts.Management == nil {
				http.Error(w, "managed inventory unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := opts.Management.Managed(r.Context())
			if err != nil {
				http.Error(w, "managed inventory unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/capabilities":
			if opts.Probe == nil {
				http.Error(w, "probe unavailable", http.StatusServiceUnavailable)
				return
			}
			_ = json.NewEncoder(w).Encode(opts.Probe(r.Context()))
		case "/api/discovery":
			if opts.Discover == nil {
				http.Error(w, "discovery unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := opts.Discover(r.Context())
			if err != nil {
				http.Error(w, "incus discovery unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/resources":
			if opts.Sample == nil {
				http.Error(w, "resources unavailable", http.StatusServiceUnavailable)
				return
			}
			pool := r.URL.Query().Get("pool")
			if !poolPattern.MatchString(pool) {
				http.Error(w, "invalid pool", http.StatusBadRequest)
				return
			}
			result, err := opts.Sample(r.Context(), pool)
			if err != nil {
				http.Error(w, "incus resources unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		default:
			http.NotFound(w, r)
		}
	})
}
