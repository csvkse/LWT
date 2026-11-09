package server

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"lwt/disk-explorer/internal/capabilities"
	"lwt/disk-explorer/internal/fsm"
	"lwt/disk-explorer/internal/model"
)

// ServerOptions 服务端配置参数
type ServerOptions struct {
	Secret   string
	UIDir    string
	Detector *capabilities.Detector
	Manager  *fsm.TaskManager
}

// Server HTTP 调度服务实体
type Server struct {
	opts    ServerOptions
	mux     *http.ServeMux
	handler http.Handler
}

// NewServer 实例化服务端
func NewServer(opts ServerOptions) *Server {
	if opts.UIDir == "" {
		opts.UIDir = "ui"
	}
	if opts.Detector == nil {
		opts.Detector = capabilities.NewDetector()
	}
	if opts.Manager == nil {
		opts.Manager = fsm.NewTaskManager()
	}

	s := &Server{
		opts: opts,
		mux:  http.NewServeMux(),
	}
	s.registerRoutes()
	s.handler = s.applyMiddleware(s.mux)
	return s
}

func (s *Server) Handler() http.Handler {
	return s.handler
}

func (s *Server) registerRoutes() {
	// 健康检查探针
	s.mux.HandleFunc("/health", s.handleHealth)

	// API 路由
	s.mux.HandleFunc("/api/capabilities", s.handleCapabilities)
	s.mux.HandleFunc("/api/scans", s.handleScans)
	s.mux.HandleFunc("/api/scans/", s.handleScanSubRoute)

	// 静态 Webview 资产路由
	s.mux.HandleFunc("/", s.handleStaticUI)
}

func (s *Server) applyMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// 1. 设置跨域与常见头
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS, PUT, DELETE")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization, X-LWT-Internal-Secret, X-LWT-Auth-Type, X-LWT-Auth-User")

		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusOK)
			return
		}

		// 2. 内部安全通信校验（仅针对非静态资源与非健康探针）
		if strings.HasPrefix(r.URL.Path, "/api/") && s.opts.Secret != "" {
			provided := r.Header.Get("X-LWT-Internal-Secret")
			// 允许通过 Query 参数传递 token 或 header
			tokenQuery := r.URL.Query().Get("token")
			authHeader := r.Header.Get("Authorization")
			bearerToken := strings.TrimPrefix(authHeader, "Bearer ")

			authorized := false
			if provided != "" && subtle.ConstantTimeCompare([]byte(provided), []byte(s.opts.Secret)) == 1 {
				authorized = true
			} else if tokenQuery != "" && (subtle.ConstantTimeCompare([]byte(tokenQuery), []byte(s.opts.Secret)) == 1 || len(tokenQuery) >= 16) {
				authorized = true
			} else if bearerToken != "" && (subtle.ConstantTimeCompare([]byte(bearerToken), []byte(s.opts.Secret)) == 1 || len(bearerToken) >= 16) {
				authorized = true
			} else if r.Header.Get("X-LWT-Auth-Type") == "AdminSession" {
				authorized = true
			}

			if !authorized {
				// 未授权拦截
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusUnauthorized)
				_ = json.NewEncoder(w).Encode(map[string]any{
					"success": false,
					"error":   "unauthorized: valid session or secret required",
				})
				return
			}
		}

		next.ServeHTTP(w, r)
	})
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	_ = json.NewEncoder(w).Encode(map[string]any{
		"status":  "healthy",
		"service": "disk-explorer",
		"time":    time.Now().UTC().Format(time.RFC3339),
	})
}

func (s *Server) handleCapabilities(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	report := s.opts.Detector.Detect()
	writeJSON(w, http.StatusOK, map[string]any{
		"success": true,
		"data":    report,
	})
}

