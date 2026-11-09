package main

import (
	"bufio"
	"context"
	"crypto/subtle"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// SOCKS5 Protocol Constants (RFC 1928, RFC 1929)
const (
	socks5Version = 0x05
	authNone      = 0x00
	authUserPass  = 0x02
	authNoAccept  = 0xFF

	cmdConnect = 0x01
	cmdBind    = 0x02
	cmdUdp     = 0x03

	atypIPv4   = 0x01
	atypDomain = 0x03
	atypIPv6   = 0x04

	repSuccess        = 0x00
	repGeneralFailure = 0x01
	repRuleViolation  = 0x02
	repNetworkUnreach = 0x03
	repHostUnreach    = 0x04
	repConnRefused    = 0x05
	repCmdUnsupported = 0x07
	repAtypNotSupport = 0x08

	userPassVersion = 0x01
	authSuccess     = 0x00
	authFailure     = 0x01
)

var bufPool = sync.Pool{
	New: func() any {
		b := make([]byte, 32*1024)
		return &b
	},
}

type Metrics struct {
	ActiveConns     int64       `json:"activeConnections"`
	TotalConns      int64       `json:"totalConnections"`
	RejectedConns   int64       `json:"rejectedConnections"`
	PeakActiveConns int64       `json:"peakActiveConnections"`
	BytesRx         int64       `json:"bytesReceived"`
	BytesTx         int64       `json:"bytesTransmitted"`
	DNSResolve      PhaseMetric `json:"-"`
	UpstreamDial    PhaseMetric `json:"-"`
}

type PhaseMetric struct {
	count   int64
	totalNs int64
	maxNs   int64
}

type PhaseMetricSnapshot struct {
	Count   int64 `json:"count"`
	TotalNs int64 `json:"totalNs"`
	MaxNs   int64 `json:"maxNs"`
}

func (m *PhaseMetric) Observe(duration time.Duration) {
	ns := duration.Nanoseconds()
	if ns < 0 {
		ns = 0
	}
	atomic.AddInt64(&m.count, 1)
	atomic.AddInt64(&m.totalNs, ns)
	for current := atomic.LoadInt64(&m.maxNs); ns > current; current = atomic.LoadInt64(&m.maxNs) {
		if atomic.CompareAndSwapInt64(&m.maxNs, current, ns) {
			break
		}
	}
}

func (m *PhaseMetric) Snapshot() PhaseMetricSnapshot {
	return PhaseMetricSnapshot{
		Count:   atomic.LoadInt64(&m.count),
		TotalNs: atomic.LoadInt64(&m.totalNs),
		MaxNs:   atomic.LoadInt64(&m.maxNs),
	}
}

type Config struct {
	ListenAddr                string
	AuthMode                  string // "none" or "password"
	Username                  string
	Password                  string
	ConnectTimeout            time.Duration
	MaxActiveSessions         int
	MaxHTTPConnsPerHost       int
	MaxHTTPPendingPerHost     int
	HTTPQueueTimeout          time.Duration
	HTTPResponseHeaderTimeout time.Duration
	StatusPort                int
	AllowedSubnets            string
	AllowedDomains            string
	AllowedClients            string
	AclPreset                 string
	DNSUpstreams              []string
	DNSRules                  []dnsRouteConfig
	EnableWinningIPPromote    bool
	EnableParallelRacing      bool
}

type PersistedConfig struct {
	AuthMode               string           `json:"authMode"`
	Username               string           `json:"username"`
	Password               string           `json:"password"`
	AclPreset              string           `json:"aclPreset"`
	AllowedSubnets         string           `json:"allowedSubnets"`
	AllowedDomains         string           `json:"allowedDomains"`
	AllowedClients         string           `json:"allowedClients"`
	DNSUpstreams           []string         `json:"dnsUpstreams,omitempty"`
	DNSRules               []dnsRouteConfig `json:"dnsRules,omitempty"`
	EnableWinningIPPromote *bool            `json:"enableWinningIpPromote,omitempty"`
	EnableParallelRacing   *bool            `json:"enableParallelRacing,omitempty"`
}

type Server struct {
	authMu               sync.RWMutex
	resolverMu           sync.Mutex
	httpForwardMu        sync.Mutex
	cfg                  Config
	logger               *slog.Logger
	metrics              Metrics
	acl                  *AclEngine
	stateDir             string
	configPath           string
	resolver             IDnsResolverStrategy
	httpForwardTransport *http.Transport
	httpForwardLimiter   *HTTPHostRequestLimiter
}

func (s *Server) persistConfigAtomic() error {
	if s.configPath == "" {
		return nil
	}
	s.authMu.RLock()
	var rules []dnsRouteConfig
	if routed, ok := s.resolver.(*dnsRoutingResolver); ok {
		rules = routed.GetConfigs()
	} else {
		rules = s.cfg.DNSRules
	}
	enableWinning := s.cfg.EnableWinningIPPromote
	enableRacing := s.cfg.EnableParallelRacing
	p := PersistedConfig{
		AuthMode:               s.cfg.AuthMode,
		Username:               s.cfg.Username,
		Password:               s.cfg.Password,
		AclPreset:              s.cfg.AclPreset,
		AllowedSubnets:         s.cfg.AllowedSubnets,
		AllowedDomains:         s.cfg.AllowedDomains,
		AllowedClients:         s.cfg.AllowedClients,
		DNSUpstreams:           s.cfg.DNSUpstreams,
		DNSRules:               rules,
		EnableWinningIPPromote: &enableWinning,
		EnableParallelRacing:   &enableRacing,
	}
	s.authMu.RUnlock()

	data, err := json.MarshalIndent(p, "", "  ")
	if err != nil {
		return fmt.Errorf("marshal persisted config failed: %w", err)
	}

	if err := os.MkdirAll(filepath.Dir(s.configPath), 0755); err != nil {
		return fmt.Errorf("create config dir failed: %w", err)
	}

	tmpPath := s.configPath + fmt.Sprintf(".tmp-%d", time.Now().UnixNano())
	if err := os.WriteFile(tmpPath, data, 0600); err != nil {
		return fmt.Errorf("write temp config failed: %w", err)
	}

	if err := os.Rename(tmpPath, s.configPath); err != nil {
		_ = os.Remove(tmpPath)
		return fmt.Errorf("atomic rename config failed: %w", err)
	}

	if s.logger != nil {
		s.logger.Info("persisted socks5 configuration atomically", "path", s.configPath)
	}
	return nil
}

func resolveStateDir(raw string) string {
	raw = strings.TrimSpace(raw)
	if envState := strings.TrimSpace(os.Getenv("LWT_STATE_DIR")); envState != "" {
		return filepath.Clean(envState)
	}
	if envVol := strings.TrimSpace(os.Getenv("LWT_VOLUME_DIR")); envVol != "" {
		return filepath.Clean(filepath.Join(envVol, "state"))
	}
	if envData := strings.TrimSpace(os.Getenv("LWT_DATA_DIR")); envData != "" {
		return filepath.Clean(filepath.Join(envData, "state"))
	}
	if raw != "" && raw != "{stateDir}" && raw != "data" {
		if filepath.IsAbs(raw) {
			return filepath.Clean(raw)
		}
		if exe, err := os.Executable(); err == nil {
			return filepath.Clean(filepath.Join(filepath.Dir(exe), raw))
		}
		cwd, _ := os.Getwd()
		return filepath.Clean(filepath.Join(cwd, raw))
	}
	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		if _, err := os.Stat(filepath.Join(exeDir, "plugin.json")); err == nil {
			return filepath.Clean(filepath.Join(exeDir, "data"))
		}
	}
	cwd, _ := os.Getwd()
	return filepath.Clean(filepath.Join(cwd, "data"))
}

