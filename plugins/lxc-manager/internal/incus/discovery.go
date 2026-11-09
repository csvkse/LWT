package incus

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
)

type InstanceInfo struct {
	Name        string `json:"name"`
	Status      string `json:"status"`
	CPU         string `json:"cpu,omitempty"`
	Memory      string `json:"memory,omitempty"`
	Disk        string `json:"disk,omitempty"`
	StoragePool string `json:"storagePool,omitempty"`
	NetworkName string `json:"networkName,omitempty"`
	IPv4        string `json:"ipv4,omitempty"`
	IPv6        string `json:"ipv6,omitempty"`
	MAC         string `json:"mac,omitempty"`
}
type ImageInfo struct {
	Fingerprint  string `json:"fingerprint"`
	Architecture string `json:"architecture"`
	Description  string `json:"description"`
}
type PoolInfo struct {
	Name   string `json:"name"`
	Driver string `json:"driver"`
	Status string `json:"status"`
}
type NetworkInfo struct {
	Name    string `json:"name"`
	Type    string `json:"type"`
	Managed bool   `json:"managed"`
}
type Discovery struct {
	Instances     []InstanceInfo `json:"instances"`
	Images        []ImageInfo    `json:"images"`
	Pools         []PoolInfo     `json:"pools"`
	Networks      []NetworkInfo  `json:"networks"`
	OccupiedNames []string       `json:"-"`
}

