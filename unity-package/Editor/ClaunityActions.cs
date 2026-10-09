using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Claunity
{

// ── Data classes ──────────────────────────────────────────────────────────────

[Serializable]
public class ClaunityActionResponse
{
    public string          message;
    public ActionPayload[] actions;
    public string          question;      // project execution: Claude needs user input
    public bool            task_complete; // project execution: this task is fully done
}

[Serializable]
public class ActionPayload
{
    public string   type;
    // GameObject identity
    public string   name;
    public string   path;
    // Common write fields
    public string   newName;
    public string   parent;
    public string   componentType;
    public bool     active;
    public string   tag;
    public string   layerName;
    public bool     isStatic;
    public int      count;
    // Transform
    public Vec3Json position;
    public Vec3Json rotation;
    public Vec3Json scale;
    // Material
    public string   savePath;
    public string   shader;
    public string   materialPath;
    public string   propertyName;
    public string   propertyPath;   // dot-notation for set/get_serialized_property
    public float    propertyValue;
    public ColorJson color;
    // Scene / Asset
    public string   assetPath;
    public string   sceneName;
    public bool     additive;
    public bool     recursive;    // list_assets: include subfolders
    // Primitive / Mesh
    public string   primitiveType; // add_primitive: Cube, Sphere, Capsule, Cylinder, Plane, Quad
    public string   meshType;      // set_mesh: Cube, Sphere, Capsule, Cylinder, Plane, Quad
    // Editor
    public string   menuPath;
    // Vision
    public string   view; // "scene" | "game"
    // Scripts
    public string   scriptName;
    public string   scriptPath;
    public string   content;
    // UI
    public string   elementType;  // create_ui_element: Canvas/Panel/Button/Text/Image/etc.
    public string   text;         // UI text content
    public string   anchor;       // RectTransform anchor preset: center/top-left/stretch/etc.
    public Vec3Json size;         // UI size (x=width, y=height)
    public int      fontSize;     // Text font size
    // Animator
    public string   fromState;     // transition source state (or "Any State" / "Entry")
    public string   toState;       // transition target state
    public string   parameterType; // Float / Int / Bool / Trigger
    public string   motion;        // asset path to AnimationClip
    public bool     isDefault;     // whether this is the default state
    public bool     hasExitTime;   // transition: use exit time
    public float    exitTime;      // transition: normalized exit time (0-1)
    public float    transitionDuration; // transition: blend duration
    public int      layer;         // animator layer index (default 0)
    public AnimatorConditionJson[] conditions; // transition conditions
}

[Serializable] public class Vec3Json  { public float x, y, z; }
[Serializable] public class ColorJson { public float r, g, b, a = 1f; }

[Serializable]
public class AnimatorConditionJson
{
    public string parameter; // parameter name
    public string mode;      // Greater, Less, Equals, NotEqual, If, IfNot
    public float  threshold; // numeric threshold (for Float/Int conditions)
}

// ── Pending post-reload actions ────────────────────────────────────────────────

[Serializable]
public class PendingActionsWrapper
{
    public ActionPayload[] actions;
    public bool autoResume;
}

// ── Executor ──────────────────────────────────────────────────────────────────

public static class ClaunityActionExecutor
{
    private const string PendingKey   = "Claunity_PendingActions";
    private const string PendingTsKey = "Claunity_PendingTimestamp";
    private const double PendingMaxAgeSec = 120.0; // discard if older than 2 minutes

    public static void SavePendingActions(ActionPayload[] actions, bool autoResume = false)
    {
        EditorPrefs.SetString(PendingKey,
            JsonUtility.ToJson(new PendingActionsWrapper { actions = actions, autoResume = autoResume }));
        EditorPrefs.SetString(PendingTsKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    public static PendingActionsWrapper ConsumePendingActions()
    {
        var json = EditorPrefs.GetString(PendingKey, "");
        if (string.IsNullOrEmpty(json)) return null;

        // Check timestamp — discard stale pending actions
        var tsStr = EditorPrefs.GetString(PendingTsKey, "");
        EditorPrefs.DeleteKey(PendingKey);
        EditorPrefs.DeleteKey(PendingTsKey);

        // No timestamp = saved before this version = stale, discard
        if (string.IsNullOrEmpty(tsStr)) return null;

        if (long.TryParse(tsStr, out long savedTs))
        {
            var ageSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - savedTs;
            if (ageSec > PendingMaxAgeSec) return null; // stale — discard silently
        }

        try
        {
            var w = JsonUtility.FromJson<PendingActionsWrapper>(json);
            if (w == null) return null;
            // Return if there are pending actions OR if autoResume is set
            return (w.actions?.Length > 0 || w.autoResume) ? w : null;
        }
        catch { return null; }
    }

    /// <summary>Read-only actions — execute immediately, no confirmation needed.</summary>
    public static bool IsReadAction(string type) =>
        type == "get_scene_info"           || type == "get_gameobject" ||
        type == "read_script"              || type == "take_screenshot" ||
        type == "get_component_property"   || type == "get_serialized_property" ||
        type == "get_animator_info"        || type == "get_input_asset_info"   ||
        type == "get_console_logs"         || type == "get_material_info"      ||
        type == "select_gameobject"        || type == "validate_script"        ||
        type == "get_performance_stats"    || type == "get_audiosource_info"   ||
        type == "list_assets";

    private static readonly HashSet<string> _knownActions = new HashSet<string> {
        "create_gameobject", "delete_gameobject", "rename_gameobject", "duplicate_gameobject",
        "update_gameobject", "set_active", "reparent",
        "move_gameobject", "rotate_gameobject", "scale_gameobject",
        "add_component", "remove_component", "set_component_property",
        "set_serialized_property", "get_serialized_property",
        "create_material", "assign_material", "modify_material",
        "save_scene", "create_scene", "load_scene",
        "create_prefab", "add_asset_to_scene",
        "execute_menu_item", "add_primitive", "set_mesh", "create_ui_element", "set_rect_transform",
        "enter_play_mode", "exit_play_mode", "pause_play_mode",
        "read_script", "create_script", "edit_script", "attach_script", "recompile_scripts",
        "create_animator_controller", "add_animator_parameter", "add_animator_state",
        "add_animator_transition", "get_animator_info",
        "create_input_action_asset", "add_input_action_map", "add_input_action",
        "add_input_binding", "get_input_asset_info",
        "create_scriptable_object",
        "add_tag", "add_layer", "set_physics_gravity", "set_time_setting",
        "bake_navmesh", "add_package", "build_player",
        "take_screenshot", "get_scene_info", "get_gameobject", "get_component_property",
        "set_transform", "select_gameobject",
        "get_material_info", "get_console_logs",
        "unload_scene", "delete_scene",
        "delete_asset", "move_asset", "list_assets", "assign_asset",
        "validate_script", "get_performance_stats",
        "get_audiosource_info", "set_audiosource_property", "assign_audioclip",
    };

    /// <summary>Returns true if the action type is recognized.</summary>
    public static bool IsKnownAction(string type) => _knownActions.Contains(type);

    /// <summary>Executes an action. Returns result string starting with ✓ or ✗.</summary>
    public static string Execute(ActionPayload action)
    {
        try
        {
            switch (action.type)
            {
                // ── GameObject ─────────────────────────────────────────────
                case "create_gameobject":    return CreateGameObject(action);
                case "delete_gameobject":    return DeleteGameObject(action);
                case "rename_gameobject":    return RenameGameObject(action);
                case "duplicate_gameobject": return DuplicateGameObject(action);
                case "update_gameobject":    return UpdateGameObject(action);
                case "set_active":           return SetActive(action);
                case "reparent":             return Reparent(action);
                // ── Transform ──────────────────────────────────────────────
                case "move_gameobject":      return MoveGameObject(action);
                case "rotate_gameobject":    return RotateGameObject(action);
                case "scale_gameobject":     return ScaleGameObject(action);
                case "set_transform":        return SetTransform(action);
                case "select_gameobject":    return SelectGameObject(action);
                // ── Component ──────────────────────────────────────────────
                case "add_component":              return AddComponent(action);
                case "remove_component":           return RemoveComponent(action);
                case "set_component_property":     return SetComponentProperty(action);
                case "set_serialized_property":    return SetSerializedProperty(action);
                case "get_serialized_property":    return GetSerializedProperty(action);
                // ── Material ───────────────────────────────────────────────
                case "create_material":      return CreateMaterial(action);
                case "assign_material":      return AssignMaterial(action);
                case "modify_material":      return ModifyMaterial(action);
                case "get_material_info":    return GetMaterialInfo(action);
                // ── Scene ──────────────────────────────────────────────────
                case "save_scene":           return SaveScene(action);
                case "create_scene":         return CreateScene(action);
                case "load_scene":           return LoadScene(action);
                case "unload_scene":         return UnloadScene(action);
                case "delete_scene":         return DeleteScene(action);
                case "delete_asset":         return DeleteAsset(action);
                case "move_asset":           return MoveAsset(action);
                case "list_assets":          return ListAssets(action);
                case "assign_asset":         return AssignAsset(action);
                // ── Prefab / Asset ─────────────────────────────────────────
                case "create_prefab":        return CreatePrefab(action);
                case "add_asset_to_scene":   return AddAssetToScene(action);
                // ── Editor ─────────────────────────────────────────────────
                case "execute_menu_item":      return ExecuteMenuItem(action);
                case "add_primitive":          return AddPrimitive(action);
                case "set_mesh":               return SetMesh(action);
                case "create_ui_element":      return CreateUIElement(action);
                case "set_rect_transform":     return SetRectTransform(action);
                case "enter_play_mode":      return EnterPlayMode();
                case "exit_play_mode":       return ExitPlayMode();
                case "pause_play_mode":      return PausePlayMode();
                // ── Scripts ────────────────────────────────────────────────
                case "read_script":          return ReadScript(action);
                case "create_script":        return CreateScript(action);
                case "edit_script":          return EditScript(action);
                case "attach_script":        return AttachScript(action);
                case "recompile_scripts":    return RecompileScripts();
                // ── Animator ──────────────────────────────────────────────
                case "create_animator_controller": return CreateAnimatorController(action);
                case "add_animator_parameter":     return AddAnimatorParameter(action);
                case "add_animator_state":         return AddAnimatorState(action);
                case "add_animator_transition":    return AddAnimatorTransition(action);
                case "get_animator_info":          return GetAnimatorInfo(action);
                // ── Input System ──────────────────────────────────────────
                case "create_input_action_asset":  return CreateInputActionAsset(action);
                case "add_input_action_map":       return AddInputActionMap(action);
                case "add_input_action":           return AddInputAction(action);
                case "add_input_binding":          return AddInputBinding(action);
                case "get_input_asset_info":       return GetInputAssetInfo(action);
                // ── ScriptableObject ───────────────────────────────────────
                case "create_scriptable_object":   return CreateScriptableObject(action);
                // ── Project Settings ───────────────────────────────────────
                case "add_tag":                    return AddTag(action);
                case "add_layer":                  return AddLayer(action);
                case "set_physics_gravity":        return SetPhysicsGravity(action);
                case "set_time_setting":           return SetTimeSetting(action);
                // ── NavMesh / Packages ────────────────────────────────────
                case "bake_navmesh":         return BakeNavMesh(action);
                case "add_package":          return AddPackage(action);
                // ── Build ──────────────────────────────────────────────────
                case "build_player":         return BuildPlayer(action);
                // ── Vision ─────────────────────────────────────────────────
                case "take_screenshot":      return TakeScreenshot(action);
                // ── Read (info) ────────────────────────────────────────────
                case "get_scene_info":          return GetSceneInfo();
                case "get_gameobject":          return GetGameObjectInfo(action);
                case "get_component_property":  return GetComponentProperty(action);
                case "get_console_logs":        return GetConsoleLogs(action);
                // ── Script Validation ──────────────────────────────────────────
                case "validate_script":          return ValidateScript(action);
                // ── Performance ────────────────────────────────────────────────
                case "get_performance_stats":    return GetPerformanceStats();
                // ── Audio ──────────────────────────────────────────────────────
                case "get_audiosource_info":     return GetAudioSourceInfo(action);
                case "set_audiosource_property": return SetAudioSourceProperty(action);
                case "assign_audioclip":         return AssignAudioClip(action);
                default: return $"✗ Unknown action type: '{action.type}'";
            }
        }
        catch (Exception e)
        {
            return $"✗ {action.type}: {e.Message}";
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GameObject FindObject(ActionPayload a)
    {
        string target = !string.IsNullOrEmpty(a.name) ? a.name : a.path;
        if (string.IsNullOrEmpty(target)) return null;

        // Fast path: active objects only
        var go = GameObject.Find(target);
        if (go != null) return go;

        // Fallback: search all objects including inactive (name match only)
        foreach (var obj in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (obj.hideFlags != HideFlags.None) continue; // skip editor-only objects
            if (obj.name == target) return obj;
        }
        return null;
    }

    private static string Label(ActionPayload a) =>
        !string.IsNullOrEmpty(a.name) ? a.name :
        !string.IsNullOrEmpty(a.path) ? a.path :
        "(name not provided — set 'name' parameter)";

    private static Vector3 ToVec3(Vec3Json v) =>
        v != null ? new Vector3(v.x, v.y, v.z) : Vector3.zero;

    /// JsonUtility.FromJson never leaves object-typed fields (Vec3Json, ColorJson)
    /// null when the source JSON simply omits that key — it allocates a
    /// zero-valued instance instead. Every "optional" a.position/rotation/scale/
    /// size/color check in this file (`if (a.position != null) ...`) silently
    /// breaks as a result: omitting the field is indistinguishable from
    /// explicitly passing {x:0,y:0,z:0}. Call this right after FromJson, passing
    /// the exact raw JSON string that was parsed, to null out whichever of these
    /// fields weren't actually present on the wire.
    public static void ClearAbsentOptionalVectors(ActionPayload payload, string rawJson)
    {
        if (payload == null || string.IsNullOrEmpty(rawJson)) return;
        if (!rawJson.Contains("\"position\"")) payload.position = null;
        if (!rawJson.Contains("\"rotation\"")) payload.rotation = null;
        if (!rawJson.Contains("\"scale\""))    payload.scale    = null;
        if (!rawJson.Contains("\"size\""))     payload.size     = null;
        if (!rawJson.Contains("\"color\""))    payload.color    = null;
    }

    private static Type FindComponentType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // First pass: exact name or common Unity namespaces
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName)
                 ?? asm.GetType("UnityEngine." + typeName)
                 ?? asm.GetType("UnityEngine.UI." + typeName)
                 ?? asm.GetType("UnityEngine.AI." + typeName);
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
        }

        // Second pass: match by simple name, covering any user namespace
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.Name == typeName && typeof(Component).IsAssignableFrom(t))
                    return t;
            }
        }

        return null;
    }

    private static string EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath)) return folderPath;
        var parent = System.IO.Path.GetDirectoryName(folderPath).Replace('\\', '/');
        var child  = System.IO.Path.GetFileName(folderPath);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, child);
        return folderPath;
    }

    private static bool ValidateSavePath(string path, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets"))
        {
            error = "✗ savePath must start with 'Assets/'";
            return false;
        }
        // Block path traversal via ".."
        if (path.Contains(".."))
        {
            error = "✗ savePath must not contain '..'";
            return false;
        }
        return true;
    }

    // ── GameObject ─────────────────────────────────────────────────────────────

    private static string CreateGameObject(ActionPayload a)
    {
        var go = new GameObject(string.IsNullOrEmpty(a.name) ? "New Object" : a.name);
        Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create {go.name}");
        if (a.position != null) go.transform.position = ToVec3(a.position);
        if (a.rotation != null) go.transform.eulerAngles = ToVec3(a.rotation);
        // ClearAbsentOptionalVectors (called at parse time) already nulls a.scale
        // when the request omits it. This extra all-zero check is defense in
        // depth against an explicit (0,0,0) scale slipping through instead — a
        // real, deliberate (0,0,0) scale is never a legitimate GameObject state
        // (it and its children become invisible, and reparenting anything under
        // it corrupts the children's local position/scale when Unity tries to
        // preserve world transform through a non-invertible zero-scale matrix).
        bool scaleGiven = a.scale != null && (a.scale.x != 0f || a.scale.y != 0f || a.scale.z != 0f);
        go.transform.localScale = scaleGiven ? ToVec3(a.scale) : Vector3.one;
        if (!string.IsNullOrEmpty(a.parent))
        {
            var p = GameObject.Find(a.parent);
            if (p != null) Undo.SetTransformParent(go.transform, p.transform, $"Claunity: Parent {go.name}");
        }
        return $"✓ Created '{go.name}'";
    }

    private static string DeleteGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        var label = go.name;
        Undo.DestroyObjectImmediate(go);
        return $"✓ Deleted '{label}'";
    }

    private static string RenameGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        if (string.IsNullOrEmpty(a.newName)) return "✗ newName is required";
        Undo.RecordObject(go, $"Claunity: Rename {go.name}");
        var old = go.name; go.name = a.newName;
        return $"✓ Renamed '{old}' → '{a.newName}'";
    }

    private static string DuplicateGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        int copies = a.count > 0 ? a.count : 1;
        var names = new List<string>();
        for (int i = 0; i < copies; i++)
        {
            var copy = UnityEngine.Object.Instantiate(go, go.transform.parent);
            copy.name = string.IsNullOrEmpty(a.newName) ? go.name + " (Copy)" : copies > 1 ? $"{a.newName} ({i + 1})" : a.newName;
            Undo.RegisterCreatedObjectUndo(copy, $"Claunity: Duplicate {go.name}");
            names.Add(copy.name);
        }
        return $"✓ Duplicated '{go.name}' as {string.Join(", ", names)}";
    }

    private static string UpdateGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Undo.RecordObject(go, $"Claunity: Update {go.name}");
        if (!string.IsNullOrEmpty(a.newName))  go.name     = a.newName;
        if (!string.IsNullOrEmpty(a.tag))      go.tag      = a.tag;
        if (!string.IsNullOrEmpty(a.layerName))
        {
            int layer = LayerMask.NameToLayer(a.layerName);
            if (layer >= 0) go.layer = layer;
        }
        GameObjectUtility.SetStaticEditorFlags(go,
            a.isStatic ? StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic
                        : 0);
        return $"✓ Updated '{go.name}'";
    }

    private static string SetActive(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Undo.RecordObject(go, $"Claunity: SetActive {go.name}");
        go.SetActive(a.active);
        return $"✓ '{go.name}' is now {(a.active ? "active" : "inactive")}";
    }

    private static string Reparent(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Transform newParent = null;
        if (!string.IsNullOrEmpty(a.parent))
        {
            var p = GameObject.Find(a.parent);
            if (p == null) return $"✗ Parent not found: '{a.parent}'";
            newParent = p.transform;
        }
        Undo.SetTransformParent(go.transform, newParent, $"Claunity: Reparent {go.name}");
        return $"✓ '{go.name}' → parent: '{a.parent ?? "root"}'";
    }

    // ── Transform ──────────────────────────────────────────────────────────────

    private static string MoveGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Undo.RecordObject(go.transform, $"Claunity: Move {go.name}");
        go.transform.position = ToVec3(a.position);
        return $"✓ Moved '{go.name}' to ({a.position?.x}, {a.position?.y}, {a.position?.z})";
    }

    private static string RotateGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Undo.RecordObject(go.transform, $"Claunity: Rotate {go.name}");
        go.transform.eulerAngles = ToVec3(a.rotation);
        return $"✓ Rotated '{go.name}'";
    }

    private static string ScaleGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Undo.RecordObject(go.transform, $"Claunity: Scale {go.name}");
        go.transform.localScale = ToVec3(a.scale);
        return $"✓ Scaled '{go.name}'";
    }

    private static string SetTransform(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        if (a.position == null && a.rotation == null && a.scale == null)
            return "✗ At least one of position, rotation, or scale is required";
        Undo.RecordObject(go.transform, $"Claunity: Set transform {go.name}");
        // Local space throughout (matches localScale, which was already local) —
        // this is what "set this object's transform" should mean for a parented
        // object, e.g. an object pulled out of an organized staging scene to
        // build a prefab from. Use move_gameobject/rotate_gameobject for the
        // dedicated world-space equivalents.
        if (a.position != null) go.transform.localPosition    = ToVec3(a.position);
        if (a.rotation != null) go.transform.localEulerAngles = ToVec3(a.rotation);
        if (a.scale    != null) go.transform.localScale       = ToVec3(a.scale);
        var sb = new StringBuilder($"✓ Set transform on '{go.name}'");
        if (a.position != null) sb.Append($"  localPos={go.transform.localPosition}");
        if (a.rotation != null) sb.Append($"  localRot={go.transform.localEulerAngles}");
        if (a.scale    != null) sb.Append($"  scale={go.transform.localScale}");
        return sb.ToString();
    }

    private static string SelectGameObject(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        return $"✓ Selected '{go.name}'";
    }

    // ── Component ──────────────────────────────────────────────────────────────

    private static string AddComponent(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        var type = FindComponentType(a.componentType);
        if (type == null) return $"✗ Component type not found: '{a.componentType}'";
        if (go.GetComponent(type) != null) return $"ℹ '{go.name}' already has {a.componentType}";
        Undo.AddComponent(go, type);
        return $"✓ Added {a.componentType} to '{go.name}'";
    }

    private static string RemoveComponent(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        var type = FindComponentType(a.componentType);
        if (type == null) return $"✗ Component type not found: '{a.componentType}'";
        var comp = go.GetComponent(type);
        if (comp == null) return $"✗ '{go.name}' doesn't have {a.componentType}";
        Undo.DestroyObjectImmediate(comp);
        return $"✓ Removed {a.componentType} from '{go.name}'";
    }

    private static string SetComponentProperty(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";

        var type = FindComponentType(a.componentType);
        if (type == null) return $"✗ Component type not found: '{a.componentType}'";

        var comp = go.GetComponent(type);
        if (comp == null) return $"✗ '{go.name}' doesn't have {a.componentType}";

        if (string.IsNullOrEmpty(a.propertyName)) return "✗ propertyName is required";

        // Resolve member (property or field, case-insensitive, includes private [SerializeField])
        var propInfo  = type.GetProperty(a.propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        var fieldInfo = propInfo == null
            ? type.GetField(a.propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)
            : null;

        var memberType = propInfo?.PropertyType ?? fieldInfo?.FieldType;
        if (memberType == null)
            return $"✗ Writable property/field '{a.propertyName}' not found on {a.componentType} (checked public + private [SerializeField])";
        if (propInfo != null && !propInfo.CanWrite)
            return $"✗ Property '{a.propertyName}' is read-only";

        object value;
        if (typeof(UnityEngine.Object).IsAssignableFrom(memberType))
        {
            // Object reference — find the target GameObject and resolve the right type
            value = ResolveUnityObjectRef(a.content, memberType);
            if (value == null)
                return $"✗ Object '{a.content}' not found in scene";
        }
        else
        {
            value = ConvertValue(a.content, memberType);
            if (value == null)
                return $"✗ Cannot convert '{a.content}' to {memberType.Name}";
        }

        Undo.RecordObject(comp, $"Claunity: Set {a.componentType}.{a.propertyName}");
        propInfo?.SetValue(comp, value);
        fieldInfo?.SetValue(comp, value);
        EditorUtility.SetDirty(comp);
        return $"✓ Set {a.componentType}.{a.propertyName} = '{a.content}' on '{go.name}'";
    }

    // Convenience wrapper around SetComponentProperty for the common case of assigning a
    // project asset (prefab/material/texture/etc.) — takes a plain assetPath instead of
    // requiring the 'ObjectName:ComponentType' trick used for scene object references.
    private static string AssignAsset(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.propertyName)) return "✗ propertyName (field name) is required";
        if (string.IsNullOrEmpty(a.assetPath))    return "✗ assetPath is required";

        var forwarded = new ActionPayload
        {
            name = a.name, path = a.path, componentType = a.componentType,
            propertyName = a.propertyName, content = a.assetPath,
        };
        var result = SetComponentProperty(forwarded);
        return result.StartsWith("✓ Set ") ? "✓ Assigned " + result.Substring("✓ Set ".Length) : result;
    }

    // Finds a GameObject by name including inactive objects in the scene
    private static GameObject FindGameObjectIncludingInactive(string name)
    {
        var go = GameObject.Find(name);
        if (go != null) return go;
        // GameObject.Find misses inactive objects — search all scene objects
        foreach (var obj in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (obj.scene.IsValid() && obj.name == name)
                return obj;
        }
        return null;
    }

    private static UnityEngine.Object ResolveUnityObjectRef(string nameOrPath, Type targetType)
    {
        // 0. Direct project asset path (e.g. "Assets/Prefabs/Enemy.prefab", "Assets/Materials/Red.mat")
        //    Checked first — asset paths never collide with scene object names and this
        //    avoids GameObject.Find() misinterpreting "/" as a scene hierarchy path.
        if (nameOrPath.StartsWith("Assets/"))
        {
            if (typeof(Component).IsAssignableFrom(targetType))
            {
                // Component field pointing at a prefab: load the prefab root, then get the component off it.
                var prefabGo = AssetDatabase.LoadAssetAtPath<GameObject>(nameOrPath);
                if (prefabGo != null)
                {
                    var comp = prefabGo.GetComponent(targetType);
                    if (comp != null) return comp;
                }
            }
            var directAsset = AssetDatabase.LoadAssetAtPath(nameOrPath, targetType);
            if (directAsset != null) return directAsset;
            // fall through — path might be wrong, still try name-based lookups below
        }

        // 1. "ObjectName:ComponentType" format — explicit component on named object (including inactive)
        if (nameOrPath.Contains(':'))
        {
            var parts = nameOrPath.Split(':', 2);
            var goName = parts[0].Trim();
            var compTypeName = parts[1].Trim();
            var go0 = FindGameObjectIncludingInactive(goName);
            if (go0 != null)
            {
                var compType = FindComponentType(compTypeName);
                if (compType != null) return go0.GetComponent(compType);
                return go0;
            }
        }

        // 2. Scene object (Transform, GameObject, Component, or generic UnityEngine.Object)
        //    Searches inactive objects too via FindGameObjectIncludingInactive
        if (targetType == typeof(GameObject) || targetType == typeof(Transform) ||
            targetType == typeof(UnityEngine.Object) ||
            typeof(Component).IsAssignableFrom(targetType))
        {
            var go = FindGameObjectIncludingInactive(nameOrPath);
            if (go != null)
            {
                if (targetType == typeof(GameObject)) return go;
                if (targetType == typeof(Transform))  return go.transform;
                if (targetType == typeof(UnityEngine.Object)) return go;
                return go.GetComponent(targetType);
            }
        }

        // 3. Search by name in AssetDatabase
        var typeName = targetType == typeof(UnityEngine.Object) ? "" : $" t:{targetType.Name}";
        var guids = AssetDatabase.FindAssets(nameOrPath + typeName);
        foreach (var guid in guids)
        {
            var path  = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath(path, targetType);
            if (asset != null && asset.name == nameOrPath) return asset;
        }
        // fallback: return first match even if name doesn't match exactly
        if (guids.Length > 0)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath(path, targetType);
        }

        return null;
    }

    private static object ConvertValue(string raw, Type target)
    {
        try
        {
            if (target == typeof(bool))
                return raw.Trim().ToLower() == "true" || raw.Trim() == "1";
            if (target == typeof(float))
                return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(int))
                return int.Parse(raw);
            if (target == typeof(string))
                return raw;
            return Convert.ChangeType(raw, target);
        }
        catch { return null; }
    }

    // ── Serialized Property ────────────────────────────────────────────────────

    private static SerializedObject ResolveSerializedObject(ActionPayload a, out string label)
    {
        label = "";
        if (!string.IsNullOrEmpty(a.name) && !string.IsNullOrEmpty(a.componentType))
        {
            var go = FindObject(a);
            if (go == null) { label = $"✗ GameObject '{a.name}' not found in scene"; return null; }
            var type = FindComponentType(a.componentType);
            if (type == null) { label = $"✗ Component type '{a.componentType}' not found"; return null; }
            var comp = go.GetComponent(type);
            if (comp == null) { label = $"✗ '{go.name}' doesn't have component {a.componentType}"; return null; }
            label = $"{a.componentType} on '{go.name}'";
            return new SerializedObject(comp);
        }
        if (!string.IsNullOrEmpty(a.assetPath))
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(a.assetPath);
            if (asset == null) { label = $"✗ Asset not found at '{a.assetPath}'"; return null; }
            label = a.assetPath;
            return new SerializedObject(asset);
        }
        return null;
    }

    private static string SetSerializedProperty(ActionPayload a)
    {
        var so = ResolveSerializedObject(a, out var label);
        if (so == null)
            return string.IsNullOrEmpty(a.name) && string.IsNullOrEmpty(a.assetPath)
                ? "✗ Provide (name + componentType) for a scene object, or assetPath for an asset"
                : label.StartsWith("✗") ? label : $"✗ Could not resolve target: name='{a.name}' componentType='{a.componentType}' assetPath='{a.assetPath}'";

        if (string.IsNullOrEmpty(a.propertyPath)) return "✗ propertyPath is required";
        if (a.content == null) return "✗ content (value) is required";

        Undo.RecordObject(so.targetObject, $"Claunity: Set {a.propertyPath}");
        so.Update();
        var prop = so.FindProperty(a.propertyPath);
        if (prop == null) return $"✗ Property '{a.propertyPath}' not found on {label}";

        switch (prop.propertyType)
        {
            case SerializedPropertyType.Boolean:
                prop.boolValue = a.content.Trim().ToLower() == "true" || a.content.Trim() == "1";
                break;
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.LayerMask:
                if (!int.TryParse(a.content, out int iVal))
                    return $"✗ Cannot parse '{a.content}' as int";
                prop.intValue = iVal;
                break;
            case SerializedPropertyType.Float:
                if (!float.TryParse(a.content, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fVal))
                    return $"✗ Cannot parse '{a.content}' as float";
                prop.floatValue = fVal;
                break;
            case SerializedPropertyType.String:
                prop.stringValue = a.content;
                break;
            case SerializedPropertyType.Enum:
                int enumIdx = System.Array.IndexOf(prop.enumNames, a.content);
                if (enumIdx >= 0) prop.enumValueIndex = enumIdx;
                else if (int.TryParse(a.content, out int rawEnum)) prop.enumValueIndex = rawEnum;
                else return $"✗ Enum value '{a.content}' not valid. Options: {string.Join(", ", prop.enumNames)}";
                break;
            case SerializedPropertyType.ObjectReference:
                if (a.content.ToLower() == "null" || a.content == "")
                    prop.objectReferenceValue = null;
                else
                {
                    // Try to infer the expected type from the field declaration via reflection
                    Type objRefType = typeof(UnityEngine.Object);
                    try
                    {
                        var declaringType = so.targetObject.GetType();
                        var fi = declaringType.GetField(prop.name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (fi != null && typeof(UnityEngine.Object).IsAssignableFrom(fi.FieldType))
                            objRefType = fi.FieldType;
                    }
                    catch { }
                    var objRef = ResolveUnityObjectRef(a.content, objRefType);
                    if (objRef == null) return $"✗ Object '{a.content}' not found (expected type: {objRefType.Name})";
                    prop.objectReferenceValue = objRef;
                }
                break;
            case SerializedPropertyType.Vector2:
                var v2 = ParseVector2(a.content);
                if (v2 == null) return $"✗ Cannot parse '{a.content}' as Vector2 — use 'x,y'";
                prop.vector2Value = v2.Value;
                break;
            case SerializedPropertyType.Vector3:
                var v3 = ParseVector3(a.content);
                if (v3 == null) return $"✗ Cannot parse '{a.content}' as Vector3 — use 'x,y,z'";
                prop.vector3Value = v3.Value;
                break;
            case SerializedPropertyType.Color:
                var col = ParseColor(a.content);
                if (col == null) return $"✗ Cannot parse '{a.content}' as Color — use 'r,g,b,a' (0-1) or #hex";
                prop.colorValue = col.Value;
                break;
            case SerializedPropertyType.ArraySize:
                if (!int.TryParse(a.content, out int arrSize))
                    return $"✗ Cannot parse '{a.content}' as array size";
                prop.intValue = arrSize;
                break;
            default:
                return $"✗ Property type '{prop.propertyType}' is not yet supported by set_serialized_property";
        }

        so.ApplyModifiedProperties();
        return $"✓ Set '{a.propertyPath}' = '{a.content}' on {label}";
    }

    private static string GetSerializedProperty(ActionPayload a)
    {
        var so = ResolveSerializedObject(a, out var label);
        if (so == null)
            return string.IsNullOrEmpty(a.name) && string.IsNullOrEmpty(a.assetPath)
                ? "✗ Provide (name + componentType) or assetPath"
                : $"✗ Could not resolve target: '{a.name}' / '{a.assetPath}'";

        // No propertyPath → dump all top-level serialized properties
        if (string.IsNullOrEmpty(a.propertyPath))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Serialized properties of {label}:");
            var it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyPath == "m_Script") continue;
                sb.AppendLine($"  {it.propertyPath} ({it.propertyType}): {SerializedPropValueString(it)}");
            }
            return sb.ToString().TrimEnd();
        }

        var prop = so.FindProperty(a.propertyPath);
        if (prop == null) return $"✗ Property '{a.propertyPath}' not found on {label}";
        return $"✓ {a.propertyPath} = {SerializedPropValueString(prop)} ({prop.propertyType}) on {label}";
    }

    private static string SerializedPropValueString(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Boolean:        return p.boolValue.ToString();
            case SerializedPropertyType.Integer:        return p.intValue.ToString();
            case SerializedPropertyType.Float:          return p.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case SerializedPropertyType.String:         return p.stringValue ?? "(null)";
            case SerializedPropertyType.Enum:           return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString();
            case SerializedPropertyType.ObjectReference:return p.objectReferenceValue != null ? p.objectReferenceValue.name : "(null)";
            case SerializedPropertyType.Vector2:        var v2 = p.vector2Value; return $"{v2.x},{v2.y}";
            case SerializedPropertyType.Vector3:        var v3 = p.vector3Value; return $"{v3.x},{v3.y},{v3.z}";
            case SerializedPropertyType.Color:          var c = p.colorValue; return $"{c.r},{c.g},{c.b},{c.a}";
            case SerializedPropertyType.ArraySize:      return p.intValue.ToString();
            default:                                    return $"[{p.propertyType}]";
        }
    }

    private static Vector2? ParseVector2(string s)
    {
        var pts = s.Split(',');
        if (pts.Length >= 2 &&
            float.TryParse(pts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(pts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
            return new Vector2(x, y);
        return null;
    }

    private static Vector3? ParseVector3(string s)
    {
        var pts = s.Split(',');
        if (pts.Length >= 3 &&
            float.TryParse(pts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(pts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(pts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            return new Vector3(x, y, z);
        return null;
    }

    private static Color? ParseColor(string s)
    {
        if (s.StartsWith("#"))
            return ColorUtility.TryParseHtmlString(s, out Color col) ? col : (Color?)null;
        var pts = s.Split(',');
        if (pts.Length >= 3 &&
            float.TryParse(pts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) &&
            float.TryParse(pts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) &&
            float.TryParse(pts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
        {
            float a = 1f;
            if (pts.Length >= 4) float.TryParse(pts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a);
            return new Color(r, g, b, a);
        }
        return null;
    }

    // ── Material ───────────────────────────────────────────────────────────────

    private static string CreateMaterial(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";
        var shaderName = string.IsNullOrEmpty(a.shader) ? "Standard" : a.shader;
        var shader = Shader.Find(shaderName);
        if (shader == null) return $"✗ Shader not found: '{shaderName}'";
        var mat = new Material(shader) { name = a.name };
        if (a.color != null) mat.color = new Color(a.color.r, a.color.g, a.color.b, a.color.a);
        var rawPath = string.IsNullOrEmpty(a.savePath) ? "Assets" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder = EnsureFolder(rawPath);
        var assetPath = $"{folder}/{a.name}.mat";
        AssetDatabase.CreateAsset(mat, assetPath);
        AssetDatabase.SaveAssets();
        return $"✓ Created material '{a.name}' at {assetPath}";
    }

    private static string AssignMaterial(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        if (string.IsNullOrEmpty(a.materialPath)) return "✗ materialPath is required";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(a.materialPath);
        if (mat == null) return $"✗ Material not found at '{a.materialPath}'";
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null) return $"✗ '{go.name}' has no Renderer";
        Undo.RecordObject(renderer, $"Claunity: Assign material to {go.name}");
        renderer.sharedMaterial = mat;
        return $"✓ Assigned '{mat.name}' to '{go.name}'";
    }

    private static string ModifyMaterial(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.materialPath)) return "✗ materialPath is required";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(a.materialPath);
        if (mat == null) return $"✗ Material not found at '{a.materialPath}'";
        Undo.RecordObject(mat, $"Claunity: Modify material {mat.name}");
        if (a.color != null) mat.color = new Color(a.color.r, a.color.g, a.color.b, a.color.a);
        if (!string.IsNullOrEmpty(a.propertyName)) mat.SetFloat(a.propertyName, a.propertyValue);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return $"✓ Modified material '{mat.name}'";
    }

    private static string GetMaterialInfo(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.materialPath)) return "✗ materialPath is required";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(a.materialPath);
        if (mat == null) return $"✗ Material not found at '{a.materialPath}'";
        var sb = new StringBuilder();
        sb.AppendLine($"Material: {mat.name}");
        sb.AppendLine($"Shader: {mat.shader.name}");
        sb.AppendLine($"Render queue: {mat.renderQueue}");
        if (mat.HasProperty("_Color"))
        {
            var c = mat.color;
            sb.AppendLine($"Color: ({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})");
        }
        if (mat.HasProperty("_BaseColor"))
        {
            var c = mat.GetColor("_BaseColor");
            sb.AppendLine($"BaseColor: ({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})");
        }
        var floatProps = new[] { "_Metallic", "_Smoothness", "_Glossiness", "_BumpScale", "_Cutoff" };
        foreach (var p in floatProps)
            if (mat.HasProperty(p)) sb.AppendLine($"{p}: {mat.GetFloat(p):F3}");
        int propCount = mat.shader.GetPropertyCount();
        for (int i = 0; i < propCount; i++)
        {
            if (mat.shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
            {
                var pName = mat.shader.GetPropertyName(i);
                var tex = mat.GetTexture(pName);
                if (tex != null) sb.AppendLine($"Texture {pName}: {tex.name}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ── Scene ──────────────────────────────────────────────────────────────────

    private static string SaveScene(ActionPayload a)
    {
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene);
        return $"✓ Saved scene '{scene.name}'";
    }

    private static string CreateScene(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.sceneName)) return "✗ sceneName is required";

        // Auto-save the current active scene before creating a new one
        var activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.isDirty && !string.IsNullOrEmpty(activeScene.path))
            EditorSceneManager.SaveScene(activeScene);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
        var rawPath = string.IsNullOrEmpty(a.savePath) ? "Assets" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder = EnsureFolder(rawPath);
        EditorSceneManager.SaveScene(scene, $"{folder}/{a.sceneName}.unity");
        return $"✓ Created scene '{a.sceneName}'";
    }

    private static string LoadScene(ActionPayload a)
    {
        var path = a.assetPath;
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(a.sceneName))
        {
            var guids = AssetDatabase.FindAssets($"{a.sceneName} t:Scene");
            if (guids.Length == 0) return $"✗ Scene not found: '{a.sceneName}'";
            path = AssetDatabase.GUIDToAssetPath(guids[0]);
        }
        if (string.IsNullOrEmpty(path)) return "✗ assetPath or sceneName is required";
        var mode = a.additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
        EditorSceneManager.OpenScene(path, mode);
        return $"✓ Loaded scene '{path}'";
    }

    private static string UnloadScene(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.sceneName)) return "✗ sceneName is required";
        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var s = EditorSceneManager.GetSceneAt(i);
            if (s.name == a.sceneName || s.path == a.sceneName)
            {
                if (s == EditorSceneManager.GetActiveScene())
                    return "✗ Cannot unload the active scene. Use load_scene to switch to another scene first.";
                EditorSceneManager.CloseScene(s, true);
                return $"✓ Unloaded scene '{a.sceneName}'";
            }
        }
        return $"✗ Scene '{a.sceneName}' is not currently loaded";
    }

    private static string DeleteScene(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.sceneName)) return "✗ sceneName is required";
        string path;
        if (a.sceneName.EndsWith(".unity"))
        {
            path = a.sceneName;
        }
        else
        {
            var guids = AssetDatabase.FindAssets($"{a.sceneName} t:Scene");
            if (guids.Length == 0) return $"✗ Scene not found: '{a.sceneName}'";
            path = AssetDatabase.GUIDToAssetPath(guids[0]);
        }
        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var s = EditorSceneManager.GetSceneAt(i);
            if (s.path == path)
                return $"✗ Scene '{a.sceneName}' is currently loaded. Unload it first.";
        }
        if (!AssetDatabase.DeleteAsset(path))
            return $"✗ Failed to delete scene at '{path}'";
        return $"✓ Deleted scene '{a.sceneName}' at {path}";
    }

    private static string DeleteAsset(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.assetPath)) return "✗ assetPath is required (e.g. Assets/Scripts/MyScript.cs)";
        if (!a.assetPath.StartsWith("Assets/")) return "✗ assetPath must start with 'Assets/'";
        if (!AssetDatabase.AssetPathExists(a.assetPath)) return $"✗ Asset not found: '{a.assetPath}'";
        if (!AssetDatabase.DeleteAsset(a.assetPath)) return $"✗ Failed to delete '{a.assetPath}'";
        return $"✓ Deleted '{a.assetPath}'";
    }

    private static string MoveAsset(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.assetPath)) return "✗ assetPath (source) is required";
        if (string.IsNullOrEmpty(a.savePath))  return "✗ savePath (destination) is required";
        if (!a.assetPath.StartsWith("Assets/")) return "✗ assetPath must start with 'Assets/'";
        if (!a.savePath.StartsWith("Assets/"))  return "✗ savePath must start with 'Assets/'";
        if (!AssetDatabase.AssetPathExists(a.assetPath)) return $"✗ Source not found: '{a.assetPath}'";
        var destDir = System.IO.Path.GetDirectoryName(a.savePath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(destDir))
            EnsureFolder(destDir);
        var error = AssetDatabase.MoveAsset(a.assetPath, a.savePath);
        if (!string.IsNullOrEmpty(error)) return $"✗ Move failed: {error}";
        return $"✓ Moved '{a.assetPath}' → '{a.savePath}'";
    }

    private static string ListAssets(ActionPayload a)
    {
        var folder = string.IsNullOrEmpty(a.assetPath) ? "Assets" : a.assetPath.TrimEnd('/');
        if (!AssetDatabase.IsValidFolder(folder)) return $"✗ Folder not found: '{folder}'";

        var files = new List<string>();
        var folders = new List<string>();

        if (a.recursive)
        {
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(p)) files.Add(p);
            }
        }
        else
        {
            foreach (var sub in AssetDatabase.GetSubFolders(folder))
                folders.Add(sub + "/");
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(p)) continue;
                var parent = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/');
                if (parent == folder) files.Add(p);
            }
        }

        files.Sort();
        folders.Sort();
        if (files.Count == 0 && folders.Count == 0) return $"(empty) '{folder}'";

        var sb = new StringBuilder();
        sb.AppendLine($"Contents of '{folder}'{(a.recursive ? " (recursive)" : "")}:");
        foreach (var f in folders) sb.AppendLine($"  {f}");
        foreach (var f in files) sb.AppendLine($"  {f}");
        return sb.ToString().TrimEnd();
    }

    // ── Prefab / Asset ─────────────────────────────────────────────────────────

    private static string CreatePrefab(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        var rawPath = string.IsNullOrEmpty(a.savePath) ? "Assets/Prefabs" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder = EnsureFolder(rawPath);
        var prefabPath = $"{folder}/{go.name}.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        return $"✓ Created prefab '{go.name}' at {prefabPath}";
    }

    private static string AddAssetToScene(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.assetPath)) return "✗ assetPath is required";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(a.assetPath);
        if (prefab == null) return $"✗ Asset not found at '{a.assetPath}'";
        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (instance == null) return $"✗ Failed to instantiate '{a.assetPath}'";
        Undo.RegisterCreatedObjectUndo(instance, $"Claunity: Add {instance.name}");
        if (a.position != null) instance.transform.position = ToVec3(a.position);
        if (!string.IsNullOrEmpty(a.parent))
        {
            var p = GameObject.Find(a.parent);
            if (p != null) Undo.SetTransformParent(instance.transform, p.transform, "Claunity: Parent");
        }
        return $"✓ Added '{instance.name}' to scene";
    }

    // ── Editor ─────────────────────────────────────────────────────────────────

    private static string EnterPlayMode()
    {
        if (EditorApplication.isPlaying) return "ℹ Already in Play Mode";
        DisableMaximizeOnPlay();
        EditorApplication.isPlaying = true;
        return "✓ Entering Play Mode";
    }

    private static void DisableMaximizeOnPlay()
    {
        var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        if (gameViewType == null) return;
        var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
        if (gameView == null) return;
        // Try public property (newer Unity)
        var prop = gameViewType.GetProperty("maximizeOnPlay",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) { prop.SetValue(gameView, false); return; }
        // Try private backing field (older Unity)
        var field = gameViewType.GetField("m_MaximizeOnPlay",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(gameView, false);
    }

    private static string ExitPlayMode()
    {
        if (!EditorApplication.isPlaying) return "ℹ Not in Play Mode";
        EditorApplication.isPlaying = false;
        return "✓ Exiting Play Mode";
    }

    private static string PausePlayMode()
    {
        if (!EditorApplication.isPlaying) return "ℹ Not in Play Mode";
        EditorApplication.isPaused = !EditorApplication.isPaused;
        return $"✓ Play Mode {(EditorApplication.isPaused ? "paused" : "resumed")}";
    }

    private static string AddPrimitive(ActionPayload a)
    {
        // primitiveType = shape, name = GameObject name
        var shapeStr = !string.IsNullOrEmpty(a.primitiveType) ? a.primitiveType : a.name;
        if (string.IsNullOrEmpty(shapeStr)) shapeStr = "Cube";
        if (!Enum.TryParse<PrimitiveType>(shapeStr, true, out var primitiveType))
            return $"✗ Unknown primitive type: '{shapeStr}'. Use: Cube, Sphere, Capsule, Cylinder, Plane, Quad";

        var go = GameObject.CreatePrimitive(primitiveType);
        go.name = !string.IsNullOrEmpty(a.name) && a.name != shapeStr ? a.name
                : !string.IsNullOrEmpty(a.newName) ? a.newName
                : shapeStr;
        Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create {go.name}");

        if (a.position != null) go.transform.position = ToVec3(a.position);

        if (!string.IsNullOrEmpty(a.parent))
        {
            var p = GameObject.Find(a.parent);
            if (p != null) Undo.SetTransformParent(go.transform, p.transform, $"Claunity: Parent {go.name}");
        }

        return $"✓ Created {shapeStr} '{go.name}'";
    }

    private static string SetMesh(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";
        if (string.IsNullOrEmpty(a.meshType)) return "✗ meshType is required (Cube, Sphere, Capsule, Cylinder, Plane, Quad)";

        var go = GameObject.Find(a.name);
        if (go == null) return $"✗ GameObject '{a.name}' not found";

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) return $"✗ '{a.name}' has no MeshFilter component. Add one first with add_component.";

        var meshName = $"{a.meshType}.fbx";
        var mesh = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Mesh>($"Library/unity default resources/{a.meshType}.fbx");
        if (mesh == null)
        {
            // fallback path used in some Unity versions
            mesh = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Mesh>($"Library/unity default resources/{a.meshType} Instance");
        }
        if (mesh == null)
        {
            // Create temporary primitive, grab its mesh, destroy the primitive
            var temp = GameObject.CreatePrimitive((PrimitiveType)System.Enum.Parse(typeof(PrimitiveType), a.meshType, true));
            mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(temp);
        }
        if (mesh == null) return $"✗ Could not find built-in mesh for '{a.meshType}'";

        Undo.RecordObject(mf, $"Claunity: Set Mesh {a.meshType}");
        mf.sharedMesh = mesh;
        UnityEditor.EditorUtility.SetDirty(mf);
        return $"✓ Assigned {a.meshType} mesh to '{a.name}'";
    }

    private static string ExecuteMenuItem(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.menuPath)) return "✗ menuPath is required";
        bool ok = EditorApplication.ExecuteMenuItem(a.menuPath);
        return ok ? $"✓ Executed '{a.menuPath}'" : $"✗ Menu item not found: '{a.menuPath}'";
    }

    // ── UI Canvas ──────────────────────────────────────────────────────────────

    private static readonly string[] ValidUIElements =
    {
        "Canvas", "Panel", "Button", "Text", "Image", "RawImage",
        "InputField", "Slider", "Toggle", "ScrollView",
        "VerticalLayoutGroup", "HorizontalLayoutGroup", "GridLayoutGroup"
    };

    // Returns the scene Canvas to use as default parent, or null
    private static GameObject GetOrCreateCanvas()
    {
        var existing = UnityEngine.Object.FindFirstObjectByType<Canvas>();
        if (existing != null) return existing.gameObject;

        // Create Canvas + CanvasScaler + GraphicRaycaster
        var go = new GameObject("Canvas");
        Undo.RegisterCreatedObjectUndo(go, "Claunity: Create Canvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode          = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution  = new Vector2(1920, 1080);
        scaler.screenMatchMode      = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight   = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return go;
    }

    private static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;
        var es = new GameObject("EventSystem");
        Undo.RegisterCreatedObjectUndo(es, "Claunity: Create EventSystem");
        es.AddComponent<UnityEngine.EventSystems.EventSystem>();
        es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
    }

    // Apply a named anchor preset to a RectTransform
    private static void ApplyAnchorPreset(RectTransform rt, string preset)
    {
        if (string.IsNullOrEmpty(preset)) preset = "center";
        switch (preset.ToLower().Replace(" ", "-"))
        {
            case "stretch":
            case "stretch-full":
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                break;
            case "stretch-horizontal":
                rt.anchorMin = new Vector2(0, 0.5f); rt.anchorMax = new Vector2(1, 0.5f); break;
            case "stretch-vertical":
                rt.anchorMin = new Vector2(0.5f, 0); rt.anchorMax = new Vector2(0.5f, 1); break;
            case "top":
            case "top-center":
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1); rt.pivot = new Vector2(0.5f, 1); break;
            case "bottom":
            case "bottom-center":
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0); rt.pivot = new Vector2(0.5f, 0); break;
            case "left":
            case "middle-left":
                rt.anchorMin = rt.anchorMax = new Vector2(0, 0.5f); rt.pivot = new Vector2(0, 0.5f); break;
            case "right":
            case "middle-right":
                rt.anchorMin = rt.anchorMax = new Vector2(1, 0.5f); rt.pivot = new Vector2(1, 0.5f); break;
            case "top-left":
                rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); break;
            case "top-right":
                rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1); break;
            case "bottom-left":
                rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero; break;
            case "bottom-right":
                rt.anchorMin = rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(1, 0); break;
            default: // center
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f); break;
        }
    }

    private static void ApplySizeAndPosition(RectTransform rt, ActionPayload a)
    {
        if (a.size != null)    rt.sizeDelta          = new Vector2(a.size.x, a.size.y);
        if (a.position != null) rt.anchoredPosition  = new Vector2(a.position.x, a.position.y);
    }

    // Try to add TextMeshProUGUI, fall back to legacy Text
    private static Component AddTextComponent(GameObject go, string content, int fontSize = 24, Color? color = null)
    {
        var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro")
                   ?? Type.GetType("TMPro.TextMeshProUGUI, Assembly-CSharp");
        if (tmpType != null)
        {
            var comp = go.AddComponent(tmpType);
            tmpType.GetProperty("text")?.SetValue(comp, content ?? "");
            tmpType.GetProperty("fontSize")?.SetValue(comp, (float)(fontSize > 0 ? fontSize : 24));
            tmpType.GetProperty("alignment")?.SetValue(comp,
                Enum.Parse(tmpType.Assembly.GetType("TMPro.TextAlignmentOptions"), "Center"));
            if (color.HasValue)
                tmpType.GetProperty("color")?.SetValue(comp, color.Value);
            return comp;
        }
        // Legacy fallback
        var txt = go.AddComponent<UnityEngine.UI.Text>();
        txt.text      = content ?? "";
        txt.fontSize  = fontSize > 0 ? fontSize : 24;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color     = color ?? Color.white;
        return txt;
    }

    private static string CreateUIElement(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.elementType))
            return $"✗ elementType is required. Valid: {string.Join(", ", ValidUIElements)}";

        var etype = a.elementType.Trim();

        // Canvas is top-level — special case
        if (etype.Equals("Canvas", StringComparison.OrdinalIgnoreCase))
        {
            var cv = new GameObject(string.IsNullOrEmpty(a.name) ? "Canvas" : a.name);
            Undo.RegisterCreatedObjectUndo(cv, "Claunity: Create Canvas");
            var canvas = cv.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = cv.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight  = 0.5f;
            cv.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();
            return $"✓ Created Canvas '{cv.name}' (1920×1080 scale-with-screen)";
        }

        // Resolve parent — find by name, or auto-pick Canvas
        GameObject parentGO = null;
        if (!string.IsNullOrEmpty(a.parent))
        {
            parentGO = GameObject.Find(a.parent);
            if (parentGO == null) return $"✗ Parent not found: '{a.parent}'";
        }
        else
        {
            parentGO = GetOrCreateCanvas();
        }

        EnsureEventSystem();

        string elName = string.IsNullOrEmpty(a.name) ? etype : a.name;
        Color  tint   = a.color != null ? new Color(a.color.r, a.color.g, a.color.b, a.color.a) : Color.white;

        switch (etype.ToLower())
        {
            // ── Panel ──────────────────────────────────────────────────────────
            case "panel":
            {
                var go  = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Panel");
                go.transform.SetParent(parentGO.transform, false);
                var img = go.AddComponent<Image>();
                img.color = a.color != null ? tint : new Color(0, 0, 0, 0.6f);
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "stretch");
                if (a.anchor == null) { rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; }
                ApplySizeAndPosition(rt, a);
                return $"✓ Created Panel '{elName}' in '{parentGO.name}'";
            }

            // ── Image ──────────────────────────────────────────────────────────
            case "image":
            {
                var go  = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Image");
                go.transform.SetParent(parentGO.transform, false);
                var img = go.AddComponent<Image>();
                img.color = tint;
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                if (a.size != null) rt.sizeDelta = new Vector2(a.size.x, a.size.y);
                else                rt.sizeDelta = new Vector2(100, 100);
                ApplySizeAndPosition(rt, a);
                return $"✓ Created Image '{elName}' in '{parentGO.name}'";
            }

            // ── RawImage ───────────────────────────────────────────────────────
            case "rawimage":
            {
                var go  = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create RawImage");
                go.transform.SetParent(parentGO.transform, false);
                go.AddComponent<RawImage>().color = tint;
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(100, 100);
                ApplySizeAndPosition(rt, a);
                return $"✓ Created RawImage '{elName}' in '{parentGO.name}'";
            }

            // ── Text ───────────────────────────────────────────────────────────
            case "text":
            {
                var go  = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Text");
                go.transform.SetParent(parentGO.transform, false);
                AddTextComponent(go, a.text ?? elName, a.fontSize, a.color != null ? tint : (Color?)null);
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(160, 30);
                ApplySizeAndPosition(rt, a);
                return $"✓ Created Text '{elName}' in '{parentGO.name}'";
            }

            // ── Button ─────────────────────────────────────────────────────────
            case "button":
            {
                // Root: Image + Button
                var go  = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Button");
                go.transform.SetParent(parentGO.transform, false);
                var img = go.AddComponent<Image>();
                img.color = a.color != null ? tint : new Color(0.26f, 0.52f, 0.96f);
                go.AddComponent<Button>();
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(160, 40);
                ApplySizeAndPosition(rt, a);
                // Child: Text
                var txtGO = new GameObject("Text");
                txtGO.transform.SetParent(go.transform, false);
                AddTextComponent(txtGO, a.text ?? elName, a.fontSize > 0 ? a.fontSize : 18, Color.white);
                var txtRT = txtGO.GetComponent<RectTransform>();
                txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
                txtRT.offsetMin = Vector2.zero; txtRT.offsetMax = Vector2.zero;
                return $"✓ Created Button '{elName}' in '{parentGO.name}'";
            }

            // ── Toggle ─────────────────────────────────────────────────────────
            case "toggle":
            {
                var go = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Toggle");
                go.transform.SetParent(parentGO.transform, false);
                go.AddComponent<Image>().color = Color.white;
                var toggle = go.AddComponent<Toggle>();
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(160, 30);
                ApplySizeAndPosition(rt, a);
                // Background
                var bg = new GameObject("Background");
                bg.transform.SetParent(go.transform, false);
                var bgImg = bg.AddComponent<Image>();
                bgImg.color = new Color(0.9f, 0.9f, 0.9f);
                var bgRT = bg.GetComponent<RectTransform>();
                bgRT.sizeDelta = new Vector2(20, 20);
                bgRT.anchorMin = bgRT.anchorMax = new Vector2(0, 0.5f);
                bgRT.anchoredPosition = new Vector2(10, 0);
                // Checkmark
                var ck = new GameObject("Checkmark");
                ck.transform.SetParent(bg.transform, false);
                var ckImg = ck.AddComponent<Image>();
                ckImg.color = new Color(0.26f, 0.52f, 0.96f);
                var ckRT = ck.GetComponent<RectTransform>();
                ckRT.anchorMin = Vector2.zero; ckRT.anchorMax = Vector2.one;
                ckRT.offsetMin = new Vector2(2,2); ckRT.offsetMax = new Vector2(-2,-2);
                toggle.graphic = ckImg;
                // Label
                var lbl = new GameObject("Label");
                lbl.transform.SetParent(go.transform, false);
                AddTextComponent(lbl, a.text ?? elName, a.fontSize > 0 ? a.fontSize : 14, Color.black);
                var lblRT = lbl.GetComponent<RectTransform>();
                lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one;
                lblRT.offsetMin = new Vector2(28, 0); lblRT.offsetMax = Vector2.zero;
                return $"✓ Created Toggle '{elName}' in '{parentGO.name}'";
            }

            // ── Slider ─────────────────────────────────────────────────────────
            case "slider":
            {
                var go = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create Slider");
                go.transform.SetParent(parentGO.transform, false);
                var slider = go.AddComponent<Slider>();
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(200, 20);
                ApplySizeAndPosition(rt, a);
                // Background
                var bg = new GameObject("Background");
                bg.transform.SetParent(go.transform, false);
                var bgImg = bg.AddComponent<Image>();
                bgImg.color = new Color(0.8f, 0.8f, 0.8f);
                var bgRT  = bg.GetComponent<RectTransform>();
                bgRT.anchorMin = new Vector2(0, 0.25f); bgRT.anchorMax = new Vector2(1, 0.75f);
                bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
                // Fill Area
                var fillArea = new GameObject("Fill Area");
                fillArea.transform.SetParent(go.transform, false);
                var faRT = fillArea.AddComponent<RectTransform>();
                faRT.anchorMin = new Vector2(0, 0.25f); faRT.anchorMax = new Vector2(1, 0.75f);
                faRT.offsetMin = new Vector2(5, 0); faRT.offsetMax = new Vector2(-15, 0);
                // Fill
                var fill = new GameObject("Fill");
                fill.transform.SetParent(fillArea.transform, false);
                var fillImg = fill.AddComponent<Image>();
                fillImg.color = new Color(0.26f, 0.52f, 0.96f);
                slider.fillRect = fill.GetComponent<RectTransform>();
                slider.targetGraphic = bgImg;
                slider.value = 1f;
                return $"✓ Created Slider '{elName}' in '{parentGO.name}'";
            }

            // ── Layout Groups ──────────────────────────────────────────────────
            case "verticallayoutgroup":
            case "horizontallayoutgroup":
            case "gridlayoutgroup":
            {
                var go = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create {etype}");
                go.transform.SetParent(parentGO.transform, false);
                go.AddComponent<Image>().color = a.color != null ? tint : new Color(0,0,0,0);
                if (etype.ToLower() == "verticallayoutgroup")        go.AddComponent<VerticalLayoutGroup>();
                else if (etype.ToLower() == "horizontallayoutgroup") go.AddComponent<HorizontalLayoutGroup>();
                else                                                  go.AddComponent<GridLayoutGroup>();
                go.AddComponent<ContentSizeFitter>();
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                if (a.size != null) rt.sizeDelta = new Vector2(a.size.x, a.size.y);
                ApplySizeAndPosition(rt, a);
                return $"✓ Created {etype} '{elName}' in '{parentGO.name}'";
            }

            // ── InputField ─────────────────────────────────────────────────────
            case "inputfield":
            {
                // Try TMP InputField first
                var tmpIfType = Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro")
                             ?? Type.GetType("TMPro.TMP_InputField, Assembly-CSharp");
                var go = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create InputField");
                go.transform.SetParent(parentGO.transform, false);
                var bgImg = go.AddComponent<Image>();
                bgImg.color = new Color(0.15f, 0.15f, 0.15f);
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(200, 40);
                ApplySizeAndPosition(rt, a);
                // Text area
                var textGO = new GameObject("Text");
                textGO.transform.SetParent(go.transform, false);
                AddTextComponent(textGO, a.text ?? "", a.fontSize > 0 ? a.fontSize : 16, Color.white);
                var textRT = textGO.GetComponent<RectTransform>();
                textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
                textRT.offsetMin = new Vector2(8, 4); textRT.offsetMax = new Vector2(-8, -4);
                // Legacy InputField as fallback (TMP requires more setup)
                if (tmpIfType == null)
                {
                    var inputField = go.AddComponent<InputField>();
                    inputField.textComponent = textGO.GetComponent<Text>();
                    inputField.placeholder = null;
                }
                return $"✓ Created InputField '{elName}' in '{parentGO.name}'";
            }

            // ── ScrollView ─────────────────────────────────────────────────────
            case "scrollview":
            {
                var go = new GameObject(elName);
                Undo.RegisterCreatedObjectUndo(go, $"Claunity: Create ScrollView");
                go.transform.SetParent(parentGO.transform, false);
                go.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.8f);
                var sr = go.AddComponent<ScrollRect>();
                var rt = go.GetComponent<RectTransform>();
                ApplyAnchorPreset(rt, a.anchor ?? "center");
                rt.sizeDelta = a.size != null ? new Vector2(a.size.x, a.size.y) : new Vector2(300, 200);
                ApplySizeAndPosition(rt, a);
                // Viewport
                var vp = new GameObject("Viewport");
                vp.transform.SetParent(go.transform, false);
                var vpImg = vp.AddComponent<Image>(); vpImg.color = Color.clear;
                vp.AddComponent<Mask>().showMaskGraphic = false;
                var vpRT = vp.GetComponent<RectTransform>();
                vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
                vpRT.offsetMin = vpRT.offsetMax = Vector2.zero;
                // Content
                var content = new GameObject("Content");
                content.transform.SetParent(vp.transform, false);
                var vlg = content.AddComponent<VerticalLayoutGroup>();
                vlg.childForceExpandWidth = true;
                content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var contentRT = content.GetComponent<RectTransform>();
                contentRT.anchorMin = new Vector2(0,1); contentRT.anchorMax = Vector2.one;
                contentRT.pivot     = new Vector2(0.5f, 1);
                contentRT.sizeDelta = Vector2.zero;
                sr.viewport  = vpRT;
                sr.content   = contentRT;
                sr.horizontal = false;
                sr.vertical   = true;
                return $"✓ Created ScrollView '{elName}' in '{parentGO.name}'";
            }

            default:
                return $"✗ Unknown elementType: '{etype}'. Valid: {string.Join(", ", ValidUIElements)}";
        }
    }

    private static string SetRectTransform(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) return $"✗ '{go.name}' has no RectTransform — is it a UI element?";

        Undo.RecordObject(rt, $"Claunity: SetRectTransform {go.name}");

        if (!string.IsNullOrEmpty(a.anchor))      ApplyAnchorPreset(rt, a.anchor);
        if (a.size != null)                        rt.sizeDelta         = new Vector2(a.size.x, a.size.y);
        if (a.position != null)                    rt.anchoredPosition  = new Vector2(a.position.x, a.position.y);

        EditorUtility.SetDirty(rt);
        return $"✓ Updated RectTransform on '{go.name}'";
    }

    // ── Scripts ────────────────────────────────────────────────────────────────

    private static string ResolveScriptPath(ActionPayload a)
    {
        if (!string.IsNullOrEmpty(a.scriptPath) && System.IO.File.Exists(a.scriptPath))
            return a.scriptPath;

        var name = !string.IsNullOrEmpty(a.scriptName)
            ? a.scriptName
            : System.IO.Path.GetFileNameWithoutExtension(a.scriptPath ?? "");
        if (string.IsNullOrEmpty(name)) return null;

        var guids = AssetDatabase.FindAssets($"{name} t:Script");
        foreach (var guid in guids)
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(p) == name) return p;
        }
        return guids.Length > 0 ? AssetDatabase.GUIDToAssetPath(guids[0]) : null;
    }

    private static string ReadScript(ActionPayload a)
    {
        var path = ResolveScriptPath(a);
        if (path == null) return $"Script not found: '{a.scriptName ?? a.scriptPath}'";
        var text = System.IO.File.ReadAllText(path);
        return $"File: {path}\n\n{text}";
    }

    private static string CreateScript(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.scriptName)) return "✗ scriptName is required";
        if (string.IsNullOrEmpty(a.content))    return "✗ content is required";
        var rawPath = string.IsNullOrEmpty(a.savePath) ? "Assets/Scripts" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder = EnsureFolder(rawPath);
        var path   = $"{folder}/{a.scriptName}.cs";
        if (System.IO.File.Exists(path))
        {
            // File already exists — treat as edit to prevent silent data loss
            System.IO.File.WriteAllText(path, a.content);
            AssetDatabase.Refresh();
            return $"✓ Updated existing script '{a.scriptName}' at {path} (file already existed)";
        }
        System.IO.File.WriteAllText(path, a.content);
        AssetDatabase.Refresh();
        return $"✓ Created script '{a.scriptName}' at {path}";
    }

    public static string EditScript(ActionPayload a)
    {
        var path = ResolveScriptPath(a);
        if (path == null) return $"✗ Script not found: '{a.scriptName ?? a.scriptPath}'";
        if (string.IsNullOrEmpty(a.content)) return "✗ content is required";
        System.IO.File.WriteAllText(path, a.content);
        AssetDatabase.Refresh();
        return $"✓ Updated script '{System.IO.Path.GetFileName(path)}'";
    }

    public static string ReadScriptForDiff(ActionPayload a)
    {
        var path = ResolveScriptPath(a);
        return path != null ? System.IO.File.ReadAllText(path) : null;
    }

    private static string AttachScript(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";
        if (string.IsNullOrEmpty(a.scriptName)) return "✗ scriptName is required";
        var guids = AssetDatabase.FindAssets($"{a.scriptName} t:Script");
        if (guids.Length == 0) return $"✗ Script not found: '{a.scriptName}' (recompile may be needed)";
        var scriptAssetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
        if (monoScript == null) return $"✗ Could not load script at '{scriptAssetPath}'";
        var type = monoScript.GetClass();
        if (type == null) return $"✗ Script '{a.scriptName}' has no valid class — try recompile first";
        Undo.AddComponent(go, type);
        return $"✓ Attached '{a.scriptName}' to '{go.name}'";
    }

    private static string RecompileScripts()
    {
        AssetDatabase.Refresh();
        return "✓ Triggered script recompilation (AssetDatabase.Refresh)";
    }

    // ── Input System ───────────────────────────────────────────────────────────

    private static string _inputNotInstalled =>
        "✗ Input System not installed. Run: {\"type\":\"add_package\",\"content\":\"com.unity.inputsystem\"} then restart Unity.";