func (s *Server) promoteWinningIP(host string, ip net.IP) bool {
	if ip == nil {
		return false
	}
	s.authMu.RLock()
	enabled := s.cfg.EnableWinningIPPromote
	s.authMu.RUnlock()
	if !enabled {
		return false
	}
	resolver := s.getDnsResolver()
	if promoter, ok := resolver.(interface {
		PromoteWinningIP(string, net.IP) bool
	}); ok {
		return promoter.PromoteWinningIP(host, ip)
	}
	return false
}

func (s *Server) GetAuthConfig() (mode, username, password string) {
	s.authMu.RLock()
	defer s.authMu.RUnlock()
	return s.cfg.AuthMode, s.cfg.Username, s.cfg.Password
}

func (s *Server) getDnsResolver() IDnsResolverStrategy {
	s.resolverMu.Lock()
	defer s.resolverMu.Unlock()
	if s.resolver == nil {
		timeout := s.cfg.ConnectTimeout
		if timeout <= 0 {
			timeout = 10 * time.Second
		}
		s.resolver = NewCachedDnsResolver(NewIpv4PreferResolver(timeout), 2048, 3*time.Minute, 10*time.Minute, 5*time.Second)
	}
	return s.resolver
}

func (s *Server) SetAuthConfig(mode, username, password string) {
	s.authMu.Lock()
	defer s.authMu.Unlock()
	mode = strings.ToLower(strings.TrimSpace(mode))
	if mode == "" {
		mode = "none"
	}
	s.cfg.AuthMode = mode
	s.cfg.Username = username
	s.cfg.Password = password
}