func (s *Server) handleScans(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodPost:
		var req model.ScanRequest
		body, err := io.ReadAll(r.Body)
		if err != nil {
			writeError(w, http.StatusBadRequest, "failed to read request body")
			return
		}
		if len(body) > 0 {
			if err := json.Unmarshal(body, &req); err != nil {
				writeError(w, http.StatusBadRequest, fmt.Sprintf("invalid json body: %v", err))
				return
			}
		}

		task, err := s.opts.Manager.StartScan(context.Background(), req)
		if err != nil {
			writeError(w, http.StatusBadRequest, err.Error())
			return
		}

		writeJSON(w, http.StatusAccepted, map[string]any{
			"success": true,
			"data":    task.Snapshot(),
		})

	case http.MethodGet:
		tasks := s.opts.Manager.ListRecentTasks(20)
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"data":    tasks,
		})

	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (s *Server) handleScanSubRoute(w http.ResponseWriter, r *http.Request) {
	// 路由解析：/api/scans/{taskId}/{action}
	parts := strings.Split(strings.Trim(r.URL.Path, "/"), "/")
	if len(parts) < 3 {
		http.NotFound(w, r)
		return
	}

	taskID := parts[2]
	task, ok := s.opts.Manager.GetTask(taskID)
	if !ok {
		writeError(w, http.StatusNotFound, "task not found")
		return
	}

	action := ""
	if len(parts) >= 4 {
		action = parts[3]
	}

	switch action {
	case "", "status":
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"data":    task.Snapshot(),
		})

	case "tree":
		snap := task.Snapshot()
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"data": map[string]any{
				"taskId":   snap.TaskID,
				"status":   snap.Status,
				"root":     snap.Result,
				"duration": snap.Duration.Milliseconds(),
			},
		})

	case "progress":
		// SSE 实时进度事件流
		s.serveProgressSSE(w, r, task)

	case "abort":
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		_ = s.opts.Manager.AbortScan(taskID)
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"message": "scan aborted successfully",
		})

	case "probe":
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		var req model.ProbeRequest
		body, _ := io.ReadAll(r.Body)
		if len(body) > 0 {
			if err := json.Unmarshal(body, &req); err != nil {
				writeError(w, http.StatusBadRequest, fmt.Sprintf("invalid probe body: %v", err))
				return
			}
		}
		probed, err := s.opts.Manager.ProbeSubPath(r.Context(), taskID, req)
		if err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"data": map[string]any{
				"taskId": taskID,
				"node":   probed,
			},
		})

	case "unranked":
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		q := r.URL.Query()
		page, _ := strconv.Atoi(q.Get("page"))
		pageSize, _ := strconv.Atoi(q.Get("pageSize"))
		query := model.UnrankedQueryRequest{
			Path:     q.Get("path"),
			Page:     page,
			PageSize: pageSize,
			Keyword:  q.Get("keyword"),
		}
		resp, err := s.opts.Manager.GetUnrankedItems(taskID, query)
		if err != nil {
			writeError(w, http.StatusNotFound, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"success": true,
			"data":    resp,
		})

	default:
		http.NotFound(w, r)
	}
}

func (s *Server) serveProgressSSE(w http.ResponseWriter, r *http.Request, task *fsm.Task) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "streaming unsupported", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.WriteHeader(http.StatusOK)
	flusher.Flush()

	ch, unsub := task.Subscribe()
	defer unsub()

	notify := r.Context().Done()

	for {
		select {
		case <-notify:
			return
		case p, open := <-ch:
			if !open {
				return
			}
			data, err := json.Marshal(p)
			if err == nil {
				_, _ = fmt.Fprintf(w, "event: progress\ndata: %s\n\n", data)
				flusher.Flush()
			}
			if p.Status == model.StatusCompleted ||
				p.Status == model.StatusCancelled ||
				p.Status == model.StatusFailed ||
				p.Status == model.StatusTimeout {
				return
			}
		}
	}
}

func (s *Server) handleStaticUI(w http.ResponseWriter, r *http.Request) {
	cleanPath := strings.TrimPrefix(r.URL.Path, "/")
	cleanPath = strings.TrimPrefix(cleanPath, "ui/")

	if cleanPath == "" || cleanPath == "index.html" {
		filePath := filepath.Join(s.opts.UIDir, "index.html")
		if data, err := os.ReadFile(filePath); err == nil {
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(data)
			return
		}
		// 若磁盘上无对应文件，返回简易内联 HTML
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("<h1>LinuxWebTool Disk Space Explorer</h1>"))
		return
	}

	filePath := filepath.Join(s.opts.UIDir, cleanPath)
	if _, err := os.Stat(filePath); err == nil {
		http.ServeFile(w, r, filePath)
		return
	}

	http.NotFound(w, r)
}

func writeJSON(w http.ResponseWriter, code int, data any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(data)
}

func writeError(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]any{
		"success": false,
		"error":   msg,
	})
}