#if ENABLE_INPUT_SYSTEM
    // Cache recently created assets so subsequent actions in the same batch can find them
    private static readonly Dictionary<string, UnityEngine.InputSystem.InputActionAsset> _inputAssetCache
        = new Dictionary<string, UnityEngine.InputSystem.InputActionAsset>();

    private static UnityEngine.InputSystem.InputActionAsset LoadInputAsset(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_inputAssetCache.TryGetValue(path, out var cached) && cached != null) return cached;
        return AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
    }

    // Write asset as JSON (proper .inputactions format), reimport, refresh cache
    private static void SaveInputAsset(UnityEngine.InputSystem.InputActionAsset asset, string path)
    {
        var json = asset.ToJson();
        if (!string.IsNullOrEmpty(json))
            System.IO.File.WriteAllText(path, json);
        else
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var live = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
        if (live != null) _inputAssetCache[path] = live;
    }

    private static UnityEngine.InputSystem.InputActionMap FindMap(
        UnityEngine.InputSystem.InputActionAsset asset, string mapName)
    {
        if (string.IsNullOrEmpty(mapName))
            return asset.actionMaps.Count > 0 ? asset.actionMaps[0] : null;
        return asset.FindActionMap(mapName, throwIfNotFound: false);
    }
#endif

    private static string CreateInputActionAsset(ActionPayload a)
    {
#if !ENABLE_INPUT_SYSTEM
        return _inputNotInstalled;
#else
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";
        var savePath = string.IsNullOrEmpty(a.savePath) ? "Assets" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(savePath, out var pathErr)) return pathErr;
        EnsureFolder(savePath);
        var path = $"{savePath}/{a.name}.inputactions";

        // Write minimal valid JSON — the format Unity's Input System editor uses
        var json = $"{{\"name\":\"{a.name}\",\"maps\":[],\"controlSchemes\":[]}}";
        System.IO.File.WriteAllText(path, json);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

        var live = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
        if (live == null) return $"✗ Created file but failed to import: '{path}'";
        _inputAssetCache[path] = live;
        return $"✓ Created InputActionAsset '{a.name}' at '{path}'";
