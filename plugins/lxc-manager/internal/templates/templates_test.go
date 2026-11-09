package templates

import "testing"

func TestBuiltinTemplates(t *testing.T) {
	tmpl := BuiltinTemplates()
	if len(tmpl) < 4 {
		t.Fatalf("expected at least 4 templates, got %d", len(tmpl))
	}

	for _, item := range tmpl {
		if item.ID == "" || item.Name == "" || item.OS == "" {
			t.Errorf("template %s has empty fields", item.ID)
		}
		if item.RecommendedCPU < 1 || item.RecommendedMem < 256 || item.RecommendedDisk < 1 {
			t.Errorf("template %s has invalid resource recommendations", item.ID)
		}
	}
}

func TestBuiltinMirrors(t *testing.T) {
	mirrors := BuiltinMirrors()
	if len(mirrors) < 2 {
		t.Fatalf("expected at least 2 mirrors, got %d", len(mirrors))
	}
	defaultCount := 0
	for _, m := range mirrors {
		if m.IsDefault {
			defaultCount++
		}
	}
	if defaultCount != 1 {
		t.Errorf("expected exactly 1 default mirror, got %d", defaultCount)
	}
}