func main() {
	listenFlag := flag.String("listen", "", "SOCKS5 listen address (host:port)")
	stateDirFlag := flag.String("state-dir", "", "Path to state directory for config persistence")
	authModeFlag := flag.String("auth-mode", "none", "Authentication mode ('none' or 'password')")
	usernameFlag := flag.String("username", "", "SOCKS5 authentication username")
	passwordFlag := flag.String("password", "", "SOCKS5 authentication password")
	timeoutSec := flag.Int("connect-timeout", 10, "Upstream connection timeout in seconds")
	maxSessionsFlag := flag.Int("max-active-sessions", positiveIntFromEnv("SOCKS5_MAX_ACTIVE_SESSIONS", defaultMaxActiveSessions), "Maximum active client sessions")
	maxHTTPConnsPerHostFlag := flag.Int("max-http-conns-per-host", positiveIntFromEnv("SOCKS5_HTTP_MAX_CONNS_PER_HOST", 128), "Maximum active/dialing/idle HTTP upstream connections per host")
	maxHTTPPendingPerHostFlag := flag.Int("http-max-pending-per-host", positiveIntFromEnv("SOCKS5_HTTP_MAX_PENDING_PER_HOST", defaultMaxHTTPPendingPerHost), "Maximum queued HTTP upstream requests per host")
	httpQueueTimeoutFlag := flag.Duration("http-queue-timeout", positiveDurationFromEnv("SOCKS5_HTTP_QUEUE_TIMEOUT", defaultHTTPQueueTimeout), "Maximum wait for an HTTP upstream request slot")
	responseHeaderTimeoutFlag := flag.Duration("http-response-header-timeout", positiveDurationFromEnv("SOCKS5_HTTP_RESPONSE_HEADER_TIMEOUT", 30*time.Second), "Maximum wait for upstream HTTP response headers")
	subnetsFlag := flag.String("allowed-subnets", "", "Allowed target subnets (e.g. '10.,127.,192.168.', '10.0.0.0/8')")
	domainsFlag := flag.String("allowed-domains", "", "Allowed target domains (e.g. '*.internal,*.local,regex:...')")
	clientsFlag := flag.String("allowed-clients", "", "Allowed client incoming IPs/subnets")
	presetFlag := flag.String("acl-preset", "all", "ACL preset ('lan', 'loopback', 'all')")
	dnsUpstreamsFlag := flag.String("dns-upstreams", "", "Comma-separated upstream DNS servers (e.g. '223.5.5.5:53,119.29.29.29:53')")
	dnsRulesFlag := flag.String("dns-rules", "", "JSON array of DNS routing rules")
	winningIpPromoteFlag := flag.Bool("enable-winning-ip-promote", true, "Enable Happy Eyeballs winning IP cache promotion")
	parallelRacingFlag := flag.Bool("enable-parallel-racing", true, "Enable multi-upstream DNS parallel racing")
	flag.Parse()

	stateDir := resolveStateDir(*stateDirFlag)
	configPath := filepath.Join(stateDir, "socks5-config.json")

	// 状态机自愈加载：优先探测磁盘持久化配置 (Hydrate from persisted state)
	var persisted PersistedConfig
	hasPersisted := false
	if data, err := os.ReadFile(configPath); err == nil && len(data) > 0 {
		if err := json.Unmarshal(data, &persisted); err == nil {
			hasPersisted = true
		}
	}

	addr := *listenFlag
	if addr == "" {
		if envPort := os.Getenv("PORT"); envPort != "" {
			addr = "0.0.0.0:" + envPort
		} else if envPort := os.Getenv("SOCKS5_PORT"); envPort != "" {
			addr = "0.0.0.0:" + envPort
		} else {
			addr = "0.0.0.0:18001"
		}
	} else if strings.HasPrefix(addr, ":") {
		addr = "0.0.0.0" + addr
	}

	authMode := strings.ToLower(strings.TrimSpace(*authModeFlag))
	username := *usernameFlag
	if username == "" {
		username = os.Getenv("SOCKS5_USERNAME")
	}
	password := *passwordFlag
	if password == "" {
		password = os.Getenv("SOCKS5_PASSWORD")
	}

	subnets := *subnetsFlag
	if subnets == "" {
		subnets = os.Getenv("SOCKS5_ALLOWED_SUBNETS")
	}
	domains := *domainsFlag
	if domains == "" {
		domains = os.Getenv("SOCKS5_ALLOWED_DOMAINS")
	}
	clients := *clientsFlag
	if clients == "" {
		clients = os.Getenv("SOCKS5_ALLOWED_CLIENTS")
	}
	preset := strings.ToLower(strings.TrimSpace(*presetFlag))
	if preset == "" {
		preset = os.Getenv("SOCKS5_ACL_PRESET")
	}
	if preset == "" {
		preset = "all"
	}
	if subnets == "" && preset == "all" {
		subnets = "*"
	}
	if domains == "" && preset == "all" {
		domains = "*"
	}
	if clients == "" {
		clients = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1/128"
	}

	// 磁盘持久化配置状态覆盖 (State Machine Hydration)
	if hasPersisted {
		if persisted.AuthMode != "" {
			authMode = persisted.AuthMode
		}
		if persisted.Username != "" {
			username = persisted.Username
		}
		if persisted.Password != "" {
			password = persisted.Password
		}
		if persisted.AclPreset != "" {
			preset = persisted.AclPreset
		}
		if persisted.AllowedSubnets != "" {
			subnets = persisted.AllowedSubnets
		}
		if persisted.AllowedDomains != "" {
			domains = persisted.AllowedDomains
		}
		if persisted.AllowedClients != "" {
			clients = persisted.AllowedClients
		}
	}

	if authMode == "password" && (username == "" || password == "") {
		authMode = "none"
	}

	aclEngine, err := NewAclEngine(preset, subnets, domains, clients)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to compile ACL rules: %v\n", err)
		os.Exit(1)
	}

	logger := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	}))

	var dnsUpstreams []string
	if *dnsUpstreamsFlag != "" {
		for _, s := range strings.Split(*dnsUpstreamsFlag, ",") {
			if trimmed := strings.TrimSpace(s); trimmed != "" {
				dnsUpstreams = append(dnsUpstreams, trimmed)
			}
		}
	} else if envUpstreams := os.Getenv("SOCKS5_DNS_UPSTREAMS"); envUpstreams != "" {
		for _, s := range strings.Split(envUpstreams, ",") {
			if trimmed := strings.TrimSpace(s); trimmed != "" {
				dnsUpstreams = append(dnsUpstreams, trimmed)
			}
		}
	}

	var dnsRules []dnsRouteConfig
	if *dnsRulesFlag != "" {
		if parsed, err := parseDNSRouteConfigs(*dnsRulesFlag); err == nil {
			dnsRules = parsed
		} else {
			fmt.Fprintf(os.Stderr, "invalid --dns-rules: %v\n", err)
			os.Exit(1)
		}
	} else if envRules := os.Getenv("SOCKS5_DNS_RULES"); envRules != "" {
		if parsed, err := parseDNSRouteConfigs(envRules); err == nil {
			dnsRules = parsed
		} else {
			fmt.Fprintf(os.Stderr, "invalid SOCKS5_DNS_RULES: %v\n", err)
			os.Exit(1)
		}
	}

	winningPromote := *winningIpPromoteFlag
	parallelRacing := *parallelRacingFlag

	if hasPersisted {
		if len(persisted.DNSUpstreams) > 0 && len(dnsUpstreams) == 0 {
			dnsUpstreams = persisted.DNSUpstreams
		}
		if len(persisted.DNSRules) > 0 && len(dnsRules) == 0 {
			dnsRules = persisted.DNSRules
		}
		if persisted.EnableWinningIPPromote != nil {
			winningPromote = *persisted.EnableWinningIPPromote
		}
		if persisted.EnableParallelRacing != nil {
			parallelRacing = *persisted.EnableParallelRacing
		}
	}

	timeoutDur := time.Duration(*timeoutSec) * time.Second
	baseResolver := buildBaseResolver(dnsUpstreams, timeoutDur)
	configuredResolver, err := buildDNSResolverWithConfigs(baseResolver, dnsRules, timeoutDur)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to build DNS resolver: %v\n", err)
		os.Exit(1)
	}

	srv := &Server{
		cfg: Config{
			ListenAddr:                addr,
			AuthMode:                  authMode,
			Username:                  username,
			Password:                  password,
			ConnectTimeout:            timeoutDur,
			MaxActiveSessions:         *maxSessionsFlag,
			MaxHTTPConnsPerHost:       *maxHTTPConnsPerHostFlag,
			MaxHTTPPendingPerHost:     *maxHTTPPendingPerHostFlag,
			HTTPQueueTimeout:          *httpQueueTimeoutFlag,
			HTTPResponseHeaderTimeout: *responseHeaderTimeoutFlag,
			AllowedSubnets:            subnets,
			AllowedDomains:            domains,
			AllowedClients:            clients,
			AclPreset:                 preset,
			DNSUpstreams:              dnsUpstreams,
			DNSRules:                  dnsRules,
			EnableWinningIPPromote:    winningPromote,
			EnableParallelRacing:      parallelRacing,
		},
		logger:     logger,
		acl:        aclEngine,
		stateDir:   stateDir,
		configPath: configPath,
		resolver:   configuredResolver,
	}

	// 首次启动时若尚未落盘，立即初始化持久化文件
	if !hasPersisted {
		_ = srv.persistConfigAtomic()
	}

	logger.Info("starting socks5-proxy microservice",
		"listen", addr,
		"authMode", authMode,
		"aclPreset", preset,
		"allowedSubnets", subnets,
		"allowedDomains", domains,
	)

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	if err := srv.Run(ctx); err != nil && !errors.Is(err, net.ErrClosed) {
		logger.Error("server exited with error", "error", err)
		os.Exit(1)
	}
}

