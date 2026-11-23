import os
import unittest
from agentsleep import plan, snapshot

def path(name="virtual", adapter="a", id=2, available=True, active=False, tech=17):
    return dict(device_path=name, target_adapter=adapter, target_id=id, available=available,
                active=active, technology=tech)

class PlanTests(unittest.TestCase):
    def test_select_by_exact_device_path(self):
        physical = path("physical", id=1, active=True, tech=5)
        virtual = path("virtual-device")
        result = plan([physical, virtual], "VIRTUAL-DEVICE")
        self.assertEqual(result["selected"], virtual)
        self.assertEqual(result["baseline_active_display_paths"], [physical])
        self.assertEqual(result["would_remove_from_desktop"], [physical])
        self.assertTrue(result["dry_run"])
        self.assertFalse(result["ready_to_apply"])
        with self.assertRaisesRegex(ValueError, "not found"):
            plan([physical, virtual], "virtual")

    def test_unknown_or_physical_rejected(self):
        for tech in (5, 10, -1):
            with self.assertRaisesRegex(ValueError, "connector hint"):
                plan([path(tech=tech)], "virtual")

    def test_unavailable_target_rejected(self):
        with self.assertRaisesRegex(ValueError, "unavailable"):
            plan([path(available=False)], "virtual")

    def test_duplicate_device_identifier_rejected(self):
        with self.assertRaisesRegex(ValueError, "more than one target"):
            plan([path(), path(id=3)], "virtual")

    def test_ambiguous_sources_rejected(self):
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            plan([path(), path()], "virtual")

    def test_missing_active_baseline_rejected(self):
        with self.assertRaisesRegex(ValueError, "active desktop"):
            plan([path()], "virtual")

    def test_active_path_preferred_among_multiple_paths(self):
        active = path(active=True)
        result = plan([path(), active], "virtual")
        self.assertEqual(result["selected"], active)

    @unittest.skipUnless(os.name == "nt" and os.getenv("AGENTSLEEP_LIVE_READ") == "1", "opt in to read-only Windows integration test")
    def test_real_interactive_read_only(self):
        active = snapshot()
        self.assertTrue(active)
        self.assertTrue(all(p["active"] for p in active))
        self.assertTrue(any(p["desktop_mode"] for p in active))
        all_paths = snapshot(all_paths=True)
        self.assertTrue(len(all_paths) >= len(active))

if __name__ == "__main__":
    unittest.main()
