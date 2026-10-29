using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using static StudioMcp.Host.StudioTools;

namespace StudioMcp.Host;

[McpServerToolType]
public static class AnimationTools
{
    [McpServerTool, Description("Inspect a character's native Studio FK/IK enable state, groups and control counts. Does not require KKPE.")]
    public static Task<JsonObject> CharacterState(BridgeClient bridge, int objectId)
        => bridge.Call("character.state", Args(new { objectId }));

    [McpServerTool, Description("List native FK or IK controls for a character, including controlId, group, active state, saved guide-local position/rotation and world position. mode is FK or IK. Rotation units are Euler degrees; handles must be reacquired after character replacement or scene load.")]
    public static Task<JsonObject> CharacterControls(BridgeClient bridge, int objectId, string mode, int offset = 0, int limit = 50)
        => bridge.Call("character.controls", Args(new { objectId, mode, offset, limit }));

    [McpServerTool, Description("Enable/disable native FK or IK through Studio's original API. With group omitted, changes the whole mode using Studio's normal mutual-exclusion behavior. With group supplied, updates only that group's saved flag; use a group from character_state. Does not install or use a replacement solver.")]
    public static Task<JsonObject> CharacterKinematicsSet(BridgeClient bridge, string sceneSession, int objectId,
        string mode, bool enabled, string? group = null)
        => bridge.Call("character.kinematics", Args(new { sceneSession, objectId, mode, enabled, group }));

    [McpServerTool, Description("Set an inspected native FK rotation or IK target through its saved GuideObject. mode is FK or IK; enable that mode/group first. FK accepts rotation only; IK channels depend on the control's canPosition/canRotation. space is local (saved guide-local, default) or world; rotations are Euler degrees.")]
    public static Task<JsonObject> CharacterControlSet(BridgeClient bridge, string sceneSession, int objectId,
        string mode, int controlId, float[]? position = null, float[]? rotation = null, string space = "local")
        => bridge.Call("character.control_set", Args(new { sceneSession, objectId, mode, controlId, position, rotation, space }));

    [McpServerTool, Description("List KKPE-owned bones and their current local position, Euler rotation, scale and override state. Optional name query and pagination. Attached objects with their own KKPE controller are excluded. Returns available=false when KKPE is absent; reacquire boneId after hierarchy rebuilds.")]
    public static Task<JsonObject> PoseBones(BridgeClient bridge, int objectId, string query = "", int offset = 0, int limit = 50)
        => bridge.Call("pose.bones", Args(new { objectId, query, offset, limit }));

    [McpServerTool, Description("Edit a bone through KKPE's original override/persistence methods. Values are bone-local, rotations are Euler degrees. Active FK rotation follows KKPE's native FK route. Applies KKPE's correction pass before returning; no new override loop or save format.")]
    public static Task<JsonObject> PoseBoneSet(BridgeClient bridge, string sceneSession, int objectId, int boneId,
        float[]? position = null, float[]? rotation = null, float[]? scale = null)
        => bridge.Call("pose.bone_set", Args(new { sceneSession, objectId, boneId, position, rotation, scale }));

    [McpServerTool, Description("Reset the selected bone's KKPE overrides using KKPE's recorded originals, without resetting children. channel is all, position, rotation or scale. Does not reset unrelated native FK edits.")]
    public static Task<JsonObject> PoseBoneReset(BridgeClient bridge, string sceneSession, int objectId, int boneId, string channel = "all")
        => bridge.Call("pose.bone_reset", Args(new { sceneSession, objectId, boneId, channel }));

    [McpServerTool, Description("List KKPE blendshapes by actual rendererId and unique shapeName, with current Unity weight and override state. Optional renderer and shape-name filters; paginated. Returns available=false when KKPE is absent.")]
    public static Task<JsonObject> PoseBlendshapes(BridgeClient bridge, int objectId, int? rendererId = null,
        string query = "", int offset = 0, int limit = 50)
        => bridge.Call("pose.blendshapes", Args(new { objectId, rendererId, query, offset, limit }));

    [McpServerTool, Description("Set one named blendshape through KKPE's original name-based dirty-state API and apply it before returning. Preserves KKPE's saved originals and scene persistence. Typical weights are 0-100; finite negative/overdrive values are supported.")]
    public static Task<JsonObject> PoseBlendshapeSet(BridgeClient bridge, string sceneSession, int objectId, int rendererId, string shapeName, float weight)
        => bridge.Call("pose.blendshape_set", Args(new { sceneSession, objectId, rendererId, shapeName, weight }));

    [McpServerTool, Description("Remove one named KKPE blendshape override and restore its recorded original weight, preserving other shapes.")]
    public static Task<JsonObject> PoseBlendshapeReset(BridgeClient bridge, string sceneSession, int objectId, int rendererId, string shapeName)
        => bridge.Call("pose.blendshape_reset", Args(new { sceneSession, objectId, rendererId, shapeName }));
}
