"""AgentSleep Windows discovery prototype. No display mutation implemented."""
import argparse
import ctypes as C
import json
import os
import sys
from datetime import datetime, timezone

U32 = C.c_uint32
I32 = C.c_int32
U16 = C.c_uint16
QDC_ALL_PATHS = 1
QDC_ONLY_ACTIVE_PATHS = 2
QDC_VIRTUAL_MODE_AWARE = 0x10
PATH_ACTIVE = 1
PATH_SUPPORT_VIRTUAL_MODE = 8
GET_TARGET_NAME = 2
ERROR_INSUFFICIENT_BUFFER = 122
INDIRECT = {16, 17}
TECH = {-1: "other", 0: "VGA", 4: "DVI", 5: "HDMI", 6: "LVDS",
        10: "DisplayPort", 11: "embedded DisplayPort", 15: "Miracast",
        16: "indirect wired", 17: "indirect virtual", 18: "DisplayPort USB tunnel",
        -2147483648: "internal"}

class Luid(C.Structure):
    _fields_ = [("low", U32), ("high", I32)]

class Ratio(C.Structure):
    _fields_ = [("numerator", U32), ("denominator", U32)]

class Point(C.Structure):
    _fields_ = [("x", I32), ("y", I32)]

class Source(C.Structure):
    _fields_ = [("adapter", Luid), ("id", U32), ("index", U32), ("status", U32)]

class Target(C.Structure):
    _fields_ = [("adapter", Luid), ("id", U32), ("index", U32), ("technology", I32),
                ("rotation", I32), ("scaling", I32), ("refresh", Ratio),
                ("scanline", I32), ("available", I32), ("status", U32)]

class Path(C.Structure):
    _fields_ = [("source", Source), ("target", Target), ("flags", U32)]

class SourceMode(C.Structure):
    _fields_ = [("width", U32), ("height", U32), ("pixel_format", I32), ("position", Point)]

class ModePayload(C.Union):
    # The largest Windows union member (VIDEO_SIGNAL_INFO) is 48 bytes, 8-byte aligned.
    _fields_ = [("source", SourceMode), ("opaque", C.c_uint64 * 6)]

class Mode(C.Structure):
    _fields_ = [("kind", U32), ("id", U32), ("adapter", Luid), ("data", ModePayload)]

class Header(C.Structure):
    _fields_ = [("type", I32), ("size", U32), ("adapter", Luid), ("id", U32)]

class DeviceName(C.Structure):
    _fields_ = [("header", Header), ("flags", U32), ("technology", I32),
                ("manufacturer", U16), ("product", U16), ("connector", U32),
                ("friendly", C.c_wchar * 64), ("device_path", C.c_wchar * 128)]

def luid(a):
    return f"{a.high & 0xffffffff:08x}:{a.low:08x}"

def validate_abi():
    expected = {Luid: 8, Source: 20, Target: 48, Path: 72, Mode: 64, Header: 20, DeviceName: 420}
    for struct, size in expected.items():
        if C.sizeof(struct) != size:
            raise RuntimeError(f"Unsupported Windows ABI: {struct.__name__} size {C.sizeof(struct)}, expected {size}")

