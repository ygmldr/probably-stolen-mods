"""Read-only S8 preflight for the PawnMind bridge.

Usage (from the ``PawnMind`` directory)::

    python -m agent.preflight --config <bridge.conf> [--json]

The preflight performs exactly three reads:

* ``GET /health`` without a token (identity, write gate, ``latest``);
* ``GET /state`` with the bearer token (in-game/day/cash/rent summary);
* ``POST /action`` with ``op=get_recording_save_status`` (a read-only op that
  takes no ``rev``).

It never sends a write op, never reads a save file, and never prints the token.
Exit codes: 0 = ready, 1 = connected but not ready, 2 = fatal (unreachable,
authentication failure, malformed/missing ``/health`` contract, etc.).
"""

import argparse
import json
import sys

try:  # python -m agent.preflight
    from .pawnmind_client import BridgeClient, BridgeError
except ImportError:  # imported as a top-level module from agent/tests
    from pawnmind_client import BridgeClient, BridgeError

EXIT_READY = 0
EXIT_NOT_READY = 1
EXIT_FATAL = 2

READ_ONLY_OP = "get_recording_save_status"

HEALTH_FIELDS = (
    "game_version",
    "game_assembly_mvid",
    "expected_game_version",
    "expected_assembly_mvid",
    "write_gate",
    "write_gate_reason",
    "latest",
)

RECORDING_FIELDS = (
    "recording_mode",
    "configured_slot",
    "next_free_slot",
    "next_free_known",
    "configured_slot_occupied",
    "bound",
    "binding",
    "slot_matches",
    "run_matches",
    "error",
)

LATEST_WARNING = (
    "latest=UNVERIFIED — 本脚本不访问 Steam，无法证明当前是 Steam 最新稳定版；"
    "需人工核对 Steam 稳定分支与游戏内版本，再做任何录制/写操作。"
)


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def _format_value(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, sort_keys=True)
    return str(value)


def _redact(value, token):
    """Recursively remove the configured token from every output string."""
    if token and isinstance(value, str):
        return value.replace(token, "[redacted]")
    if isinstance(value, list):
        return [_redact(item, token) for item in value]
    if isinstance(value, tuple):
        return tuple(_redact(item, token) for item in value)
    if isinstance(value, dict):
        return {
            (_redact(key, token) if isinstance(key, str) else key): _redact(item, token)
            for key, item in value.items()
        }
    return value


def _new_result():
    return {
        "ok": False,
        "exit_code": EXIT_FATAL,
        "checks": [],
        "health": None,
        "state": None,
        "recording": None,
        "warnings": [LATEST_WARNING],
        "next_actions": [],
    }


def _add_warning(result, message):
    if message and message not in result["warnings"]:
        result["warnings"].append(message)


def _add_next_action(result, message):
    if message and message not in result["next_actions"]:
        result["next_actions"].append(message)


def _add_check(result, check_id, ok, summary, code=None, details=None,
               next_action=None):
    check = {
        "id": check_id,
        "ok": bool(ok),
        "code": code,
        "summary": summary,
        "details": dict(details or {}),
        "next_action": next_action,
    }
    result["checks"].append(check)
    if not ok and next_action:
        _add_next_action(result, next_action)
    return check


def _finish(result, exit_code, token=""):
    result["exit_code"] = int(exit_code)
    result["ok"] = exit_code == EXIT_READY
    return _redact(result, token)


def _error_details(error):
    details = {"error": str(error)}
    status = getattr(error, "status", None)
    if status is not None:
        details["status"] = status
    return details


def _is_auth_error(error):
    return getattr(error, "status", None) in (401, 403)


def _health_view(health):
    return {field: health.get(field) for field in HEALTH_FIELDS}


def _state_view(state):
    rent = state.get("rent")
    if isinstance(rent, dict):
        rent_summary = {
            field: rent.get(field)
            for field in ("due", "days_left", "days_until_rent", "can_pay")
        }
    else:
        rent_summary = None
    return {
        "in_game": state.get("in_game"),
        "day": state.get("day"),
        "cash": state.get("cash"),
        "rent": rent_summary,
    }


def _recording_view(recording):
    return {field: recording.get(field) for field in RECORDING_FIELDS}


def _auth_next_action():
    return (
        "bridge.conf 的 token 与游戏 ModData/pawnmind/bridge.conf 不一致；"
        "同步 token 后重试。不要把 token 写入日志、截图或录像。"
    )