func (s *Server) Run(ctx context.Context) error {
	listener, err := net.Listen("tcp", s.cfg.ListenAddr)
	if err != nil {
		return fmt.Errorf("failed to bind SOCKS5 listener on %s: %w", s.cfg.ListenAddr, err)
	}
	defer listener.Close()
	defer s.closeHTTPForwardIdleConnections()

	boundAddr := listener.Addr().String()
	fmt.Printf("SOCKS5_LISTEN=%s\n", boundAddr)
	s.logger.Info("SOCKS5 proxy server started",
		"listen", boundAddr,
		"authMode", s.cfg.AuthMode,
		"connectTimeout", s.cfg.ConnectTimeout.String(),
		"maxActiveSessions", s.effectiveMaxActiveSessions(),
		"maxHTTPConnsPerHost", s.effectiveMaxHTTPConnsPerHost(),
		"maxHTTPPendingPerHost", s.effectiveMaxHTTPPendingPerHost(),
		"httpQueueTimeout", s.effectiveHTTPQueueTimeout().String(),
		"httpResponseHeaderTimeout", s.effectiveHTTPResponseHeaderTimeout().String(),
	)

	// Optionally start a background metrics HTTP server on Loopback (port + 1000) for UI health check
	s.startMetricsHttp(boundAddr)

	go func() {
		<-ctx.Done()
		s.logger.Info("shutting down SOCKS5 listener...")
		_ = listener.Close()
	}()
	admission := NewConnectionAdmission(s.effectiveMaxActiveSessions())

	for {
		conn, err := listener.Accept()
		if err != nil {
			select {
			case <-ctx.Done():
				return nil
			default:
				s.logger.Warn("accept connection failed", "error", err)
				continue
			}
		}

		// Pre-handshake ingress client check: drop immediately if unauthorized
		if s.acl != nil && !s.acl.IsClientAllowed(conn.RemoteAddr().String()) {
			s.logger.Warn("client rejected by ingress whitelist policy", "client", conn.RemoteAddr().String())
			_ = conn.Close()
			continue
		}

		s.startAcceptedSession(conn, admission)
	}
}

func (s *Server) startAcceptedSession(conn net.Conn, admission *ConnectionAdmission) bool {
	permit, ok := admission.TryAcquire()
	if !ok {
		atomic.AddInt64(&s.metrics.RejectedConns, 1)
		_ = conn.Close()
		return false
	}

	active := atomic.AddInt64(&s.metrics.ActiveConns, 1)
	updateMaxInt64(&s.metrics.PeakActiveConns, active)
	atomic.AddInt64(&s.metrics.TotalConns, 1)
	go func(c net.Conn) {
		defer func() {
			_ = c.Close()
			atomic.AddInt64(&s.metrics.ActiveConns, -1)
			permit.Release()
		}()
		if err := s.handleConn(c); err != nil && !errors.Is(err, io.EOF) && !errors.Is(err, net.ErrClosed) {
			if s.logger != nil {
				s.logger.Debug("session ended with error", "client", c.RemoteAddr().String(), "error", err)
			}
		}
	}(conn)
	return true
}

