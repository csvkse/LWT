import importlib.util
import pathlib
import unittest

path = pathlib.Path(__file__).with_name("dns_split_proxy_compare.py")
spec = importlib.util.spec_from_file_location("dns_split_proxy_compare", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DNSProxyComparisonTest(unittest.TestCase):
    def test_target_rejects_credentials_and_non_https(self):
        for url in ("http://example.com/a", "https://u:p@example.com/a", "https://example.com:444/a"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                module.parse_target(url)
        self.assertEqual(module.parse_target("https://example.com/a.js?q=1").hostname, "example.com")

    def test_summary_does_not_hide_partial_and_inconsistent_data(self):
        rows = [
            {"group": "default", "complete": True, "status": 200, "bytes": 4, "elapsedMs": 20, "sha256": "a"},
            {"group": "split", "complete": True, "status": 200, "bytes": 4, "elapsedMs": 40, "sha256": "b"},
            {"group": "split", "complete": False, "status": 200, "bytes": 2, "elapsedMs": 100},
        ]
        summary = module.summarize_results(rows)
        self.assertEqual(summary["groups"]["split"]["complete"], 1)
        self.assertEqual(summary["groups"]["split"]["partialBytes"], [2])
        self.assertFalse(summary["allHashesEqual"])

    def test_gate_requires_rules_and_never_ignores_hash_mismatch(self):
        good = {"default": {"rules": 0}, "split": {"rules": 1}}
        summary = {"allHashesEqual": False, "groups": {
            "default": {"complete": 1, "attempts": 1},
            "split": {"complete": 1, "attempts": 1}}}
        self.assertTrue(module.failed_gate([{"before": good, "after": good, "summary": summary}], True))
        summary["allHashesEqual"] = True
        summary["groups"]["split"]["complete"] = 0
        self.assertTrue(module.failed_gate([{"before": good, "after": good, "summary": summary}], False))
        self.assertFalse(module.failed_gate([{"before": good, "after": good, "summary": summary}], True))
        bad = {"default": {"rules": 0}, "split": {"rules": 0}}
        self.assertTrue(module.failed_gate([{"before": bad, "after": good, "summary": summary}], True))


if __name__ == "__main__":
    unittest.main()
