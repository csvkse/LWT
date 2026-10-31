package catalog

import (
	"sync/atomic"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// Binding maps an exposed tool name to its target backend server and upstream name.
type Binding struct {
	ServerID        string `json:"serverId"`
	BackendRevision uint64 `json:"backendRevision"`
	ExportedName    string `json:"exportedName"`
	UpstreamName    string `json:"upstreamName"`
	SchemaDigest    string `json:"schemaDigest"`
}

// Snapshot is an immutable view of approved, exposed tools and their backend bindings.
type Snapshot struct {
	Revision uint64             `json:"revision"`
	Tools    []*mcp.Tool        `json:"tools"`
	Bindings map[string]Binding `json:"bindings"`
}

// Registry manages the atomically published snapshot.
type Registry struct {
	current atomic.Pointer[Snapshot]
}

// NewRegistry initializes a registry with an empty snapshot.
func NewRegistry() *Registry {
	r := &Registry{}
	empty := &Snapshot{
		Revision: 0,
		Tools:    make([]*mcp.Tool, 0),
		Bindings: make(map[string]Binding),
	}
	r.current.Store(empty)
	return r
}

// Current returns the active immutable snapshot.
func (r *Registry) Current() *Snapshot {
	return r.current.Load()
}

// Publish atomically updates the active snapshot.
func (r *Registry) Publish(snap *Snapshot) {
	if snap != nil {
		r.current.Store(snap)
	}
}
