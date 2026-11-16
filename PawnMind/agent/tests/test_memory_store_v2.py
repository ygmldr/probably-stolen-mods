"""P2 first half: standalone memory store (schema, evidence, scope, FTS, seeds).

Every database here is created under a temporary directory.  Nothing in this
file touches the live recording databases (``play/memory/memory.db`` or
``play/memory/memories.db``), the game bridge, a real save, or
``classification.py``.

Architecture under test: the events DB stays schema v1 and untouched; the
memory store owns a separate file and attaches the events DB read-only when it
exists.  Scope is derived from the identity columns (there is no standalone
``scope`` column): ``run_id`` -> run scope (save + run + timeline must match),
``save_id`` -> save scope (save must match), otherwise version/global scope
(filtered by ``game_version`` when supplied).
"""

import io
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.store import EventStore, MemoryUnavailable  # noqa: E402
from agent.memory.memories import (  # noqa: E402
    DEFAULT_MEMORIES_DB,
    EVIDENCE_GRADES,
    KINDS,
    MEMORY_SCHEMA_VERSION,
    MemoryStore,
    MemoryValidationError,
    STATUS_CANDIDATE,
    STATUS_CONFLICT,
    STATUS_DEPRECATED,
    STATUS_SUPERSEDED,
    STATUS_VERIFIED,
)

VERSION = "VERSION 100-Rev13"
OLD_VERSION = "VERSION 100-Rev12"
GAME_ID = "probably_stolen"
SEED_MODULE = "agent.memory.seed"


class SplitStoreTestBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.events_path = os.path.join(self.tmp.name, "memory.db")
        self.memories_path = os.path.join(self.tmp.name, "memories.db")
        self.events_store = EventStore(self.events_path)
        self.tl = self._session("sess-a", VERSION, "run-a", 40)
        self.other_tl = self._session("sess-b", VERSION, "run-b", 41)
        self.old_tl = self._session("sess-old", OLD_VERSION, "run-old", 40)
        self.memory_store = MemoryStore(
            self.memories_path, events_db=self.events_path)

    def _session(self, session_id, version, run_id, slot):
        timeline_id = self.events_store.start_session(
            agent_session_id=session_id, game_version=version,
            game_mvid="mvid", instance_id="local-01").timeline_id
        self.events_store.attach_identity(
            timeline_id, run_id=run_id, slot=slot, run_number=1)
        return timeline_id

    def tearDown(self):
        try:
            self.memory_store.close()
            self.events_store.close()
        finally:
            self.tmp.cleanup()

    def reopen_memory_store(self, events_db="same"):
        self.memory_store.close()
        if events_db == "same":
            events_db = self.events_path
        self.memory_store = MemoryStore(self.memories_path,
                                        events_db=events_db)
        return self.memory_store

    def event(self, timeline_id=None, *, event_type="observation",
              tool_name="get_state", result_status="success",
              capture_quality="complete", result=None, game_day=1):
        return self.events_store.append_event(
            timeline_id or self.tl, event_type=event_type,
            tool_name=tool_name, result_status=result_status,
            result={"ok": True} if result is None else result,
            capture_quality=capture_quality, game_day=game_day)

    def scope(self, **overrides):
        base = {
            "game_id": GAME_ID,
            "game_version": VERSION,
            "save_id": "slot:40",
            "run_id": "run-a",
            "timeline_id": self.tl,
        }
        base.update(overrides)
        return base

    def add(self, memory_id="mem_test", *, kind="warning", text="test rule",
            evidence=None, target="candidate", **overrides):
        kwargs = {
            "game_version": VERSION,
            "save_id": "slot:40",
            "run_id": "run-a",
            "timeline_id": self.tl,
        }
        kwargs.update(overrides)
        evidence = [self.event()] if evidence is None else evidence
        result = self.memory_store.add_candidate(
            kind=kind, text=text, evidence=evidence, memory_id=memory_id,
            **kwargs)
        self.assertTrue(result.get("ok"), result)
        if target == "verified":
            result = self.memory_store.verify(memory_id, actor="user:test")
            self.assertTrue(result.get("ok"), result)
        return result

    def ids(self, rows):
        return [row["memory_id"] for row in rows]


# --------------------------------------------------------------- split schema


