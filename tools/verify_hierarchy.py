"""Verify native folder, parenting and character attach-point tools in an empty, disposable Studio via stdio MCP.

Never clears an existing scene or writes the source card. Raw responses belong in an ignored, machine-local output
directory.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import time
from mcp_client import McpClient


def run(root, card, sex, server, output):
    root, card, output = Path(root).resolve(), Path(card).resolve(), Path(output).resolve()
    original_hash = hashlib.sha256(card.read_bytes()).hexdigest()
    output.mkdir(parents=True, exist_ok=False)
    fixture = output / "character.png"
    shutil.copyfile(card, fixture)
    os.environ["STUDIOMCP_CONNECTION"] = str(root / "UserData/StudioMCP/connection.json")
    os.environ["STUDIOMCP_REQUEST_TIMEOUT_SECONDS"] = "180"
    client = McpClient(server, output)
    count, passed = 0, []

    def call(name, args=None, fail=False):
        nonlocal count
        result = client.request("tools/call", {"name": name, "arguments": args or {}}, timeout=185)
        count += 1
        (output / f"{count:03d}-{name}.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        if fail:
            assert result.get("isError"), (name, result)
            return
        if result.get("isError"):
            raise RuntimeError(f"{name}: {result}")
        payload = json.loads(next(item["text"] for item in result["content"] if item["type"] == "text"))
        return payload.get("result", payload)

    def write(tool, **args):
        return call(tool, {"sceneSession": session, **args})

    def reject(tool, **args):
        call(tool, {"sceneSession": session, **args}, fail=True)

    def close(actual, expected, tolerance=0.002):
        if isinstance(expected, list):
            assert len(actual) == len(expected), (actual, expected)
            for a, b in zip(actual, expected):
                close(a, b, tolerance)
        else:
            assert abs(actual - expected) < tolerance, (actual, expected)

    def objects():
        return {o["id"]: o for o in call("scene_snapshot", {"limit": 200})["objects"]}

    def loaded(actor):
        deadline = time.monotonic() + 60
        while not objects()[actor].get("loadEnd"):
            if time.monotonic() >= deadline:
                raise TimeoutError("The character did not finish loading.")
            time.sleep(0.5)

    def fk(actor, name):
        return next(c for c in call("character_controls", {"objectId": actor, "mode": "FK", "limit": 200})["controls"]
                    if c["name"] == name)

    try:
        tools = client.request("tools/list", {})["tools"]
        assert {"add_folder", "set_parent", "character_attach_points"} <= {tool["name"] for tool in tools}
        deadline = time.monotonic() + 90
        while True:
            try:
                status = call("studio_status")
                if status["ready"]:
                    break
            except RuntimeError:
                if time.monotonic() >= deadline:
                    raise
            if time.monotonic() >= deadline:
                raise TimeoutError("Studio did not become ready.")
            time.sleep(2)
        assert status["writesEnabled"]
        session = status["sceneSession"]
        assert call("scene_snapshot")["total"] == 0, "Refuse to modify a nonempty Studio; start an owned disposable instance."

        def sphere(name, x):
            return write("add_item", group=0, category=0, itemId=0, name=name, position=[x, 0.5, 0.5], scale=[2, 2, 2])["id"]
        a, b, c = sphere("Hierarchy sphere A", 1), sphere("Hierarchy sphere B", -1), sphere("Hierarchy sphere C", 0.3)
        lamp = write("add_light", lightId=call("catalog_search", {"query": "", "limit": 1})["lights"][0]["lightId"],
                     name="Hierarchy light", position=[0, 2, 0])["id"]
        actor = write("add_character", path=str(fixture), sex=sex, name="Hierarchy character")["id"]
        loaded(actor)

        building = write("add_folder", name="01 建築結構")
        assert building["type"] == "OCIFolder" and building["name"] == "01 建築結構" and building["parentId"] is None
        living = write("add_folder", name="02 客廳", parentId=building["id"])
        assert living["parentId"] == building["id"] and living["name"] == "02 客廳"
        passed.append("Native folders with saved names, including one created inside another")

        before = objects()
        moved = write("set_parent", objectIds=[a], parentId=living["id"])
        assert moved["complete"] and moved["objects"][0]["changed"] and moved["objects"][0]["parentId"] == living["id"]
        close(moved["objects"][0]["worldPosition"], before[a]["worldPosition"])
        shift = [p + d for p, d in zip(before[building["id"]]["position"], [0, 1, 0])]
        write("set_transform", objectId=building["id"], position=shift)
        close(objects()[a]["worldPosition"], [p + d for p, d in zip(before[a]["worldPosition"], [0, 1, 0])])
        passed.append("An item moved into a nested folder keeps its world placement and follows the outer folder")

        both = write("set_parent", objectIds=[a, b], parentId=building["id"])
        assert both["complete"] and all(o["changed"] and o["parentId"] == building["id"] for o in both["objects"]), both
        again = write("set_parent", objectIds=[b], parentId=building["id"])
        assert again["complete"] and not again["objects"][0]["changed"]
        passed.append("Several objects move in one call; an object already in place is reported unchanged")

        points = {p["attachPoint"]: p for p in call("character_attach_points", {"objectId": actor})["points"]}
        assert {"a_n_hand_R", "a_n_hand_L", "a_n_head", "a_n_waist"} <= set(points), sorted(points)
        assert all(p["name"] and p["groupName"] for p in points.values()), points
        before = objects()
        held = write("set_parent", objectIds=[c], parentId=actor, attachPoint="a_n_hand_R")["objects"][0]
        assert held["parentId"] == actor and held["attachPoint"] == "a_n_hand_R" and held["attachPointName"], held
        close(held["worldPosition"], before[c]["worldPosition"])
        right = next(p for p in call("character_attach_points", {"objectId": actor})["points"] if p["attachPoint"] == "a_n_hand_R")
        assert right["childIds"] == [c], right
        write("character_kinematics_set", objectId=actor, mode="FK", enabled=True)
        write("character_kinematics_set", objectId=actor, mode="FK", group="Body", enabled=True)
        arm, hand = fk(actor, "cf_j_arm00_R"), fk(actor, "cf_j_hand_R")
        assert arm["active"], arm
        held_before = objects()[c]["worldPosition"]
        write("character_control_set", objectId=actor, mode="FK", controlId=arm["controlId"],
              rotation=[arm["rotation"][0], arm["rotation"][1], arm["rotation"][2] + 45])
        time.sleep(0.5)
        hand_after, held_after = fk(actor, "cf_j_hand_R")["worldPosition"], objects()[c]["worldPosition"]
        assert math.dist(hand["worldPosition"], hand_after) > 0.05 and math.dist(held_before, held_after) > 0.05
        close(math.dist(held_after, hand_after), math.dist(held_before, hand["worldPosition"]), 0.005)
        passed.append("An item on the right-hand attach point keeps its world placement and follows an FK arm bend")

        state = objects()
        reject("set_parent", objectIds=[building["id"]], parentId=living["id"])
        reject("set_parent", objectIds=[a], parentId=a)
        reject("set_parent", objectIds=[a], parentId=lamp)
        reject("set_parent", objectIds=[a], parentId=actor)
        reject("set_parent", objectIds=[a], parentId=actor, attachPoint="a_n_not_a_point")
        reject("set_parent", objectIds=[a], attachPoint="a_n_hand_R")
        reject("set_parent", objectIds=[actor], parentId=actor, attachPoint="a_n_hand_L")
        reject("set_parent", objectIds=[a, a], parentId=living["id"])
        reject("set_parent", objectIds=[a, 987654], parentId=living["id"])
        reject("add_folder", name=" ")
        reject("batch_apply", operations=[{"method": "object.set_parent", "args": {"objectIds": [a], "parentId": living["id"]}}])
        unchanged = objects()
        assert all(unchanged[i]["parentId"] == state[i]["parentId"] for i in state), "A rejected request moved an object."
        passed.append("Loops, self/light/bare-character parents, unknown attach points, duplicates, missing IDs and batches are rejected without moving anything")

        before = objects()
        rooted = write("set_parent", objectIds=[a])["objects"][0]
        assert rooted["parentId"] is None and rooted["changed"]
        close(rooted["worldPosition"], before[a]["worldPosition"])
        props = write("add_folder", name="Left hand props", parentId=actor, attachPoint="a_n_hand_L", position=[0, 0, 0])
        assert props["parentId"] == actor and props["attachPoint"] == "a_n_hand_L"
        close(props["position"], [0, 0, 0])
        assert write("set_parent", objectIds=[b], parentId=props["id"])["complete"]
        passed.append("Unparenting keeps world placement; a folder can be created directly on the left-hand attach point")

        expected = objects()
        checkpoint = write("scene_checkpoint")
        write("scene_restore", path=checkpoint["path"])
        new_status = call("studio_status")
        assert new_status["sceneSession"] != session
        session = new_status["sceneSession"]
        loaded(actor)
        time.sleep(1)
        restored = objects()
        assert set(restored) == set(expected), (sorted(restored), sorted(expected))
        for key, old in expected.items():
            new = restored[key]
            assert new["parentId"] == old["parentId"] and new.get("attachPoint") == old.get("attachPoint"), (old, new)
            close(new["worldPosition"], old["worldPosition"], 0.01)
        assert restored[building["id"]]["name"] == "01 建築結構" and restored[props["id"]]["name"] == "Left hand props"
        passed.append("Folder names, folder parents and hand attachments survive a scene save and reload")

        assert hashlib.sha256(card.read_bytes()).hexdigest() == original_hash
        summary = {"tools": len(tools), "passed": passed, "checkpoint": checkpoint["path"], "sourceCardUnchanged": True}
        (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"tools": len(tools), "passed": passed, "sourceCardUnchanged": True}, ensure_ascii=False, indent=2))
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--card", required=True)
    parser.add_argument("--sex", choices=("female", "male"), default="female")
    parser.add_argument("--server", default="src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    run(args.game_root, args.card, args.sex, args.server, args.output)
