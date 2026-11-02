package state

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

const (
	keyFileName = "secrets.key"
	keyLength   = 32 // AES-256
)

// SecretsVault manages encryption and decryption of sensitive bridge tokens.
type SecretsVault struct {
	key []byte
}

// OpenOrCreateSecretsVault loads the master key or creates a new one exclusively.
func OpenOrCreateSecretsVault(stateDir string) (*SecretsVault, error) {
	if err := os.MkdirAll(stateDir, 0700); err != nil {
		return nil, fmt.Errorf("failed to create state directory %q: %w", stateDir, err)
	}

	keyPath := filepath.Join(stateDir, keyFileName)
	if data, err := os.ReadFile(keyPath); err == nil {
		if len(data) != keyLength {
			return nil, fmt.Errorf("invalid secrets master key length: expected %d, got %d", keyLength, len(data))
		}
		return &SecretsVault{key: data}, nil
	} else if !os.IsNotExist(err) {
		return nil, fmt.Errorf("failed to read secrets key: %w", err)
	}

	// 历史密钥自愈迁移探测：若在子目录或历史相对路径中发现既有密钥，自动迁移复用
	candidates := []string{
		filepath.Join(stateDir, "mcp-bridge", keyFileName),
		filepath.Join("data", "mcp-bridge", keyFileName),
	}
	for _, c := range candidates {
		if data, err := os.ReadFile(c); err == nil && len(data) == keyLength {
			if err := os.WriteFile(keyPath, data, 0600); err == nil {
				return &SecretsVault{key: data}, nil
			}
		}
	}

	// Create exclusively (0600)
	key := make([]byte, keyLength)
	if _, err := io.ReadFull(rand.Reader, key); err != nil {
		return nil, fmt.Errorf("failed to generate random master key: %w", err)
	}

	f, err := os.OpenFile(keyPath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return nil, fmt.Errorf("failed to create secrets key file exclusively: %w", err)
	}
	defer f.Close()

	if _, err := f.Write(key); err != nil {
		return nil, fmt.Errorf("failed to write secrets key: %w", err)
	}

	return &SecretsVault{key: key}, nil
}

// Encrypt encrypts plaintext using AES-256-GCM and returns standard Base64 string.
func (v *SecretsVault) Encrypt(plaintext string) (string, error) {
	if plaintext == "" {
		return "", nil
	}

	block, err := aes.NewCipher(v.key)
	if err != nil {
		return "", err
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", err
	}

	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return "", err
	}

	ciphertext := gcm.Seal(nonce, nonce, []byte(plaintext), nil)
	return base64.StdEncoding.EncodeToString(ciphertext), nil
}

// Decrypt decrypts a Base64-encoded ciphertext using AES-256-GCM.
func (v *SecretsVault) Decrypt(encoded string) (string, error) {
	if encoded == "" {
		return "", nil
	}

	data, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return "", fmt.Errorf("invalid base64 secret: %w", err)
	}

	block, err := aes.NewCipher(v.key)
	if err != nil {
		return "", err
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", err
	}

	nonceSize := gcm.NonceSize()
	if len(data) < nonceSize {
		return "", errors.New("ciphertext too short")
	}

	nonce, ciphertext := data[:nonceSize], data[nonceSize:]
	plaintext, err := gcm.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return "", fmt.Errorf("failed to decrypt secret: %w", err)
	}

	return string(plaintext), nil
}