class SplitSchemaTests(SplitStoreTestBase):
    def test_events_database_stays_schema_v1_without_memory_tables(self):
        self.assertEqual(1, self.events_store.schema_version())
        names = self.events_store.table_names()
        for forbidden in ("memories", "memory_evidence", "memory_revisions",
                          "memories_fts"):
            self.assertNotIn(forbidden, names)
        self.assertNotIn("memories", self.events_store.table_names())

    def test_memories_database_is_independent_and_has_fts(self):
        self.assertNotEqual(self.events_path, self.memories_path)
        self.assertEqual(MEMORY_SCHEMA_VERSION, 2)
        self.assertEqual(2, self.memory_store.schema_version())
        names = self.memory_store.table_names()
        for expected in ("memories", "memory_evidence", "memory_revisions",
                         "memories_fts"):
            self.assertIn(expected, names)
        self.assertNotIn("events", names)
        self.assertNotIn("timelines", names)
        self.assertIn("meta", names)

    def test_default_memories_path_is_separate_from_the_events_db(self):
        self.assertTrue(DEFAULT_MEMORIES_DB.endswith(
            os.path.join("play", "memory", "memories.db")))
        self.assertFalse(DEFAULT_MEMORIES_DB.endswith(
            os.path.join("play", "memory", "memory.db")))

    def test_memories_schema_is_idempotent_on_reopen(self):
        self.add("mem_reopen", kind="fact", text="survives reopen",
                 target="verified")
        self.reopen_memory_store()
        self.assertEqual(2, self.memory_store.schema_version())
        self.assertEqual("survives reopen",
                         self.memory_store.get_memory("mem_reopen")["text"])
        self.assertIn("mem_reopen", self.ids(self.memory_store.search(
            "survives", scope=self.scope())))

    def test_unknown_memory_schema_version_is_rejected(self):
        self.memory_store.close()
        conn = sqlite3.connect(self.memories_path)
        conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
        conn.commit()
        conn.close()
        with self.assertRaises(MemoryUnavailable):
            MemoryStore(self.memories_path, events_db=self.events_path)
        # leave the fixture closed; tearDown handles a closed store

    def test_memory_writes_do_not_change_the_events_database(self):
        evidence = self.event()
        before = self.events_store.count_events(self.tl)
        self.add("mem_side_effects", evidence=[evidence], target="verified")
        self.assertEqual(before, self.events_store.count_events(self.tl))
        self.assertEqual(1, self.events_store.schema_version())


# ------------------------------------------------------------ cross-db attach