#endif
    }

    private static string AddInputActionMap(ActionPayload a)
    {
#if !ENABLE_INPUT_SYSTEM
        return _inputNotInstalled;
#else
        var asset = LoadInputAsset(a.assetPath);
        if (asset == null) return $"✗ InputActionAsset not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";
        if (asset.FindActionMap(a.name, throwIfNotFound: false) != null)
            return $"✗ Action map '{a.name}' already exists";

        asset.AddActionMap(a.name);
        SaveInputAsset(asset, a.assetPath);
        return $"✓ Added action map '{a.name}' to '{System.IO.Path.GetFileName(a.assetPath)}'";
#endif
    }

    private static string AddInputAction(ActionPayload a)
    {
#if !ENABLE_INPUT_SYSTEM
        return _inputNotInstalled;
#else
        var asset = LoadInputAsset(a.assetPath);
        if (asset == null) return $"✗ InputActionAsset not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";

        // parent = action map name
        var map = FindMap(asset, a.parent);
        if (map == null)
            return string.IsNullOrEmpty(a.parent)
                ? "✗ No action maps found — create one first with add_input_action_map"
                : $"✗ Action map '{a.parent}' not found";

        if (map.FindAction(a.name, throwIfNotFound: false) != null)
            return $"✗ Action '{a.name}' already exists in map '{map.name}'";

        // parameterType = action type (Button/Value/PassThrough)
        var actionType = UnityEngine.InputSystem.InputActionType.Button;
        switch ((a.parameterType ?? "Button").Trim().ToLower())
        {
            case "value":       actionType = UnityEngine.InputSystem.InputActionType.Value;       break;
            case "passthrough": actionType = UnityEngine.InputSystem.InputActionType.PassThrough; break;
        }

        // text = expected control type (float / Vector2 / Vector3 / etc.)
        var controlType = a.text ?? "";

        map.AddAction(a.name, actionType, expectedControlLayout: controlType);
        SaveInputAsset(asset, a.assetPath);

        var ctInfo = controlType.Length > 0 ? $"/{controlType}" : "";
        return $"✓ Added action '{a.name}' ({actionType}{ctInfo}) to map '{map.name}'";
#endif
    }

    private static string AddInputBinding(ActionPayload a)
    {
#if !ENABLE_INPUT_SYSTEM
        return _inputNotInstalled;
#else
        var asset = LoadInputAsset(a.assetPath);
        if (asset == null) return $"✗ InputActionAsset not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.name)) return "✗ name (action name) is required";

        // Find action — optionally in specific map (parent)
        UnityEngine.InputSystem.InputAction action;
        if (!string.IsNullOrEmpty(a.parent))
        {
            var map = FindMap(asset, a.parent);
            if (map == null) return $"✗ Action map '{a.parent}' not found";
            action = map.FindAction(a.name, throwIfNotFound: false);
        }
        else
        {
            action = asset.FindAction(a.name, throwIfNotFound: false);
        }
        if (action == null) return $"✗ Action '{a.name}' not found";

        // propertyName = composite type (2DVector / 1DAxis / ButtonWithOneModifier / etc.)
        if (!string.IsNullOrEmpty(a.propertyName))
        {
            // Composite binding — parts encoded in content as "up=<Keyboard>/w,down=<Keyboard>/s,..."
            if (string.IsNullOrEmpty(a.content))
                return $"✗ content is required for composite — format: 'up=<Keyboard>/w,down=<Keyboard>/s,left=<Keyboard>/a,right=<Keyboard>/d'";

            var bindingSyntax = action.AddCompositeBinding(a.propertyName);
            var parts = a.content.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length != 2) continue;
                bindingSyntax.With(kv[0].Trim(), kv[1].Trim());
            }
            SaveInputAsset(asset, a.assetPath);
            return $"✓ Added {a.propertyName} composite to '{a.name}': {a.content}";
        }

        // Simple binding — content = binding path
        if (string.IsNullOrEmpty(a.content))
            return "✗ content is required — binding path e.g. '<Keyboard>/space', '<Gamepad>/buttonSouth'";

        action.AddBinding(a.content);
        SaveInputAsset(asset, a.assetPath);
        return $"✓ Added binding '{a.content}' to action '{a.name}'";