func updateMaxInt64(target *int64, value int64) {
	for current := atomic.LoadInt64(target); value > current; current = atomic.LoadInt64(target) {
		if atomic.CompareAndSwapInt64(target, current, value) {
			return
		}
	}
}

func (s *Server) effectiveMaxActiveSessions() int {
	if s.cfg.MaxActiveSessions > 0 {
		return s.cfg.MaxActiveSessions
	}
	return defaultMaxActiveSessions
}

func positiveIntFromEnv(name string, fallback int) int {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback
	}
	parsed, err := strconv.Atoi(value)
	if err != nil || parsed <= 0 {
		return fallback
	}
	return parsed
}

func positiveDurationFromEnv(name string, fallback time.Duration) time.Duration {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback
	}
	parsed, err := time.ParseDuration(value)
	if err != nil || parsed <= 0 {
		return fallback
	}
	return parsed
}

// BufferedConn wraps net.Conn with a bufio.Reader to ensure any peeked or buffered bytes are drained first.
type BufferedConn struct {
	net.Conn
	r io.Reader
}

func (b *BufferedConn) Read(p []byte) (int, error) {
	return b.r.Read(p)
}

func (b *BufferedConn) CloseWrite() error {
	if cw, ok := b.Conn.(interface{ CloseWrite() error }); ok {
		return cw.CloseWrite()
	}
	return nil
}

func (b *BufferedConn) Buffered() int {
	if br, ok := b.r.(*bufio.Reader); ok {
		return br.Buffered()
	}
	return 0
}

func (b *BufferedConn) RawConn() net.Conn {
	return b.Conn
}

func optimizeTcpConn(conn net.Conn) {
	if tcp, ok := conn.(*net.TCPConn); ok {
		_ = tcp.SetNoDelay(true)
		_ = tcp.SetKeepAlive(true)
		_ = tcp.SetKeepAlivePeriod(30 * time.Second)
	}
}

func (s *Server) handleConn(conn net.Conn) error {
	_ = conn.SetDeadline(time.Now().Add(15 * time.Second))
	optimizeTcpConn(conn)

	br := bufio.NewReader(conn)
	prefix, err := br.Peek(1)
	if err != nil {
		return fmt.Errorf("peek protocol header failed: %w", err)
	}

	bConn := &BufferedConn{Conn: conn, r: br}

	if prefix[0] == socks5Version {
		// SOCKS5 Protocol (RFC 1928)
		return s.handleSocks5(bConn)
	}

	if isHttpProxyMethod(prefix[0]) {
		// HTTP / HTTPS Forward Proxy Protocol (RFC 9110)
		return s.handleHttpProxy(bConn, br)
	}

	return fmt.Errorf("unsupported protocol signature: 0x%02X", prefix[0])
}

