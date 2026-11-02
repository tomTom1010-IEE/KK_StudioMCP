using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Native folders and parenting. Moves go through TreeNodeCtrl.SetParent, the path behind the workspace
    // parent/unparent buttons: Studio then calls the new parent's OnAttach (or OnDetach for the root), which keeps
    // the world placement and stores position/rotation relative to the new parent. A character accepts children
    // only on its attach-point nodes (accessory parents such as hands and head), not on its own node.
    internal sealed class HierarchyCommands
    {
        private const int MaxObjects = 64;
        private readonly Plugin _plugin;
        internal HierarchyCommands(Plugin plugin) { _plugin = plugin; }

        internal static bool Handles(string method)
            => method == "scene.add_folder" || method == "object.set_parent" || method == "character.attach_points";

        internal JToken Execute(string method, JObject args)
        {
            if (Studio.Studio.Instance == null) throw new InvalidOperationException("Studio is not ready.");
            if (method == "character.attach_points") return AttachPoints(_plugin.Characters.GetCharacter((int)args["objectId"]));
            if (method == "scene.add_folder") return AddFolder(args);
            if (method == "object.set_parent") return SetParent(args);
            throw new ArgumentException("Unsupported method: " + method);
        }

        private JObject AddFolder(JObject args)
        {
            var name = (string)args["name"];
            if (name == null || name.Trim().Length == 0 || name.Length > 256) throw new ArgumentException("name requires 1-256 characters.");
            Vector3? position = args["position"] == null ? (Vector3?)null : SceneCommands.Vector(args["position"]);
            Vector3? rotation = args["rotation"] == null ? (Vector3?)null : SceneCommands.Vector(args["rotation"]);
            ObjectCtrlInfo owner;
            var node = Target(args, out owner);
            var folder = AddObjectFolder.Add();
            if (folder == null) throw new InvalidOperationException("Studio did not create the folder.");
            folder.name = name;
            if (node != null && !Move(folder, node, owner))
            {
                Studio.Studio.DeleteNode(folder.treeNodeObject);
                throw new InvalidOperationException("Studio refused the parent, so the new folder was removed.");
            }
            if (position.HasValue) folder.objectInfo.changeAmount.pos = position.Value;
            if (rotation.HasValue) folder.objectInfo.changeAmount.rot = rotation.Value;
            return _plugin.Scene.Describe(folder);
        }

        private JObject SetParent(JObject args)
        {
            var ids = args["objectIds"] as JArray;
            if (ids == null || ids.Count == 0 || ids.Count > MaxObjects) throw new ArgumentException("objectIds requires 1-" + MaxObjects + " object IDs.");
            ObjectCtrlInfo owner;
            var node = Target(args, out owner);
            // Validate every object before moving any, so a bad ID or a loop leaves the scene unchanged.
            var targets = new List<ObjectCtrlInfo>();
            foreach (var id in ids)
            {
                if (id.Type != JTokenType.Integer) throw new ArgumentException("objectIds requires integer object IDs.");
                var target = _plugin.Scene.GetObject((int)id);
                if (targets.Contains(target)) throw new ArgumentException("Object " + (int)id + " is listed twice.");
                if (!target.treeNodeObject.enableChangeParent) throw new ArgumentException("Studio does not allow object " + (int)id + " to change parent.");
                for (var ancestor = node; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor == target.treeNodeObject) throw new ArgumentException("Object " + (int)id + " cannot be placed under itself or its own child.");
                targets.Add(target);
            }
            var destination = node == null ? null : node.childRoot != null ? node.childRoot : node;
            var results = new JArray();
            var rejected = new JArray();
            foreach (var target in targets)
            {
                var changed = target.treeNodeObject.parent != destination;
                if (changed && !Move(target, node, owner)) { rejected.Add(target.objectInfo.dicKey); changed = false; }
                var described = _plugin.Scene.Describe(target);
                described["changed"] = changed;
                results.Add(described);
            }
            return new JObject { ["objects"] = results, ["rejected"] = rejected, ["complete"] = rejected.Count == 0 };
        }

        // The tree node Studio's parent button would drop onto: the parent object's node, or one of a character's
        // attach-point nodes. Null is the scene root.
        private TreeNodeObject Target(JObject args, out ObjectCtrlInfo owner)
        {
            owner = null;
            var point = args["attachPoint"];
            var hasPoint = point != null && point.Type != JTokenType.Null;
            if (args["parentId"] == null || args["parentId"].Type == JTokenType.Null)
            {
                if (hasPoint) throw new ArgumentException("attachPoint requires a character parentId.");
                return null;
            }
            if (args["parentId"].Type != JTokenType.Integer) throw new ArgumentException("parentId requires an integer object ID.");
            owner = _plugin.Scene.GetObject((int)args["parentId"]);
            var character = owner as OCIChar;
            if (character != null)
            {
                if (!hasPoint || point.Type != JTokenType.String)
                    throw new ArgumentException("A character parent requires attachPoint, a key from character_attach_points such as a_n_hand_R.");
                foreach (var pair in character.dicAccessoryPoint)
                {
                    var info = PointInfo(pair.Value);
                    if (info == null || !string.Equals(info.key, (string)point, StringComparison.OrdinalIgnoreCase)) continue;
                    // OCIChar.OnAttach parents the child to this bone; a missing bone would fail after Studio has
                    // already moved the tree node.
                    if (!character.charInfo.loadEnd || Bone(character, info.key) == null)
                        throw new InvalidOperationException("The character's attach point is not loaded yet; inspect the character again.");
                    return pair.Key;
                }
                throw new ArgumentException("Unknown attach point " + (string)point + "; list them with character_attach_points.");
            }
            if (hasPoint) throw new ArgumentException("attachPoint applies only to a character parent.");
            if (!owner.treeNodeObject.enableAddChild)
                throw new ArgumentException("Studio does not accept children under a " + owner.GetType().Name + "; use a folder, item, route or character attach point.");
            return owner.treeNodeObject;
        }

        // Studio's own parent path. False means Studio left the object where it was.
        private static bool Move(ObjectCtrlInfo target, TreeNodeObject node, ObjectCtrlInfo owner)
        {
            Studio.Studio.Instance.treeNodeCtrl.SetParent(target.treeNodeObject, node);
            if (owner == null) return target.parentInfo == null && target.treeNodeObject.parent == null;
            return target.parentInfo == owner && target.treeNodeObject.parent == (node.childRoot != null ? node.childRoot : node);
        }

        private static GameObject Bone(OCIChar character, string key)
            => Enum.IsDefined(typeof(ChaReference.RefObjKey), key)
                ? character.charReference.GetReferenceInfo((ChaReference.RefObjKey)Enum.Parse(typeof(ChaReference.RefObjKey), key)) : null;

        private static JObject AttachPoints(OCIChar character)
        {
            var groups = GroupNames();
            var points = new JArray();
            foreach (var pair in character.dicAccessoryPoint.OrderBy(p => p.Value))
            {
                var info = PointInfo(pair.Value);
                List<ObjectInfo> children;
                character.oiCharInfo.child.TryGetValue(pair.Value, out children);
                string groupName = null;
                if (info != null) groups.TryGetValue(info.group, out groupName);
                points.Add(new JObject { ["attachPoint"] = info?.key, ["index"] = pair.Value, ["name"] = info?.name,
                    ["group"] = info?.group, ["groupName"] = groupName,
                    ["childIds"] = new JArray((children ?? new List<ObjectInfo>()).Select(c => c.dicKey)) });
            }
            return new JObject { ["objectId"] = character.objectInfo.dicKey, ["points"] = points };
        }

        // The character attach point an object hangs from, or null for any other parent.
        internal static int? AttachPointIndex(ObjectCtrlInfo target)
        {
            var character = target.parentInfo as OCIChar;
            var node = target.treeNodeObject;
            int index;
            if (character == null || node == null || node.parent == null || !character.dicAccessoryPoint.TryGetValue(node.parent, out index)) return null;
            return index;
        }

        internal static Info.AccessoryPointInfo PointInfo(int index)
        {
            Info.AccessoryPointInfo info;
            return Info.Instance.dicAccessoryPointInfo.TryGetValue(index, out info) ? info : null;
        }

        // Group labels read the way AddObjectAssist.InitAccessoryPoint reads them; row 0 is the sheet header.
        private static Dictionary<int, string> GroupNames()
        {
            var names = new Dictionary<int, string>();
            var sheet = Info.Instance.accessoryPointGroup;
            if (sheet == null || sheet.list == null) return names;
            for (var row = 1; row < sheet.list.Count; row++)
            {
                var cells = sheet.list[row].list;
                int id;
                if (cells != null && cells.Count > 1 && int.TryParse(cells[0], out id)) names[id] = cells[1];
            }
            return names;
        }
    }
}