#endif
    }

    private static string GetInputAssetInfo(ActionPayload a)
    {
#if !ENABLE_INPUT_SYSTEM
        return _inputNotInstalled;
#else
        var asset = LoadInputAsset(a.assetPath);
        if (asset == null) return $"✗ InputActionAsset not found: '{a.assetPath}'";

        var sb = new StringBuilder();
        sb.AppendLine($"InputActionAsset: '{asset.name}'");
        sb.AppendLine($"Maps: {asset.actionMaps.Count}");

        foreach (var map in asset.actionMaps)
        {
            sb.AppendLine($"\n  [{map.name}] — {map.actions.Count} action(s)");
            foreach (var act in map.actions)
            {
                var ctInfo = act.expectedControlType.Length > 0 ? $"/{act.expectedControlType}" : "";
                sb.AppendLine($"    '{act.name}' {act.type}{ctInfo}");
                foreach (var b in act.bindings)
                {
                    if (b.isComposite)
                        sb.AppendLine($"      [Composite: {b.name}]");
                    else if (b.isPartOfComposite)
                        sb.AppendLine($"        {b.name}: {b.path}");
                    else
                        sb.AppendLine($"      {b.path}");
                }
            }
        }
        return sb.ToString().TrimEnd();
#endif
    }

    // ── ScriptableObject ───────────────────────────────────────────────────────

    private static string CreateScriptableObject(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.scriptName)) return "✗ scriptName is required";

        // Find ScriptableObject type across all assemblies
        Type soType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            soType = asm.GetType(a.scriptName);
            if (soType != null && typeof(ScriptableObject).IsAssignableFrom(soType)) break;
            soType = null;
        }
        if (soType == null)
            return $"✗ ScriptableObject type '{a.scriptName}' not found — make sure the script exists and compiles";

        var instance = ScriptableObject.CreateInstance(soType);
        if (instance == null) return $"✗ Failed to create instance of '{a.scriptName}'";

        var rawPath  = string.IsNullOrEmpty(a.savePath) ? "Assets/Data" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder   = EnsureFolder(rawPath);
        var assetName = string.IsNullOrEmpty(a.name) ? a.scriptName : a.name;
        var path     = $"{folder}/{assetName}.asset";

        AssetDatabase.CreateAsset(instance, path);
        AssetDatabase.SaveAssets();
        return $"✓ Created ScriptableObject '{assetName}' ({a.scriptName}) at '{path}' — use set_serialized_property with assetPath to set fields";
    }

    // ── Project Settings ───────────────────────────────────────────────────────

    private static SerializedObject LoadProjectSettingsAsset(string fileName)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath($"ProjectSettings/{fileName}");
        return assets.Length > 0 ? new SerializedObject(assets[0]) : null;
    }

    private static string AddTag(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";

        var so = LoadProjectSettingsAsset("TagManager.asset");
        if (so == null) return "✗ Could not load TagManager.asset";

        so.Update();
        var tagsProp = so.FindProperty("tags");
        if (tagsProp == null) return "✗ 'tags' property not found in TagManager";

        // Check duplicate
        for (int i = 0; i < tagsProp.arraySize; i++)
            if (tagsProp.GetArrayElementAtIndex(i).stringValue == a.name)
                return $"✗ Tag '{a.name}' already exists";

        tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
        tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = a.name;
        so.ApplyModifiedProperties();
        return $"✓ Added tag '{a.name}'";
    }

    private static string AddLayer(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";

        var so = LoadProjectSettingsAsset("TagManager.asset");
        if (so == null) return "✗ Could not load TagManager.asset";

        so.Update();
        var layersProp = so.FindProperty("layers");
        if (layersProp == null) return "✗ 'layers' property not found in TagManager";

        // Layers 0-7 are built-in, user layers start at 8
        for (int i = 8; i < layersProp.arraySize; i++)
        {
            var elem = layersProp.GetArrayElementAtIndex(i);
            if (elem.stringValue == a.name) return $"✗ Layer '{a.name}' already exists at index {i}";
        }
        for (int i = 8; i < layersProp.arraySize; i++)
        {
            var elem = layersProp.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(elem.stringValue))
            {
                elem.stringValue = a.name;
                so.ApplyModifiedProperties();
                return $"✓ Added layer '{a.name}' at index {i}";
            }
        }
        return "✗ No free layer slots (max 32 layers, slots 8–31 are user layers)";
    }

    private static string SetPhysicsGravity(ActionPayload a)
    {
        var so = LoadProjectSettingsAsset("DynamicsManager.asset");
        if (so == null) return "✗ Could not load DynamicsManager.asset (physics settings)";

        Vector3 gravity;
        if (a.position != null)
        {
            gravity = new Vector3(a.position.x, a.position.y, a.position.z);
        }
        else if (!string.IsNullOrEmpty(a.content))
        {
            var v = ParseVector3(a.content);
            if (v == null) return "✗ Cannot parse gravity — use position:{x,y,z} or content:'x,y,z'";
            gravity = v.Value;
        }
        else return "✗ Provide gravity via position:{x,y,z} or content:'x,y,z'";

        so.Update();
        var prop = so.FindProperty("m_Gravity");
        if (prop == null) return "✗ Gravity property not found in DynamicManager.asset";
        prop.vector3Value = gravity;
        so.ApplyModifiedProperties();
        return $"✓ Physics gravity set to ({gravity.x}, {gravity.y}, {gravity.z})";
    }

    private static string SetTimeSetting(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.propertyName)) return "✗ propertyName is required. Options: fixedTimestep, maximumDeltaTime, timeScale, maximumParticleDeltaTime";
        if (string.IsNullOrEmpty(a.content))      return "✗ content (value) is required";

        var so = LoadProjectSettingsAsset("TimeManager.asset");
        if (so == null) return "✗ Could not load TimeManager.asset";

        var propMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fixedTimestep",          "Fixed Timestep"           },
            { "fixedDeltaTime",         "Fixed Timestep"           },
            { "maximumDeltaTime",       "Maximum Allowed Timestep" },
            { "timeScale",              "Time Scale"               },
            { "maximumParticleDeltaTime","Particle Physics"        },
        };

        if (!propMap.TryGetValue(a.propertyName, out var internalName))
            return $"✗ Unknown time property '{a.propertyName}'. Options: fixedTimestep, maximumDeltaTime, timeScale";

        if (!float.TryParse(a.content, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float val))
            return $"✗ Cannot parse '{a.content}' as float";

        so.Update();
        var prop = so.FindProperty(internalName);
        if (prop == null) return $"✗ Property '{internalName}' not found in TimeManager.asset";
        prop.floatValue = val;
        so.ApplyModifiedProperties();
        return $"✓ Set {a.propertyName} = {val}";
    }

    // ── Animator Controller ────────────────────────────────────────────────────

    private static UnityEditor.Animations.AnimatorController LoadController(string nameOrPath)
    {
        if (string.IsNullOrEmpty(nameOrPath)) return null;

        // Try as direct asset path first
        var ctrl = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(nameOrPath);
        if (ctrl != null) return ctrl;

        // Resolve by name: search all .controller assets
        var nameOnly = System.IO.Path.GetFileNameWithoutExtension(nameOrPath);
        var guids = AssetDatabase.FindAssets($"t:AnimatorController {nameOnly}");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path)
                    .Equals(nameOnly, System.StringComparison.OrdinalIgnoreCase))
                return AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path);
        }
        return null;
    }

    private static UnityEditor.Animations.AnimatorStateMachine GetLayerSM(
        UnityEditor.Animations.AnimatorController ctrl, int layerIndex)
    {
        if (ctrl.layers.Length <= layerIndex) return null;
        return ctrl.layers[layerIndex].stateMachine;
    }

    private static UnityEditor.Animations.AnimatorState FindAnimState(
        UnityEditor.Animations.AnimatorStateMachine sm, string name)
    {
        foreach (var cs in sm.states)
            if (cs.state.name == name) return cs.state;
        return null;
    }

    private static string CreateAnimatorController(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";
        var rawPath = string.IsNullOrEmpty(a.savePath) ? "Assets/Animations" : a.savePath.TrimEnd('/');
        if (!ValidateSavePath(rawPath, out var pathErr)) return pathErr;
        var folder = EnsureFolder(rawPath);
        var path   = $"{folder}/{a.name}.controller";

        var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
        AssetDatabase.SaveAssets();
        return $"✓ Created AnimatorController '{a.name}' at '{path}'";
    }

    private static string AddAnimatorParameter(ActionPayload a)
    {
        var ctrl = LoadController(a.assetPath);
        if (ctrl == null) return $"✗ AnimatorController not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";

        foreach (var p in ctrl.parameters)
            if (p.name == a.name) return $"✗ Parameter '{a.name}' already exists";

        var typeStr = (!string.IsNullOrEmpty(a.parameterType) ? a.parameterType
                    : !string.IsNullOrEmpty(a.content)        ? a.content
                    : "Float").Trim();

        AnimatorControllerParameterType paramType;
        switch (typeStr.ToLower())
        {
            case "int":     paramType = AnimatorControllerParameterType.Int;     break;
            case "bool":    paramType = AnimatorControllerParameterType.Bool;    break;
            case "trigger": paramType = AnimatorControllerParameterType.Trigger; break;
            default:        paramType = AnimatorControllerParameterType.Float;   break;
        }

        ctrl.AddParameter(a.name, paramType);
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        return $"✓ Added parameter '{a.name}' ({paramType}) to '{System.IO.Path.GetFileName(a.assetPath)}'";
    }

    private static string AddAnimatorState(ActionPayload a)
    {
        var ctrl = LoadController(a.assetPath);
        if (ctrl == null) return $"✗ AnimatorController not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.name)) return "✗ name is required";

        var sm = GetLayerSM(ctrl, a.layer);
        if (sm == null) return $"✗ Layer {a.layer} doesn't exist in this controller";

        if (FindAnimState(sm, a.name) != null)
            return $"✗ State '{a.name}' already exists in layer {a.layer}";

        // Auto-position: stack new states vertically
        var pos = a.position != null
            ? new Vector3(a.position.x, a.position.y, 0)
            : new Vector3(300, 60 + sm.states.Length * 70, 0);

        var state = sm.AddState(a.name, pos);

        // Assign AnimationClip if motion path provided
        if (!string.IsNullOrEmpty(a.motion))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(a.motion);
            if (clip != null) state.motion = clip;
            else return $"✓ State '{a.name}' created but clip not found at '{a.motion}'";
        }

        if (a.isDefault) sm.defaultState = state;

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();

        var motionInfo   = state.motion != null ? $" motion={state.motion.name}" : "";
        var defaultInfo  = a.isDefault ? " [default]" : "";
        return $"✓ Added state '{a.name}'{motionInfo}{defaultInfo} in layer {a.layer} of '{System.IO.Path.GetFileName(a.assetPath)}'";
    }

    private static string AddAnimatorTransition(ActionPayload a)
    {
        var ctrl = LoadController(a.assetPath);
        if (ctrl == null) return $"✗ AnimatorController not found: '{a.assetPath}'";
        if (string.IsNullOrEmpty(a.fromState)) return "✗ fromState is required";
        if (string.IsNullOrEmpty(a.toState))   return "✗ toState is required";

        var sm = GetLayerSM(ctrl, a.layer);
        if (sm == null) return $"✗ Layer {a.layer} doesn't exist";

        var toState = FindAnimState(sm, a.toState);
        if (toState == null) return $"✗ State '{a.toState}' not found in layer {a.layer}";

        UnityEditor.Animations.AnimatorStateTransition transition;

        var fromLower = a.fromState.ToLower();
        if (fromLower == "any state" || fromLower == "any")
        {
            transition = sm.AddAnyStateTransition(toState);
        }
        else
        {
            var fromState = FindAnimState(sm, a.fromState);
            if (fromState == null) return $"✗ State '{a.fromState}' not found in layer {a.layer}";
            transition = fromState.AddTransition(toState);
        }

        // Configure transition timing
        transition.hasExitTime      = a.hasExitTime;
        transition.exitTime         = a.exitTime > 0 ? a.exitTime : 0.9f;
        transition.duration         = a.transitionDuration > 0 ? a.transitionDuration : 0.1f;
        transition.hasFixedDuration = false; // normalized duration

        // Add conditions
        if (a.conditions != null)
        {
            foreach (var cond in a.conditions)
            {
                if (string.IsNullOrEmpty(cond.parameter)) continue;

                AnimatorConditionMode mode;
                switch ((cond.mode ?? "greater").ToLower())
                {
                    case "less":       mode = AnimatorConditionMode.Less;      break;
                    case "equals":     mode = AnimatorConditionMode.Equals;    break;
                    case "notequal":
                    case "notequals":  mode = AnimatorConditionMode.NotEqual;  break;
                    case "if":         mode = AnimatorConditionMode.If;        break;
                    case "ifnot":      mode = AnimatorConditionMode.IfNot;     break;
                    default:           mode = AnimatorConditionMode.Greater;   break;
                }
                transition.AddCondition(mode, cond.threshold, cond.parameter);
            }
        }

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();

        var condStr = a.conditions != null && a.conditions.Length > 0
            ? string.Join(", ", System.Array.ConvertAll(a.conditions,
                c => $"{c.parameter} {c.mode ?? "Greater"} {c.threshold}"))
            : "no conditions";
        return $"✓ Added transition '{a.fromState}' → '{a.toState}' [{condStr}] in '{System.IO.Path.GetFileName(a.assetPath)}'";
    }

    private static string GetAnimatorInfo(ActionPayload a)
    {
        var nameOrPath = !string.IsNullOrEmpty(a.assetPath) ? a.assetPath : a.name;
        var ctrl = LoadController(nameOrPath);
        if (ctrl == null) return $"✗ AnimatorController not found: '{nameOrPath}'";

        var sb = new StringBuilder();
        sb.AppendLine($"AnimatorController: {ctrl.name}");

        // Parameters
        sb.AppendLine($"\nParameters ({ctrl.parameters.Length}):");
        foreach (var p in ctrl.parameters)
            sb.AppendLine($"  {p.name} ({p.type})" +
                (p.type == AnimatorControllerParameterType.Float ? $" = {p.defaultFloat}" :
                 p.type == AnimatorControllerParameterType.Int   ? $" = {p.defaultInt}"   :
                 p.type == AnimatorControllerParameterType.Bool  ? $" = {p.defaultBool}"  : ""));

        // Layers and states
        for (int i = 0; i < ctrl.layers.Length; i++)
        {
            var layer = ctrl.layers[i];
            var sm    = layer.stateMachine;
            sb.AppendLine($"\nLayer {i}: '{layer.name}' (weight={layer.defaultWeight})");
            sb.AppendLine($"  Default state: {sm.defaultState?.name ?? "(none)"}");
            sb.AppendLine($"  States ({sm.states.Length}):");
            foreach (var cs in sm.states)
            {
                var st = cs.state;
                sb.AppendLine($"    '{st.name}'" + (st.motion != null ? $" motion='{st.motion.name}'" : ""));
                foreach (var tr in st.transitions)
                {
                    var condParts = string.Join(", ", System.Array.ConvertAll(tr.conditions,
                        c => $"{c.parameter} {c.mode} {c.threshold}"));
                    sb.AppendLine($"      → '{tr.destinationState?.name}'" +
                        $" exitTime={tr.hasExitTime} dur={tr.duration}" +
                        (condParts.Length > 0 ? $" [{condParts}]" : ""));
                }
            }
            // Any State transitions
            if (sm.anyStateTransitions.Length > 0)
            {
                sb.AppendLine($"  Any State transitions ({sm.anyStateTransitions.Length}):");
                foreach (var tr in sm.anyStateTransitions)
                {
                    var condParts = string.Join(", ", System.Array.ConvertAll(tr.conditions,
                        c => $"{c.parameter} {c.mode} {c.threshold}"));
                    sb.AppendLine($"    → '{tr.destinationState?.name}'" +
                        (condParts.Length > 0 ? $" [{condParts}]" : ""));
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── NavMesh ────────────────────────────────────────────────────────────────

    private static string BakeNavMesh(ActionPayload a)
    {
        // 1. New NavMesh Surface API (com.unity.ai.navigation package)
        var surfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
        if (surfaceType != null)
        {
            var surfaces = UnityEngine.Object.FindObjectsByType(surfaceType, FindObjectsSortMode.None);
            if (surfaces.Length > 0)
            {
                foreach (var surface in surfaces)
                    surfaceType.GetMethod("BuildNavMesh")?.Invoke(surface, null);
                return $"✓ NavMesh baked on {surfaces.Length} NavMeshSurface(s)";
            }
            return "✗ No NavMeshSurface components found in scene — add one to a GameObject first";
        }

        // 2. Legacy UnityEditor.AI.NavMeshBuilder
        try
        {
            var builderType = Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEditor");
            if (builderType != null)
            {
                var method = builderType.GetMethod("BuildNavMesh",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    method.Invoke(null, null);
                    return "✓ NavMesh baked (legacy NavMeshBuilder)";
                }
            }
        }
        catch (Exception e) { return $"✗ NavMesh bake failed: {e.Message}"; }

        return "✗ NavMesh API not found — install 'AI Navigation' package (com.unity.ai.navigation)";
    }

    // ── Package Manager ────────────────────────────────────────────────────────

    private static string AddPackage(ActionPayload a)
    {
        var packageId = !string.IsNullOrEmpty(a.content) ? a.content
                      : !string.IsNullOrEmpty(a.name)    ? a.name
                      : null;
        if (string.IsNullOrEmpty(packageId))
            return "✗ packageId is required in 'content' field (e.g. 'com.unity.inputsystem')";

        try
        {
            var clientType = Type.GetType("UnityEditor.PackageManager.Client, UnityEditor");
            if (clientType == null) return "✗ PackageManager.Client not available in this Unity version";

            var addMethod = clientType.GetMethod("Add",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (addMethod == null) return "✗ PackageManager.Client.Add not found";

            addMethod.Invoke(null, new object[] { packageId });
            return $"✓ Package installation started: '{packageId}' — check Package Manager window for progress";
        }
        catch (Exception e)
        {
            return $"✗ Failed to start package install: {e.Message}";
        }
    }

    // ── Vision ─────────────────────────────────────────────────────────────────

    private static string TakeScreenshot(ActionPayload a)
    {
        try
        {
            switch (a.view)
            {
                case "game":   return CaptureGameView();
                case "camera": return CaptureCamera();
                default:       return CaptureSceneView(); // "scene" or unspecified
            }
        }
        catch (Exception e)
        {
            return $"✗ Screenshot failed: {e.Message}";
        }
    }

    private static string CaptureCamera()
    {
        var cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
        if (cam == null) return "✗ No camera found in scene";
        return RenderCameraToBase64(cam);
    }

    private static string CaptureSceneView()
    {
        var sv = UnityEditor.SceneView.lastActiveSceneView;
        if (sv == null) return "✗ No active Scene View";
        return CaptureEditorWindow(sv);
    }

    private static string CaptureGameView()
    {
        // In Play Mode, use ScreenCapture.CaptureScreenshotAsTexture — captures the Game View
        // directly without requiring Camera.main (works even with no camera in the scene)
        if (EditorApplication.isPlaying)
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex != null) return CompressAndEncode(tex);
        }

        // In Edit Mode, try to capture the Game View window via screen grab
        var gameViewType = typeof(UnityEditor.EditorWindow).Assembly
            .GetType("UnityEditor.GameView");
        if (gameViewType != null)
        {
            var gameView = UnityEditor.EditorWindow.GetWindow(gameViewType, false, null, false);
            if (gameView != null) return CaptureEditorWindow(gameView);
        }

        // Fallback: render via main camera
        var cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
        if (cam == null) return "✗ No camera found in scene";
        return RenderCameraToBase64(cam);
    }

    private static string CaptureEditorWindow(UnityEditor.EditorWindow window)
    {
        // Focus window so it draws, then grab pixels from its position on screen
        window.Focus();
        window.Repaint();

        var pos  = window.position;
        int x    = Mathf.RoundToInt(pos.x);
        int y    = Mathf.RoundToInt(pos.y);
        int w    = Mathf.Clamp(Mathf.RoundToInt(pos.width),  64, 1920);
        int h    = Mathf.Clamp(Mathf.RoundToInt(pos.height), 64, 1080);

        // Unity coords: screen Y is flipped relative to texture
        var colors = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
            new Vector2(x, y), w, h);

        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.SetPixels(colors);
        tex.Apply();

        return CompressAndEncode(tex);
    }

    private static string RenderCameraToBase64(Camera cam)
    {
        int w = Mathf.Clamp(cam.pixelWidth,  64, 1920);
        int h = Mathf.Clamp(cam.pixelHeight, 64, 1080);

        var rt   = new RenderTexture(w, h, 24);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();

        cam.targetTexture = prev;
        RenderTexture.active = null;
        UnityEngine.Object.DestroyImmediate(rt);

        return CompressAndEncode(tex);
    }

    /// <summary>
    /// Resize screenshot to max 1024px on the longest side and encode as JPG (quality 75).
    /// Reduces payload from ~5-10MB PNG to ~100-300KB JPG.
    /// </summary>
    private static string CompressAndEncode(Texture2D source)
    {
        const int MaxDimension = 1024;
        const int JpgQuality   = 75;

        int srcW = source.width;
        int srcH = source.height;
        int dstW = srcW;
        int dstH = srcH;

        // Downscale if either dimension exceeds max
        if (srcW > MaxDimension || srcH > MaxDimension)
        {
            float scale = Mathf.Min((float)MaxDimension / srcW, (float)MaxDimension / srcH);
            dstW = Mathf.Max(64, Mathf.RoundToInt(srcW * scale));
            dstH = Mathf.Max(64, Mathf.RoundToInt(srcH * scale));

            var rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);
            UnityEngine.Object.DestroyImmediate(source);

            RenderTexture.active = rt;
            var resized = new Texture2D(dstW, dstH, TextureFormat.RGB24, false);
            resized.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            resized.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            source = resized;
        }

        var bytes = source.EncodeToJPG(JpgQuality);
        UnityEngine.Object.DestroyImmediate(source);
        return "[IMAGE:" + Convert.ToBase64String(bytes) + "]";
    }

    // ── Read (info) ────────────────────────────────────────────────────────────

    private static string GetSceneInfo()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var sb = new StringBuilder();
        sb.AppendLine($"Scene: {scene.name}  |  Path: {scene.path}");
        sb.AppendLine($"Root objects: {scene.rootCount}");
        sb.AppendLine();
        foreach (var root in scene.GetRootGameObjects())
            AppendHierarchy(sb, root, 0);
        return sb.ToString().TrimEnd();
    }

    private static void AppendHierarchy(StringBuilder sb, GameObject go, int depth)
    {
        var indent = new string(' ', depth * 2);
        var comps  = go.GetComponents<Component>()
                       .Where(c => c != null && !(c is Transform))
                       .Select(c => c.GetType().Name);
        var active = go.activeInHierarchy ? "●" : "○";
        sb.AppendLine($"{indent}{active} {go.name}  [{string.Join(", ", comps)}]");
        foreach (Transform child in go.transform)
            AppendHierarchy(sb, child.gameObject, depth + 1);
    }

    private static string GetComponentProperty(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"Not found: '{Label(a)}'";

        var type = FindComponentType(a.componentType);
        if (type == null) return $"Component type not found: '{a.componentType}'";

        var comp = go.GetComponent(type);
        if (comp == null) return $"'{go.name}' doesn't have {a.componentType}";

        if (string.IsNullOrEmpty(a.propertyName))
        {
            // No specific property — dump public + [SerializeField] private fields and properties
            var sb = new StringBuilder();
            sb.AppendLine($"{a.componentType} on '{go.name}':");
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                try { sb.AppendLine($"  [field] {f.Name} = {f.GetValue(comp)}"); } catch { }
            foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (f.GetCustomAttributes(typeof(SerializeField), true).Length > 0)
                    try { sb.AppendLine($"  [SerializeField] {f.Name} = {f.GetValue(comp)}"); } catch { }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (p.CanRead) try { sb.AppendLine($"  [prop] {p.Name} = {p.GetValue(comp)}"); } catch { }
            return sb.ToString().TrimEnd();
        }

        // Specific property/field — search public then all fields (including private), case-insensitive
        var prop = type.GetProperty(a.propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanRead)
            return $"✓ {a.componentType}.{prop.Name} = {prop.GetValue(comp)}";

        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (string.Equals(f.Name, a.propertyName, StringComparison.OrdinalIgnoreCase))
                return $"✓ {a.componentType}.{f.Name} = {f.GetValue(comp)}";

        return $"✗ Property/field '{a.propertyName}' not found on {a.componentType}";
    }

    private static string GetGameObjectInfo(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"Not found: '{Label(a)}'";
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {go.name}  |  Active: {go.activeSelf}");
        sb.AppendLine($"Tag: {go.tag}  |  Layer: {LayerMask.LayerToName(go.layer)}");
        sb.AppendLine($"Position: {go.transform.position}");
        sb.AppendLine($"Rotation: {go.transform.eulerAngles}");
        sb.AppendLine($"Scale: {go.transform.localScale}");
        sb.AppendLine($"Parent: {(go.transform.parent != null ? go.transform.parent.name : "none")}");
        sb.AppendLine($"Children: {go.transform.childCount}");
        sb.AppendLine("Components:");
        foreach (var comp in go.GetComponents<Component>())
            if (comp != null) sb.AppendLine($"  - {comp.GetType().Name}");
        return sb.ToString().TrimEnd();
    }

    private static string GetConsoleLogs(ActionPayload a)
    {
        var entries = ClaunityConsole.GetEntries();
        int requested = a.count > 0 ? a.count : entries.Count;
        var subset = entries.Skip(Math.Max(0, entries.Count - requested)).ToList();
        if (subset.Count == 0) return "No errors or warnings in the console.";
        int errors   = subset.Count(e => e.logType == "error");
        int warnings = subset.Count(e => e.logType == "warning");
        var sb = new StringBuilder();
        sb.AppendLine($"Console: {errors} error(s), {warnings} warning(s)");
        foreach (var e in subset)
        {
            var prefix = e.count > 1 ? $"(x{e.count}) " : "";
            sb.AppendLine($"[{e.logType.ToUpper()}][{e.time}] {prefix}{e.message}");
            if (!string.IsNullOrEmpty(e.stackTrace))
                sb.AppendLine($"  at {e.stackTrace}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Script Validation ──────────────────────────────────────────────────────

    private static string ValidateScript(ActionPayload a)
    {
        if (string.IsNullOrEmpty(a.content)) return "✗ content is required";

        // Try Roslyn syntax analysis — Microsoft.CodeAnalysis.CSharp is loaded in Unity Editor
        try
        {
            var roslynAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(x => x.GetName().Name == "Microsoft.CodeAnalysis.CSharp");

            if (roslynAsm != null)
            {
                var syntaxTreeType = roslynAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                var parseMethod    = syntaxTreeType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Where(m => m.Name == "ParseText")
                    .FirstOrDefault(m => m.GetParameters().Length >= 1 &&
                                        m.GetParameters()[0].ParameterType == typeof(string));

                if (parseMethod != null)
                {
                    var args = new object[parseMethod.GetParameters().Length];
                    args[0]  = a.content;
                    var tree = parseMethod.Invoke(null, args);

                    var getDiags = tree?.GetType().GetMethod("GetDiagnostics", Type.EmptyTypes);
                    var diags    = getDiags?.Invoke(tree, null);

                    if (diags != null)
                    {
                        var errors   = new List<string>();
                        var warnings = new List<string>();

                        foreach (object d in (System.Collections.IEnumerable)diags)
                        {
                            var dType    = d.GetType();
                            var severity = (int)(dType.GetProperty("Severity")?.GetValue(d) ?? -1);
                            if (severity < 2) continue; // skip Hidden (0) and Info (1)

                            var getMsg = dType.GetMethod("GetMessage", new[] { typeof(System.IFormatProvider) });
                            var msg    = getMsg?.Invoke(d, new object[] { null }) as string ?? "";

                            var location   = dType.GetProperty("Location")?.GetValue(d);
                            int lineNum    = 0;
                            if (location != null)
                            {
                                var lineSpan = location.GetType().GetMethod("GetLineSpan")?.Invoke(location, null);
                                if (lineSpan != null)
                                {
                                    var startPos = lineSpan.GetType().GetProperty("StartLinePosition")?.GetValue(lineSpan);
                                    if (startPos != null)
                                        lineNum = (int)(startPos.GetType().GetProperty("Line")?.GetValue(startPos) ?? 0) + 1;
                                }
                            }

                            var entry = lineNum > 0 ? $"Line {lineNum}: {msg}" : msg;
                            if (severity >= 3) errors.Add(entry);
                            else               warnings.Add(entry);
                        }

                        if (errors.Count == 0 && warnings.Count == 0)
                            return $"✓ Syntax valid — Roslyn: 0 errors in {a.content.Count(c => c == '\n') + 1} lines";

                        var sb = new StringBuilder();
                        if (errors.Count > 0)
                        {
                            sb.AppendLine($"✗ {errors.Count} syntax error(s) (Roslyn):");
                            foreach (var e in errors.Take(10)) sb.AppendLine($"  • {e}");
                        }
                        if (warnings.Count > 0)
                            sb.AppendLine($"  + {warnings.Count} warning(s)");
                        return sb.ToString().TrimEnd();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Claunity] ValidateScript Roslyn failed: {ex.Message}");
        }

        return ValidateScriptBasic(a.content);
    }

    private static string ValidateScriptBasic(string content)
    {
        int braces = 0, parens = 0;
        bool inString = false, inChar = false, inLineComment = false, inBlockComment = false;
        var lines  = content.Split('\n');
        var errors = new List<string>();

        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            inLineComment = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c    = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';

                if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
                if (inLineComment)  break;
                if (inString) { if (c == '\\') i++; else if (c == '"')  inString = false; continue; }
                if (inChar)   { if (c == '\\') i++; else if (c == '\'') inChar   = false; continue; }

                if (c == '/' && next == '/') { inLineComment  = true;  break; }
                if (c == '/' && next == '*') { inBlockComment = true;  i++; continue; }
                if (c == '"')  { inString = true; continue; }
                if (c == '\'') { inChar   = true; continue; }

                if      (c == '{') braces++;
                else if (c == '}') { if (--braces < 0) { errors.Add($"Line {li + 1}: unexpected '}}'"); braces = 0; } }
                else if (c == '(') parens++;
                else if (c == ')') { if (--parens < 0) { errors.Add($"Line {li + 1}: unexpected ')'"); parens = 0; } }
            }
        }

        if (braces != 0) errors.Add($"Unbalanced braces: {braces} unclosed '{{'");
        if (parens != 0) errors.Add($"Unbalanced parentheses: {parens} unclosed '('");

        if (errors.Count == 0)
            return $"✓ Syntax valid (basic check): {lines.Length} lines, balanced braces/parens";

        return $"✗ {errors.Count} issue(s) (basic check):\n" +
               string.Join("\n", errors.Select(e => $"  • {e}"));
    }

    // ── Performance Stats ──────────────────────────────────────────────────────

    private static string GetPerformanceStats()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Performance Stats ===");

        // Memory
        sb.AppendLine($"Memory Allocated:   {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L):F1} MB");
        sb.AppendLine($"Memory Reserved:    {UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024L * 1024L):F1} MB");
        sb.AppendLine($"Mono Heap:          {UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024L * 1024L):F1} MB");
        sb.AppendLine($"Mono Used:          {UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024L * 1024L):F1} MB");

        // Rendering stats (what Unity shows in the Game View Stats panel)
        sb.AppendLine($"Draw Calls:         {UnityEditor.UnityStats.drawCalls}");
        sb.AppendLine($"Batches:            {UnityEditor.UnityStats.batches}");
        sb.AppendLine($"Triangles:          {UnityEditor.UnityStats.triangles:N0}");
        sb.AppendLine($"Vertices:           {UnityEditor.UnityStats.vertices:N0}");
        sb.AppendLine($"Dynamic Batched:    {UnityEditor.UnityStats.dynamicBatchedDrawCalls}");
        sb.AppendLine($"Static Batched:     {UnityEditor.UnityStats.staticBatchedDrawCalls}");

        // FPS
        bool playing = EditorApplication.isPlaying;
        if (playing && Time.deltaTime > 0f)
            sb.AppendLine($"FPS:                {1f / Time.deltaTime:F1}");
        else
            sb.AppendLine($"FPS:                N/A ({(playing ? "paused" : "not in Play Mode")})");

        sb.AppendLine($"Play Mode:          {(playing ? "Yes" : "No")}");
        return sb.ToString().TrimEnd();
    }

    // ── Audio Tools ────────────────────────────────────────────────────────────

    private static string GetAudioSourceInfo(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";

        var audio = go.GetComponent<AudioSource>();
        if (audio == null) return $"✗ '{go.name}' has no AudioSource. Add it first: add_component AudioSource.";

        var sb = new StringBuilder();
        sb.AppendLine($"AudioSource on '{go.name}':");
        sb.AppendLine($"  Clip:          {(audio.clip != null ? $"'{audio.clip.name}'" : "none")}");
        sb.AppendLine($"  Volume:        {audio.volume:F2}");
        sb.AppendLine($"  Pitch:         {audio.pitch:F2}");
        sb.AppendLine($"  Loop:          {audio.loop}");
        sb.AppendLine($"  Mute:          {audio.mute}");
        sb.AppendLine($"  PlayOnAwake:   {audio.playOnAwake}");
        sb.AppendLine($"  SpatialBlend:  {audio.spatialBlend:F2}  (0=2D, 1=3D)");
        sb.AppendLine($"  StereoPan:     {audio.panStereo:F2}  (-1=left, 1=right)");
        sb.AppendLine($"  Priority:      {audio.priority}  (0=highest, 256=lowest)");
        sb.AppendLine($"  MinDistance:   {audio.minDistance:F1}");
        sb.AppendLine($"  MaxDistance:   {audio.maxDistance:F1}");
        sb.AppendLine($"  RolloffMode:   {audio.rolloffMode}");
        sb.AppendLine($"  OutputMixer:   {(audio.outputAudioMixerGroup != null ? $"'{audio.outputAudioMixerGroup.name}'" : "none")}");
        if (EditorApplication.isPlaying)
            sb.AppendLine($"  IsPlaying:     {audio.isPlaying}");
        return sb.ToString().TrimEnd();
    }

    private static string SetAudioSourceProperty(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";

        var audio = go.GetComponent<AudioSource>();
        if (audio == null) return $"✗ '{go.name}' has no AudioSource. Add it first: add_component AudioSource.";

        if (string.IsNullOrEmpty(a.propertyName)) return "✗ propertyName is required";
        if (string.IsNullOrEmpty(a.content))      return "✗ content (value) is required";

        Undo.RecordObject(audio, $"Claunity: Set AudioSource.{a.propertyName}");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;

        switch (a.propertyName.ToLower().Replace("_", ""))
        {
            case "volume":
                if (!float.TryParse(a.content, ns, ci, out float vol)) return $"✗ Cannot parse '{a.content}' as float";
                audio.volume = Mathf.Clamp01(vol); break;
            case "pitch":
                if (!float.TryParse(a.content, ns, ci, out float pitch)) return $"✗ Cannot parse '{a.content}' as float";
                audio.pitch = pitch; break;
            case "loop":        audio.loop        = a.content.ToLower() == "true" || a.content == "1"; break;
            case "mute":        audio.mute        = a.content.ToLower() == "true" || a.content == "1"; break;
            case "playonawake": audio.playOnAwake = a.content.ToLower() == "true" || a.content == "1"; break;
            case "spatialblend":
                if (!float.TryParse(a.content, ns, ci, out float blend)) return $"✗ Cannot parse '{a.content}' as float";
                audio.spatialBlend = Mathf.Clamp01(blend); break;
            case "mindistance":
                if (!float.TryParse(a.content, ns, ci, out float minD)) return $"✗ Cannot parse '{a.content}' as float";
                audio.minDistance = minD; break;
            case "maxdistance":
                if (!float.TryParse(a.content, ns, ci, out float maxD)) return $"✗ Cannot parse '{a.content}' as float";
                audio.maxDistance = maxD; break;
            case "priority":
                if (!int.TryParse(a.content, out int prio)) return $"✗ Cannot parse '{a.content}' as int";
                audio.priority = Mathf.Clamp(prio, 0, 256); break;
            case "stereopan":
            case "pan":
                if (!float.TryParse(a.content, ns, ci, out float pan)) return $"✗ Cannot parse '{a.content}' as float";
                audio.panStereo = Mathf.Clamp(pan, -1f, 1f); break;
            default:
                return $"✗ Unknown AudioSource property '{a.propertyName}'. Valid: volume, pitch, loop, mute, playOnAwake, spatialBlend, minDistance, maxDistance, priority, stereoPan";
        }

        EditorUtility.SetDirty(audio);
        return $"✓ Set AudioSource.{a.propertyName} = '{a.content}' on '{go.name}'";
    }

    private static string AssignAudioClip(ActionPayload a)
    {
        var go = FindObject(a);
        if (go == null) return $"✗ Not found: '{Label(a)}'";

        var audio = go.GetComponent<AudioSource>();
        if (audio == null) return $"✗ '{go.name}' has no AudioSource. Add it first: add_component AudioSource.";

        if (string.IsNullOrEmpty(a.assetPath)) return "✗ assetPath is required (e.g. Assets/Audio/MyClip.wav or clip name)";

        // Try direct asset path first
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(a.assetPath);

        // Fallback: search by name
        if (clip == null)
        {
            var guids = AssetDatabase.FindAssets($"{a.assetPath} t:AudioClip");
            foreach (var guid in guids)
            {
                var path      = AssetDatabase.GUIDToAssetPath(guid);
                var candidate = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (candidate != null && candidate.name == a.assetPath) { clip = candidate; break; }
            }
            if (clip == null && guids.Length > 0)
                clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        if (clip == null) return $"✗ AudioClip not found: '{a.assetPath}'";

        Undo.RecordObject(audio, $"Claunity: Assign AudioClip to {go.name}");
        audio.clip = clip;
        EditorUtility.SetDirty(audio);
        return $"✓ Assigned AudioClip '{clip.name}' to AudioSource on '{go.name}'";
    }

    // ── Build Player ────────────────────────────────────────────────────────────

    private static string BuildPlayer(ActionPayload action)
    {
        // content = output path (e.g. "Builds/Windows64/MyGame.exe")
        // propertyName = platform string
        // parent = scenes mode: "all" or "current"
        string outputPath  = action.content?.Trim();
        string platformStr = action.propertyName ?? "Windows64";
        string scenesMode  = action.parent ?? "all";

        if (string.IsNullOrEmpty(outputPath))
            return "✗ build_player: content must be the output path (e.g. Builds/Windows64/MyGame.exe)";

        BuildTarget target;
        switch (platformStr.ToLower())
        {
            case "windows32":   target = BuildTarget.StandaloneWindows;   break;
            case "macos":       target = BuildTarget.StandaloneOSX;       break;
            case "linux64":     target = BuildTarget.StandaloneLinux64;   break;
            case "android":     target = BuildTarget.Android;             break;
            case "ios":         target = BuildTarget.iOS;                 break;
            case "webgl":       target = BuildTarget.WebGL;               break;
            default:            target = BuildTarget.StandaloneWindows64; break;
        }

        string[] scenes;
        if (scenesMode == "all")
        {
            scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
                scenes = new[] { EditorSceneManager.GetActiveScene().path };
        }
        else
        {
            scenes = new[] { EditorSceneManager.GetActiveScene().path };
        }

        string outDir = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir))
            System.IO.Directory.CreateDirectory(outDir);

        var t0     = DateTime.Now;
        var report = BuildPipeline.BuildPlayer(scenes, outputPath, target, BuildOptions.None);
        var elapsed = (DateTime.Now - t0).TotalSeconds;

        var sum     = report.summary;
        bool ok     = sum.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
        double mb   = sum.totalSize / (1024.0 * 1024.0);
        int mins    = (int)(elapsed / 60);
        int secs    = (int)(elapsed % 60);

        if (ok)
            return $"✅ Build succeeded: {mb:F1} MB · {mins:D2}:{secs:D2} · {sum.totalErrors} errors · {sum.totalWarnings} warnings\nOutput: {outputPath}";
        else
            return $"✗ Build failed: {sum.totalErrors} errors · {sum.totalWarnings} warnings";
    }
}

}