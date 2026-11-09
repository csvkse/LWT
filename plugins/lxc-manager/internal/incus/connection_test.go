package incus

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestNewConnectionRejectsInvalidRemoteConfiguration(t *testing.T) {
	tests := []struct {
		name   string
		config ConnectionConfig
		want   string
	}{
		{name: "remote HTTP is forbidden", config: ConnectionConfig{Endpoint: "http://incus.example"}, want: "HTTPS"},
		{name: "TLS credentials are required", config: ConnectionConfig{Endpoint: "https://incus.example"}, want: "certificate"},
		{name: "endpoint credentials are forbidden", config: ConnectionConfig{Endpoint: "https://user:secret@incus.example"}, want: "endpoint"},
		{name: "endpoint query delimiter is forbidden", config: ConnectionConfig{Endpoint: "https://incus.example?"}, want: "endpoint"},
		{name: "server pin without endpoint is rejected", config: ConnectionConfig{ServerCertificateSHA256: strings.Repeat("a", 64)}, want: "HTTPS endpoint"},
		{name: "server pin must be a SHA-256 hex fingerprint", config: ConnectionConfig{Endpoint: "https://incus.example", Project: "lwt-test", ServerCAFile: "ca.pem", ServerCertificateSHA256: "not-a-pin", ClientCertificateFile: "client.pem", ClientKeyFile: "client-key.pem"}, want: "64 hexadecimal"},
		{name: "remote project is required", config: ConnectionConfig{Endpoint: "https://incus.example", ServerCAFile: "ca.pem", ServerCertificateSHA256: strings.Repeat("a", 64), ClientCertificateFile: "client.pem", ClientKeyFile: "client-key.pem"}, want: "project"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if _, err := NewConnection(tt.config); err == nil || !strings.Contains(strings.ToLower(err.Error()), strings.ToLower(tt.want)) {
				t.Fatalf("NewConnection() error = %v, want message containing %q", err, tt.want)
			}
		})
	}
}

func TestNewConnectionDefaultsToUnixSocketAdapter(t *testing.T) {
	client, err := NewConnection(ConnectionConfig{})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}
	transport, ok := client.HTTP.Transport.(*http.Transport)
	if !ok || transport.DialContext == nil {
		t.Fatal("default connection does not use a Unix-socket HTTP transport")
	}
	if client.Endpoint != "http://incus" || client.Project != "" {
		t.Fatalf("default adapter = endpoint %q, project %q", client.Endpoint, client.Project)
	}
}

func TestNewConnectionUsesMutualTLSAndVerifiesServer(t *testing.T) {
	clientCertFile, clientKeyFile := writeClientCertificate(t)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS == nil || len(r.TLS.PeerCertificates) != 1 {
			http.Error(w, "client certificate missing", http.StatusUnauthorized)
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	server.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	server.StartTLS()
	t.Cleanup(server.Close)

	serverCAFile := filepath.Join(t.TempDir(), "server-ca.pem")
	serverCertPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: server.Certificate().Raw})
	if err := os.WriteFile(serverCAFile, serverCertPEM, 0o600); err != nil {
		t.Fatal(err)
	}

	client, err := NewConnection(ConnectionConfig{
		Endpoint:                server.URL,
		Project:                 "lwt-test",
		ServerCAFile:            serverCAFile,
		ServerCertificateSHA256: fmt.Sprintf("%x", sha256.Sum256(server.Certificate().Raw)),
		ClientCertificateFile:   clientCertFile,
		ClientKeyFile:           clientKeyFile,
	})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}
	if client.Project != "lwt-test" {
		t.Fatalf("connection project = %q, want lwt-test", client.Project)
	}

	resp, err := client.HTTP.Get(client.Endpoint + "/1.0")
	if err != nil {
		t.Fatalf("mutual TLS request failed: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want %d", resp.StatusCode, http.StatusOK)
	}
}

func TestNewConnectionRejectsUntrustedServerCertificate(t *testing.T) {
	clientCertFile, clientKeyFile := writeClientCertificate(t)
	server := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	t.Cleanup(server.Close)

	serverCAFile := filepath.Join(t.TempDir(), "wrong-ca.pem")
	wrongCertPEM, err := os.ReadFile(clientCertFile)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(serverCAFile, wrongCertPEM, 0o600); err != nil {
		t.Fatal(err)
	}

	client, err := NewConnection(ConnectionConfig{
		Endpoint:                server.URL,
		Project:                 "lwt-test",
		ServerCAFile:            serverCAFile,
		ServerCertificateSHA256: fmt.Sprintf("%x", sha256.Sum256(server.Certificate().Raw)),
		ClientCertificateFile:   clientCertFile,
		ClientKeyFile:           clientKeyFile,
	})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}
	if _, err = client.HTTP.Get(client.Endpoint + "/1.0"); err == nil {
		t.Fatal("request succeeded with an untrusted server certificate")
	}
}