func (c Client) getList(ctx context.Context, path string, recursion string, out any) error {
	if c.HTTP == nil {
		return errors.New("missing HTTP client")
	}
	query := url.Values{"recursion": []string{recursion}}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.apiURL(path, query), nil)
	if err != nil {
		return err
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("incus discovery failed (HTTP %d)", resp.StatusCode)
	}
	var result struct {
		Type       string          `json:"type"`
		StatusCode int             `json:"status_code"`
		Metadata   json.RawMessage `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(&result) != nil || result.Type != "sync" || result.StatusCode != 200 || len(result.Metadata) == 0 || string(result.Metadata) == "null" {
		return errors.New("invalid discovery response")
	}
	if err = json.Unmarshal(result.Metadata, out); err != nil {
		return errors.New("invalid discovery list")
	}
	return nil
}

// GetHostNetworks fetches the complete host network interface topology from Incus.
func (c Client) GetHostNetworks(ctx context.Context) ([]NetworkInfo, error) {
	var nets []NetworkInfo
	if err := c.getList(ctx, "/1.0/networks", "1", &nets); err != nil {
		return nil, err
	}
	return nets, nil
}

// Discover projects only non-sensitive fields and does not infer quota support.
func (c Client) Discover(ctx context.Context) (Discovery, error) {
	result := Discovery{Instances: []InstanceInfo{}, Images: []ImageInfo{}, Pools: []PoolInfo{}, Networks: []NetworkInfo{}}
	var instances []struct {
		Name            string            `json:"name"`
		Type            string            `json:"type"`
		Status          string            `json:"status"`
		ExpandedConfig  map[string]string `json:"expanded_config"`
		Config          map[string]string `json:"config"`
		ExpandedDevices map[string]struct {
			Type    string `json:"type"`
			NICType string `json:"nictype"`
			Parent  string `json:"parent"`
			Network string `json:"network"`
			Pool    string `json:"pool"`
			Size    string `json:"size"`
		} `json:"expanded_devices"`
		Devices map[string]struct {
			Type    string `json:"type"`
			NICType string `json:"nictype"`
			Parent  string `json:"parent"`
			Network string `json:"network"`
			Pool    string `json:"pool"`
			Size    string `json:"size"`
		} `json:"devices"`
		State *struct {
			Network map[string]struct {
				Addresses []struct {
					Address string `json:"address"`
					Family  string `json:"family"`
					Scope   string `json:"scope"`
				} `json:"addresses"`
				Hwaddr string `json:"hwaddr"`
			} `json:"network"`
		} `json:"state"`
	}
	var images []struct {
		Fingerprint  string `json:"fingerprint"`
		Type         string `json:"type"`
		Architecture string `json:"architecture"`
		Properties   struct {
			Description string `json:"description"`
		} `json:"properties"`
	}
	var pools []PoolInfo
	var networks []struct {
		Name    string `json:"name"`
		Type    string `json:"type"`
		Managed bool   `json:"managed"`
	}
	tasks := []struct {
		path      string
		recursion string
		output    any
	}{
		{"/1.0/instances", "2", &instances},
		{"/1.0/images", "1", &images},
		{"/1.0/storage-pools", "1", &pools},
		{"/1.0/networks", "1", &networks},
	}
	for _, task := range tasks {
		if err := c.getList(ctx, task.path, task.recursion, task.output); err != nil {
			return Discovery{}, err
		}
	}
	for _, v := range instances {
		result.OccupiedNames = append(result.OccupiedNames, v.Name)
		if v.Type == "container" {
			info := InstanceInfo{
				Name:   v.Name,
				Status: v.Status,
			}
			if v.ExpandedConfig != nil {
				info.CPU = v.ExpandedConfig["limits.cpu"]
				info.Memory = v.ExpandedConfig["limits.memory"]
			}
			if info.CPU == "" && v.Config != nil {
				info.CPU = v.Config["limits.cpu"]
			}
			if info.Memory == "" && v.Config != nil {
				info.Memory = v.Config["limits.memory"]
			}

			devices := v.ExpandedDevices
			if len(devices) == 0 {
				devices = v.Devices
			}
			if devices != nil {
				if rootDev, ok := devices["root"]; ok {
					info.StoragePool = rootDev.Pool
					info.Disk = rootDev.Size
				}
				for _, dev := range devices {
					if dev.Type == "nic" {
						if dev.Network != "" {
							info.NetworkName = dev.Network
							break
						} else if dev.NICType == "macvlan" && dev.Parent != "" {
							info.NetworkName = "macvlan:" + dev.Parent
							break
						} else if dev.NICType == "bridged" && dev.Parent != "" {
							info.NetworkName = "bridged:" + dev.Parent
							break
						} else if dev.Parent != "" {
							info.NetworkName = dev.Parent
							break
						}
					}
				}
			}

			if v.State != nil && v.State.Network != nil {
				if eth0, ok := v.State.Network["eth0"]; ok {
					info.MAC = eth0.Hwaddr
					for _, addr := range eth0.Addresses {
						if addr.Scope == "global" {
							if addr.Family == "inet" && info.IPv4 == "" {
								info.IPv4 = addr.Address
							} else if addr.Family == "inet6" && info.IPv6 == "" {
								info.IPv6 = addr.Address
							}
						}
					}
				}
				if info.IPv4 == "" || info.IPv6 == "" {
					for ifName, iface := range v.State.Network {
						if ifName == "lo" || ifName == "eth0" {
							continue
						}
						if info.MAC == "" {
							info.MAC = iface.Hwaddr
						}
						for _, addr := range iface.Addresses {
							if addr.Scope == "global" {
								if addr.Family == "inet" && info.IPv4 == "" {
									info.IPv4 = addr.Address
								} else if addr.Family == "inet6" && info.IPv6 == "" {
									info.IPv6 = addr.Address
								}
							}
						}
					}
				}
			}

			result.Instances = append(result.Instances, info)
		}
	}
	for _, v := range images {
		if v.Type == "container" {
			result.Images = append(result.Images, ImageInfo{v.Fingerprint, v.Architecture, v.Properties.Description})
		}
	}
	result.Pools = append(result.Pools, pools...)
	for _, v := range networks {
		if v.Managed && v.Type == "bridge" {
			result.Networks = append(result.Networks, NetworkInfo{
				Name:    v.Name,
				Type:    v.Type,
				Managed: v.Managed,
			})
		}
	}
	return result, nil
}