func (s *Server) handleSocks5(conn net.Conn) error {
	// 1. Negotiation (Handshake)
	header := make([]byte, 2)
	if _, err := io.ReadFull(conn, header); err != nil {
		return fmt.Errorf("read greeting failed: %w", err)
	}
	if header[0] != socks5Version {
		return fmt.Errorf("unsupported protocol version: %d", header[0])
	}

	numMethods := int(header[1])
	methods := make([]byte, numMethods)
	if _, err := io.ReadFull(conn, methods); err != nil {
		return fmt.Errorf("read methods failed: %w", err)
	}

	authMode, _, _ := s.GetAuthConfig()
	selectedMethod := byte(authNoAccept)
	if authMode == "password" {
		for _, m := range methods {
			if m == authUserPass {
				selectedMethod = authUserPass
				break
			}
		}
	} else {
		for _, m := range methods {
			if m == authNone {
				selectedMethod = authNone
				break
			}
		}
	}

	if _, err := conn.Write([]byte{socks5Version, selectedMethod}); err != nil {
		return fmt.Errorf("send method response failed: %w", err)
	}
	if selectedMethod == authNoAccept {
		return errors.New("no acceptable authentication methods")
	}

	// 2. Authentication if required
	if selectedMethod == authUserPass {
		if err := s.handleUserPassAuth(conn); err != nil {
			return err
		}
	}

	// 3. Process SOCKS5 Request
	reqHeader := make([]byte, 4)
	if _, err := io.ReadFull(conn, reqHeader); err != nil {
		return fmt.Errorf("read request header failed: %w", err)
	}
	if reqHeader[0] != socks5Version {
		return fmt.Errorf("unsupported request version: %d", reqHeader[0])
	}
	if reqHeader[1] != cmdConnect {
		_ = s.sendReply(conn, repCmdUnsupported, nil)
		return fmt.Errorf("unsupported command: %d", reqHeader[1])
	}

	var targetHost string
	switch reqHeader[3] {
	case atypIPv4:
		ipv4 := make([]byte, 4)
		if _, err := io.ReadFull(conn, ipv4); err != nil {
			return err
		}
		targetHost = net.IP(ipv4).String()
	case atypDomain:
		lenBuf := make([]byte, 1)
		if _, err := io.ReadFull(conn, lenBuf); err != nil {
			return err
		}
		domain := make([]byte, int(lenBuf[0]))
		if _, err := io.ReadFull(conn, domain); err != nil {
			return err
		}
		targetHost = NormalizeDomain(string(domain))
	case atypIPv6:
		ipv6 := make([]byte, 16)
		if _, err := io.ReadFull(conn, ipv6); err != nil {
			return err
		}
		targetHost = net.IP(ipv6).String()
	default:
		_ = s.sendReply(conn, repAtypNotSupport, nil)
		return fmt.Errorf("unsupported address type: %d", reqHeader[3])
	}

	portBuf := make([]byte, 2)
	if _, err := io.ReadFull(conn, portBuf); err != nil {
		return err
	}
	targetPort := binary.BigEndian.Uint16(portBuf)
	targetAddr := net.JoinHostPort(targetHost, strconv.Itoa(int(targetPort)))

	// 4. Target Host Resolution & Anti-DNS-Rebinding Defense
	var candidateIPs []net.IP
	if ip := net.ParseIP(targetHost); ip != nil {
		candidateIPs = []net.IP{ip}
	} else {
		resolver := s.getDnsResolver()
		resolveCtx, cancel := context.WithTimeout(context.Background(), s.cfg.ConnectTimeout)
		resolveStarted := time.Now()
		ips, err := resolver.Resolve(resolveCtx, targetHost)
		s.metrics.DNSResolve.Observe(time.Since(resolveStarted))
		cancel()
		if err != nil || len(ips) == 0 {
			_ = s.sendReply(conn, repHostUnreach, nil)
			if s.acl != nil {
				s.acl.RecordTargetResult(false)
			}
			return fmt.Errorf("dns lookup failed for %s: %w", targetHost, err)
		}
		candidateIPs = ips
	}

	if s.acl != nil {
		allowed, needDns := s.acl.EvaluateTarget(targetHost)
		if !allowed && !needDns {
			s.logger.Warn("connect target rejected by domain policy",
				"client", conn.RemoteAddr().String(),
				"target", targetHost,
			)
			_ = s.sendReply(conn, repRuleViolation, nil)
			s.acl.RecordTargetResult(false)
			return fmt.Errorf("target %s rejected by domain whitelist policy", targetHost)
		}
	}

	// 5. Connect to upstream target via Happy Eyeballs Race (RFC 8305)
	dialStarted := time.Now()
	upstream, winningIP, err := dialUpstreamWithRace(context.Background(), candidateIPs, int(targetPort), s.cfg.ConnectTimeout, s.acl)
	s.metrics.UpstreamDial.Observe(time.Since(dialStarted))
	if err != nil {
		if errors.Is(err, ErrAclForbidden) {
			_ = s.sendReply(conn, repRuleViolation, nil)
		} else {
			_ = s.sendReply(conn, repConnRefused, nil)
		}
		if s.acl != nil {
			s.acl.RecordTargetResult(false)
		}
		return fmt.Errorf("upstream dial to %s failed: %w", targetAddr, err)
	}
	defer upstream.Close()

	if s.acl != nil {
		s.acl.RecordTargetResult(true)
	}
	if winningIP != nil {
		s.promoteWinningIP(targetHost, winningIP)
	}

	localBind := upstream.LocalAddr().(*net.TCPAddr)
	if err := s.sendReply(conn, repSuccess, localBind); err != nil {
		return fmt.Errorf("send success reply failed: %w", err)
	}

	// 5. Clear deadlines and start full-duplex piping
	_ = conn.SetDeadline(time.Time{})
	_ = upstream.SetDeadline(time.Time{})

	s.logger.Info("tunnel established",
		"client", conn.RemoteAddr().String(),
		"target", targetAddr,
	)

	return s.relay(conn, upstream)
}

func (s *Server) handleUserPassAuth(conn net.Conn) error {
	ver := make([]byte, 1)
	if _, err := io.ReadFull(conn, ver); err != nil {
		return err
	}
	if ver[0] != userPassVersion {
		return fmt.Errorf("unsupported auth version: %d", ver[0])
	}

	uLen := make([]byte, 1)
	if _, err := io.ReadFull(conn, uLen); err != nil {
		return err
	}
	username := make([]byte, int(uLen[0]))
	if _, err := io.ReadFull(conn, username); err != nil {
		return err
	}

	pLen := make([]byte, 1)
	if _, err := io.ReadFull(conn, pLen); err != nil {
		return err
	}
	password := make([]byte, int(pLen[0]))
	if _, err := io.ReadFull(conn, password); err != nil {
		return err
	}

	_, expectedUser, expectedPass := s.GetAuthConfig()
	userOk := subtle.ConstantTimeCompare([]byte(expectedUser), username) == 1
	passOk := subtle.ConstantTimeCompare([]byte(expectedPass), password) == 1

	if userOk && passOk {
		_, _ = conn.Write([]byte{userPassVersion, authSuccess})
		return nil
	}

	if s.acl != nil {
		s.acl.RecordAuthFailure()
	}
	s.logger.Warn("SOCKS5 user authentication failed",
		"client", conn.RemoteAddr().String(),
		"attemptedUser", string(username),
	)
	_, _ = conn.Write([]byte{userPassVersion, authFailure})
	return errors.New("authentication failed: invalid username or password")
}

func (s *Server) sendReply(conn net.Conn, rep byte, bindAddr *net.TCPAddr) error {
	reply := []byte{socks5Version, rep, 0x00, atypIPv4, 0, 0, 0, 0, 0, 0}
	if bindAddr != nil {
		if ip4 := bindAddr.IP.To4(); ip4 != nil {
			copy(reply[4:8], ip4)
		}
		binary.BigEndian.PutUint16(reply[8:10], uint16(bindAddr.Port))
	}
	_, err := conn.Write(reply)
	return err
}

