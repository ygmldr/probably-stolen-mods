"""Semantic classification for the recorder.

The recorder must not guess from the HTTP method: ``POST /action`` carries both
read-only and state-changing operations. This module owns the single Python
copy of that classification, and a contract test keeps it in sync with the C#
allowlist (``src/Bridge/ActionRouter.cs``) so the two cannot drift silently.
"""

# Mirrors ActionRouter.ReadOnlyOps. Contract-tested against the C# source.
READ_ONLY_ACTION_OPS = frozenset({
    "bargain_preview",
    "contraband_status",
    "events_status",
    "expedition_status",
    "get_item",
    "get_layout",
    "get_new_game_options",
    "get_pending_prompt",
    "get_recording_save_status",
    "get_ui_snapshot",
    "health_status",
    "husbandry_status",
    "inspect_ticket",
    "list_analyzers",
    "list_books",
    "list_contacts",
    "list_containers",
    "list_factions",
    "list_goods",
    "list_interactions",
    "list_inventory",
    "list_labels",
    "list_locations",
    "list_machines",
    "list_marks",
    "list_requests",
    "list_services",
    "list_signs",
    "list_tickets",
    "list_upgrades",
    "list_wine",
    "lottery_status",
    "machine_grow_status",
    "machine_status",
    "phone_state",
    "read_book",
    "read_item",
    "security_status",
    "wanted_status",
    "barter_status",
    "wine_status",
    "capture_screenshot",
    "list_screenshots",
})

# Tools served by /state or /events instead of /action. They never mutate.
MCP_STATE_READS = frozenset({
    "ping",
    "get_state",
    "get_run_summary",
    "get_legal_actions",
    "get_action_history",
    "wait_for_events",
    "wait_for_change",
    "wait_until",
})

READ_ONLY_OPS = READ_ONLY_ACTION_OPS | MCP_STATE_READS

# Operations whose outcome is a request rather than a completed change: the
# recorder must wait for an observed state transition before calling them done.
ASYNC_COMPLETION_OPS = frozenset({"sleep"})

# Operations that write to disk in the game and therefore must never be retried
# after an unknown outcome.
PERSISTENT_OPS = frozenset({
    "confirm_new_game",
    "save_game",
    "save_recording_game",
    "load_game",
    "load_recording_game",
})

# Operations that fork the history timeline when their effect is observed.
LOAD_OPS = frozenset({"load_game", "load_recording_game"})


def is_read_only(op):
    return op in READ_ONLY_OPS


def is_async_completion(op):
    return op in ASYNC_COMPLETION_OPS


def is_persistent(op):
    return op in PERSISTENT_OPS
