package incus

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
)

type InstanceInfo struct {
	Name   string `json:"name"`
	Status string `json:"status"`
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
	Name string `json:"name"`
}
type Discovery struct {
	Instances     []InstanceInfo `json:"instances"`
	Images        []ImageInfo    `json:"images"`
	Pools         []PoolInfo     `json:"pools"`
	Networks      []NetworkInfo  `json:"networks"`
	OccupiedNames []string       `json:"-"`
}

func (c Client) getList(ctx context.Context, path string, out any) error {
	if c.HTTP == nil {
		return errors.New("missing HTTP client")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.Endpoint, "/")+path+"?recursion=1", nil)
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

// Discover projects only non-sensitive fields and does not infer quota support.
func (c Client) Discover(ctx context.Context) (Discovery, error) {
	result := Discovery{Instances: []InstanceInfo{}, Images: []ImageInfo{}, Pools: []PoolInfo{}, Networks: []NetworkInfo{}}
	var instances []struct {
		Name   string `json:"name"`
		Type   string `json:"type"`
		Status string `json:"status"`
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
	for _, task := range []struct {
		path   string
		output any
	}{{"/1.0/instances", &instances}, {"/1.0/images", &images}, {"/1.0/storage-pools", &pools}, {"/1.0/networks", &networks}} {
		if err := c.getList(ctx, task.path, task.output); err != nil {
			return Discovery{}, err
		}
	}
	for _, v := range instances {
		result.OccupiedNames = append(result.OccupiedNames, v.Name)
		if v.Type == "container" {
			result.Instances = append(result.Instances, InstanceInfo{v.Name, v.Status})
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
			result.Networks = append(result.Networks, NetworkInfo{v.Name})
		}
	}
	return result, nil
}
