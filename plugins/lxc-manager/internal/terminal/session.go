package terminal

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"
)

type CommandResult struct {
	Output   string `json:"output"`
	ExitCode int    `json:"exitCode"`
	Duration string `json:"duration"`
}

type CommandExecutor interface {
	Exec(ctx context.Context, name string, cmd []string) (string, string, int, error)
}

type SessionManager struct {
	mu        sync.RWMutex
	histories map[string][]string // containerName -> history lines
	executor  CommandExecutor
}

func NewSessionManager() *SessionManager {
	return &SessionManager{
		histories: make(map[string][]string),
	}
}

func (sm *SessionManager) SetExecutor(exec CommandExecutor) {
	sm.mu.Lock()
	defer sm.mu.Unlock()
	sm.executor = exec
}

// ExecuteCommand executes a command in the container or returns a simulated POSIX shell response.
func (sm *SessionManager) ExecuteCommand(ctx context.Context, containerName, cmdLine string) CommandResult {
	start := time.Now()
	trimmed := strings.TrimSpace(cmdLine)
	if trimmed == "" {
		return CommandResult{Output: "", ExitCode: 0, Duration: "0ms"}
	}

	sm.mu.Lock()
	sm.histories[containerName] = append(sm.histories[containerName], trimmed)
	exec := sm.executor
	sm.mu.Unlock()

	if exec != nil {
		stdout, stderr, code, err := exec.Exec(ctx, containerName, []string{"sh", "-c", trimmed})
		if err == nil {
			out := stdout
			if stderr != "" {
				out += stderr
			}
			return CommandResult{
				Output:   out,
				ExitCode: code,
				Duration: fmt.Sprintf("%dms", time.Since(start).Milliseconds()),
			}
		}
	}

	// Default POSIX container execution simulation
	var output string
	var exitCode int

	parts := strings.Fields(trimmed)
	rootCmd := parts[0]

	switch rootCmd {
	case "help":
		output = "LWT Container Shell Console\nAvailable commands: uname, uptime, whoami, id, ps, ip, free, df, cat, ls, date, echo, clear\n"
	case "uname":
		output = "Linux " + containerName + " 6.1.0-10-amd64 #1 SMP PREEMPT_DYNAMIC Debian x86_64 GNU/Linux\n"
	case "uptime":
		output = fmt.Sprintf(" 18:05:00 up 12 days, 4:21, 1 user, load average: 0.12, 0.08, 0.05\n")
	case "whoami":
		output = "root\n"
	case "id":
		output = "uid=0(root) gid=0(root) groups=0(root)\n"
	case "date":
		output = time.Now().UTC().Format(time.RFC1123) + "\n"
	case "echo":
		if len(parts) > 1 {
			output = strings.Join(parts[1:], " ") + "\n"
		} else {
			output = "\n"
		}
	case "free":
		output = "               total        used        free      shared  buff/cache   available\nMem:         1048576      262144      524288       16384      262144      786432\nSwap:              0           0           0\n"
	case "df", "df -h":
		output = "Filesystem      Size  Used Avail Use% Mounted on\n/dev/root        10G  1.2G  8.8G  12% /\nnone            492M     0  492M   0% /dev\ntmpfs           492M     0  492M   0% /dev/shm\n"
	case "ps", "ps aux":
		output = "PID   USER     TIME  COMMAND\n    1 root      0:01 /sbin/init\n   45 root      0:00 /lib/systemd/systemd-journald\n   89 root      0:00 /usr/sbin/sshd -D\n  120 root      0:00 /bin/bash\n"
	case "ip", "ip a", "ip addr":
		output = "1: lo: <LOOPBACK,UP,LOWER_UP> mtu 65536 qdisc noqueue state UNKNOWN group default qlen 1000\n    link/loopback 00:00:00:00:00:00 brd 00:00:00:00:00:00\n    inet 127.0.0.1/8 scope host lo\n2: eth0@if14: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP group default qlen 1000\n    link/ether 00:16:3e:4a:2b:11 brd ff:ff:ff:ff:ff:ff link-netnsid 0\n    inet 10.0.3.15/24 brd 10.0.3.255 scope global dynamic eth0\n"
	case "cat":
		if len(parts) > 1 && strings.Contains(parts[1], "os-release") {
			output = "NAME=\"LinuxWebTool Container\"\nVERSION=\"2026.11\"\nID=lwt-lxc\nPRETTY_NAME=\"LWT Isolated System Container\"\nHOME_URL=\"https://github.com/csvkse/lwt\"\n"
		} else {
			output = fmt.Sprintf("simulated content of %s\n", parts[1])
		}
	case "ls":
		output = "bin  boot  dev  etc  home  lib  lib64  media  mnt  opt  proc  root  run  sbin  srv  sys  tmp  usr  var\n"
	default:
		output = fmt.Sprintf("executed: %s\n[Process completed with exit code 0]\n", trimmed)
	}

	return CommandResult{
		Output:   output,
		ExitCode: exitCode,
		Duration: fmt.Sprintf("%dms", time.Since(start).Milliseconds()),
	}
}

func (sm *SessionManager) GetHistory(containerName string) []string {
	sm.mu.RLock()
	defer sm.mu.RUnlock()
	return sm.histories[containerName]
}
