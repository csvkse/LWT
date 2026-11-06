//go:build !linux

package jobs

import "sync"

// Non-Linux support is for local tests only, not a cross-process guarantee.
var admissionMutex sync.Mutex

func lockAdmission(string) (func(), error) { admissionMutex.Lock(); return admissionMutex.Unlock, nil }