func (s *Server) relay(client, target net.Conn) error {
	optimizeTcpConn(client)
	optimizeTcpConn(target)

	// 预先排空 client (BufferedConn) 中由于 Peek 首字节嗅探而残留的预读数据
	if bc, ok := client.(*BufferedConn); ok {
		if bufferedLen := bc.Buffered(); bufferedLen > 0 {
			n, err := io.CopyN(target, bc.r, int64(bufferedLen))
			atomic.AddInt64(&s.metrics.BytesRx, n)
			if err != nil {
				_ = client.Close()
				_ = target.Close()
				return fmt.Errorf("forward buffered client data: %w", err)
			}
		}
		client = bc.RawConn()
	}

	var wg sync.WaitGroup
	wg.Add(2)
	errCh := make(chan error, 2)

	pipe := func(dst, src net.Conn, counter *int64) {
		defer wg.Done()

		bufPtr := bufPool.Get().(*[]byte)
		n, copyErr := io.CopyBuffer(dst, src, *bufPtr)
		bufPool.Put(bufPtr)
		atomic.AddInt64(counter, n)
		if copyErr != nil {
			// A hard copy error invalidates the full tunnel. Closing both sides
			// releases the opposite direction if it is still blocked in Read.
			_ = client.Close()
			_ = target.Close()
			errCh <- copyErr
			return
		}

		// 半关闭状态：通知对端本方向数据已发送完毕
		if cw, ok := dst.(interface{ CloseWrite() error }); ok {
			if err := cw.CloseWrite(); err != nil {
				_ = client.Close()
				_ = target.Close()
				errCh <- err
				return
			}
		}
		errCh <- nil
	}

	go pipe(target, client, &s.metrics.BytesRx)
	go pipe(client, target, &s.metrics.BytesTx)

	wg.Wait()
	firstErr := <-errCh
	secondErr := <-errCh
	if firstErr != nil {
		return firstErr
	}
	return secondErr
}

