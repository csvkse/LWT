package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestDnsAPI_GetAndPostConfig(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "socks5-dns-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	configPath := filepath.Join(tempDir, "socks5-config.json")

	base := NewCachedDnsResolver(NewIpv4PreferResolver(2*time.Second), 2048, 3*time.Minute, 10*time.Minute, 5*time.Second)
	router := &dnsRoutingResolver{base: base}

	srv := &Server{
		cfg: Config{
			ConnectTimeout:         5 * time.Second,
			EnableWinningIPPromote: true,
			EnableParallelRacing:   true,
		},
		stateDir:   tempDir,
		configPath: configPath,
		resolver:   router,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/dns/config", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodGet {
			s := srv
			s.authMu.RLock()
			upstreams := s.cfg.DNSUpstreams
			winningPromote := s.cfg.EnableWinningIPPromote
			parallelRacing := s.cfg.EnableParallelRacing
			s.authMu.RUnlock()

			var routes []dnsRouteConfig
			if routed, ok := s.resolver.(*dnsRoutingResolver); ok {
				routes = routed.GetConfigs()
			}

			_ = json.NewEncoder(w).Encode(map[string]any{
				"status":                 "ok",
				"upstreams":              upstreams,
				"routes":                 routes,
				"enableWinningIpPromote": winningPromote,
				"enableParallelRacing":   parallelRacing,
			})
			return
		}

		if r.Method == http.MethodPost {
			s := srv
			var body struct {
				Upstreams              []string         `json:"upstreams"`
				Routes                 []dnsRouteConfig `json:"routes"`
				EnableWinningIPPromote *bool            `json:"enableWinningIpPromote"`
				EnableParallelRacing   *bool            `json:"enableParallelRacing"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				w.WriteHeader(http.StatusBadRequest)
				return
			}

			cleanUpstreams := make([]string, 0, len(body.Upstreams))
			for _, u := range body.Upstreams {
				valid, err := validateDNSServer(u)
				if err != nil {
					w.WriteHeader(http.StatusBadRequest)
					_ = json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
					return
				}
				cleanUpstreams = append(cleanUpstreams, valid)
			}

			newBase := buildBaseResolver(cleanUpstreams, s.cfg.ConnectTimeout)
			newRoutes, err := buildDNSRoutes(body.Routes, s.cfg.ConnectTimeout)
			if err != nil {
				w.WriteHeader(http.StatusBadRequest)
				_ = json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
				return
			}

			if routed, ok := s.resolver.(*dnsRoutingResolver); ok {
				routed.Update(newBase, newRoutes, body.Routes)
			}

			s.authMu.Lock()
			s.cfg.DNSUpstreams = cleanUpstreams
			s.cfg.DNSRules = body.Routes
			if body.EnableWinningIPPromote != nil {
				s.cfg.EnableWinningIPPromote = *body.EnableWinningIPPromote
			}
			if body.EnableParallelRacing != nil {
				s.cfg.EnableParallelRacing = *body.EnableParallelRacing
			}
			s.authMu.Unlock()

			_ = s.persistConfigAtomic()

			_ = json.NewEncoder(w).Encode(map[string]any{
				"status":     "ok",
				"routeCount": len(newRoutes),
				"upstreams":  cleanUpstreams,
			})
			return
		}
	})

	// 1. Initial GET
	req := httptest.NewRequest(http.MethodGet, "/api/dns/config", nil)
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, req)

	if w.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d", w.Code)
	}

	// 2. POST update with new routes & upstreams
	payload := map[string]any{
		"upstreams": []string{"223.5.5.5:53", "119.29.29.29:53"},
		"routes": []map[string]any{
			{
				"suffix":  "bilibili.com",
				"servers": []string{"223.5.5.5:53", "119.29.29.29:53"},
				"softTtl": "5m",
				"hardTtl": "15m",
			},
			{
				"suffix":  "hdslb.com",
				"servers": []string{"223.5.5.5:53", "119.29.29.29:53"},
				"softTtl": "5m",
				"hardTtl": "15m",
			},
		},
		"enableWinningIpPromote": true,
		"enableParallelRacing":   true,
	}
	bodyData, _ := json.Marshal(payload)
	postReq := httptest.NewRequest(http.MethodPost, "/api/dns/config", bytes.NewReader(bodyData))
	postW := httptest.NewRecorder()
	mux.ServeHTTP(postW, postReq)

	if postW.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for POST, got %d: %s", postW.Code, postW.Body.String())
	}

	// 3. Verify router state updated
	if router.RouteCount() != 2 {
		t.Fatalf("expected 2 active routes, got %d", router.RouteCount())
	}

	// 4. Verify persisted file exists on disk and contains routes
	persistedData, err := os.ReadFile(configPath)
	if err != nil {
		t.Fatalf("expected persisted config file at %s: %v", configPath, err)
	}
	var persisted PersistedConfig
	if err := json.Unmarshal(persistedData, &persisted); err != nil {
		t.Fatalf("failed to unmarshal persisted config: %v", err)
	}
	if len(persisted.DNSUpstreams) != 2 || len(persisted.DNSRules) != 2 {
		t.Fatalf("unexpected persisted config content: %+v", persisted)
	}
}
