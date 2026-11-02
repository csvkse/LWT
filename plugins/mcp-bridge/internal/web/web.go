package web

import (
	"bytes"
	_ "embed"
	"net/http"
)

//go:embed index.html
var IndexHTML []byte

// Handler returns an http.Handler that serves the self-contained management single-page web UI without token injection.
func Handler() http.Handler {
	return HandlerWithToken("")
}

// HandlerWithToken returns an http.Handler that serves the UI with optional injected initial token.
func HandlerWithToken(token string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusMethodNotAllowed)
			_, _ = w.Write([]byte(`{"success":false,"message":"method not allowed"}`))
			return
		}

		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		if token != "" && bytes.Contains(IndexHTML, []byte("/*__INJECT_TOKEN__*/")) {
			injected := bytes.Replace(IndexHTML, []byte("/*__INJECT_TOKEN__*/"), []byte("window.__MCP_BRIDGE_TOKEN__ = "+`"`+token+`";`), 1)
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(injected)
			return
		}

		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(IndexHTML)
	})
}
