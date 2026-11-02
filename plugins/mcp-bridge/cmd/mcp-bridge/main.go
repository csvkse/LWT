package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/admin"
	"linuxwebtool.local/mcp-bridge/internal/runtime"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "version" {
		fmt.Println("mcp-bridge v1.0.0 (Go single-process OpenAI Tunnel / MCP Bridge)")
		return
	}

	// Flag definition
	fs := flag.NewFlagSet("serve", flag.ExitOnError)
	stateDir := fs.String("state-dir", "", "Path to private bridge state directory")
	tokenFile := fs.String("control-token-file", "", "Path to host-provided control token file")
	runID := fs.String("run-id", "", "Unique worker run execution identity")
	listenAddr := fs.String("listen", "127.0.0.1:0", "Numeric loopback listen address for control plane")

	args := os.Args[1:]
	if len(args) > 0 && args[0] == "serve" {
		args = args[1:]
	}
	_ = fs.Parse(args)

	*stateDir = resolveStateDir(*stateDir)
	_ = os.MkdirAll(*stateDir, 0700)

	if *runID == "" || *runID == "{runId}" {
		*runID = fmt.Sprintf("run-%d", time.Now().UnixNano())
	}

	if *listenAddr == "127.0.0.1:0" || *listenAddr == "{listenAddr}" || *listenAddr == "" {
		if envPort := os.Getenv("PORT"); envPort != "" {
			*listenAddr = "127.0.0.1:" + envPort
		}
	}

	var controlToken string
	if *tokenFile != "" && *tokenFile != "{controlTokenFile}" {
		data, err := os.ReadFile(*tokenFile)
		if err != nil {
			fmt.Fprintf(os.Stderr, "failed to read control token file %q: %v\n", *tokenFile, err)
			os.Exit(1)
		}
		controlToken = strings.TrimSpace(string(data))
	} else {
		// Fallback for standalone debugging or managed service profile
		controlToken = "dev-token-" + *runID
	}

	logger := slog.New(slog.NewJSONHandler(os.Stderr, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	}))

	logger.Info("starting mcp-bridge", "runId", *runID, "stateDir", *stateDir)

	auditLogger := admin.NewAuditLogger(*stateDir)

	// Construct Runtime
	rtCfg := runtime.Config{
		StateDir: *stateDir,
		RunID:    *runID,
		Listen:   *listenAddr,
		Logger:   logger,
		Audit:    auditLogger,
	}

	rt, err := runtime.NewRuntime(rtCfg)
	if err != nil {
		logger.Error("failed to initialize bridge runtime", "error", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// Setup HTTP server
	mux := http.NewServeMux()
	handler := admin.NewHandler(rt, controlToken, *runID, auditLogger, func() {
		logger.Info("admin shutdown requested")
		stop()
	})
	handler.RegisterRoutes(mux)

	// Validate listen address is strictly loopback
	if !strings.HasPrefix(*listenAddr, "127.0.0.1:") && !strings.HasPrefix(*listenAddr, "[::1]:") && *listenAddr != "localhost:0" {
		logger.Error("listen address must be a numeric loopback address (127.0.0.1:<port>)", "addr", *listenAddr)
		os.Exit(1)
	}

	listener, err := net.Listen("tcp", *listenAddr)
	if err != nil {
		logger.Error("failed to bind loopback listener", "addr", *listenAddr, "error", err)
		_ = rt.Stop(context.Background())
		os.Exit(1)
	}

	boundAddr := listener.Addr().String()
	fmt.Printf("CONTROL_LISTEN=%s\n", boundAddr)
	logger.Info("control listener ready", "addr", boundAddr)

	server := &http.Server{
		Handler:      mux,
		ReadTimeout:  15 * time.Second,
		WriteTimeout: 30 * time.Second,
	}

	serverErrCh := make(chan error, 1)
	go func() {
		if err := server.Serve(listener); err != nil && err != http.ErrServerClosed {
			serverErrCh <- err
		}
	}()

	// Fast boot ready: start background watchdog hydration and continuous self-healing loop
	if sup := rt.Supervisor(); sup != nil {
		sup.StartAsync(ctx)
	}

	// Wait for shutdown trigger
	select {
	case <-ctx.Done():
		logger.Info("shutting down on signal...")
	case err := <-serverErrCh:
		logger.Error("control server error", "error", err)
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	_ = server.Shutdown(shutdownCtx)
	_ = auditLogger.Close()
	if err := rt.Stop(shutdownCtx); err != nil {
		logger.Warn("errors stopping runtime", "error", err)
	}

	logger.Info("mcp-bridge terminated cleanly")
}

func resolveStateDir(raw string) string {
	raw = strings.TrimSpace(raw)
	// 1. 最高优先级：宿主环境变量标准注入
	if envState := strings.TrimSpace(os.Getenv("LWT_STATE_DIR")); envState != "" {
		return filepath.Clean(envState)
	}
	if envVol := strings.TrimSpace(os.Getenv("LWT_VOLUME_DIR")); envVol != "" {
		return filepath.Clean(filepath.Join(envVol, "state"))
	}
	if envData := strings.TrimSpace(os.Getenv("LWT_DATA_DIR")); envData != "" {
		return filepath.Clean(filepath.Join(envData, "state"))
	}

	// 2. 占位符或默认参数处理
	if raw == "" || raw == "{stateDir}" || raw == "{pluginDir}/data" || raw == "data/mcp-bridge" || raw == "data" {
		if exe, err := os.Executable(); err == nil {
			exeDir := filepath.Dir(exe)
			if _, err := os.Stat(filepath.Join(exeDir, "plugin.json")); err == nil {
				return filepath.Clean(filepath.Join(exeDir, "data"))
			}
		}
		cwd, _ := os.Getwd()
		return filepath.Clean(filepath.Join(cwd, "data"))
	}

	if strings.Contains(raw, "{pluginDir}") {
		if exe, err := os.Executable(); err == nil {
			raw = strings.ReplaceAll(raw, "{pluginDir}", filepath.Dir(exe))
		}
	}

	if filepath.IsAbs(raw) {
		return filepath.Clean(raw)
	}

	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		if _, err := os.Stat(filepath.Join(exeDir, "plugin.json")); err == nil {
			return filepath.Clean(filepath.Join(exeDir, raw))
		}
	}
	cwd, _ := os.Getwd()
	return filepath.Clean(filepath.Join(cwd, raw))
}
