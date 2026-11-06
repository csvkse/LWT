//go:build linux

package jobs

import (
	"bufio"
	"fmt"
	"os"
	"os/exec"
	"testing"
	"time"
)

func TestAdmissionLockProcessHelper(t *testing.T) {
	dir := os.Getenv("LWT_TEST_LOCK_DIR")
	if dir == "" {
		return
	}
	fmt.Println("ready")
	unlock, err := lockAdmission(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer unlock()
	fmt.Println("acquired")
}

func TestAdmissionLockExcludesOtherProcess(t *testing.T) {
	dir := t.TempDir()
	unlock, err := lockAdmission(dir)
	if err != nil {
		t.Fatal(err)
	}
	locked := true
	defer func() {
		if locked {
			unlock()
		}
	}()
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(exe, "-test.run=^TestAdmissionLockProcessHelper$")
	cmd.Env = append(os.Environ(), "LWT_TEST_LOCK_DIR="+dir)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err = cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() {
		if cmd.ProcessState == nil {
			_ = cmd.Process.Kill()
			_ = cmd.Wait()
		}
	}()
	reader := bufio.NewReader(stdout)
	line, err := reader.ReadString('\n')
	if err != nil || line != "ready\n" {
		t.Fatalf("helper ready=%q err=%v", line, err)
	}
	acquired := make(chan string, 1)
	go func() { line, _ := reader.ReadString('\n'); acquired <- line }()
	select {
	case line := <-acquired:
		t.Fatalf("helper acquired while held: %q", line)
	case <-time.After(200 * time.Millisecond):
	}
	unlock()
	locked = false
	select {
	case line := <-acquired:
		if line != "acquired\n" {
			t.Fatalf("helper output=%q", line)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("helper did not acquire after release")
	}
	if err = cmd.Wait(); err != nil {
		t.Fatal(err)
	}
}
