//go:build linux

package jobs

import (
	"os"
	"path/filepath"
	"syscall"
)

// flock guards marker transitions between processes and is released by the
// kernel if a process dies. The guard inode must never be removed.
func lockAdmission(dir string) (func(), error) {
	f, err := os.OpenFile(filepath.Join(dir, "admission.guard"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err = syscall.Flock(int(f.Fd()), syscall.LOCK_EX); err != nil {
		f.Close()
		return nil, err
	}
	return func() { _ = syscall.Flock(int(f.Fd()), syscall.LOCK_UN); _ = f.Close() }, nil
}
