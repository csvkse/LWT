//go:build !linux

package jobs

// The durable task store is only supported on Linux; this permits local tests.
func syncDir(string) error { return nil }
