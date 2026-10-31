package web

import (
	_ "embed"
	"net/http"
)

//go:embed index.html
var IndexHTML []byte

// Handler returns an http.Handler that serves the self-contained management single-page web UI.
func Handler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(IndexHTML)
	})
}
