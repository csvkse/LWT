//go:build !windows

package state

import (
	"os"
	"syscall"
)

func checkProcessRunning(p *os.Process, pid int) bool {
	err := p.Signal(syscall.Signal(0))
	return err == nil
}
