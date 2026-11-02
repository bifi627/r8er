// Package identity is the gateway's long-term DTLS identity: one persistent
// certificate whose Fingerprint browsers pin (design: specs/2026-11-01-r8er-
// agent-pairing-design.md, "Gateway certificate").
package identity

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/pion/webrtc/v4"
)

// LoadOrCreate returns the gateway's persistent DTLS certificate and its
// fingerprint. Generated once: an ephemeral cert would silently break every
// browser's pin on restart. Pion's GenerateCertificate expires in a month, so
// build our own 10-year cert.
func LoadOrCreate(dir string) (*webrtc.Certificate, string, error) {
	path := filepath.Join(dir, "cert.pem")
	pems, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if err != nil {
			return nil, "", err
		}
		serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 127))
		cert, err := webrtc.NewCertificate(key, x509.Certificate{
			SerialNumber: serial,
			Subject:      pkix.Name{CommonName: "r8er-gateway"},
			NotBefore:    time.Now().Add(-time.Hour),
			NotAfter:     time.Now().AddDate(10, 0, 0),
		})
		if err != nil {
			return nil, "", err
		}
		s, err := cert.PEM()
		if err != nil {
			return nil, "", err
		}
		if err := os.MkdirAll(dir, 0o700); err != nil {
			return nil, "", err
		}
		if err := os.WriteFile(path, []byte(s), 0o600); err != nil {
			return nil, "", err
		}
		pems = []byte(s)
	} else if err != nil {
		return nil, "", err
	}
	cert, err := webrtc.CertificateFromPEM(string(pems))
	if err != nil {
		return nil, "", fmt.Errorf("%s: %w", path, err)
	}
	block, _ := pem.Decode(pems) // Pion writes the CERTIFICATE block first
	return cert, Fingerprint(block.Bytes), nil
}

// Fingerprint is the contract form `sha-256 AA:BB:…` (docs/protocol.md).
func Fingerprint(der []byte) string {
	sum := sha256.Sum256(der)
	hex := make([]string, len(sum))
	for i, b := range sum {
		hex[i] = fmt.Sprintf("%02X", b)
	}
	return "sha-256 " + strings.Join(hex, ":")
}
