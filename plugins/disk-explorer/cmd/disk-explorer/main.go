package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"syscall"
	"time"

	"lwt/disk-explorer/internal/capabilities"
	"lwt/disk-explorer/internal/fsm"
	"lwt/disk-explorer/internal/server"
)

func main() {
	var (
		portFlag     int
		stateDirFlag string
	)

	fs := flag.NewFlagSet("disk-explorer", flag.ExitOnError)
	fs.IntVar(&portFlag, "port", 0, "HTTP listen port")
	fs.StringVar(&stateDirFlag, "state-dir", "", "State directory path")

	args := os.Args[1:]
	if len(args) > 0 && args[0] == "serve" {
		args = args[1:]
	}
	_ = fs.Parse(args)

	// 端口获取优先级：命令行参数 > 环境变量 PORT > 默认 18095
	port := portFlag
	if port <= 0 {
		if envPort := os.Getenv("PORT"); envPort != "" {
			if p, err := strconv.Atoi(envPort); err == nil && p > 0 {
				port = p
			}
		}
	}
	if port <= 0 {
		port = 18095
	}

	// 状态目录
	stateDir := stateDirFlag
	if stateDir == "" {
		stateDir = os.Getenv("LWT_STATE_DIR")
	}
	if stateDir == "" {
		stateDir = filepath.Join(os.TempDir(), "lwt-disk-explorer-state")
	}
	_ = os.MkdirAll(stateDir, 0755)

	// 平台内部密钥
	internalSecret := os.Getenv("LWT_INTERNAL_SECRET")

	// 查找 Webview UI 静态资产目录
	uiDir := resolveUIDir()

	log.Printf("[DiskExplorer] Starting Disk Space Explorer on port %d (StateDir: %s, UIDir: %s)", port, stateDir, uiDir)

	detector := capabilities.NewDetector()
	capReport := detector.Detect()
	log.Printf("[DiskExplorer] Capability probed: Container=%v, Privileged=%v, HostAccessible=%v, Engines=%v",
		capReport.IsContainer, capReport.IsPrivileged, capReport.CanAccessHost, capReport.InstalledEngines)

	taskManager := fsm.NewTaskManager()

	srv := server.NewServer(server.ServerOptions{
		Secret:   internalSecret,
		UIDir:    uiDir,
		Detector: detector,
		Manager:  taskManager,
	})

	httpServer := &http.Server{
		Addr:         fmt.Sprintf(":%d", port),
		Handler:      srv.Handler(),
		ReadTimeout:  60 * time.Second,
		WriteTimeout: 0, // SSE 长连接需要无写超时
		IdleTimeout:  120 * time.Second,
	}

	// 优雅退出监听
	stopChan := make(chan os.Signal, 1)
	signal.Notify(stopChan, os.Interrupt, syscall.SIGTERM)

	go func() {
		if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("[DiskExplorer] Server exited with error: %v", err)
		}
	}()

	log.Printf("[DiskExplorer] Service successfully listening on http://127.0.0.1:%d/", port)

	<-stopChan
	log.Printf("[DiskExplorer] Shutting down gracefully...")

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		log.Printf("[DiskExplorer] Forced shutdown error: %v", err)
	}
	log.Printf("[DiskExplorer] Server stopped.")
}

func resolveUIDir() string {
	candidates := []string{
		"ui",
		filepath.Join(".", "ui"),
		filepath.Join("plugins", "disk-explorer", "ui"),
	}

	// 检查可执行文件同级目录
	if exePath, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exePath)
		candidates = append(candidates, filepath.Join(exeDir, "ui"))
	}

	for _, c := range candidates {
		if fi, err := os.Stat(c); err == nil && fi.IsDir() {
			return c
		}
	}

	return "ui"
}