func TestNewConnectionRejectsTLS12OnlyServer(t *testing.T) {
	clientCertFile, clientKeyFile := writeClientCertificate(t)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	server.TLS = &tls.Config{
		MinVersion: tls.VersionTLS12,
		MaxVersion: tls.VersionTLS12,
		ClientAuth: tls.RequireAnyClientCert,
	}
	server.StartTLS()
	t.Cleanup(server.Close)

	serverCAFile := filepath.Join(t.TempDir(), "server-ca.pem")
	serverCertPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: server.Certificate().Raw})
	if err := os.WriteFile(serverCAFile, serverCertPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	client, err := NewConnection(ConnectionConfig{
		Endpoint:                server.URL,
		Project:                 "lwt-test",
		ServerCAFile:            serverCAFile,
		ServerCertificateSHA256: fmt.Sprintf("%x", sha256.Sum256(server.Certificate().Raw)),
		ClientCertificateFile:   clientCertFile,
		ClientKeyFile:           clientKeyFile,
	})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}
	if _, err := client.HTTP.Get(client.Endpoint + "/1.0"); err == nil {
		t.Fatal("request succeeded against a TLS 1.2-only server")
	}
}

func TestNewConnectionDoesNotFollowRedirects(t *testing.T) {
	clientCertFile, clientKeyFile := writeClientCertificate(t)
	finalRequests := 0
	final := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { finalRequests++ }))
	t.Cleanup(final.Close)
	redirect := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, final.URL, http.StatusFound)
	}))
	redirect.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	redirect.StartTLS()
	t.Cleanup(redirect.Close)

	serverCAFile := filepath.Join(t.TempDir(), "server-ca.pem")
	serverCertPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: redirect.Certificate().Raw})
	if err := os.WriteFile(serverCAFile, serverCertPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	client, err := NewConnection(ConnectionConfig{
		Endpoint:                redirect.URL,
		Project:                 "lwt-test",
		ServerCAFile:            serverCAFile,
		ServerCertificateSHA256: fmt.Sprintf("%x", sha256.Sum256(redirect.Certificate().Raw)),
		ClientCertificateFile:   clientCertFile,
		ClientKeyFile:           clientKeyFile,
	})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}

	resp, err := client.HTTP.Get(client.Endpoint + "/1.0")
	if err != nil {
		t.Fatalf("request failed: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusFound || finalRequests != 0 {
		t.Fatalf("status = %d, redirected requests = %d; want 302 and 0", resp.StatusCode, finalRequests)
	}
}

func TestNewConnectionRejectsWrongServerCertificatePin(t *testing.T) {
	clientCertFile, clientKeyFile := writeClientCertificate(t)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("server received a request despite a mismatched certificate pin")
		w.WriteHeader(http.StatusOK)
	}))
	server.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	server.StartTLS()
	t.Cleanup(server.Close)

	serverCAFile := filepath.Join(t.TempDir(), "server-ca.pem")
	serverCertPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: server.Certificate().Raw})
	if err := os.WriteFile(serverCAFile, serverCertPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	client, err := NewConnection(ConnectionConfig{
		Endpoint:                server.URL,
		Project:                 "lwt-test",
		ServerCAFile:            serverCAFile,
		ServerCertificateSHA256: strings.Repeat("0", 64),
		ClientCertificateFile:   clientCertFile,
		ClientKeyFile:           clientKeyFile,
	})
	if err != nil {
		t.Fatalf("NewConnection() error = %v", err)
	}
	if _, err := client.HTTP.Get(client.Endpoint + "/1.0"); err == nil {
		t.Fatal("request succeeded with a mismatched server certificate pin")
	}
}

func writeClientCertificate(t *testing.T) (string, string) {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject:      pkix.Name{CommonName: "lwt-test-client"},
		NotBefore:    now.Add(-time.Minute),
		NotAfter:     now.Add(time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}

	dir := t.TempDir()
	certFile := filepath.Join(dir, "client.pem")
	keyFile := filepath.Join(dir, "client-key.pem")
	if err := os.WriteFile(certFile, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyFile, pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}), 0o600); err != nil {
		t.Fatal(err)
	}
	return certFile, keyFile
}