def _connection_next_action():
    return (
        "启动游戏并确认 PawnMind Mod 已加载；核对 bridge.conf 的 port 与游戏监听端口一致；"
        "若端口被其它进程占用，先关闭占用进程。然后重新运行本预检。"
    )


def _evaluate_recording(result, recording):
    mode = recording.get("recording_mode")
    error = recording.get("error")
    configured = recording.get("configured_slot")
    next_free = recording.get("next_free_slot")
    bound = recording.get("bound") is True

    if mode == "locked":
        _add_check(
            result, "recording", False,
            "录制已锁定（recording_mode=locked）：error=%s；"
            "在再次开局/存读档前必须解除。"
            % _format_value(error),
            code="recording_locked",
            details={"recording_mode": mode, "error": error,
                     "configured_slot": configured, "bound": bound},
            next_action=(
                "立即停止：录制策略已锁定，不能继续开局/存读档。保留 error 与 "
                "Mod 日志，退出游戏/Mod；人工核对专用槽、binding.run_id 与 "
                "bridge.conf 后按受控流程重新开始。不得继续写操作，不得手动"
                "修改存档文件。"
            ),
        )
        return

    if mode not in ("armed", "bound"):
        if mode == "disabled":
            code = (error if isinstance(error, str)
                    and error.startswith("recording_")
                    else "recording_unconfigured")
        else:
            code = "recording_invalid_mode"
        details = {"recording_mode": mode, "error": error,
                   "configured_slot": configured,
                   "next_free_slot": next_free}
        next_action = (
            "在 bridge.conf 中设置 recording_mode=recording、"
            "recording_slot=<下一个空闲槽>"
        )
        if next_free is not None:
            next_action += "（当前 next_free_slot=%s）" % _format_value(next_free)
        next_action += (
            "、recording_isolation_ack=true；确认专用槽为空并重启游戏/Mod，"
            "再运行本预检。"
        )
        _add_check(
            result, "recording", False,
            "录制未配置：recording_mode=%s，error=%s。"
            % (_format_value(mode), _format_value(error)),
            code=code, details=details, next_action=next_action,
        )
        return

    if mode == "bound" and not bound:
        _add_check(
            result, "recording", False,
            "recording_mode=bound 但 bound=false，状态自相矛盾。",
            code="recording_not_bound",
            details={"recording_mode": mode, "bound": bound, "error": error},
            next_action=(
                "重启游戏/Mod 后重新运行本预检；若仍显示 bound=false，"
                "停止 S8 并检查 Mod 日志，不得继续写操作。"
            ),
        )
        return

    if bound:
        if mode != "bound":
            _add_warning(
                result,
                "recording_mode=%s 但 bound=true；以 bound 绑定校验为准。"
                % _format_value(mode),
            )
        slot_matches = recording.get("slot_matches") is True
        run_matches = recording.get("run_matches") is True
        if slot_matches and run_matches:
            _add_check(
                result, "recording_binding", True,
                "已 Bound：slot_matches=true，run_matches=true，"
                "配置槽与当前局绑定一致。",
                code="ok",
                details={"configured_slot": configured,
                         "slot_matches": slot_matches,
                         "run_matches": run_matches},
            )
        else:
            _add_check(
                result, "recording_binding", False,
                "已 Bound 但绑定校验失败：slot_matches=%s，run_matches=%s。"
                % (_format_value(slot_matches), _format_value(run_matches)),
                code="recording_binding_mismatch",
                details={"configured_slot": configured,
                         "binding": recording.get("binding"),
                         "slot_matches": slot_matches,
                         "run_matches": run_matches},
                next_action=(
                    "立即停止，不要再做写操作，也不要读取其它存档；核对 "
                    "get_recording_save_status.binding.slot 是否等于 bridge.conf 的 "
                    "recording_slot、binding.run_id 是否属于当前这一局。若不一致，"
                    "退出游戏/Mod 后重新做受控绑定；不得手动修改存档文件。"
                ),
            )
        return

    # Armed, not bound yet: the configured slot must be the predicted next free
    # slot, otherwise the native start would create/write a different slot.
    if mode != "armed":
        _add_check(
            result, "recording", False,
            "录制状态无效：recording_mode=%s，bound=%s。"
            % (_format_value(mode), _format_value(bound)),
            code="recording_invalid_mode",
            details={"recording_mode": mode, "bound": bound, "error": error},
            next_action=(
                "重启游戏/Mod；若状态仍不是 armed/bound，停止 S8 并检查 Mod 日志。"
            ),
        )
        return

    if next_free is None or not _is_int(next_free):
        _add_check(
            result, "recording_slot", False,
            "录制槽预测不可用：next_free_known=%s，next_free_slot=%s。"
            % (_format_value(recording.get("next_free_known")),
               _format_value(next_free)),
            code="recording_next_free_unknown",
            details={"configured_slot": configured,
                     "next_free_slot": next_free,
                     "next_free_known": recording.get("next_free_known")},
            next_action=(
                "先确认游戏存档索引可读且 recording_slot 已设置；重启游戏/Mod 后重试。"
                "若 next_free_slot 仍不可预测，停止 S8，人工在游戏 UI 确认下一个"
                "空槽后再决定，不得猜测槽号。"
            ),
        )
        return

    if configured is None or not _is_int(configured):
        _add_check(
            result, "recording_slot", False,
            "录制槽未配置，但 next_free_slot=%s 可预测。"
            % _format_value(next_free),
            code="recording_slot_missing",
            details={"configured_slot": configured,
                     "next_free_slot": next_free,
                     "next_free_known": recording.get("next_free_known")},
            next_action=(
                "在 bridge.conf 中设置 recording_slot=%s（必须等于 next_free_slot），"
                "确认该槽为空并重启游戏/Mod 后再运行本预检。"
                % _format_value(next_free)
            ),
        )
        return

    if configured != next_free:
        difference = next_free - configured
        difference_text = "%+d" % difference
        _add_check(
            result, "recording_slot", False,
            "配置槽与下一个空闲槽不一致：configured_slot=%s，"
            "next_free_slot=%s，相差 %s。"
            % (_format_value(configured), _format_value(next_free),
               difference_text),
            code="recording_slot_mismatch",
            details={"configured_slot": configured,
                     "next_free_slot": next_free,
                     "difference": difference,
                     "delta": difference},
            next_action=(
                "把 bridge.conf 的 recording_slot 改为 %s（当前 configured_slot=%s、"
                "next_free_slot=%s，相差 %s）；确认该槽为空后重启游戏/Mod，"
                "再运行本预检。"
                % (_format_value(next_free), _format_value(configured),
                   _format_value(next_free), difference_text)
            ),
        )
        return

    if recording.get("configured_slot_occupied") is True:
        _add_check(
            result, "recording_slot", False,
            "配置槽 %s 已被占用（configured_slot_occupied=true）。"
            % _format_value(configured),
            code="recording_slot_occupied",
            details={"configured_slot": configured,
                     "next_free_slot": next_free,
                     "configured_slot_occupied": True},
            next_action=(
                "停止 S8：不要覆盖/删除任何存档。人工在游戏 UI 确认专用空槽，"
                "把 bridge.conf 的 recording_slot 改为确认过的下一个空闲槽，"
                "重启游戏/Mod 后再运行本预检。"
            ),
        )
        return

    _add_check(
        result, "recording_slot", True,
        "未开始录制且槽位匹配：configured_slot=%s 等于 next_free_slot=%s。"
        % (_format_value(configured), _format_value(next_free)),
        code="ok",
        details={"configured_slot": configured,
                 "next_free_slot": next_free,
                 "next_free_known": recording.get("next_free_known"),
                 "configured_slot_occupied":
                     recording.get("configured_slot_occupied")},
    )


