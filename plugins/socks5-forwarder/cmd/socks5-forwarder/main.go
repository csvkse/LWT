package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"
)

func main() {
	configPath := flag.String("config", "config.json", "配置文件存储路径")
	webPort := flag.Int("port", 18090, "控制台与管理 API 监听端口")
	flag.Parse()

	log.Printf("==========================================================")
	log.Printf("SOCKS5 客户端边缘中继网关 (socks5-forwarder) v1.0.0 启动")
	log.Printf("==========================================================")

	// 1. 加载持久化配置
	cfg, err := LoadConfig(*configPath)
	if err != nil {
		log.Fatalf("加载配置失败: %v", err)
	}

	// 2. 初始化中继管理器并拉起所有已启用的 Profile
	mgr := NewProfileManager(cfg)
	mgr.StartAll()
	defer mgr.StopAll()

	// 3. 构建 HTTP REST 控制面与 UI 静态托管路由
	mux := http.NewServeMux()

	// API: 获取完整快照
	mux.HandleFunc("GET /api/profiles", func(w http.ResponseWriter, r *http.Request) {
		upstreams, inbounds := mgr.GetSnapshot()
		writeJSON(w, http.StatusOK, map[string]any{
			"upstreams": upstreams,
			"inbounds":  inbounds,
			"chains":    mgr.GetChains(),
		})
	})

	// API: 创建 / 修改入站规则
	mux.HandleFunc("POST /api/inbounds", func(w http.ResponseWriter, r *http.Request) {
		var p InboundProfile
		if err := json.NewDecoder(r.Body).Decode(&p); err != nil {
			writeError(w, http.StatusBadRequest, "解析入站配置 JSON 失败: "+err.Error())
			return
		}
		saved, err := mgr.UpsertInbound(p)
		if err != nil {
			writeError(w, http.StatusBadRequest, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, saved)
	})

	// API: 启停入站规则
	mux.HandleFunc("POST /api/inbounds/toggle", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		if id == "" {
			writeError(w, http.StatusBadRequest, "缺少必需的 id 参数")
			return
		}
		toggled, err := mgr.ToggleInbound(id)
		if err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, toggled)
	})

	// API: 删除入站规则
	mux.HandleFunc("DELETE /api/inbounds", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		if id == "" {
			writeError(w, http.StatusBadRequest, "缺少必需的 id 参数")
			return
		}
		if err := mgr.DeleteInbound(id); err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"message": "已成功删除入站规则"})
	})

	// API: 创建 / 修改上游节点
	mux.HandleFunc("POST /api/upstreams", func(w http.ResponseWriter, r *http.Request) {
		var node UpstreamNode
		if err := json.NewDecoder(r.Body).Decode(&node); err != nil {
			writeError(w, http.StatusBadRequest, "解析节点配置 JSON 失败: "+err.Error())
			return
		}
		saved, err := mgr.UpsertUpstream(node)
		if err != nil {
			writeError(w, http.StatusBadRequest, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, saved)
	})

	// API: 删除上游节点
	mux.HandleFunc("DELETE /api/upstreams", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		if id == "" {
			writeError(w, http.StatusBadRequest, "缺少必需的 id 参数")
			return
		}
		if err := mgr.DeleteUpstream(id); err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"message": "已成功删除上游节点"})
	})

	// API: 即时测速上游节点
	mux.HandleFunc("POST /api/upstreams/test", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		if id == "" {
			writeError(w, http.StatusBadRequest, "缺少必需的 id 参数")
			return
		}
		latency, err := mgr.TestUpstreamNode(id)
		if err != nil {
			writeJSON(w, http.StatusOK, map[string]any{
				"id":        id,
				"status":    "dead",
				"latencyMs": 0,
				"error":     err.Error(),
			})
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"id":        id,
			"status":    "healthy",
			"latencyMs": latency,
		})
	})

	// API: 解析标准协议链接 (vless, trojan, ss, socks5, http, vmess)
	mux.HandleFunc("POST /api/upstreams/parse-link", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			Link string `json:"link"`
			Text string `json:"text"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "解析请求参数失败: "+err.Error())
			return
		}

		if req.Text != "" {
			nodes, errs := ParseUpstreamLinks(req.Text)
			writeJSON(w, http.StatusOK, map[string]any{
				"nodes":  nodes,
				"errors": errs,
			})
			return
		}

		if req.Link == "" {
			writeError(w, http.StatusBadRequest, "缺少 link 或 text 参数")
			return
		}

		node, err := ParseUpstreamLink(req.Link)
		if err != nil {
			writeError(w, http.StatusBadRequest, "解析链接失败: "+err.Error())
			return
		}
		writeJSON(w, http.StatusOK, node)
	})

	// API: 获取内置分流规则模板库
	mux.HandleFunc("GET /api/routing-templates", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{
			"templates": GetBuiltinRoutingTemplates(),
		})
	})

	// API: 探查宿主容器 TUN 虚拟网卡与特权状态
	mux.HandleFunc("GET /api/system/tun-status", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, CheckTunEnvironment())
	})

	// API: 获取所有链式代理配置
	mux.HandleFunc("GET /api/chains", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{
			"chains": mgr.GetChains(),
		})
	})

	// API: 创建 / 修改链式代理配置
	mux.HandleFunc("POST /api/chains", func(w http.ResponseWriter, r *http.Request) {
		var c ProxyChainConfig
		if err := json.NewDecoder(r.Body).Decode(&c); err != nil {
			writeError(w, http.StatusBadRequest, "解析链式代理配置失败: "+err.Error())
			return
		}
		saved, err := mgr.UpsertChain(c)
		if err != nil {
			writeError(w, http.StatusBadRequest, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, saved)
	})

	// API: 删除链式代理配置
	mux.HandleFunc("DELETE /api/chains", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		if id == "" {
			writeError(w, http.StatusBadRequest, "缺少必需的 id 参数")
			return
		}
		if err := mgr.DeleteChain(id); err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"message": "已成功删除链式代理配置"})
	})

	// 静态 Webview UI 文件服务
	execPath, _ := os.Executable()
	baseDir := filepath.Dir(execPath)
	uiDir := filepath.Join(baseDir, "ui")
	if _, err := os.Stat(uiDir); os.IsNotExist(err) {
		uiDir = "ui" // 备用开发相对路径
	}
	fs := http.FileServer(http.Dir(uiDir))
	mux.Handle("/", fs)

	serverAddr := fmt.Sprintf("0.0.0.0:%d", *webPort)
	server := &http.Server{
		Addr:         serverAddr,
		Handler:      corsMiddleware(mux),
		ReadTimeout:  15 * time.Second,
		WriteTimeout: 15 * time.Second,
	}

	go func() {
		log.Printf("[ControlPlane] Web 控制台与 REST API 已就绪: http://%s", serverAddr)
		if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("控制面监听失败: %v", err)
		}
	}()

	// 4. 监听退出信号
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, syscall.SIGINT, syscall.SIGTERM)
	<-sigChan

	log.Printf("[Shutdown] 收到优雅退出信号，正在关闭服务...")
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer shutdownCancel()
	_ = server.Shutdown(shutdownCtx)
	log.Printf("[Shutdown] socks5-forwarder 退出完成")
}

func corsMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization, X-LWT-Token")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusOK)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(data)
}

func writeError(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}
