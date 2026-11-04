package main

import (
	"testing"
)

func TestGetBuiltinRoutingTemplates(t *testing.T) {
	templates := GetBuiltinRoutingTemplates()
	if len(templates) < 5 {
		t.Fatalf("expected at least 5 templates, got %d", len(templates))
	}

	tplBalanced := GetRoutingTemplateByID("tpl_balanced")
	if tplBalanced == nil {
		t.Fatalf("tpl_balanced not found")
	}
	if !tplBalanced.DirectPrivateNetwork {
		t.Errorf("expected DirectPrivateNetwork true")
	}
	if tplBalanced.DefaultAction != "proxy" {
		t.Errorf("expected DefaultAction proxy, got %s", tplBalanced.DefaultAction)
	}
	if len(tplBalanced.Rules) == 0 {
		t.Errorf("expected rules in balanced template")
	}

	tplAi := GetRoutingTemplateByID("tpl_ai_developer")
	if tplAi == nil || len(tplAi.Rules) == 0 {
		t.Fatalf("tpl_ai_developer invalid")
	}

	notFound := GetRoutingTemplateByID("non_existent")
	if notFound != nil {
		t.Errorf("expected nil for non_existent template")
	}
}
