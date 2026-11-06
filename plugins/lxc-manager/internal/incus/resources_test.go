package incus

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestSampleResourcesReadsHostAndPoolWithoutClaimingQuota(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/1.0/resources":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"cpu":{"total":8},"memory":{"total":8589934592,"used":2147483648}}}`))
		case "/1.0/storage-pools/pool1/resources":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"space":{"total":107374182400,"used":21474836480}}}`))
		default:
			t.Errorf("unexpected path: %s", r.URL.Path)
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	s, err := c.SampleResources(context.Background(), "pool1")
	if err != nil {
		t.Fatal(err)
	}
	if s.CPUThreads != 8 || s.MemoryTotalBytes != 8589934592 || s.MemoryUsedBytes != 2147483648 || s.PoolTotalBytes != 107374182400 || s.PoolUsedBytes != 21474836480 {
		t.Fatalf("result=%+v", s)
	}
}

func TestSampleResourcesRejectsMalformedAndInvalidValues(t *testing.T) {
	for _, body := range []string{`{"type":"sync","status_code":200,"metadata":{"cpu":{"total":1},"memory":{"total":2,"used":3}}}`, `{"type":"sync","status_code":200,"metadata":{"cpu":{"total":0},"memory":{"total":3,"used":2}}}`, `{"type":"sync","status_code":200,"metadata":{}}`} {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte(body)) }))
		c := Client{HTTP: server.Client(), Endpoint: server.URL}
		_, err := c.SampleResources(context.Background(), "pool1")
		server.Close()
		if err == nil {
			t.Fatalf("accepted %s", body)
		}
	}
	c := Client{HTTP: http.DefaultClient, Endpoint: "http://localhost"}
	if _, err := c.SampleResources(context.Background(), "../invalid"); err == nil {
		t.Fatal("accepted invalid pool")
	}
}
