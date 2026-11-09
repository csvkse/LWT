package network

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
)

type PortMapping struct {
	ID          string `json:"id"`
	ContainerID string `json:"containerId"`
	Protocol    string `json:"protocol"` // "tcp" or "udp"
	HostPort    int    `json:"hostPort"`
	TargetPort  int    `json:"targetPort"`
	TargetIP    string `json:"targetIp"`
	Enabled     bool   `json:"enabled"`
	Description string `json:"description"`
}

type Store struct {
	mu   sync.RWMutex
	path string
	data map[string]PortMapping // ID -> PortMapping
}

func OpenStore(storageDir string) (*Store, error) {
	if err := os.MkdirAll(storageDir, 0755); err != nil {
		return nil, err
	}
	filePath := filepath.Join(storageDir, "port_mappings.json")
	s := &Store{
		path: filePath,
		data: make(map[string]PortMapping),
	}
	_ = s.load()
	return s, nil
}

func (s *Store) load() error {
	raw, err := os.ReadFile(s.path)
	if err != nil {
		return err
	}
	var list []PortMapping
	if err := json.Unmarshal(raw, &list); err != nil {
		return err
	}
	for _, item := range list {
		s.data[item.ID] = item
	}
	return nil
}

func (s *Store) save() error {
	list := make([]PortMapping, 0, len(s.data))
	for _, item := range s.data {
		list = append(list, item)
	}
	raw, err := json.MarshalIndent(list, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(s.path, raw, 0644)
}

func (s *Store) List(containerID string) []PortMapping {
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make([]PortMapping, 0)
	for _, m := range s.data {
		if containerID == "" || m.ContainerID == containerID {
			result = append(result, m)
		}
	}
	return result
}

func (s *Store) Upsert(mapping PortMapping) error {
	if mapping.ID == "" {
		return errors.New("missing mapping id")
	}
	if mapping.HostPort < 1 || mapping.HostPort > 65535 || mapping.TargetPort < 1 || mapping.TargetPort > 65535 {
		return errors.New("port out of range 1-65535")
	}
	if mapping.Protocol != "tcp" && mapping.Protocol != "udp" {
		mapping.Protocol = "tcp"
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	// Conflict check: ensure hostPort is not taken by another container with same protocol
	for id, existing := range s.data {
		if id != mapping.ID && existing.Enabled && mapping.Enabled &&
			existing.HostPort == mapping.HostPort && existing.Protocol == mapping.Protocol {
			return fmt.Errorf("host port %d (%s) already occupied by mapping %s", mapping.HostPort, mapping.Protocol, id)
		}
	}

	s.data[mapping.ID] = mapping
	return s.save()
}

func (s *Store) Delete(id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.data, id)
	return s.save()
}

// GenerateIptablesRule generates standard Linux NAT port forwarding commands.
func (m PortMapping) GenerateIptablesRule() []string {
	if !m.Enabled || m.TargetIP == "" {
		return nil
	}
	return []string{
		fmt.Sprintf("iptables -t nat -A PREROUTING -p %s --dport %d -j DNAT --to-destination %s:%d",
			m.Protocol, m.HostPort, m.TargetIP, m.TargetPort),
		fmt.Sprintf("iptables -A FORWARD -p %s -d %s --dport %d -m state --state NEW,ESTABLISHED,RELATED -j ACCEPT",
			m.Protocol, m.TargetIP, m.TargetPort),
	}
}
