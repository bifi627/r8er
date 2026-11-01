package identity

import (
	"regexp"
	"testing"
	"time"
)

// A pin is only useful if the agent presents the same cert after a restart.
func TestCertStableAcrossReload(t *testing.T) {
	dir := t.TempDir()
	c1, fp1, err := LoadOrCreate(dir)
	if err != nil {
		t.Fatal(err)
	}
	c2, fp2, err := LoadOrCreate(dir)
	if err != nil {
		t.Fatal(err)
	}
	if fp1 != fp2 || !c1.Equals(*c2) {
		t.Fatalf("fingerprint changed across reload: %s vs %s", fp1, fp2)
	}
	if !regexp.MustCompile(`^sha-256 ([0-9A-F]{2}:){31}[0-9A-F]{2}$`).MatchString(fp1) {
		t.Fatalf("fingerprint %q not in contract form", fp1)
	}
	if c1.Expires().Before(time.Now().AddDate(9, 0, 0)) {
		t.Fatalf("cert expires %s; want ~10 years (Pion's default is a month)", c1.Expires())
	}
}