def run_preflight(config_path, timeout=8.0):
    """Run the three read-only checks and return a JSON-serializable result."""
    result = _new_result()
    token = ""

    try:
        client = BridgeClient(config_path, timeout=timeout)
        token = client.token
    except (BridgeError, OSError, ValueError) as error:
        _add_check(
            result, "config", False,
            "无法读取 bridge.conf：%s" % error,
            code="config_invalid",
            details=_error_details(error),
            next_action=(
                "确认 --config 指向游戏 ModData/pawnmind/bridge.conf，文件中包含 "
                "port 和至少 32 字符的 token；修正后重试。"
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    try:
        health = client.health()
    except (BridgeError, ValueError) as error:
        auth = _is_auth_error(error)
        _add_check(
            result, "health", False,
            ("认证失败：GET /health 返回 %s。" % error) if auth
            else ("无法连接或读取 /health：%s。" % error),
            code="auth_failed" if auth else "connect_failed",
            details=_error_details(error),
            next_action=_auth_next_action() if auth else _connection_next_action(),
        )
        return _finish(result, EXIT_FATAL, token)

    if not isinstance(health, dict) or health.get("ok") is not True:
        _add_check(
            result, "health", False,
            "/health 返回了无法识别的响应（ok 不为 true）。",
            code="health_invalid",
            details={"payload": health},
            next_action=(
                "确认 bridge.conf 的 port 指向 PawnMind 桥；升级/重新构建 Mod "
                "后重启游戏，再运行本预检。"
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    missing = [field for field in HEALTH_FIELDS if field not in health]
    if missing:
        _add_check(
            result, "health", False,
            "/health 缺少必需字段：%s。"
            % ", ".join(missing),
            code="health_missing_fields",
            details={"missing": missing, "health": _health_view(health)},
            next_action=(
                "升级或重新构建 PawnMind Mod，使 /health 输出 %s；"
                "重启游戏后重试。"
                % ", ".join(HEALTH_FIELDS)
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    invalid = [
        field for field in ("game_version", "game_assembly_mvid",
                            "write_gate", "latest")
        if not isinstance(health.get(field), str) or not health[field].strip()
    ]
    if invalid:
        _add_check(
            result, "health", False,
            "/health 字段值无效：%s。" % ", ".join(invalid),
            code="health_invalid_fields",
            details={"invalid": invalid, "health": _health_view(health)},
            next_action=(
                "重启游戏/Mod 使 /health 重新填充版本身份；若仍为空，"
                "升级/重新构建 Mod 后再试。"
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    result["health"] = _health_view(health)
    _add_check(result, "health", True,
               "/health 可读，版本身份与写门禁字段齐全。", code="ok")

    if health.get("latest") != "UNVERIFIED":
        _add_warning(
            result,
            "latest=%s 不是 UNVERIFIED；本脚本仍无法证明 Steam 最新，"
            "需人工核对 Steam 稳定分支。"
            % _format_value(health.get("latest")),
        )

    gate = health.get("write_gate")
    reason = health.get("write_gate_reason")
    if gate == "open":
        _add_check(result, "write_gate", True,
                   "write_gate=open，写门禁已打开。", code="ok")
    else:
        _add_check(
            result, "write_gate", False,
            "write_gate=%s（write_gate_reason=%s），写操作仍被锁定。"
            % (_format_value(gate), _format_value(reason)),
            code="write_gate_locked",
            details={"write_gate": gate, "write_gate_reason": reason},
            next_action=(
                "先人工核对 /health 的 game_version/game_assembly_mvid 是否为 "
                "Steam 当前稳定版并与目标版本一致；确认后把 "
                "expected_game_version 和 expected_assembly_mvid 写入 bridge.conf"
                "（必须完全一致），重启游戏/Mod，再运行本预检。"
            ),
        )

    try:
        state = client.get_state()
    except (BridgeError, ValueError) as error:
        auth = _is_auth_error(error)
        _add_check(
            result, "state", False,
            ("认证失败：GET /state 返回 %s。" % error) if auth
            else ("无法读取 /state：%s。" % error),
            code="auth_failed" if auth else "state_unavailable",
            details=_error_details(error),
            next_action=_auth_next_action() if auth else _connection_next_action(),
        )
        return _finish(result, EXIT_FATAL, token)

    if not isinstance(state, dict) or not isinstance(state.get("in_game"), bool):
        _add_check(
            result, "state", False,
            "/state 响应无效：缺少布尔型 in_game。",
            code="state_invalid",
            details={"payload_keys": sorted(state) if isinstance(state, dict) else None},
            next_action=(
                "确认 bridge.conf 的 port/token 指向 PawnMind 桥；升级/重新构建 "
                "Mod 后重启游戏，再运行本预检。"
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    result["state"] = _state_view(state)
    if state["in_game"]:
        _add_check(
            result, "state", True,
            "/state 可读：in_game=true，day=%s，cash=%s。"
            % (_format_value(state.get("day")), _format_value(state.get("cash"))),
            code="ok",
        )
    else:
        _add_check(result, "state", True,
                   "/state 可读：in_game=false（当前不在游戏中）。", code="ok")

    try:
        recording = client.action(READ_ONLY_OP)
    except (BridgeError, ValueError) as error:
        auth = _is_auth_error(error)
        _add_check(
            result, "recording", False,
            ("认证失败：读取 get_recording_save_status 返回 %s。" % error) if auth
            else ("无法读取 get_recording_save_status：%s。" % error),
            code="auth_failed" if auth else "recording_unavailable",
            details=_error_details(error),
            next_action=_auth_next_action() if auth else _connection_next_action(),
        )
        return _finish(result, EXIT_FATAL, token)

    if not isinstance(recording, dict) or recording.get("ok") is not True:
        _add_check(
            result, "recording", False,
            "读取 get_recording_save_status 失败（error=%s）。"
            % _format_value(recording.get("error")
                            if isinstance(recording, dict) else None),
            code="recording_status_unavailable",
            details={"payload_keys": sorted(recording)
                     if isinstance(recording, dict) else None},
            next_action=(
                "确认 Mod 已实现 get_recording_save_status；升级/重新构建 Mod "
                "后重启游戏。不要改用写 op 去探测状态。"
            ),
        )
        return _finish(result, EXIT_FATAL, token)

    result["recording"] = _recording_view(recording)
    _evaluate_recording(result, recording)

    if any(not check["ok"] for check in result["checks"]):
        return _finish(result, EXIT_NOT_READY, token)
    return _finish(result, EXIT_READY, token)


def render_text(result):
    """Render a human-readable report. No token is ever included."""
    print("PawnMind S8 preflight (read-only)")
    print("=" * 48)

    print("[health] GET /health (no token)")
    health = result.get("health")
    if isinstance(health, dict):
        for field in HEALTH_FIELDS:
            print("  %s: %s" % (field, _format_value(health.get(field))))
    else:
        print("  (unavailable)")

    print("")
    print("[state] GET /state (Bearer)")
    state = result.get("state")
    if isinstance(state, dict):
        for field in ("in_game", "day", "cash"):
            print("  %s: %s" % (field, _format_value(state.get(field))))
        rent = state.get("rent")
        if isinstance(rent, dict):
            print("  rent: due=%s days_left=%s days_until_rent=%s can_pay=%s"
                  % (_format_value(rent.get("due")),
                     _format_value(rent.get("days_left")),
                     _format_value(rent.get("days_until_rent")),
                     _format_value(rent.get("can_pay"))))
        else:
            print("  rent: null")
    else:
        print("  (unavailable)")

    print("")
    print("[recording] action=get_recording_save_status (read-only, no rev)")
    recording = result.get("recording")
    if isinstance(recording, dict):
        for field in RECORDING_FIELDS:
            print("  %s: %s" % (field, _format_value(recording.get(field))))
    else:
        print("  (unavailable)")

    print("")
    print("[checks]")
    for check in result["checks"]:
        code = " (%s)" % check["code"] if check.get("code") else ""
        print("  [%s] %s%s" % ("OK" if check["ok"] else "FAIL",
                               check["id"], code))
        print("       %s" % check["summary"])
        if not check["ok"] and check.get("next_action"):
            print("       NEXT: %s" % check["next_action"])

    print("")
    print("[warnings]")
    for warning in result["warnings"]:
        print("  - %s" % warning)

    print("")
    print("[next actions]")
    if result["next_actions"]:
        for action in result["next_actions"]:
            print("  - %s" % action)
    else:
        print("  (none)")

    print("")
    print("RESULT: %s (exit_code=%d)"
          % ("READY" if result["exit_code"] == EXIT_READY
             else "NOT READY" if result["exit_code"] == EXIT_NOT_READY
             else "FATAL", result["exit_code"]))


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="python -m agent.preflight",
        description=(
            "Read-only S8 preflight for PawnMind: GET /health (no token), "
            "GET /state (Bearer), and the read-only "
            "get_recording_save_status action. Never sends a write op."
        ),
    )
    parser.add_argument("--config", required=True,
                        help="path to the game's ModData/pawnmind/bridge.conf")
    parser.add_argument("--json", action="store_true",
                        help="emit one stable JSON object for S8 logs")
    parser.add_argument("--timeout", type=float, default=8.0,
                        help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    result = run_preflight(args.config, timeout=args.timeout)
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        render_text(result)
    return int(result["exit_code"])


if __name__ == "__main__":
    sys.exit(main())