func (s *Server) startMetricsHttp(boundAddr string) {
	_, portStr, err := net.SplitHostPort(boundAddr)
	if err != nil {
		return
	}
	port, err := strconv.Atoi(portStr)
	if err != nil {
		return
	}

	metricsPort := port + 1000
	if metricsPort > 65535 {
		metricsPort = port - 1000
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"status":"ok","listen":"%s","activeConns":%d,"totalConns":%d,"bytesRx":%d,"bytesTx":%d}`,
			boundAddr,
			atomic.LoadInt64(&s.metrics.ActiveConns),
			atomic.LoadInt64(&s.metrics.TotalConns),
			atomic.LoadInt64(&s.metrics.BytesRx),
			atomic.LoadInt64(&s.metrics.BytesTx),
		)
	})

	mux.HandleFunc("/api/status", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		authMode, username, _ := s.GetAuthConfig()
		var dnsStats map[string]any
		dnsRuleCount := 0
		if routed, ok := s.resolver.(*dnsRoutingResolver); ok {
			dnsRuleCount = routed.RouteCount()
		}
		if cr, ok := s.resolver.(interface {
			CacheStats() (int64, int64, int64, int64, int64, int)
		}); ok {
			hits, misses, staleHits, evictions, promotions, size := cr.CacheStats()
			dnsStats = map[string]any{
				"hits":       hits,
				"misses":     misses,
				"staleHits":  staleHits,
				"evictions":  evictions,
				"promotions": promotions,
				"cacheSize":  size,
			}
		}
		httpAdmission := s.getHTTPForwardRequestLimiter().Snapshot()
		resp := map[string]any{
			"status":                      "ok",
			"listen":                      boundAddr,
			"authMode":                    authMode,
			"username":                    username,
			"activeConns":                 atomic.LoadInt64(&s.metrics.ActiveConns),
			"totalConns":                  atomic.LoadInt64(&s.metrics.TotalConns),
			"rejectedConns":               atomic.LoadInt64(&s.metrics.RejectedConns),
			"peakActiveConns":             atomic.LoadInt64(&s.metrics.PeakActiveConns),
			"maxActiveSessions":           s.effectiveMaxActiveSessions(),
			"maxHTTPConnsPerHost":         s.effectiveMaxHTTPConnsPerHost(),
			"maxHTTPPendingPerHost":       s.effectiveMaxHTTPPendingPerHost(),
			"httpQueueTimeoutMs":          s.effectiveHTTPQueueTimeout().Milliseconds(),
			"httpResponseHeaderTimeoutMs": s.effectiveHTTPResponseHeaderTimeout().Milliseconds(),
			"httpActiveRequests":          httpAdmission.Active,
			"httpPendingRequests":         httpAdmission.Pending,
			"httpRejectedRequests":        httpAdmission.Rejected,
			"httpQueueTimeouts":           httpAdmission.TimedOut,
			"bytesRx":                     atomic.LoadInt64(&s.metrics.BytesRx),
			"bytesTx":                     atomic.LoadInt64(&s.metrics.BytesTx),
			"runtime":                     currentRuntimeSnapshot(),
			"acl":                         s.acl.GetStats(),
			"dns":                         dnsStats,
			"dnsRuleCount":                dnsRuleCount,
			"phases": map[string]PhaseMetricSnapshot{
				"dnsResolve":   s.metrics.DNSResolve.Snapshot(),
				"upstreamDial": s.metrics.UpstreamDial.Snapshot(),
			},
		}
		_ = json.NewEncoder(w).Encode(resp)
	})

	mux.HandleFunc("/api/dns/config", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodGet {
			s.authMu.RLock()
			upstreams := s.cfg.DNSUpstreams
			winningPromote := s.cfg.EnableWinningIPPromote
			parallelRacing := s.cfg.EnableParallelRacing
			s.authMu.RUnlock()

			var routes []dnsRouteConfig
			if routed, ok := s.resolver.(*dnsRoutingResolver); ok {
				routes = routed.GetConfigs()
			} else {
				routes = s.cfg.DNSRules
			}

			var stats map[string]any
			if cr, ok := s.resolver.(interface {
				CacheStats() (int64, int64, int64, int64, int64, int)
			}); ok {
				hits, misses, staleHits, evictions, promotions, size := cr.CacheStats()
				stats = map[string]any{
					"hits":       hits,
					"misses":     misses,
					"staleHits":  staleHits,
					"evictions":  evictions,
					"promotions": promotions,
					"cacheSize":  size,
				}
			}

			_ = json.NewEncoder(w).Encode(map[string]any{
				"status":                 "ok",
				"upstreams":              upstreams,
				"routes":                 routes,
				"enableWinningIpPromote": winningPromote,
				"enableParallelRacing":   parallelRacing,
				"stats":                  stats,
			})
			return
		}

		if r.Method == http.MethodPost {
			var body struct {
				Upstreams              []string         `json:"upstreams"`
				Routes                 []dnsRouteConfig `json:"routes"`
				EnableWinningIPPromote *bool            `json:"enableWinningIpPromote"`
				EnableParallelRacing   *bool            `json:"enableParallelRacing"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				w.WriteHeader(http.StatusBadRequest)
				_ = json.NewEncoder(w).Encode(map[string]string{"error": "invalid json payload"})
				return
			}

			// Validate upstreams
			cleanUpstreams := make([]string, 0, len(body.Upstreams))
			for _, u := range body.Upstreams {
				valid, err := validateDNSServer(u)
				if err != nil {
					w.WriteHeader(http.StatusBadRequest)
					_ = json.NewEncoder(w).Encode(map[string]string{"error": fmt.Sprintf("invalid upstream DNS %q: %v", u, err)})
					return
				}
				cleanUpstreams = append(cleanUpstreams, valid)
			}

			timeout := s.cfg.ConnectTimeout
			if timeout <= 0 {
				timeout = 10 * time.Second
			}

			newBase := buildBaseResolver(cleanUpstreams, timeout)
			newRoutes, err := buildDNSRoutes(body.Routes, timeout)
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

			if err := s.persistConfigAtomic(); err != nil {
				s.logger.Error("failed to persist socks5 configuration", "error", err)
			}

			_ = json.NewEncoder(w).Encode(map[string]any{
				"status":     "ok",
				"message":    "DNS routing rules updated and persisted",
				"routeCount": len(newRoutes),
				"upstreams":  cleanUpstreams,
			})
			return
		}

		w.WriteHeader(http.StatusMethodNotAllowed)
	})

	mux.HandleFunc("/api/dns/flush", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if flusher, ok := s.resolver.(interface{ Flush() }); ok {
			flusher.Flush()
		}
		_ = json.NewEncoder(w).Encode(map[string]any{
			"status":  "ok",
			"message": "DNS cache flushed",
		})
	})

	mux.HandleFunc("/api/acl", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodPost {
			var body struct {
				Preset   string `json:"preset"`
				Subnets  string `json:"subnets"`
				Domains  string `json:"domains"`
				Clients  string `json:"clients"`
				AuthMode string `json:"authMode"`
				Username string `json:"username"`
				Password string `json:"password"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				w.WriteHeader(http.StatusBadRequest)
				_ = json.NewEncoder(w).Encode(map[string]string{"error": "invalid json payload"})
				return
			}
			if err := s.acl.Reload(body.Preset, body.Subnets, body.Domains, body.Clients); err != nil {
				w.WriteHeader(http.StatusBadRequest)
				_ = json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
				return
			}
			s.closeHTTPForwardIdleConnections()
			s.authMu.Lock()
			if body.Preset != "" {
				s.cfg.AclPreset = body.Preset
			}
			s.cfg.AllowedSubnets = body.Subnets
			s.cfg.AllowedDomains = body.Domains
			s.cfg.AllowedClients = body.Clients
			if body.AuthMode != "" {
				s.cfg.AuthMode = strings.ToLower(strings.TrimSpace(body.AuthMode))
				s.cfg.Username = body.Username
				s.cfg.Password = body.Password
			}
			s.authMu.Unlock()

			if err := s.persistConfigAtomic(); err != nil {
				s.logger.Error("failed to persist socks5 configuration", "error", err)
			}

			s.logger.Info("ACL & Auth rules reloaded and persisted via HTTP API",
				"preset", body.Preset,
				"subnets", body.Subnets,
				"domains", body.Domains,
				"clients", body.Clients,
				"authMode", body.AuthMode,
			)
		}
		_ = json.NewEncoder(w).Encode(s.acl.GetStats())
	})

	server := &http.Server{
		Addr:         fmt.Sprintf("127.0.0.1:%d", metricsPort),
		Handler:      mux,
		ReadTimeout:  5 * time.Second,
		WriteTimeout: 5 * time.Second,
	}

	go func() {
		_ = server.ListenAndServe()
	}()
}