def snapshot(all_paths=False):
    if os.name != "nt":
        raise RuntimeError("Display discovery requires an interactive Windows desktop session.")
    validate_abi()
    user32 = C.WinDLL("user32")
    counts = user32.GetDisplayConfigBufferSizes
    counts.argtypes = [U32, C.POINTER(U32), C.POINTER(U32)]
    counts.restype = I32
    query = user32.QueryDisplayConfig
    query.argtypes = [U32, C.POINTER(U32), C.POINTER(Path), C.POINTER(U32),
                      C.POINTER(Mode), C.c_void_p]
    query.restype = I32
    get_device = user32.DisplayConfigGetDeviceInfo
    get_device.argtypes = [C.POINTER(Header)]
    get_device.restype = I32
    flags = (QDC_ALL_PATHS if all_paths else QDC_ONLY_ACTIVE_PATHS) | QDC_VIRTUAL_MODE_AWARE
    for _ in range(4):
        pc, mc = U32(), U32()
        rc = counts(flags, C.byref(pc), C.byref(mc))
        if rc:
            raise OSError(f"GetDisplayConfigBufferSizes failed (Windows error {rc}); run from the desktop session.")
        paths, modes = (Path * pc.value)(), (Mode * mc.value)()
        rc = query(flags, C.byref(pc), paths, C.byref(mc), modes, None)
        if rc == ERROR_INSUFFICIENT_BUFFER:
            continue
        if rc:
            raise OSError(f"QueryDisplayConfig failed (Windows error {rc}); run from the desktop session.")
        break
    else:
        raise RuntimeError("Display topology changed repeatedly. Retry when it is stable.")
    cache, result = {}, []
    for path in paths[:pc.value]:
        source, target = path.source, path.target
        target_key = (luid(target.adapter), int(target.id))
        if target_key not in cache:
            name = DeviceName()
            name.header.type, name.header.size = GET_TARGET_NAME, C.sizeof(name)
            name.header.adapter, name.header.id = target.adapter, target.id
            status = get_device(C.byref(name.header))
            cache[target_key] = (name.friendly, name.device_path, status)
        friendly, device_path, status = cache[target_key]
        source_index = (source.index >> 16) if (path.flags & PATH_SUPPORT_VIRTUAL_MODE) else source.index
        mode = None
        if source_index < mc.value:
            entry = modes[source_index]
            if entry.kind == 1 and entry.id == source.id and luid(entry.adapter) == luid(source.adapter):
                sm = entry.data.source
                mode = {"width": sm.width, "height": sm.height, "x": sm.position.x, "y": sm.position.y}
        result.append({"device_path": device_path or None, "friendly": friendly or None,
                       "target_adapter": target_key[0], "target_id": target_key[1],
                       "source_adapter": luid(source.adapter), "source_id": int(source.id),
                       "technology": int(target.technology), "connector": TECH.get(int(target.technology), "unknown"),
                       "available": bool(target.available), "active": bool(path.flags & PATH_ACTIVE),
                       "name_error": int(status) or None,
                       "desktop_mode": mode, "rotation": int(target.rotation),
                       "refresh": [target.refresh.numerator, target.refresh.denominator]})
    return result

def plan(paths, exact_path):
    """Describe a conceptual target-only trial, not a native/restore snapshot."""
    if not exact_path or not exact_path.strip():
        raise ValueError("Provide an exact non-empty monitor device path from discover.")
    matches = [p for p in paths if p["device_path"] and p["device_path"].casefold() == exact_path.casefold()]
    if not matches:
        raise ValueError("Exact monitor device path not found. Run discover again; no changes were made.")
    if len({(p["target_adapter"], p["target_id"]) for p in matches}) != 1:
        raise ValueError("Path maps to more than one target; cannot select it.")
    available = [p for p in matches if p["available"] and p["technology"] in INDIRECT]
    if not available:
        raise ValueError("Target is unavailable or lacks an indirect/virtual connector hint; no trial is proposed.")
    active_candidates = [p for p in available if p["active"]]
    selected = active_candidates[0] if len(active_candidates) == 1 else available[0] if len(available) == 1 else None
    if selected is None:
        raise ValueError("Target has ambiguous source paths; narrow it in Windows before planning.")
    active = [p for p in paths if p["active"]]
    if not active:
        raise ValueError("No active desktop topology could be read; cannot plan a reversible trial.")
    target_identity = (selected["target_adapter"], selected["target_id"])
    proposed_disable = [p for p in active if (p["target_adapter"], p["target_id"]) != target_identity]
    return {"dry_run": True, "created_utc": datetime.now(timezone.utc).isoformat(),
            "selected": selected, "baseline_active_display_paths": active,
            "would_remove_from_desktop": proposed_disable,
            "ready_to_apply": False,
            "remaining_checks": ["Manually confirm the exact monitor belongs to the installed virtual driver. Indirect alone does not prove virtual.",
                                 "Validate native supplied topology without saving to the Windows database.",
                                 "Arm and verify independent, time-limited rollback before any display change.",
                                 "Verify capture/input/physical panel behavior with local supervision."]}

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    cmds = parser.add_subparsers(dest="cmd", required=True)
    cmds.add_parser("status", help="show only active display paths")
    cmds.add_parser("discover", help="list available and inactive display paths")
    p = cmds.add_parser("plan", help="dry run with one explicitly selected device path")
    p.add_argument("--target-device-path", required=True)
    a = parser.parse_args(argv)
    try:
        paths = snapshot(all_paths=a.cmd != "status")
        out = plan(paths, a.target_device_path) if a.cmd == "plan" else {"read_only": True, "paths": paths}
    except (OSError, ValueError, RuntimeError) as exc:
        print(f"AgentSleep: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(out, indent=2))
    return 0

if __name__ == "__main__":
    sys.exit(main())
