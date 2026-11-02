package state

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/config"
)

const (
	stateFileName       = "bridge-state.json"
	stateBackupFileName = "bridge-state.json.bak"
)

var (
	ErrRevisionConflict = errors.New("state revision conflict: concurrent modification detected")
	ErrConfigTooLarge   = errors.New("state exceeds maximum allowed size (4 MiB)")
)

// Store provides thread-safe, atomic, versioned persistence for Bridge state.
type Store struct {
	mu       sync.RWMutex
	stateDir string
	filePath string
	vault    *SecretsVault
	cached   config.State
}

// NewStore loads existing state or initializes a new state file.
func NewStore(stateDir string, vault *SecretsVault) (*Store, error) {
	if err := os.MkdirAll(stateDir, 0700); err != nil {
		return nil, fmt.Errorf("failed to create state directory: %w", err)
	}

	filePath := filepath.Join(stateDir, stateFileName)
	bakPath := filepath.Join(stateDir, stateBackupFileName)
	s := &Store{
		stateDir: stateDir,
		filePath: filePath,
		vault:    vault,
	}

	// 1. 尝试读取主状态文件
	if data, err := os.ReadFile(filePath); err == nil && len(data) > 0 {
		var loaded config.State
		if err := json.Unmarshal(data, &loaded); err == nil {
			if err := config.Validate(loaded); err == nil {
				s.cached = loaded
				return s, nil
			}
			// 尝试轻量自愈（剔除孤立引用、修复格式冲突等）
			if config.SanitizeAndHealState(&loaded) && config.Validate(loaded) == nil {
				if writeErr := s.writeAtomic(loaded); writeErr == nil {
					s.cached = loaded
					return s, nil
				}
			}
		}
		// 若主文件损坏但无法解析，保护性备份以防丢失原始配置现场
		corruptedBackup := fmt.Sprintf("%s.corrupted.%d", filePath, time.Now().Unix())
		_ = os.WriteFile(corruptedBackup, data, 0600)
	}

	// 2. 历史路径探测与平滑自愈迁移（处理历史相对路径遗留配置）
	migrationCandidates := []string{
		filepath.Join(stateDir, "mcp-bridge", stateFileName),
		filepath.Join("data", "mcp-bridge", stateFileName),
	}
	for _, cand := range migrationCandidates {
		if candData, err := os.ReadFile(cand); err == nil && len(candData) > 0 {
			var loaded config.State
			if err := json.Unmarshal(candData, &loaded); err == nil {
				if config.Validate(loaded) == nil || (config.SanitizeAndHealState(&loaded) && config.Validate(loaded) == nil) {
					if writeErr := s.writeAtomic(loaded); writeErr == nil {
						s.cached = loaded
						return s, nil
					}
				}
			}
		}
	}

	// 3. 容灾恢复：尝试从滚动备份 (.bak) 还原
	if bakData, err := os.ReadFile(bakPath); err == nil && len(bakData) > 0 {
		var loaded config.State
		if err := json.Unmarshal(bakData, &loaded); err == nil {
			if config.Validate(loaded) == nil || (config.SanitizeAndHealState(&loaded) && config.Validate(loaded) == nil) {
				if writeErr := s.writeAtomic(loaded); writeErr == nil {
					s.cached = loaded
					return s, nil
				}
			}
		}
	}

	// 4. 首次启动或全新初始化（仅在没有任何有效历史与备份时）
	initial := config.NewInitialState()
	if err := s.writeAtomic(initial); err != nil {
		return nil, fmt.Errorf("failed to initialize state file: %w", err)
	}
	s.cached = initial

	return s, nil
}

// Vault returns the secrets vault.
func (s *Store) Vault() *SecretsVault {
	return s.vault
}

// Read returns an independent snapshot of current state.
func (s *Store) Read(_ context.Context) (config.State, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.cached.DeepCopy()
}

// SaveDraft mutates draft state with optimistic concurrency check.
func (s *Store) SaveDraft(_ context.Context, expectedRevision uint64, mutate func(*config.State) error) (uint64, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if expectedRevision != 0 && s.cached.DraftRevision != expectedRevision {
		return s.cached.DraftRevision, fmt.Errorf("%w: expected draft revision %d, actual is %d",
			ErrRevisionConflict, expectedRevision, s.cached.DraftRevision)
	}

	candidate, err := s.cached.DeepCopy()
	if err != nil {
		return s.cached.DraftRevision, err
	}

	if err := mutate(&candidate); err != nil {
		return s.cached.DraftRevision, err
	}

	candidate.DraftRevision++
	candidate.DesiredRevision = candidate.DraftRevision

	if err := config.Validate(candidate); err != nil {
		return s.cached.DraftRevision, fmt.Errorf("draft state validation failed: %w", err)
	}

	if err := s.writeAtomic(candidate); err != nil {
		return s.cached.DraftRevision, err
	}

	s.cached = candidate
	return candidate.DraftRevision, nil
}

// Commit records an approved candidate version as active.
func (s *Store) Commit(_ context.Context, expectedRevision uint64, mutate func(*config.State) error) (uint64, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if expectedRevision != 0 && s.cached.DesiredRevision != expectedRevision {
		return s.cached.DesiredRevision, fmt.Errorf("%w: expected desired revision %d, actual is %d",
			ErrRevisionConflict, expectedRevision, s.cached.DesiredRevision)
	}

	candidate, err := s.cached.DeepCopy()
	if err != nil {
		return s.cached.DesiredRevision, err
	}

	if err := mutate(&candidate); err != nil {
		return s.cached.DesiredRevision, err
	}

	candidate.ActiveRevision = candidate.DesiredRevision
	candidate.LastAppliedAt = time.Now()

	if err := config.Validate(candidate); err != nil {
		return s.cached.DesiredRevision, fmt.Errorf("committed state validation failed: %w", err)
	}

	if err := s.writeAtomic(candidate); err != nil {
		return s.cached.DesiredRevision, err
	}

	s.cached = candidate
	return candidate.ActiveRevision, nil
}

func (s *Store) writeAtomic(state config.State) error {
	data, err := json.MarshalIndent(state, "", "  ")
	if err != nil {
		return err
	}

	if len(data) > config.MaxConfigEnvelopeBytes {
		return ErrConfigTooLarge
	}

	tmpPath := s.filePath + ".tmp"
	f, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil {
		return fmt.Errorf("failed to open tmp state file: %w", err)
	}

	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to write tmp state file: %w", err)
	}

	if err := f.Sync(); err != nil {
		_ = f.Close()
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to sync tmp state file: %w", err)
	}

	if err := f.Close(); err != nil {
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to close tmp state file: %w", err)
	}

	// 滚动快照：在覆盖主文件前，备份前一个健康版本为 .bak
	bakPath := filepath.Join(s.stateDir, stateBackupFileName)
	if existingData, err := os.ReadFile(s.filePath); err == nil && len(existingData) > 0 {
		_ = os.WriteFile(bakPath, existingData, 0600)
	}

	if err := os.Rename(tmpPath, s.filePath); err != nil {
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to atomic rename state file: %w", err)
	}

	return nil
}