class AttachValidationTests(SplitStoreTestBase):
    def test_attached_events_are_validated_and_grade_derived(self):
        observed = self.event()
        reported = self.event(event_type="intent", tool_name="confirm_trade",
                              capture_quality="partial")
        first = self.memory_store.add_candidate(
            kind="warning", text="observed rule", evidence=[observed],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_observed")
        second = self.memory_store.add_candidate(
            kind="warning", text="reported rule", evidence=[reported],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_reported_grade")
        self.assertEqual(0, first["evidence_unverified"])
        self.assertEqual("observed", first["evidence"][0]["evidence_grade"])
        self.assertEqual(0, second["evidence_unverified"])
        self.assertEqual("reported", second["evidence"][0]["evidence_grade"])

    def test_missing_event_with_attached_events_db_is_rejected(self):
        result = self.memory_store.add_candidate(
            kind="fact", text="points at a missing event",
            evidence=["ev_does_not_exist"], game_version=VERSION,
            timeline_id=self.tl)
        self.assertFalse(result["ok"])
        self.assertEqual("no_evidence", result["reason"])
        self.assertIn("ev_does_not_exist", result["missing_event_ids"])
        self.assertEqual(0, self.memory_store.status()["memories"])

    def test_missing_events_database_degrades_to_unverified(self):
        self.memory_store.close()
        self.memory_store = MemoryStore(
            self.memories_path,
            events_db=os.path.join(self.tmp.name, "does-not-exist.db"))
        result = self.memory_store.add_candidate(
            kind="fact", text="standalone memory", evidence=["ev_ghost"],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_standalone")
        self.assertTrue(result["ok"], result)
        self.assertEqual(1, result["evidence_unverified"])
        self.assertEqual("unknown", result["evidence"][0]["evidence_grade"])
        blocked = self.memory_store.verify("mem_standalone")
        self.assertFalse(blocked["ok"])
        self.assertEqual("unverifiable", blocked["reason"])
        self.assertIn("evidence_unverified", blocked["detail"])

        # Reopening with the events DB present does not silently bless it.
        self.reopen_memory_store(events_db=self.events_path)
        blocked = self.memory_store.verify("mem_standalone")
        self.assertFalse(blocked["ok"])
        self.assertEqual("unverifiable", blocked["reason"])

    def test_no_events_db_configuration_degrades_to_unverified(self):
        self.memory_store.close()
        self.memory_store = MemoryStore(self.memories_path, events_db=None)
        result = self.memory_store.add_candidate(
            kind="fact", text="no events configured", evidence=["ev_ghost"],
            game_version=VERSION, timeline_id=self.tl,
            memory_id="mem_no_events")
        self.assertTrue(result["ok"], result)
        self.assertEqual(1, result["evidence_unverified"])
        blocked = self.memory_store.verify("mem_no_events")
        self.assertFalse(blocked["ok"])
        self.assertEqual("unverifiable", blocked["reason"])

    def test_events_database_is_attached_read_only(self):
        before = self.events_store.count_events(self.tl)
        with self.memory_store._attached_events() as attached:
            self.assertTrue(attached)
            with self.assertRaises(sqlite3.OperationalError):
                self.memory_store._require().execute(
                    "INSERT INTO events.events(event_id, timeline_id, seq,"
                    " event_type, occurred_at)"
                    " VALUES('ev_readonly_probe', ?, 9999, 'observation',"
                    " '2026-11-01T00:00:00Z')", (self.tl,))
        self.assertEqual(before, self.events_store.count_events(self.tl))

    def test_revalidate_clears_unverified_flag_when_events_db_returns(self):
        evidence = self.event()
        # Store the memory while the events DB is deliberately unavailable.
        self.memory_store.close()
        self.memory_store = MemoryStore(self.memories_path, events_db=None)
        created = self.memory_store.add_candidate(
            kind="warning", text="revalidate me", evidence=[evidence],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_revalidate")
        self.assertEqual(1, created["evidence_unverified"])
        self.assertEqual("unknown", created["evidence"][0]["evidence_grade"])

        # The events DB reappears: explicit revalidation clears the flag.
        self.reopen_memory_store(events_db=self.events_path)
        revalidated = self.memory_store.revalidate_evidence("mem_revalidate")
        self.assertTrue(revalidated["ok"], revalidated)
        self.assertEqual(0, revalidated["evidence_unverified"])
        self.assertEqual("observed",
                         revalidated["evidence"][0]["evidence_grade"])
        verified = self.memory_store.verify("mem_revalidate",
                                           actor="user:alice")
        self.assertTrue(verified["ok"], verified)

    def test_revalidate_reports_missing_events_without_clearing_the_flag(self):
        self.memory_store.close()
        self.memory_store = MemoryStore(self.memories_path, events_db=None)
        created = self.memory_store.add_candidate(
            kind="fact", text="ghost evidence", evidence=["ev_still_missing"],
            game_version=VERSION, timeline_id=self.tl,
            memory_id="mem_ghost_evidence")
        self.assertEqual(1, created["evidence_unverified"])
        self.reopen_memory_store(events_db=self.events_path)
        result = self.memory_store.revalidate_evidence("mem_ghost_evidence")
        self.assertFalse(result["ok"])
        self.assertEqual("no_evidence", result["reason"])
        self.assertIn("ev_still_missing", result["missing_event_ids"])
        self.assertEqual(
            1, self.memory_store.get_memory("mem_ghost_evidence")
            ["evidence_unverified"])

    def test_foreign_sqlite_file_degrades_instead_of_failing(self):
        foreign = os.path.join(self.tmp.name, "unrelated.db")
        conn = sqlite3.connect(foreign)
        conn.execute("CREATE TABLE unrelated(x INTEGER)")
        conn.commit()
        conn.close()
        self.memory_store.close()
        self.memory_store = MemoryStore(self.memories_path, events_db=foreign)
        result = self.memory_store.add_candidate(
            kind="fact", text="foreign events database",
            evidence=["ev_whatever"], game_version=VERSION,
            timeline_id=self.tl, memory_id="mem_foreign_events")
        self.assertTrue(result["ok"], result)
        self.assertEqual(1, result["evidence_unverified"])
        blocked = self.memory_store.verify("mem_foreign_events")
        self.assertFalse(blocked["ok"])
        self.assertEqual("unverifiable", blocked["reason"])

    def test_explicit_grade_does_not_clear_the_unverified_flag(self):
        self.memory_store.close()
        self.memory_store = MemoryStore(self.memories_path, events_db=None)
        result = self.memory_store.add_candidate(
            kind="fact", text="trust me", evidence=[
                {"event_id": "ev_ghost", "evidence_grade": "observed"}],
            game_version=VERSION, timeline_id=self.tl,
            memory_id="mem_claimed_observed")
        self.assertEqual(1, result["evidence_unverified"])
        self.assertEqual("observed", result["evidence"][0]["evidence_grade"])
        blocked = self.memory_store.verify("mem_claimed_observed")
        self.assertFalse(blocked["ok"])
        self.assertIn("evidence_unverified", blocked["detail"])


# ------------------------------------------------------------------------ fts


class FtsTests(SplitStoreTestBase):
    def test_fts_insert_update_delete_stay_in_sync(self):
        self.add("mem_fts", text="inventory capacity warning")

        def matches(term):
            return self.memory_store._require().execute(
                "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
                (term,)).fetchall()

        self.assertEqual(1, len(matches('"inventory"*')))
        self.memory_store._require().execute(
            "UPDATE memories SET text=? WHERE memory_id=?",
            ("rent due warning", "mem_fts"))
        self.assertEqual([], matches('"inventory"*'))
        self.assertEqual(1, len(matches('"rent"*')))
        self.memory_store._require().execute(
            "DELETE FROM memories WHERE memory_id='mem_fts'")
        self.assertEqual([], matches('"rent"*'))

    def test_fts_rebuild_restores_index(self):
        self.add("mem_rebuild", text="rebuild me")
        conn = self.memory_store._require()
        conn.execute("INSERT INTO memories_fts(memories_fts) VALUES('delete-all')")
        self.assertEqual([], conn.execute(
            "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
            ('"rebuild"*',)).fetchall())
        self.memory_store.rebuild_fts()
        self.assertEqual(1, len(conn.execute(
            "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
            ('"rebuild"*',)).fetchall()))

    def test_chinese_prefix_query_hits_via_fts_prefix(self):
        self.add("mem_cn", kind="warning", target="verified",
                 text="库存接近上限时不要继续进货，先卖出或整理货架。")
        rows = self.memory_store.search("库存接近上限还进货吗", limit=5,
                                        scope=self.scope())
        self.assertIn("mem_cn", self.ids(rows))
        hit = [row for row in rows if row["memory_id"] == "mem_cn"][0]
        self.assertEqual("fts_prefix", hit["recall_reason"])

    def test_chinese_mid_text_falls_back_to_substring(self):
        self.add("mem_cn_mid", kind="warning", target="verified",
                 text="库存接近上限时不要继续进货，先卖出或整理货架。")
        rows = self.memory_store.search("要继续进货", limit=5,
                                        scope=self.scope())
        self.assertIn("mem_cn_mid", self.ids(rows))
        hit = [row for row in rows if row["memory_id"] == "mem_cn_mid"][0]
        self.assertEqual("substring", hit["recall_reason"])


# ------------------------------------------------------------------ candidates


class CandidateTests(SplitStoreTestBase):
    def test_add_candidate_without_evidence_is_rejected_and_not_stored(self):
        result = self.memory_store.add_candidate(
            kind="fact", text="no evidence at all", evidence=[],
            game_version=VERSION, timeline_id=self.tl)
        self.assertFalse(result["ok"])
        self.assertEqual("no_evidence", result["reason"])
        self.assertEqual(0, self.memory_store.status()["memories"])

    def test_duplicate_dedupe_key_is_a_noop(self):
        first_event = self.event()
        first = self.memory_store.add_candidate(
            kind="fact", text="alpha fact", evidence=[first_event],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, dedupe_key="dedupe-alpha",
            memory_id="mem_alpha")
        self.assertTrue(first["ok"])
        second_event = self.event()
        second = self.memory_store.add_candidate(
            kind="fact", text="completely different text",
            evidence=[second_event], game_version=VERSION,
            save_id="slot:40", run_id="run-a", timeline_id=self.tl,
            dedupe_key="dedupe-alpha", memory_id="mem_should_not_exist")
        self.assertTrue(second["ok"])
        self.assertTrue(second["deduped"])
        self.assertEqual("mem_alpha", second["memory_id"])
        self.assertEqual(1, self.memory_store.status()["memories"])
        self.assertEqual(1, self.memory_store.get_memory("mem_alpha")
                         ["evidence_count"])
        self.assertEqual(1, len(self.memory_store.revisions("mem_alpha")))
        with self.assertRaises(MemoryUnavailable):
            self.memory_store.get_memory("mem_should_not_exist")

    def test_derived_dedupe_key_normalises_case_and_whitespace(self):
        self.memory_store.add_candidate(
            kind="warning", text="Never BUY when cash is low.",
            evidence=[self.event()], game_version=VERSION,
            save_id="slot:40", run_id="run-a", timeline_id=self.tl,
            memory_id="mem_norm_a")
        duplicate = self.memory_store.add_candidate(
            kind="warning", text="never buy   when cash is low",
            evidence=[self.event()], game_version=VERSION,
            save_id="slot:40", run_id="run-a", timeline_id=self.tl,
            memory_id="mem_norm_b")
        self.assertTrue(duplicate["deduped"])
        self.assertEqual("mem_norm_a", duplicate["memory_id"])
        self.assertEqual(1, self.memory_store.status()["memories"])

    def test_cannot_create_a_verified_memory_directly(self):
        with self.assertRaises(MemoryValidationError):
            self.memory_store.add_candidate(
                kind="fact", text="skip the gate", evidence=[self.event()],
                game_version=VERSION, timeline_id=self.tl,
                status="verified")

    def test_candidate_is_not_returned_by_bootstrap(self):
        self.add("mem_cand", kind="fact", text="unproven candidate")
        self.assertEqual([], self.memory_store.bootstrap(
            limit=10, scope=self.scope()))

    def test_invalid_kind_and_confidence_are_rejected(self):
        with self.assertRaises(MemoryValidationError):
            self.memory_store.add_candidate(
                kind="rumour", text="x", evidence=[self.event()],
                game_version=VERSION, timeline_id=self.tl)
        with self.assertRaises(MemoryValidationError):
            self.memory_store.add_candidate(
                kind="fact", text="x", evidence=[self.event()],
                game_version=VERSION, timeline_id=self.tl,
                confidence=1.5)


# ---------------------------------------------------------------------- verify


class VerifyTests(SplitStoreTestBase):
    def test_reported_evidence_and_complete_scope_verifies(self):
        evidence = self.event(capture_quality="partial",
                              event_type="intent", tool_name="confirm_trade")
        created = self.memory_store.add_candidate(
            kind="warning", text="reported rule", evidence=[
                {"event_id": evidence, "evidence_grade": "reported"}],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_reported")
        self.assertEqual("candidate", created["status"])
        self.assertEqual(0, created["evidence_unverified"])
        result = self.memory_store.verify("mem_reported", actor="user:alice")
        self.assertTrue(result["ok"], result)
        self.assertEqual("verified", result["status"])
        revisions = self.memory_store.revisions("mem_reported")
        self.assertEqual(2, len(revisions))
        self.assertEqual("user:alice", revisions[-1]["actor"])
        self.assertIn("status", revisions[-1]["changed_fields"])

    def test_unknown_evidence_grade_blocks_verification(self):
        evidence = self.event(result_status="unknown",
                              capture_quality="partial")
        self.memory_store.add_candidate(
            kind="fact", text="unknown outcome", evidence=[evidence],
            game_version=VERSION, save_id="slot:40", run_id="run-a",
            timeline_id=self.tl, memory_id="mem_unknown_grade")
        self.assertEqual("unknown",
                         self.memory_store.get_memory("mem_unknown_grade")
                         ["evidence"][0]["evidence_grade"])
        result = self.memory_store.verify("mem_unknown_grade",
                                          actor="user:alice")
        self.assertFalse(result["ok"])
        self.assertEqual("unverifiable", result["reason"])
        self.assertIn("unknown", result["detail"])
        self.assertEqual("candidate",
                         self.memory_store.get_memory("mem_unknown_grade")
                         ["status"])

    def test_pending_intent_is_not_usable_as_evidence(self):
        pending = self.events_store.append_intent(
            self.tl, tool_name="confirm_trade", args={"item_id": 1},
            action_id="act-pending-evidence", rev=10, game_day=1)
        created = self.memory_store.add_candidate(
            kind="warning", text="pending trade is not proof",
            evidence=[pending], game_version=VERSION, save_id="slot:40",
            run_id="run-a", timeline_id=self.tl, memory_id="mem_pending")
        self.assertEqual("unknown",
                         created["evidence"][0]["evidence_grade"])
        result = self.memory_store.verify("mem_pending", actor="user:alice")
        self.assertFalse(result["ok"])
        self.assertEqual("unverifiable", result["reason"])

    def test_incomplete_scope_blocks_verification(self):
        unknown_tl = self.events_store.start_session(
            agent_session_id="sess-unknown", game_version="unknown",
            game_mvid=None, instance_id=None).timeline_id
        evidence = self.event(timeline_id=unknown_tl)
        self.memory_store.add_candidate(
            kind="fact", text="versionless fact", evidence=[evidence],
            game_version="unknown", timeline_id=unknown_tl,
            memory_id="mem_no_version")
        result = self.memory_store.verify("mem_no_version", actor="user:alice")
        self.assertFalse(result["ok"])
        self.assertEqual("unverifiable", result["reason"])
        self.assertIn("scope", result["detail"])

    def test_evidence_from_another_version_blocks_verification(self):
        old_event = self.event(timeline_id=self.old_tl)
        self.memory_store.add_candidate(
            kind="fact", text="claimed for the wrong version",
            evidence=[old_event], game_version=VERSION,
            save_id="slot:40", run_id="run-a", timeline_id=self.tl,
            memory_id="mem_wrong_version")
        result = self.memory_store.verify("mem_wrong_version",
                                          actor="user:alice")
        self.assertFalse(result["ok"])
        self.assertEqual("unverifiable", result["reason"])
        self.assertIn("scope", result["detail"])

    def test_deprecated_memory_cannot_be_verified(self):
        self.add("mem_dep_then_verify", kind="fact", text="temporary rule")
        self.memory_store.deprecate("mem_dep_then_verify", actor="user:alice",
                                    reason="superseded by policy")
        result = self.memory_store.verify("mem_dep_then_verify",
                                          actor="user:bob")
        self.assertFalse(result["ok"])
        self.assertEqual("invalid_state", result["reason"])
        self.assertEqual("deprecated",
                         self.memory_store.get_memory("mem_dep_then_verify")
                         ["status"])

    def test_every_evidence_grade_is_accepted_by_the_enum(self):
        self.assertEqual({"observed", "reported", "unknown"},
                         set(EVIDENCE_GRADES))
        with self.assertRaises(MemoryValidationError):
            self.memory_store.add_candidate(
                kind="fact", text="bad grade",
                evidence=[{"event_id": self.event(),
                           "evidence_grade": "certain"}],
                game_version=VERSION, timeline_id=self.tl)


# ----------------------------------------------------------------------- scope


class ScopeTests(SplitStoreTestBase):
    def test_version_scope_hides_memories_from_other_versions(self):
        self.add("mem_version", kind="fact", text="version rule",
                 target="verified", save_id=None, run_id=None)
        self.assertEqual([], self.memory_store.search(
            "version rule", scope=self.scope(game_version=OLD_VERSION)))
        self.assertIn("mem_version", self.ids(self.memory_store.search(
            "version rule", scope=self.scope())))

    def test_save_scope_hides_memories_from_other_saves(self):
        self.add("mem_save", kind="fact", text="save private rule",
                 target="verified", run_id=None)
        self.assertEqual([], self.memory_store.search(
            "save private rule", scope=self.scope(
                save_id="slot:41", run_id="run-b", timeline_id=self.other_tl)))
        self.assertIn("mem_save", self.ids(self.memory_store.search(
            "save private rule", scope=self.scope())))

    def test_run_scope_requires_matching_run_and_timeline(self):
        self.add("mem_run", kind="fact", text="run private fact",
                 target="verified")
        self.assertEqual([], self.memory_store.search(
            "run private fact", scope=self.scope(
                run_id="run-b", save_id="slot:41", timeline_id=self.other_tl)))
        rollback_tl = self.events_store.fork_timeline(
            self.tl, run_id="run-a", slot=40, run_number=1, reason="rollback")
        self.assertEqual([], self.memory_store.search(
            "run private fact", scope=self.scope(timeline_id=rollback_tl)))
        self.assertIn("mem_run", self.ids(self.memory_store.search(
            "run private fact", scope=self.scope())))

    def test_search_returns_recall_reason_and_touches_hit_stats(self):
        self.add("mem_hit", kind="fact", text="inventory capacity warning",
                 target="verified")
        rows = self.memory_store.search("inventory", scope=self.scope())
        self.assertEqual(["mem_hit"], self.ids(rows))
        self.assertEqual("fts", rows[0]["recall_reason"])
        loaded = self.memory_store.get_memory("mem_hit")
        self.assertEqual(1, loaded["hit_count"])
        self.assertIsNotNone(loaded["last_used_at"])
        self.memory_store.search("inventory", scope=self.scope())
        self.assertEqual(2, self.memory_store.get_memory("mem_hit")
                         ["hit_count"])


# ---------------------------------------------------------------- state machine


class StateMachineTests(SplitStoreTestBase):
    def test_deprecate_hides_memory_and_is_idempotent(self):
        self.add("mem_dep", kind="warning", text="clear shelf every night",
                 target="verified")
        self.assertIn("mem_dep", self.ids(self.memory_store.search(
            "clear shelf", scope=self.scope())))
        first = self.memory_store.deprecate(
            "mem_dep", actor="user:alice", reason="wrong advice")
        self.assertTrue(first["ok"])
        self.assertEqual("deprecated", first["status"])
        self.assertIsNotNone(first["valid_until"])
        self.assertIn("status", self.memory_store.revisions("mem_dep")[-1]
                      ["changed_fields"])
        self.assertEqual([], self.memory_store.search("clear shelf",
                                                      scope=self.scope()))
        self.assertEqual([], self.memory_store.bootstrap(
            limit=10, scope=self.scope()))

        revision_count = len(self.memory_store.revisions("mem_dep"))
        second = self.memory_store.deprecate(
            "mem_dep", actor="user:bob", reason="again")
        self.assertTrue(second["ok"])
        self.assertTrue(second["deduped"])
        self.assertEqual(revision_count,
                         len(self.memory_store.revisions("mem_dep")))
        self.assertEqual(first["valid_until"], second["valid_until"])

    def test_supersede_sets_pointer_and_hides_the_old_rule(self):
        self.add("mem_old_rule", kind="warning", text="alpha old rule",
                 target="verified")
        self.add("mem_new_rule", kind="warning", text="alpha new rule",
                 target="verified")
        result = self.memory_store.supersede(
            "mem_old_rule", "mem_new_rule", actor="user:alice",
            reason="better evidence")
        self.assertTrue(result["ok"], result)
        self.assertEqual("superseded", result["status"])
        self.assertEqual("mem_new_rule", result["superseded_by"])
        self.assertIsNotNone(result["valid_until"])
        self.assertEqual(["mem_new_rule"], self.ids(self.memory_store.search(
            "alpha", scope=self.scope())))
        self.assertEqual(["mem_new_rule"], self.ids(self.memory_store.bootstrap(
            limit=10, scope=self.scope())))
        superseded = [m for m in self.memory_store.list_memories()
                      if m["status"] == "superseded"]
        self.assertEqual(["mem_old_rule"], self.ids(superseded))

    def test_revise_updates_text_resets_verification_and_reindexes(self):
        self.add("mem_revised", kind="procedure", text="alpha obsolete step",
                 target="verified")
        before = self.memory_store.get_memory("mem_revised")
        result = self.memory_store.revise(
            "mem_revised", {"text": "bravo replacement step"},
            actor="user:alice", reason="correct the procedure")
        self.assertTrue(result["ok"], result)
        self.assertEqual("candidate", result["status"])
        self.assertEqual("bravo replacement step", result["text"])
        self.assertNotEqual(before["dedupe_key"], result["dedupe_key"])
        self.assertEqual([], self.memory_store.search("alpha",
                                                      scope=self.scope()))
        self.assertIn("mem_revised", self.ids(self.memory_store.search(
            "bravo", scope=self.scope())))
        self.assertEqual(3, len(self.memory_store.revisions("mem_revised")))

    def test_revise_rejects_immutable_fields_and_terminal_states(self):
        self.add("mem_terminal", kind="fact", text="terminal rule")
        with self.assertRaises(MemoryValidationError):
            self.memory_store.revise(
                "mem_terminal", {"memory_id": "mem_other"},
                actor="user:alice", reason="nope")
        self.memory_store.deprecate("mem_terminal", actor="user:alice",
                                    reason="done")
        result = self.memory_store.revise(
            "mem_terminal", {"text": "changed"}, actor="user:alice",
            reason="too late")
        self.assertFalse(result["ok"])
        self.assertEqual("invalid_state", result["reason"])

    def test_revision_audit_records_every_transition(self):
        self.add("mem_audit", kind="fact", text="audit rule")
        self.memory_store.verify("mem_audit", actor="user:alice")
        self.memory_store.revise("mem_audit", {"text": "audit rule v2"},
                                 actor="user:bob", reason="clarify")
        self.memory_store.deprecate("mem_audit", actor="user:carol",
                                    reason="retired")
        revisions = self.memory_store.revisions("mem_audit")
        self.assertEqual([1, 2, 3, 4],
                         [row["revision"] for row in revisions])
        self.assertEqual(["system", "user:alice", "user:bob", "user:carol"],
                         [row["actor"] for row in revisions])
        self.assertIn("status", revisions[1]["changed_fields"])
        self.assertIn("text", revisions[2]["changed_fields"])
        self.assertIn("valid_until", revisions[3]["changed_fields"])
        for row in revisions:
            self.assertIsInstance(row["changed_fields"], list)
            self.assertTrue(row["reason"])


# ------------------------------------------------------------------- bootstrap


class BootstrapTests(SplitStoreTestBase):
    def test_bootstrap_only_returns_verified_unexpired_memories(self):
        self.add("mem_live", kind="fact", text="live verified rule",
                 target="verified", valid_until="2999-01-01T00:00:00Z")
        self.add("mem_expired", kind="fact", text="expired verified rule",
                 target="verified", valid_from="1999-01-01T00:00:00Z",
                 valid_until="2000-01-01T00:00:00Z")
        self.add("mem_pending", kind="fact", text="pending rule")
        self.add("mem_conflict_boot", kind="strategy", text="conflict rule",
                 status=STATUS_CONFLICT)
        self.add("mem_deprecated_boot", kind="fact", text="deprecated rule")
        self.memory_store.deprecate("mem_deprecated_boot", actor="user:alice",
                                    reason="old")
        self.assertEqual(["mem_live"], self.ids(self.memory_store.bootstrap(
            limit=10, scope=self.scope())))

    def test_bootstrap_orders_by_confidence_first(self):
        self.add("mem_high", kind="fact", text="high confidence rule",
                 target="verified", confidence=0.9)
        self.add("mem_medium", kind="fact", text="medium confidence rule",
                 target="verified", confidence=0.6)
        self.assertEqual(["mem_high", "mem_medium"],
                         self.ids(self.memory_store.bootstrap(
                             limit=10, scope=self.scope())))

    def test_bootstrap_prefers_recently_hit_memory_on_confidence_tie(self):
        self.add("mem_alpha_boot", kind="fact", text="alpha boot rule",
                 target="verified", confidence=0.8)
        self.add("mem_bravo_boot", kind="fact", text="bravo boot rule",
                 target="verified", confidence=0.8)
        self.memory_store.search("bravo", scope=self.scope())
        self.assertEqual(["mem_bravo_boot", "mem_alpha_boot"],
                         self.ids(self.memory_store.bootstrap(
                             limit=10, scope=self.scope())))

    def test_bootstrap_respects_scope(self):
        self.add("mem_boot_version", kind="fact", text="boot version rule",
                 target="verified", save_id=None, run_id=None)
        self.add("mem_boot_other_save", kind="fact", text="boot other save",
                 target="verified", save_id="slot:41", run_id="run-b",
                 timeline_id=self.other_tl,
                 evidence=[self.event(timeline_id=self.other_tl)])
        rows = self.memory_store.bootstrap(limit=10, scope=self.scope())
        self.assertEqual(["mem_boot_version"], self.ids(rows))
        self.assertEqual(["mem_boot_version", "mem_boot_other_save"],
                         self.ids(self.memory_store.bootstrap(
                             limit=10, scope=self.scope(
                                 save_id="slot:41", run_id="run-b",
                                 timeline_id=self.other_tl))))


# -------------------------------------------------------------------- conflict


class ConflictTests(SplitStoreTestBase):
    def test_conflicting_memories_coexist_without_overwriting_each_other(self):
        self.add("mem_conflict_high", kind="strategy",
                 text="测试数据：陈酿葡萄酒应该报高价。",
                 status=STATUS_CONFLICT, confidence=0.4)
        self.add("mem_conflict_low", kind="strategy",
                 text="测试数据：陈酿葡萄酒应该报低价。",
                 status=STATUS_CONFLICT, confidence=0.4)
        high = self.memory_store.get_memory("mem_conflict_high")
        low = self.memory_store.get_memory("mem_conflict_low")
        self.assertEqual("conflict", high["status"])
        self.assertEqual("conflict", low["status"])
        self.assertNotEqual(high["text"], low["text"])
        found = self.ids(self.memory_store.search("陈酿", scope=self.scope()))
        self.assertIn("mem_conflict_high", found)
        self.assertIn("mem_conflict_low", found)
        self.assertEqual([], self.memory_store.bootstrap(
            limit=10, scope=self.scope()))


# ------------------------------------------------------------------------ seed


class SeedTests(unittest.TestCase):
    def test_seed_import_is_idempotent_and_has_required_fixtures(self):
        from agent.memory import seed

        tmp = tempfile.TemporaryDirectory()
        try:
            memories_path = os.path.join(tmp.name, "memories.db")
            events_path = os.path.join(tmp.name, "events.db")
            first = seed.seed_database(memories_path, events_path)
            self.assertGreaterEqual(first["memories_added"], 10)
            self.assertLessEqual(first["memories_added"], 15)
            self.assertGreaterEqual(first["events_added"], 10)
            self.assertEqual(0, first["memories_unverified"])
            self.assertEqual(1, first["events_schema_version"])
            self.assertEqual(2, first["memory_schema_version"])

            second = seed.seed_database(memories_path, events_path)
            self.assertEqual(0, second["memories_added"])
            self.assertEqual(0, second["events_added"])
            self.assertEqual(first["memories_added"],
                             second["memories_skipped"])

            events = EventStore(events_path)
            try:
                self.assertEqual(1, events.schema_version())
                self.assertNotIn("memories", events.table_names())
            finally:
                events.close()

            store = MemoryStore(memories_path, events_db=events_path)
            try:
                self.assertEqual(2, store.schema_version())
                status = store.status()
                self.assertEqual(first["memories_added"], status["memories"])
                self.assertGreaterEqual(status["by_status"].get("verified", 0), 7)
                self.assertEqual(1, status["by_status"].get("deprecated", 0))
                self.assertEqual(2, status["by_status"].get("conflict", 0))
                self.assertEqual(1, status["by_status"].get("superseded", 0))
                self.assertEqual(1, status["by_status"].get("candidate", 0))

                capacity = store.get_memory("mem_seed_inv_capacity")
                self.assertEqual("verified", capacity["status"])
                self.assertGreaterEqual(capacity["evidence_count"], 1)
                self.assertEqual(0, capacity["evidence_unverified"])

                deprecated = store.get_memory(
                    "mem_seed_deprecated_clear_shelf")
                self.assertEqual("deprecated", deprecated["status"])
                self.assertIsNotNone(deprecated["valid_until"])

                high = store.get_memory("mem_seed_conflict_high")
                low = store.get_memory("mem_seed_conflict_low")
                self.assertEqual("conflict", high["status"])
                self.assertEqual("conflict", low["status"])
                self.assertNotEqual(high["text"], low["text"])

                condition = store.get_memory("mem_seed_inventory_condition")
                self.assertEqual(
                    0.9,
                    condition["scope_extra"]["conditions"]["state"]
                    ["inventory_occupancy_min"])

                chinese = [m for m in store.list_memories()
                           if "纯水" in m["text"]]
                self.assertTrue(chinese, "seed must contain a Chinese entity")
                for memory in store.list_memories():
                    self.assertTrue(
                        memory["text"].startswith("测试数据：")
                        or memory["text"].startswith("Test data only:"),
                        "every seed rule must be visibly marked as test data: %r"
                        % memory["text"])

                hits = store.search(
                    "纯水", scope={"game_id": GAME_ID,
                                   "game_version": VERSION,
                                   "save_id": "slot:40",
                                   "run_id": "run-seed-a",
                                   "timeline_id": "tl_seed_main"})
                self.assertIn("mem_seed_water_price",
                              [m["memory_id"] for m in hits])
            finally:
                store.close()
        finally:
            tmp.cleanup()

    def test_seed_module_cli_imports_two_databases(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            memories_path = os.path.join(tmp.name, "memories.db")
            events_path = os.path.join(tmp.name, "events.db")
            completed = subprocess.run(
                [sys.executable, "-B", "-m", SEED_MODULE,
                 "--memories-db", memories_path, "--db", events_path],
                cwd=ROOT, capture_output=True, text=True, timeout=60)
            self.assertEqual(0, completed.returncode, completed.stderr)
            payload = json.loads(completed.stdout)
            self.assertEqual(True, payload["ok"])
            self.assertGreaterEqual(payload["memories_added"], 10)
            self.assertEqual(memories_path, payload["memories_db"])
            events = EventStore(events_path)
            try:
                self.assertEqual(1, events.schema_version())
                self.assertGreaterEqual(events.count_events(), 10)
            finally:
                events.close()
            store = MemoryStore(memories_path, events_db=events_path)
            try:
                self.assertGreaterEqual(store.status()["memories"], 10)
            finally:
                store.close()
        finally:
            tmp.cleanup()

    def test_seed_module_cli_accepts_legacy_db_flag(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            memories_path = os.path.join(tmp.name, "legacy-memories.db")
            completed = subprocess.run(
                [sys.executable, "-B", "-m", SEED_MODULE,
                 "--db", memories_path],
                cwd=ROOT, capture_output=True, text=True, timeout=60)
            self.assertEqual(0, completed.returncode, completed.stderr)
            payload = json.loads(completed.stdout)
            self.assertEqual(memories_path, payload["memories_db"])
            self.assertEqual(os.path.join(tmp.name, "events.db"),
                             payload["events_db"])
            self.assertTrue(os.path.isfile(payload["events_db"]))
        finally:
            tmp.cleanup()


# ------------------------------------------------------------------------- cli


class CliStatusTests(unittest.TestCase):
    def test_cli_status_reports_both_databases(self):
        from agent.memory import seed
        from agent.memory.cli import main

        tmp = tempfile.TemporaryDirectory()
        try:
            memories_path = os.path.join(tmp.name, "memories.db")
            events_path = os.path.join(tmp.name, "events.db")
            seed.seed_database(memories_path, events_path)
            buffer = io.StringIO()
            with redirect_stdout(buffer):
                code = main(["--db", events_path,
                             "--memories-db", memories_path,
                             "--format", "json", "status"])
            self.assertEqual(0, code)
            payload = json.loads(buffer.getvalue())
            self.assertEqual(1, payload["schema_version"])
            self.assertEqual(2, payload["memory_schema_version"])
            self.assertEqual(memories_path, payload["memories_db"])
            self.assertGreaterEqual(payload["memories"], 10)
            self.assertGreaterEqual(
                payload["memories_by_status"]["verified"], 7)
            self.assertEqual(1, payload["memories_by_status"]["deprecated"])
            self.assertEqual(2, payload["memories_by_status"]["conflict"])
        finally:
            tmp.cleanup()

    def test_cli_status_without_memories_db_says_not_configured(self):
        from agent.memory.cli import main

        tmp = tempfile.TemporaryDirectory()
        try:
            events_path = os.path.join(tmp.name, "memory.db")
            EventStore(events_path).close()
            buffer = io.StringIO()
            with redirect_stdout(buffer):
                code = main(["--db", events_path, "--format", "json", "status"])
            self.assertEqual(0, code)
            payload = json.loads(buffer.getvalue())
            self.assertEqual("not configured", payload["memories"])
            self.assertEqual("not configured", payload["memories_by_status"])
            self.assertIsNone(payload["memories_db"])
        finally:
            tmp.cleanup()


if __name__ == "__main__":
    unittest.main()
