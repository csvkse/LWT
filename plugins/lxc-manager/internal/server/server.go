package server

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"
	"regexp"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
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

// New trusts only a platform-generated per-process secret and its authenticated
// admin decision; the listener must be bound to loopback.
func New(secret string, probe Probe, discover Discover, flows ...CreateFlow) http.Handler {
	return NewWithResources(secret, probe, discover, nil, flows...)
}

func NewWithResources(secret string, probe Probe, discover Discover, sample SampleResources, flows ...CreateFlow) http.Handler {
	return NewWithManagement(secret, probe, discover, sample, nil, flows...)
}

func NewWithManagement(secret string, probe Probe, discover Discover, sample SampleResources, management ManagementFlow, flows ...CreateFlow) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		provided := r.Header.Get("X-LWT-Internal-Secret")
		if secret == "" || provided == "" || subtle.ConstantTimeCompare([]byte(provided), []byte(secret)) != 1 {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		if r.Header.Get("X-LWT-Auth-Type") != "AdminSession" {
			http.Error(w, "administrator required", http.StatusForbidden)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		if (r.URL.Path == "/api/lifecycle" || r.URL.Path == "/api/lifecycle/reconcile") && r.Method == http.MethodPost && management != nil {
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
				result, err = management.Submit(r.Context(), req.Key, req.Owner, req.Spec, req.Action)
			} else {
				result, err = management.Reconcile(r.Context(), req.Key, req.Owner, req.Spec, req.Action)
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
		if (r.URL.Path == "/api/instances" || r.URL.Path == "/api/instances/reconcile") && r.Method == http.MethodPost && len(flows) > 0 && flows[0] != nil {
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
				result, err = flows[0].Submit(r.Context(), request.Key, request.Spec)
			} else {
				result, err = flows[0].Reconcile(r.Context(), request.Key, request.Spec)
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
			if discover == nil {
				http.Error(w, "discovery unavailable", http.StatusServiceUnavailable)
				return
			}
			resources, err := discover(r.Context())
			if err != nil {
				http.Error(w, "incus discovery unavailable", http.StatusBadGateway)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(incus.Preflight(spec, resources, incus.PreflightEvidence{}))
			return
		}
		if r.Method != http.MethodGet {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/tasks":
			if management == nil {
				http.Error(w, "tasks unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := management.Pending(r.Context())
			if err != nil {
				http.Error(w, "tasks unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/managed":
			if management == nil {
				http.Error(w, "managed inventory unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := management.Managed(r.Context())
			if err != nil {
				http.Error(w, "managed inventory unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/capabilities":
			_ = json.NewEncoder(w).Encode(probe(r.Context()))
		case "/api/discovery":
			if discover == nil {
				http.Error(w, "discovery unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := discover(r.Context())
			if err != nil {
				http.Error(w, "incus discovery unavailable", http.StatusBadGateway)
				return
			}
			_ = json.NewEncoder(w).Encode(result)
		case "/api/resources":
			if sample == nil {
				http.Error(w, "resources unavailable", http.StatusServiceUnavailable)
				return
			}
			pool := r.URL.Query().Get("pool")
			if !poolPattern.MatchString(pool) {
				http.Error(w, "invalid pool", http.StatusBadRequest)
				return
			}
			result, err := sample(r.Context(), pool)
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
