package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

type singleConnListener struct {
	conn      net.Conn
	accepted  bool
	closed    chan struct{}
	closeOnce sync.Once
}

func newSingleConnListener(conn net.Conn) *singleConnListener {
	return &singleConnListener{conn: conn, closed: make(chan struct{})}
}

func (l *singleConnListener) Accept() (net.Conn, error) {
	if !l.accepted {
		l.accepted = true
		return l.conn, nil
	}
	<-l.closed
	return nil, net.ErrClosed
}

func (l *singleConnListener) Close() error {
	l.closeOnce.Do(func() { close(l.closed) })
	return nil
}

func (l *singleConnListener) Addr() net.Addr { return l.conn.LocalAddr() }

type httpForwardResponseBufferPool struct{}

func (httpForwardResponseBufferPool) Get() []byte {
	if pooled, ok := bufPool.Get().(*[]byte); ok && pooled != nil && cap(*pooled) >= 32*1024 {
		return (*pooled)[:32*1024]
	}
	return make([]byte, 32*1024)
}

func (httpForwardResponseBufferPool) Put(buffer []byte) {
	if cap(buffer) < 32*1024 {
		return
	}
	buffer = buffer[:32*1024]
	bufPool.Put(&buffer)
}

func (s *Server) serveHTTPProxy(conn net.Conn) error {
	// Protocol detection has a short deadline. Let net/http apply a bounded
	// header-read timeout and leave body/response streaming unbounded.
	_ = conn.SetDeadline(time.Time{})
	listener := newSingleConnListener(conn)
	forwardProxy := s.newHTTPForwardReverseProxy()
	server := &http.Server{
		Handler: http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
			s.serveHTTPProxyRequest(w, req, listener, forwardProxy)
		}),
		ReadHeaderTimeout: 15 * time.Second,
		ErrorLog:          log.New(io.Discard, "", 0),
		ConnState: func(_ net.Conn, state http.ConnState) {
			if state == http.StateClosed {
				_ = listener.Close()
			}
		},
	}
	if err := server.Serve(listener); err != nil && !errors.Is(err, net.ErrClosed) && !errors.Is(err, http.ErrServerClosed) {
		return fmt.Errorf("serve HTTP proxy connection: %w", err)
	}
	return nil
}

func (s *Server) serveHTTPProxyRequest(w http.ResponseWriter, req *http.Request, listener *singleConnListener, forwardProxy *httputil.ReverseProxy) {
	if !s.authorizeHTTPProxyRequest(w, req) {
		return
	}
	if req.Method == http.MethodConnect {
		hijacker, ok := w.(http.Hijacker)
		if !ok {
			writeHTTPProxyResponse(w, http.StatusInternalServerError, "HTTP connection hijacking is unavailable.\n")
			return
		}
		conn, rw, err := hijacker.Hijack()
		if err != nil {
			return
		}
		// Keep any bytes net/http buffered after CONNECT (commonly the first TLS
		// ClientHello bytes) in front of the raw connection reader.
		bufferedConn := &BufferedConn{Conn: conn, r: rw.Reader}
		if err := s.handleHTTPConnect(bufferedConn, req); err != nil && s.logger != nil {
			s.logger.Debug("HTTP CONNECT session ended", "client", conn.RemoteAddr().String(), "error", err)
		}
		_ = conn.Close()
		_ = listener.Close()
		return
	}

	route, err := s.prepareHTTPForwardRoute(req)
	if err != nil {
		status := http.StatusBadGateway
		body := "Unable to resolve proxy target.\n"
		if errors.Is(err, ErrAclForbidden) {
			status = http.StatusForbidden
			body = "Target forbidden by ACL.\n"
		} else if errors.Is(err, errInvalidHTTPProxyTarget) {
			status = http.StatusBadRequest
			body = "Invalid proxy target.\n"
		}
		if req.Context().Err() == nil {
			writeHTTPProxyResponse(w, status, body)
		}
		if s.acl != nil {
			s.acl.RecordTargetResult(false)
		}
		return
	}

	queueCtx, cancelQueueWait := context.WithTimeout(req.Context(), s.effectiveHTTPQueueTimeout())
	permit, admissionErr := s.getHTTPForwardRequestLimiter().Acquire(queueCtx, net.JoinHostPort(route.host, strconv.Itoa(route.port)))
	cancelQueueWait()
	if admissionErr != nil {
		if req.Context().Err() != nil {
			return
		}
		status := http.StatusServiceUnavailable
		body := "HTTP upstream request capacity is full.\n"
		if errors.Is(admissionErr, context.DeadlineExceeded) {
			status = http.StatusGatewayTimeout
			body = "Timed out waiting for HTTP upstream request capacity.\n"
		}
		writeHTTPProxyResponse(w, status, body)
		return
	}
	defer permit.Release()

	ctx := context.WithValue(req.Context(), httpForwardRouteContextKey{}, route)
	forwardProxy.ServeHTTP(w, req.WithContext(ctx))
}

func (s *Server) authorizeHTTPProxyRequest(w http.ResponseWriter, req *http.Request) bool {
	mode, username, password := s.GetAuthConfig()
	if mode != "password" || validateBasicAuth(req.Header.Get("Proxy-Authorization"), username, password) {
		return true
	}
	if s.acl != nil {
		s.acl.RecordAuthFailure()
	}
	w.Header().Set("Proxy-Authenticate", `Basic realm="LWT Proxy"`)
	writeHTTPProxyResponse(w, http.StatusProxyAuthRequired, "Proxy authentication required.\n")
	return false
}

func writeHTTPProxyResponse(w http.ResponseWriter, status int, body string) {
	w.Header().Set("Connection", "close")
	http.Error(w, strings.TrimRight(body, "\r\n"), status)
}

func (s *Server) newHTTPForwardReverseProxy() *httputil.ReverseProxy {
	return &httputil.ReverseProxy{
		Transport:  s.getHTTPForwardTransport(),
		BufferPool: httpForwardResponseBufferPool{},
		Rewrite: func(proxyReq *httputil.ProxyRequest) {
			proxyReq.SetURL(&url.URL{Scheme: proxyReq.In.URL.Scheme, Host: proxyReq.In.URL.Host})
			proxyReq.Out.Host = proxyReq.In.Host
			// The old forwarder passed these end-to-end headers through. Rewrite
			// removes them by default, so copy them explicitly for compatibility.
			for key, values := range proxyReq.In.Header {
				lowerKey := strings.ToLower(key)
				if lowerKey != "forwarded" && !strings.HasPrefix(lowerKey, "x-forwarded-") {
					continue
				}
				proxyReq.Out.Header[key] = append([]string(nil), values...)
			}
			proxyReq.Out.Header.Del("Proxy-Authorization")
		},
		ErrorHandler: func(w http.ResponseWriter, req *http.Request, err error) {
			if req.Context().Err() != nil {
				return
			}
			status := http.StatusBadGateway
			body := "Upstream request failed.\n"
			if errors.Is(err, ErrAclForbidden) {
				status = http.StatusForbidden
				body = "Target forbidden by ACL.\n"
			}
			writeHTTPProxyResponse(w, status, body)
		},
	}
}

var _ net.Listener = (*singleConnListener)(nil)
