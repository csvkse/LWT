package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"syscall"
	"time"
)

type ControlHandshakeResponse struct {
	ProtocolVersion int    `json:"protocolVersion"`
	Status          string `json:"status"`
	ServiceName     string `json:"serviceName"`
	Version         string `json:"version"`
	RunID           string `json:"runId,omitempty"`
}

type ControlHealthResponse struct {
	Status string `json:"status"`
	Uptime int64  `json:"uptimeSeconds"`
}

type ControlInvokeResponse struct {
	Status string      `json:"status"`
	Result interface{} `json:"result,omitempty"`
	Error  string      `json:"error,omitempty"`
}

func main() {
	mode := flag.String("mode", "command", "Mode: command, resident, managed")
	action := flag.String("action", "", "Command action: convert, metrics, fail, echo")
	exitCode := flag.Int("exit-code", 1, "Exit code for fail action")
	artifactPath := flag.String("artifact", "", "Artifact file path to create")
	text := flag.String("text", "hello world", "Input text")
	progress := flag.Bool("progress", false, "Emit progress lines")
	port := flag.Int("port", 0, "Managed control port")
	flag.Parse()

	// Environment variable overrides
	if envPort := os.Getenv("LWT_CONTROL_PORT"); envPort != "" && *port == 0 {
		if p, err := strconv.Atoi(envPort); err == nil {
			*port = p
		}
	}
	controlToken := os.Getenv("LWT_CONTROL_TOKEN")
	runID := os.Getenv("LWT_RUN_ID")

	switch *mode {
	case "resident":
		runResidentMode()
	case "managed":
		runManagedMode(*port, controlToken, runID)
	case "command":
		runCommandMode(*action, *exitCode, *artifactPath, *text, *progress)
	default:
		fmt.Fprintf(os.Stderr, "Unknown mode: %s\n", *mode)
		os.Exit(2)
	}
}

func runResidentMode() {
	fmt.Println("Resident service started")
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)

	ticker := time.NewTicker(1 * time.Second)
	defer ticker.Stop()

	count := 0
	for {
		select {
		case <-sigChan:
			fmt.Println("Received termination signal, shutting down gracefully...")
			return
		case <-ticker.C:
			count++
			fmt.Printf("Resident heartbeat count=%d\n", count)
		}
	}
}

func runManagedMode(port int, expectedToken, runID string) {
	if port <= 0 {
		port = 19999
	}
	startTime := time.Now()

	mux := http.NewServeMux()

	authMiddleware := func(next http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			token := r.Header.Get("X-LWT-Control-Token")
			if expectedToken != "" && token != expectedToken {
				http.Error(w, "Unauthorized", http.StatusUnauthorized)
				return
			}
			reqRunID := r.Header.Get("X-LWT-Run-Id")
			if runID != "" && reqRunID != "" && reqRunID != runID {
				http.Error(w, "Run-Id mismatch", http.StatusConflict)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			next(w, r)
		}
	}

	server := &http.Server{
		Addr:    fmt.Sprintf("127.0.0.1:%d", port),
		Handler: mux,
	}

	mux.HandleFunc("/control/handshake", authMiddleware(func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(ControlHandshakeResponse{
			ProtocolVersion: 1,
			Status:          "ready",
			ServiceName:     "native-tool-fixture",
			Version:         "1.0.0",
			RunID:           runID,
		})
	}))

	mux.HandleFunc("/control/health", authMiddleware(func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(ControlHealthResponse{
			Status: "healthy",
			Uptime: int64(time.Since(startTime).Seconds()),
		})
	}))

	invokeHandler := func(w http.ResponseWriter, r *http.Request) {
		var req map[string]interface{}
		_ = json.NewDecoder(r.Body).Decode(&req)
		actionName := filepath.Base(r.URL.Path)
		if actionName == "invoke" || actionName == "/" {
			actionName = "default"
		}
		json.NewEncoder(w).Encode(ControlInvokeResponse{
			Status: "ok",
			Result: map[string]interface{}{"action": actionName, "echo": req},
		})
	}
	mux.HandleFunc("/control/invoke", authMiddleware(invokeHandler))
	mux.HandleFunc("/control/invoke/", authMiddleware(invokeHandler))

	mux.HandleFunc("/control/shutdown", authMiddleware(func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]string{"status": "shutting_down"})
		go func() {
			time.Sleep(100 * time.Millisecond)
			server.Close()
		}()
	}))

	fmt.Printf("Managed control server listening on http://127.0.0.1:%d\n", port)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		fmt.Fprintf(os.Stderr, "Server error: %v\n", err)
	}
}

func runCommandMode(action string, exitCode int, artifactPath, text string, progress bool) {
	if progress {
		fmt.Println(`{"type":"progress","progress":25,"message":"starting"}`)
		fmt.Println(`{"type":"progress","progress":50,"message":"working"}`)
		fmt.Println(`{"type":"progress","progress":100,"message":"done"}`)
	}

	switch action {
	case "convert":
		if artifactPath != "" {
			_ = os.MkdirAll(filepath.Dir(artifactPath), 0755)
			_ = os.WriteFile(artifactPath, []byte("Converted artifact: "+text), 0644)
		}
		fmt.Printf("Conversion completed for: %s\n", text)
	case "metrics":
		res := map[string]interface{}{
			"protocolVersion": 1,
			"status":          "succeeded",
			"data": map[string]interface{}{
				"cpu":    12.5,
				"memory": 256,
				"text":   text,
			},
		}
		b, _ := json.Marshal(res)
		fmt.Println(string(b))
	case "fail":
		fmt.Fprintf(os.Stderr, "Operation failed intentionally with exit code %d\n", exitCode)
		os.Exit(exitCode)
	case "echo":
		fmt.Println(text)
	default:
		fmt.Fprintf(os.Stderr, "Unknown action: %s\n", action)
		os.Exit(1)
	}
}
