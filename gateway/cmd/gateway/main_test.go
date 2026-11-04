package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadEnvFile(t *testing.T) {
	p := filepath.Join(t.TempDir(), "gateway.env")
	os.WriteFile(p, []byte("# comment\n\nR8ER_T_A = from-file\nR8ER_T_B=from-file\nnot a pair\n"), 0o600)
	t.Setenv("R8ER_T_B", "from-env")
	os.Unsetenv("R8ER_T_A")
	t.Cleanup(func() { os.Unsetenv("R8ER_T_A") })
	loadEnvFile(p)
	if got := os.Getenv("R8ER_T_A"); got != "from-file" {
		t.Errorf("R8ER_T_A = %q, want from-file", got)
	}
	if got := os.Getenv("R8ER_T_B"); got != "from-env" {
		t.Errorf("R8ER_T_B = %q, want from-env (real env wins)", got)
	}
	loadEnvFile(filepath.Join(t.TempDir(), "missing")) // no-op, no panic
}
