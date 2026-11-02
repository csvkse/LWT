package state_test

import (
	"context"
	"testing"

	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/state"
)

func TestSecretsVault(t *testing.T) {
	tempDir := t.TempDir()

	vault, err := state.OpenOrCreateSecretsVault(tempDir)
	if err != nil {
		t.Fatalf("failed to open secrets vault: %v", err)
	}

	secret := "sk-test-super-secret-12345"
	encrypted, err := vault.Encrypt(secret)
	if err != nil {
		t.Fatalf("failed to encrypt secret: %v", err)
	}

	if encrypted == secret {
		t.Error("encrypted secret must not equal plaintext")
	}

	decrypted, err := vault.Decrypt(encrypted)
	if err != nil {
		t.Fatalf("failed to decrypt secret: %v", err)
	}

	if decrypted != secret {
		t.Errorf("expected %q, got %q", secret, decrypted)
	}

	// Reopen existing vault
	reopened, err := state.OpenOrCreateSecretsVault(tempDir)
	if err != nil {
		t.Fatalf("failed to reopen vault: %v", err)
	}

	decrypted2, err := reopened.Decrypt(encrypted)
	if err != nil {
		t.Fatalf("failed to decrypt with reopened vault: %v", err)
	}

	if decrypted2 != secret {
		t.Errorf("expected %q, got %q", secret, decrypted2)
	}
}

func TestOwnerLock(t *testing.T) {
	tempDir := t.TempDir()

	lock1, err := state.AcquireOwnerLock(tempDir, "run-1")
	if err != nil {
		t.Fatalf("failed to acquire initial lock: %v", err)
	}

	// Second acquire by same/another instance while active should fail
	_, err = state.AcquireOwnerLock(tempDir, "run-2")
	if err == nil {
		t.Error("expected error acquiring second lock on same active directory")
	}

	// Release lock
	if err := lock1.Release(); err != nil {
		t.Fatalf("failed to release lock: %v", err)
	}

	// After release, acquiring lock again succeeds
	lock3, err := state.AcquireOwnerLock(tempDir, "run-3")
	if err != nil {
		t.Fatalf("failed to re-acquire lock after release: %v", err)
	}
	_ = lock3.Release()
}

func TestStorePersistenceAndRevisions(t *testing.T) {
	tempDir := t.TempDir()
	ctx := context.Background()

	vault, err := state.OpenOrCreateSecretsVault(tempDir)
	if err != nil {
		t.Fatalf("failed to create vault: %v", err)
	}

	store, err := state.NewStore(tempDir, vault)
	if err != nil {
		t.Fatalf("failed to create store: %v", err)
	}

	initial, err := store.Read(ctx)
	if err != nil {
		t.Fatalf("failed to read initial state: %v", err)
	}

	if initial.DraftRevision != 1 || initial.DesiredRevision != 1 || initial.ActiveRevision != 0 {
		t.Errorf("unexpected initial revisions: %+v", initial)
	}

	// Save draft modification
	rev, err := store.SaveDraft(ctx, 1, func(s *config.State) error {
		s.Tunnel.TunnelID = "tunnel_0123456789abcdef0123456789abcdef"
		s.Servers["test-srv"] = &config.BackendSpec{
			ID:        "test-srv",
			Name:      "Test Server",
			Transport: config.TransportStreamableHTTP,
			URL:       "http://127.0.0.1:9090/mcp",
		}
		return nil
	})
	if err != nil {
		t.Fatalf("failed to save draft: %v", err)
	}
	if rev != 2 {
		t.Errorf("expected revision 2, got %d", rev)
	}

	// Revision conflict test
	_, err = store.SaveDraft(ctx, 1, func(s *config.State) error {
		return nil
	})
	if err == nil {
		t.Error("expected revision conflict error when passing stale expectedRevision")
	}

	// Commit revision 2 to active
	activeRev, err := store.Commit(ctx, 2, func(s *config.State) error {
		return nil
	})
	if err != nil {
		t.Fatalf("failed to commit: %v", err)
	}
	if activeRev != 2 {
		t.Errorf("expected active revision 2, got %d", activeRev)
	}

	// Reopen store from disk and check state
	store2, err := state.NewStore(tempDir, vault)
	if err != nil {
		t.Fatalf("failed to reload store: %v", err)
	}

	reloaded, err := store2.Read(ctx)
	if err != nil {
		t.Fatalf("failed to read reloaded state: %v", err)
	}

	if reloaded.ActiveRevision != 2 || reloaded.Tunnel.TunnelID != "tunnel_0123456789abcdef0123456789abcdef" {
		t.Errorf("reloaded state mismatch: %+v", reloaded)
	}
}
