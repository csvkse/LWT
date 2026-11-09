package incus

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"
	"unicode"
)

// ConnectionConfig selects either the local Unix socket or an authenticated,
// certificate-verified Incus HTTPS endpoint. TLS material is read from files
// so deployments can mount certificates as secrets instead of passing their
// contents through environment variables.
type ConnectionConfig struct {
	Endpoint                string
	UnixSocket              string
	Project                 string
	ServerCAFile            string
	ServerCertificateSHA256 string
	ClientCertificateFile   string
	ClientKeyFile           string
	RequestTimeout          time.Duration
}

// NewConnection builds the Incus API adapter over either a local Unix socket
// or a certificate-verified remote HTTPS endpoint.
func NewConnection(config ConnectionConfig) (Client, error) {
	if config.Endpoint == "" {
		if hasTLSFiles(config) {
			return Client{}, errors.New("Incus TLS certificate files require an HTTPS endpoint")
		}

		socket := config.UnixSocket
		if socket == "" {
			socket = "/var/lib/incus/unix.socket"
		}

		transport := &http.Transport{
			DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
				return (&net.Dialer{}).DialContext(ctx, "unix", socket)
			},
			MaxIdleConns:          16,
			MaxIdleConnsPerHost:   8,
			IdleConnTimeout:       30 * time.Second,
			ResponseHeaderTimeout: 5 * time.Second,
		}
		return Client{HTTP: newHTTPClient(transport, config.RequestTimeout), Endpoint: "http://incus", Project: config.Project}, nil
	}

	if config.UnixSocket != "" {
		return Client{}, errors.New("configure either the Incus Unix socket or HTTPS endpoint, not both")
	}
	if config.Endpoint != strings.TrimSpace(config.Endpoint) {
		return Client{}, errors.New("invalid Incus HTTPS endpoint")
	}

	endpoint, err := url.Parse(config.Endpoint)
	if err != nil || endpoint.Scheme != "https" {
		return Client{}, errors.New("remote Incus endpoint must use HTTPS")
	}
	if endpoint.Host == "" || endpoint.User != nil || endpoint.RawQuery != "" || endpoint.ForceQuery || endpoint.Fragment != "" || (endpoint.Path != "" && endpoint.Path != "/") {
		return Client{}, errors.New("invalid Incus HTTPS endpoint")
	}
	if config.ServerCAFile == "" || config.ServerCertificateSHA256 == "" || config.ClientCertificateFile == "" || config.ClientKeyFile == "" {
		return Client{}, errors.New("Incus HTTPS requires a server CA, pinned server certificate and client certificate/key")
	}
	if !validProjectName(config.Project) {
		return Client{}, errors.New("Incus HTTPS requires a valid dedicated project")
	}
	pinnedServerCertificate, err := hex.DecodeString(config.ServerCertificateSHA256)
	if err != nil || len(pinnedServerCertificate) != sha256.Size {
		return Client{}, errors.New("Incus server certificate SHA-256 pin must be 64 hexadecimal characters")
	}

	clientCertificate, err := tls.LoadX509KeyPair(config.ClientCertificateFile, config.ClientKeyFile)
	if err != nil {
		return Client{}, fmt.Errorf("load Incus client certificate: %w", err)
	}
	caPEM, err := os.ReadFile(config.ServerCAFile)
	if err != nil {
		return Client{}, fmt.Errorf("read Incus server CA: %w", err)
	}
	serverRoots := x509.NewCertPool()
	if !serverRoots.AppendCertsFromPEM(caPEM) {
		return Client{}, errors.New("Incus server CA file contains no valid certificates")
	}

	transport := &http.Transport{
		Proxy: nil,
		TLSClientConfig: &tls.Config{
			MinVersion:   tls.VersionTLS13,
			RootCAs:      serverRoots,
			Certificates: []tls.Certificate{clientCertificate},
			VerifyConnection: func(state tls.ConnectionState) error {
				if len(state.PeerCertificates) == 0 {
					return errors.New("Incus server did not present a certificate")
				}
				fingerprint := sha256.Sum256(state.PeerCertificates[0].Raw)
				if subtle.ConstantTimeCompare(fingerprint[:], pinnedServerCertificate) != 1 {
					return errors.New("Incus server certificate fingerprint mismatch")
				}
				return nil
			},
		},
		MaxIdleConns:          16,
		MaxIdleConnsPerHost:   8,
		IdleConnTimeout:       30 * time.Second,
		TLSHandshakeTimeout:   5 * time.Second,
		ResponseHeaderTimeout: 5 * time.Second,
	}
	return Client{HTTP: newHTTPClient(transport, config.RequestTimeout), Endpoint: strings.TrimRight(config.Endpoint, "/"), Project: config.Project}, nil
}

func validProjectName(project string) bool {
	if project == "" || len(project) > 255 || strings.TrimSpace(project) != project {
		return false
	}
	for _, r := range project {
		if unicode.IsControl(r) {
			return false
		}
	}
	return true
}

func hasTLSFiles(config ConnectionConfig) bool {
	return config.ServerCAFile != "" || config.ServerCertificateSHA256 != "" || config.ClientCertificateFile != "" || config.ClientKeyFile != ""
}

func newHTTPClient(transport *http.Transport, timeout time.Duration) *http.Client {
	if timeout <= 0 {
		timeout = 5 * time.Second
	}
	return &http.Client{
		Transport:     transport,
		Timeout:       timeout,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
}
