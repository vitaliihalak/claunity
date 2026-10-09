import anthropic
import logging
import subprocess
import shutil
import sys

log = logging.getLogger("claunity")

# ── System prompt ─────────────────────────────────────────────────────────────
# Short and precise — tool schemas handle all the "how to use" details.

SYSTEM_PROMPT = (
    "You are Claunity — an AI assistant running live inside the user's Unity Editor. "
    "You have direct tool access to the open Unity project. "
    "CRITICAL: You are already inside the editor. Never tell the user to open Unity, use the Inspector manually, drag things, or do anything by hand — use your tools to do it directly instead. "
    "Speak as a capable partner who acts inside the editor, not as an outside advisor. "
    "Key rules:\n"
    "1. Always read before writing: call get_scene_info or read_script first when you need information.\n"
    "2. After creating or editing any script, always call recompile_scripts.\n"
    "3. After recompile_scripts, set any required serialized field references with set_component_property.\n"
    "4. Never touch anything inside Assets/Claunity/ or Packages/Claunity/ — that is the tool's own code.\n"
    "5. Check the render pipeline (get_scene_info → look for UniversalAdditionalCameraData = URP, "
    "HDAdditionalCameraData = HDRP, otherwise Built-in) before creating materials.\n"
    "6. Be concise. Format code in markdown blocks with the language tag.\n"
    "7. Never use markdown tables — use bullet lists instead."
)

# Per-mode additions
MODE_HINTS = {
    "chat": (
        "\nYou are in Chat Mode — conversational and autonomous. "
        "For complex tasks: briefly state the plan, then execute all steps without stopping. "
        "Give a short summary when done."
    ),
    "project": (
        "\nYou are in Project Mode (planning). Ask clarifying questions (max 4-5, all at once), "
        "then generate the full plan as JSON:\n"
        '{"plan":{"title":"...","epics":[{"name":"...","tasks":[{"id":1,"name":"...","description":"..."}]}]}}'
    ),
    "project_execution": (
        "\nYou are executing a specific project task autonomously. "
        "IMPORTANT: Before making any change, check if it is already done — "
        "do not install packages that are already installed, do not create files that already exist, "
        "do not add components that are already present. "
        "Execute the task completely using tools. Make reasonable decisions without asking. "
        "Only ask if you are genuinely blocked with no way forward."
    ),
    "test": (
        "\nYou are a QA engineer. Analyze ONLY the provided screenshot and console output — do NOT read scripts, do NOT describe code, do NOT include file contents."
        " Base your report strictly on what is visible in the screenshot and what appears in the console logs."
        " Your response MUST start immediately with the first section header — no intro text, no preamble."
        " Use EXACTLY these section headers on their own line:\n"
        "WORKING:\nERRORS:\nWARNINGS:\nVISUAL:\nRECOMMENDATIONS:\n"
        "Each section: short bullet points only. Each recommendation: number, [Short Title], one concise line."
    ),
}

# ── Unity Tool Definitions ────────────────────────────────────────────────────

def _vec3(desc="Position"):
    return {
        "type": "object",
        "description": desc,
        "properties": {
            "x": {"type": "number"},
            "y": {"type": "number"},
            "z": {"type": "number"},
        },
    }

def _color():
    return {
        "type": "object",
        "description": "RGBA color (0-1 range)",
        "properties": {
            "r": {"type": "number"},
            "g": {"type": "number"},
            "b": {"type": "number"},
            "a": {"type": "number"},
        },
    }

UNITY_TOOLS = [

    # ── Read / Info ─────────────────────────────────────────────────────────

    {
        "name": "get_scene_info",
        "description": "Get all GameObjects in the current Unity scene with their components and hierarchy. Call this first when you need to understand the scene.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "get_gameobject",
        "description": "Get detailed info about a specific GameObject: all components, properties, children.",
        "input_schema": {
            "type": "object",
            "properties": {"name": {"type": "string", "description": "GameObject name"}},
            "required": ["name"],
        },
    },
    {
        "name": "read_script",
        "description": "Read the full content of a C# script file. Always call this before editing a script.",
        "input_schema": {
            "type": "object",
            "properties": {
                "scriptName": {"type": "string", "description": "Script name without .cs extension"},
                "scriptPath": {"type": "string", "description": "Full script path — use to disambiguate when multiple scripts share the same name"},
            },
            "required": [],
        },
    },
    {
        "name": "get_component_property",
        "description": "Get the value of a component property on a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name"},
                "componentType": {"type": "string", "description": "Component type (e.g. Rigidbody, Transform)"},
                "propertyName":  {"type": "string", "description": "Property name (optional — omit to list all)"},
            },
            "required": ["name", "componentType"],
        },
    },
    {
        "name": "get_serialized_property",
        "description": "Get a serialized property value using dot-notation path. Target a scene object with name+componentType, OR a project asset (prefab/ScriptableObject/etc.) with assetPath.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name (scene object target)"},
                "componentType": {"type": "string", "description": "Component/script type name (scene object target)"},
                "assetPath":     {"type": "string", "description": "Project asset path (alternative to name+componentType)"},
                "propertyPath":  {"type": "string", "description": "Dot-notation path (optional — omit to list all)"},
            },
            "required": [],
        },
    },
    {
        "name": "get_animator_info",
        "description": "Get all parameters, states and transitions of an AnimatorController asset.",
        "input_schema": {
            "type": "object",
            "properties": {"assetPath": {"type": "string", "description": "Path to the .controller asset"}},
            "required": ["assetPath"],
        },
    },
    {
        "name": "get_input_asset_info",
        "description": "Get all action maps, actions and bindings from an Input Action Asset.",
        "input_schema": {
            "type": "object",
            "properties": {"assetPath": {"type": "string", "description": "Path to the .inputactions asset"}},
            "required": ["assetPath"],
        },
    },

    {
        "name": "get_console_logs",
        "description": "Get recent Unity console errors and warnings. Call this to inspect runtime errors during Play Mode or after script recompilation.",
        "input_schema": {
            "type": "object",
            "properties": {"count": {"type": "integer", "description": "Max entries to return (0 or omit = all)"}},
        },
    },
    {
        "name": "get_material_info",
        "description": "Get info about a material asset: shader, color, float properties, assigned textures.",
        "input_schema": {
            "type": "object",
            "properties": {"materialPath": {"type": "string", "description": "Asset path to the material (e.g. Assets/Materials/MyMat.mat)"}},
            "required": ["materialPath"],
        },
    },

    # ── GameObjects ─────────────────────────────────────────────────────────

    {
        "name": "create_gameobject",
        "description": "Create a new empty GameObject in the scene.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string", "description": "Name for the new GameObject"},
                "position": _vec3("World position"),
                "rotation": _vec3("Euler angles (degrees)"),
                "scale":    _vec3("Local scale"),
                "parent":   {"type": "string", "description": "Parent GameObject name (optional)"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "add_primitive",
        "description": "Add a primitive mesh GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad).",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string", "description": "Primitive type: Cube/Sphere/Capsule/Cylinder/Plane/Quad"},
                "newName":  {"type": "string", "description": "Name to give the created object (optional)"},
                "position": _vec3("World position"),
                "parent":   {"type": "string", "description": "Parent GameObject name (optional)"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "set_mesh",
        "description": "Assign a built-in Unity mesh to an existing MeshFilter on a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string", "description": "GameObject name"},
                "meshType": {"type": "string", "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]},
            },
            "required": ["name", "meshType"],
        },
    },
    {
        "name": "delete_gameobject",
        "description": "Delete a GameObject from the scene.",
        "input_schema": {
            "type": "object",
            "properties": {"name": {"type": "string", "description": "GameObject name to delete"}},
            "required": ["name"],
        },
    },
    {
        "name": "rename_gameobject",
        "description": "Rename a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":    {"type": "string", "description": "Current GameObject name"},
                "newName": {"type": "string", "description": "New name"},
            },
            "required": ["name", "newName"],
        },
    },
    {
        "name": "duplicate_gameobject",
        "description": "Duplicate a GameObject, optionally multiple times.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":    {"type": "string", "description": "Source GameObject name"},
                "newName": {"type": "string", "description": "Name for the duplicate(s)"},
                "count":   {"type": "integer", "description": "How many copies to make (default 1)"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "update_gameobject",
        "description": "Update tag, layer or static flag on a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":      {"type": "string"},
                "tag":       {"type": "string"},
                "layerName": {"type": "string"},
                "isStatic":  {"type": "boolean"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "set_active",
        "description": "Activate or deactivate a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":   {"type": "string", "description": "GameObject name"},
                "active": {"type": "boolean", "description": "true = active, false = inactive"},
            },
            "required": ["name", "active"],
        },
    },
    {
        "name": "reparent",
        "description": "Change the parent of a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":   {"type": "string", "description": "GameObject to reparent"},
                "parent": {"type": "string", "description": "New parent GameObject name (empty string = unparent to root)"},
            },
            "required": ["name", "parent"],
        },
    },
    {
        "name": "move_gameobject",
        "description": "Move a GameObject to a new world position.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "position": _vec3("Target world position"),
            },
            "required": ["name", "position"],
        },
    },
    {
        "name": "rotate_gameobject",
        "description": "Set the rotation of a GameObject (Euler angles in degrees).",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "rotation": _vec3("Euler rotation in degrees"),
            },
            "required": ["name", "rotation"],
        },
    },
    {
        "name": "scale_gameobject",
        "description": "Set the local scale of a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":  {"type": "string"},
                "scale": _vec3("Local scale (1,1,1 = default)"),
            },
            "required": ["name", "scale"],
        },
    },
    {
        "name": "set_transform",
        "description": "Set local position, local rotation and/or local scale of a GameObject in one call — relative to its parent (world if it has none). Include only the fields you want to change; omitted fields are left untouched.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "position": _vec3("Local position"),
                "rotation": _vec3("Local Euler rotation in degrees"),
                "scale":    _vec3("Local scale"),
            },
            "required": ["name"],
        },
    },
    {
        "name": "select_gameobject",
        "description": "Select a GameObject in the Unity Editor hierarchy and ping it in the scene view.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "path": {"type": "string", "description": "Full hierarchy path if name is ambiguous"},
            },
        },
    },

    # ── Components ──────────────────────────────────────────────────────────

    {
        "name": "add_component",
        "description": "Add a component to a GameObject (e.g. Rigidbody, BoxCollider, AudioSource).",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name"},
                "componentType": {"type": "string", "description": "Component type name (e.g. Rigidbody)"},
            },
            "required": ["name", "componentType"],
        },
    },
    {
        "name": "remove_component",
        "description": "Remove a component from a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string"},
                "componentType": {"type": "string"},
            },
            "required": ["name", "componentType"],
        },
    },
    {
        "name": "set_component_property",
        "description": (
            "Set a property on a component. content is always a string. "
            "For object references: use the target GameObject name, or 'ObjectName:ComponentType'. "
            "For asset references: use the asset path (e.g. Assets/Prefabs/Coin.prefab) — "
            "or prefer assign_asset, which takes a plain assetPath with no formatting guesswork."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name"},
                "componentType": {"type": "string", "description": "Component/script type name"},
                "propertyName":  {"type": "string", "description": "Property field name"},
                "content":       {"type": "string", "description": "New value as string"},
            },
            "required": ["name", "componentType", "propertyName", "content"],
        },
    },
    {
        "name": "set_serialized_property",
        "description": "Set a serialized property using dot-notation path. Target a scene object with name+componentType, OR a project asset (prefab/ScriptableObject/etc.) with assetPath. For ObjectReference, content accepts 'ObjectName:ComponentType' (scene object) or a direct 'Assets/...' path.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name (scene object target)"},
                "componentType": {"type": "string", "description": "Component/script type name (scene object target)"},
                "assetPath":     {"type": "string", "description": "Project asset path — target to modify (alternative to name+componentType)"},
                "propertyPath":  {"type": "string", "description": "Dot-notation path"},
                "content":       {"type": "string"},
            },
            "required": ["propertyPath", "content"],
        },
    },

    # ── Scripts ─────────────────────────────────────────────────────────────

    {
        "name": "create_script",
        "description": "Create a new C# script file. After this, always call recompile_scripts.",
        "input_schema": {
            "type": "object",
            "properties": {
                "scriptName": {"type": "string", "description": "Script name without .cs extension"},
                "savePath":   {"type": "string", "description": "Folder path (e.g. Assets/Scripts/)"},
                "content":    {"type": "string", "description": "Full C# source code"},
            },
            "required": ["scriptName", "savePath", "content"],
        },
    },
    {
        "name": "edit_script",
        "description": "Overwrite a C# script with new content. Always call read_script first to get the current code. After this, always call recompile_scripts.",
        "input_schema": {
            "type": "object",
            "properties": {
                "scriptName": {"type": "string", "description": "Script name without .cs extension"},
                "scriptPath": {"type": "string", "description": "Full script path — use to disambiguate when multiple scripts share the same name"},
                "content":    {"type": "string", "description": "Complete new file content"},
            },
            "required": ["scriptName", "content"],
        },
    },
    {
        "name": "attach_script",
        "description": "Attach a script component to a GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":       {"type": "string", "description": "GameObject name"},
                "scriptName": {"type": "string", "description": "Script name without .cs"},
            },
            "required": ["name", "scriptName"],
        },
    },
    {
        "name": "recompile_scripts",
        "description": "Trigger Unity script recompilation. Always call this after create_script or edit_script.",
        "input_schema": {"type": "object", "properties": {}},
    },

    # ── Materials ───────────────────────────────────────────────────────────

    {
        "name": "create_material",
        "description": "Create a new material asset. Use correct shader for the render pipeline: URP → 'Universal Render Pipeline/Lit', HDRP → 'HDRP/Lit', Built-in → 'Standard'.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string", "description": "Material name"},
                "savePath": {"type": "string", "description": "Folder path (e.g. Assets/Materials/)"},
                "shader":   {"type": "string", "description": "Shader name"},
                "color":    _color(),
            },
            "required": ["name", "savePath", "shader"],
        },
    },
    {
        "name": "assign_material",
        "description": "Assign a material to a GameObject's renderer.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":         {"type": "string", "description": "GameObject name"},
                "materialPath": {"type": "string", "description": "Asset path to the material"},
            },
            "required": ["name", "materialPath"],
        },
    },
    {
        "name": "modify_material",
        "description": "Modify a material property: color, or a float property (propertyName + propertyValue, e.g. '_Metallic', '_Smoothness').",
        "input_schema": {
            "type": "object",
            "properties": {
                "materialPath": {"type": "string"},
                "color":        _color(),
                "propertyName":  {"type": "string", "description": "Float shader property name, e.g. '_Metallic'"},
                "propertyValue": {"type": "number", "description": "Value for propertyName"},
            },
            "required": ["materialPath"],
        },
    },

    # ── Scene ───────────────────────────────────────────────────────────────

    {
        "name": "save_scene",
        "description": "Save the current scene.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "create_scene",
        "description": "Create a new empty scene.",
        "input_schema": {
            "type": "object",
            "properties": {
                "sceneName": {"type": "string"},
                "savePath":  {"type": "string", "description": "Folder path (e.g. Assets/Scenes/)"},
            },
            "required": ["sceneName", "savePath"],
        },
    },
    {
        "name": "load_scene",
        "description": "Load a scene in the Editor.",
        "input_schema": {
            "type": "object",
            "properties": {
                "sceneName": {"type": "string"},
                "additive":  {"type": "boolean", "description": "Load additively (true) or replace (false)"},
            },
            "required": ["sceneName"],
        },
    },
    {
        "name": "unload_scene",
        "description": "Unload an additively loaded scene. Cannot unload the active scene.",
        "input_schema": {
            "type": "object",
            "properties": {"sceneName": {"type": "string"}},
            "required": ["sceneName"],
        },
    },
    {
        "name": "delete_scene",
        "description": "Delete a scene asset from the project. The scene must not be currently loaded.",
        "input_schema": {
            "type": "object",
            "properties": {"sceneName": {"type": "string", "description": "Scene name or full .unity path"}},
            "required": ["sceneName"],
        },
    },

    {
        "name": "delete_asset",
        "description": "Delete any asset file from the project (script, prefab, material, texture, etc.).",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string", "description": "Full asset path starting with Assets/ (e.g. Assets/Scripts/OldScript.cs)"},
            },
            "required": ["assetPath"],
        },
    },
    {
        "name": "move_asset",
        "description": "Move or rename an asset file within the project. Creates destination folder if needed.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string", "description": "Source path (e.g. Assets/Scripts/MyScript.cs)"},
                "savePath":  {"type": "string", "description": "Destination path including filename (e.g. Assets/Code/MyScript.cs)"},
            },
            "required": ["assetPath", "savePath"],
        },
    },

    # ── Prefabs & Assets ────────────────────────────────────────────────────

    {
        "name": "create_prefab",
        "description": "Create a prefab from an existing GameObject.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string", "description": "GameObject name in the scene"},
                "savePath": {"type": "string", "description": "Asset save path (e.g. Assets/Prefabs/Player.prefab)"},
            },
            "required": ["name", "savePath"],
        },
    },
    {
        "name": "add_asset_to_scene",
        "description": "Instantiate a prefab or asset into the scene.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string"},
                "position":  _vec3(),
            },
            "required": ["assetPath"],
        },
    },

    # ── UI ──────────────────────────────────────────────────────────────────

    {
        "name": "create_ui_element",
        "description": "Create a UI element. elementType: Canvas/Panel/Button/Text/Image/InputField/Slider/Toggle/ScrollView/VerticalLayoutGroup/HorizontalLayoutGroup/GridLayoutGroup.",
        "input_schema": {
            "type": "object",
            "properties": {
                "elementType": {"type": "string"},
                "name":        {"type": "string"},
                "parent":      {"type": "string"},
                "anchor":      {"type": "string", "description": "Anchor preset: center/top-left/stretch/etc."},
                "size":        _vec3("Width (x) and height (y)"),
                "text":        {"type": "string"},
                "fontSize":    {"type": "integer"},
                "color":       _color(),
            },
            "required": ["elementType", "name"],
        },
    },
    {
        "name": "set_rect_transform",
        "description": "Set RectTransform properties (anchor, size, position) on a UI element.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "anchor":   {"type": "string"},
                "size":     _vec3("Width (x) and height (y)"),
                "position": _vec3(),
            },
            "required": ["name"],
        },
    },

    # ── Play Mode ───────────────────────────────────────────────────────────

    {
        "name": "enter_play_mode",
        "description": "Enter Unity Play Mode.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "exit_play_mode",
        "description": "Exit Unity Play Mode.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "pause_play_mode",
        "description": "Pause/unpause Unity Play Mode.",
        "input_schema": {"type": "object", "properties": {}},
    },

    # ── Animator ────────────────────────────────────────────────────────────

    {
        "name": "create_animator_controller",
        "description": "Create a new AnimatorController asset.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "savePath": {"type": "string"},
            },
            "required": ["name", "savePath"],
        },
    },
    {
        "name": "add_animator_parameter",
        "description": "Add a parameter to an AnimatorController.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath":     {"type": "string"},
                "name":          {"type": "string"},
                "parameterType": {"type": "string", "description": "Float / Int / Bool / Trigger"},
            },
            "required": ["assetPath", "name", "parameterType"],
        },
    },
    {
        "name": "add_animator_state",
        "description": "Add a state to an AnimatorController.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string"},
                "name":      {"type": "string"},
                "isDefault": {"type": "boolean"},
                "motion":    {"type": "string", "description": "Asset path to AnimationClip"},
                "layer":     {"type": "integer"},
            },
            "required": ["assetPath", "name"],
        },
    },
    {
        "name": "add_animator_transition",
        "description": "Add a transition between states in an AnimatorController.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath":          {"type": "string"},
                "fromState":          {"type": "string", "description": "Source state (or 'Any State'/'Entry')"},
                "toState":            {"type": "string"},
                "hasExitTime":        {"type": "boolean"},
                "exitTime":           {"type": "number"},
                "transitionDuration": {"type": "number"},
                "conditions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "parameter": {"type": "string"},
                            "mode":      {"type": "string", "description": "Greater/Less/Equals/NotEqual/If/IfNot"},
                            "threshold": {"type": "number"},
                        },
                    },
                },
            },
            "required": ["assetPath", "fromState", "toState"],
        },
    },

    # ── Input System ────────────────────────────────────────────────────────

    {
        "name": "create_input_action_asset",
        "description": "Create a new Input Action Asset.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":     {"type": "string"},
                "savePath": {"type": "string"},
            },
            "required": ["name", "savePath"],
        },
    },
    {
        "name": "add_input_action_map",
        "description": "Add an action map to an Input Action Asset.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string"},
                "name":      {"type": "string"},
            },
            "required": ["assetPath", "name"],
        },
    },
    {
        "name": "add_input_action",
        "description": "Add an input action to an action map.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath":     {"type": "string"},
                "name":          {"type": "string"},
                "parent":        {"type": "string", "description": "Action map name"},
                "parameterType": {"type": "string", "description": "value/passthrough/button"},
                "text":          {"type": "string"},
            },
            "required": ["assetPath", "name", "parent"],
        },
    },
    {
        "name": "add_input_binding",
        "description": "Add a binding to an input action. For WASD composite: propertyName=2DVector, content=up=<Keyboard>/w,down=<Keyboard>/s,left=<Keyboard>/a,right=<Keyboard>/d",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath":    {"type": "string"},
                "name":         {"type": "string", "description": "Binding name"},
                "parent":       {"type": "string", "description": "Action name"},
                "content":      {"type": "string", "description": "Binding path or composite definition"},
                "propertyName": {"type": "string", "description": "Composite type (e.g. 2DVector)"},
            },
            "required": ["assetPath", "name", "parent", "content"],
        },
    },

    # ── Editor & Misc ───────────────────────────────────────────────────────

    {
        "name": "execute_menu_item",
        "description": "Execute a Unity Editor menu item by path.",
        "input_schema": {
            "type": "object",
            "properties": {"menuPath": {"type": "string", "description": "Menu path (e.g. 'GameObject/Create Empty')"}},
            "required": ["menuPath"],
        },
    },
    {
        "name": "add_tag",
        "description": "Add a new tag to the project.",
        "input_schema": {
            "type": "object",
            "properties": {"name": {"type": "string"}},
            "required": ["name"],
        },
    },
    {
        "name": "add_layer",
        "description": "Add a new layer to the project.",
        "input_schema": {
            "type": "object",
            "properties": {"name": {"type": "string"}},
            "required": ["name"],
        },
    },
    {
        "name": "set_physics_gravity",
        "description": "Set the global physics gravity vector.",
        "input_schema": {
            "type": "object",
            "properties": {"position": _vec3("Gravity vector (default 0,-9.81,0)")},
            "required": ["position"],
        },
    },
    {
        "name": "set_time_setting",
        "description": "Set a Time Manager property (fixedDeltaTime, maximumDeltaTime, timeScale).",
        "input_schema": {
            "type": "object",
            "properties": {
                "propertyName": {"type": "string"},
                "content":      {"type": "string"},
            },
            "required": ["propertyName", "content"],
        },
    },
    {
        "name": "bake_navmesh",
        "description": "Bake the NavMesh for AI navigation.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "add_package",
        "description": "Add a Unity package by name (e.g. com.unity.inputsystem).",
        "input_schema": {
            "type": "object",
            "properties": {"content": {"type": "string", "description": "Package identifier"}},
            "required": ["content"],
        },
    },
    {
        "name": "create_scriptable_object",
        "description": "Create a ScriptableObject asset.",
        "input_schema": {
            "type": "object",
            "properties": {
                "scriptName": {"type": "string", "description": "The ScriptableObject class name"},
                "name":       {"type": "string", "description": "Asset filename (optional, defaults to scriptName)"},
                "savePath":   {"type": "string", "description": "Folder path (optional, defaults to Assets/Data)"},
            },
            "required": ["scriptName"],
        },
    },
    {
        "name": "take_screenshot",
        "description": "Capture a screenshot of the scene or game view.",
        "input_schema": {
            "type": "object",
            "properties": {"view": {"type": "string", "description": "scene / game / camera"}},
            "required": ["view"],
        },
    },
    # ── Project execution workflow ──────────────────────────────────────────

    {
        "name": "task_complete",
        "description": "Call this when the current project task is fully executed. Use in project_execution mode only.",
        "input_schema": {
            "type": "object",
            "properties": {
                "summary": {"type": "string", "description": "Brief summary of what was done"},
            },
            "required": ["summary"],
        },
    },
    {
        "name": "ask_user",
        "description": "Call this when you are genuinely blocked and cannot proceed without user input. Use in project_execution mode only.",
        "input_schema": {
            "type": "object",
            "properties": {
                "question": {"type": "string", "description": "The specific question to ask the user"},
            },
            "required": ["question"],
        },
    },

    # ── Build ────────────────────────────────────────────────────────────────

    {
        "name": "build_player",
        "description": "Build the Unity player.",
        "input_schema": {
            "type": "object",
            "properties": {
                "content":      {"type": "string", "description": "Output path, e.g. 'Builds/Windows64/MyGame.exe'"},
                "propertyName": {"type": "string", "description": "Platform: windows64/windows32/macos/linux64/android/ios/webgl"},
                "parent":       {"type": "string", "description": "Which scenes to include: 'all' (default, enabled scenes from Build Settings) or 'current' (active scene only)"},
            },
            "required": ["content", "propertyName"],
        },
    },

    # ── Script Validation ────────────────────────────────────────────────────

    {
        "name": "validate_script",
        "description": "Validate C# script syntax before saving. Returns errors with line numbers (Roslyn) or a basic brace-balance check. Use BEFORE create_script or edit_script to avoid unnecessary domain reloads.",
        "input_schema": {
            "type": "object",
            "properties": {
                "content": {"type": "string", "description": "Full C# source code to validate"},
            },
            "required": ["content"],
        },
    },

    # ── Performance Stats ────────────────────────────────────────────────────

    {
        "name": "get_performance_stats",
        "description": "Get current performance stats: memory (allocated/reserved/mono), draw calls, batches, triangles, vertices, and FPS (if in Play Mode). Equivalent to the Unity Game View Stats panel.",
        "input_schema": {"type": "object", "properties": {}},
    },

    # ── Audio Tools ──────────────────────────────────────────────────────────

    {
        "name": "get_audiosource_info",
        "description": "Get all AudioSource properties from a GameObject (clip, volume, pitch, loop, spatialBlend, mixer group, etc.).",
        "input_schema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "GameObject name"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "set_audiosource_property",
        "description": "Set a single AudioSource property on a GameObject. propertyName options: volume (0-1), pitch, loop (true/false), mute, playOnAwake, spatialBlend (0=2D 1=3D), minDistance, maxDistance, priority (0-256), stereoPan (-1 to 1).",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":         {"type": "string", "description": "GameObject name"},
                "propertyName": {"type": "string", "description": "AudioSource property name"},
                "content":      {"type": "string", "description": "Value as string"},
            },
            "required": ["name", "propertyName", "content"],
        },
    },
    {
        "name": "assign_audioclip",
        "description": "Assign an AudioClip asset to the AudioSource on a GameObject. Accepts full asset path (Assets/Audio/Shot.wav) or clip name.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":      {"type": "string", "description": "GameObject name"},
                "assetPath": {"type": "string", "description": "AudioClip asset path or clip name"},
            },
            "required": ["name", "assetPath"],
        },
    },

    # ── Project Assets ──────────────────────────────────────────────────────

    {
        "name": "list_assets",
        "description": "List files and subfolders directly inside a project folder (non-recursive by default). Use this before referencing an asset by path — don't guess asset paths, look them up.",
        "input_schema": {
            "type": "object",
            "properties": {
                "assetPath": {"type": "string", "description": "Folder path, e.g. 'Assets/Prefabs' (defaults to 'Assets')"},
                "recursive": {"type": "boolean", "description": "List files in all subfolders too (default false)"},
            },
            "required": [],
        },
    },
    {
        "name": "assign_asset",
        "description": "Assign a project asset (prefab, material, texture, ScriptableObject, AnimationClip, etc.) to an ObjectReference field on a component. Prefer this over set_component_property whenever the value is a project asset — no formatting needed, just a plain assetPath.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name":          {"type": "string", "description": "GameObject name (the scene object that has the component)"},
                "componentType": {"type": "string"},
                "propertyName":  {"type": "string", "description": "Field name on the component"},
                "assetPath":     {"type": "string", "description": "Project asset path, e.g. 'Assets/Prefabs/Enemy.prefab'"},
            },
            "required": ["name", "componentType", "propertyName", "assetPath"],
        },
    },
]

# ── Smart context ─────────────────────────────────────────────────────────────

_CLASSIFY_PROMPT = (
    'Reply with only "yes" or "no". '
    'Is this message purely casual conversation with no coding or Unity task? '
    'Message: "{msg}"'
)


def _is_conversational(api_key: str, message: str) -> bool:
    """Ask Haiku to classify the message. Returns True if just casual chat."""
    try:
        client = anthropic.Anthropic(api_key=api_key)
        resp = client.messages.create(
            model="claude-haiku-4-5-20251001",
            max_tokens=5,
            messages=[{"role": "user", "content": _CLASSIFY_PROMPT.format(msg=message)}],
        )
        return "yes" in resp.content[0].text.lower()
    except Exception:
        return False  # on any error — include context (safe fallback)


def build_context(message: str, project_files: str, api_key: str = "") -> str:
    """Return only the context that is actually useful for this message."""
    if not project_files:
        return ""

    # Claude Code path — no api_key available, always include context
    if not api_key:
        return project_files

    # API path — let Haiku decide
    if _is_conversational(api_key, message):
        return ""

    return project_files


# ── Max tokens ────────────────────────────────────────────────────────────────

_MODEL_LIMITS = {
    "claude-opus":   8192,
    "claude-sonnet": 8192,
    "claude-haiku":  4096,
    "claude-fable":  8192,
}
_DEFAULT_MAX_TOKENS = 4096


def _max_tokens(model: str) -> int:
    for prefix, limit in _MODEL_LIMITS.items():
        if model.startswith(prefix):
            return limit
    return _DEFAULT_MAX_TOKENS


# ── Agentic loop (Claude API with tool_use) ───────────────────────────────────

def _build_system(mode: str, personal_prompt: str = "") -> str:
    hint = MODE_HINTS.get(mode, "")
    system = SYSTEM_PROMPT + hint
    if personal_prompt and personal_prompt.strip():
        system += f"\n\n[User context]\n{personal_prompt.strip()}"
    return system


def _get_client(api_key: str) -> anthropic.Anthropic:
    return anthropic.Anthropic(api_key=api_key)


def start_agentic(api_key: str, model: str, messages: list, mode: str, personal_prompt: str = "") -> dict:
    """
    Start one agentic iteration.
    Returns:
        {"type": "tool_request", "tool_name": ..., "tool_input": ..., "tool_use_id": ..., "usage": {...}}
      OR
        {"type": "final", "reply": ..., "stop_reason": ..., "usage": {...}}
    """
    client = _get_client(api_key)
    system = _build_system(mode, personal_prompt=personal_prompt)
    max_tok = _max_tokens(model)

    response = client.messages.create(
        model=model,
        max_tokens=max_tok,
        system=system,
        tools=UNITY_TOOLS,
        messages=messages,
    )

    usage = {
        "input_tokens":  response.usage.input_tokens,
        "output_tokens": response.usage.output_tokens,
    }

    text_before_tool = ""
    for block in response.content:
        if block.type == "text":
            text_before_tool = block.text.strip()
        elif block.type == "tool_use":
            return {
                "type":        "tool_request",
                "tool_name":   block.name,
                "tool_input":  block.input,   # dict
                "tool_use_id": block.id,
                "narration":   text_before_tool,
                "usage":       usage,
            }

    text = next((b.text for b in response.content if hasattr(b, "text")), "")
    return {
        "type":        "final",
        "reply":       text,
        "stop_reason": response.stop_reason,
        "usage":       usage,
    }


def continue_agentic(api_key: str, model: str, messages: list, mode: str, personal_prompt: str = "") -> dict:
    """Continue an agentic loop after a tool result has been appended to messages."""
    return start_agentic(api_key, model, messages, mode, personal_prompt=personal_prompt)


def _extract_json_array(s: str):
    """Try to extract a JSON array from text that may contain prose or markdown fences."""
    import json as _j, re as _re
    try:
        return _j.loads(s)
    except Exception:
        pass
    s2 = _re.sub(r"^```[a-z]*\s*", "", s.strip())
    s2 = _re.sub(r"\s*```$", "", s2.strip())
    try:
        return _j.loads(s2)
    except Exception:
        pass
    m = _re.search(r'(\[[\s\S]*\])', s)
    if m:
        try:
            return _j.loads(m.group(1))
        except Exception:
            pass
    return None


# ── Scout: find real Asset Store assets via web search ────────────────────────

SCOUT_SYSTEM_PROMPT = """You are a senior Unity developer helping find a specific Unity Asset Store asset.

The user describes exactly what asset they need. Your job: find the best 1-3 real options on assetstore.unity.com.

WORKFLOW:
1. Run a targeted web search: "site:assetstore.unity.com [keywords from user request]"
2. Compare results by: relevance, rating, number of reviews, recency, pipeline support (URP/HDRP/Built-in)
3. Pick the top 1-3 best matches

RULES:
- Only include assets you confirmed exist with a real URL on assetstore.unity.com. Never fabricate URLs.
- Max 3 assets
- price format: "Free" or "$XX" (e.g. "$15")
- "why" must explain specifically why this asset matches the request

OUTPUT: Return ONLY a valid JSON array, no other text, no markdown fences.
Format:
[{"category": "short category label", "name": "Exact Asset Name", "url": "https://assetstore.unity.com/packages/...", "price": "Free" or "$XX", "why": "one sentence why it matches the request"}, ...]"""


def scout_assets(api_key: str, description: str, free_only: bool = False) -> tuple[list[dict], dict]:
    """
    Ask Claude to find real Asset Store assets using web search (API path).
    Returns (assets list, usage dict).
    """
    client = _get_client(api_key)
    free_hint = " Only FREE assets (price $0)." if free_only else ""
    user_msg = f"Find this asset on Unity Asset Store: {description}{free_hint}"

    response = client.messages.create(
        model="claude-haiku-4-5-20251001",
        max_tokens=1024,
        system=SCOUT_SYSTEM_PROMPT,
        tools=[{"type": "web_search_20250305", "name": "web_search"}],
        messages=[{"role": "user", "content": user_msg}],
    )

    usage = {
        "input_tokens":  response.usage.input_tokens,
        "output_tokens": response.usage.output_tokens,
    }

    text = next((b.text for b in response.content if hasattr(b, "text") and b.text), "[]")

    result = _extract_json_array(text)
    if result is not None:
        return result, usage

    log.warning(f"scout_assets: failed to parse response: {text[:300]}")
    return [], usage


def scout_assets_claude_code(description: str, free_only: bool = False) -> tuple[list[dict], dict]:
    """
    Find Asset Store assets using Claude Code CLI with built-in web search.
    Used when api_key is not set but Claude Code is available.
    Returns (assets list, usage dict with approximate=True).
    """
    if not shutil.which("claude"):
        raise RuntimeError("Claude Code not found. Install it: npm install -g @anthropic-ai/claude-code")

    free_hint = " Only FREE assets (price $0)." if free_only else ""
    user_msg = f"Find this asset on Unity Asset Store: {description}{free_hint}"

    cmd = [
        shutil.which("claude") or "claude", "-p", user_msg,
        "--system-prompt", SCOUT_SYSTEM_PROMPT,
        "--model", "claude-haiku-4-5-20251001",
        "--dangerously-skip-permissions",
        "--output-format", "stream-json",
        "--verbose",
        "--strict-mcp-config",   # no Unity MCP tools needed
        # intentionally no --tools "" — keep built-in web search
    ]

    final_text = ""
    output_texts = []
    proc = None
    try:
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        for raw_line in proc.stdout:
            line = raw_line.strip()
            if not line:
                continue
            try:
                event = _json.loads(line)
                etype = event.get("type", "")
                if etype == "assistant":
                    for block in event.get("message", {}).get("content", []):
                        if block.get("type") == "text":
                            t = block.get("text", "").strip()
                            if t:
                                output_texts.append(t)
                elif etype == "result":
                    final_text = event.get("result", "")
                    if final_text:
                        output_texts.append(final_text)
            except _json.JSONDecodeError:
                if line:
                    final_text += line + "\n"

        proc.stderr.read()
        try:
            proc.wait(timeout=1200)
        except subprocess.TimeoutExpired:
            proc.kill()
            raise RuntimeError("Claude Code subprocess timed out after 120 seconds")
        if proc.returncode != 0:
            raise RuntimeError("Claude Code returned a non-zero exit code")
    except Exception as e:
        raise RuntimeError(f"Claude Code scout failed: {e}")
    finally:
        if proc and proc.poll() is None:
            proc.kill()
            proc.wait()

    text = final_text.strip() or " ".join(output_texts).strip()

    approx_in  = len(user_msg + SCOUT_SYSTEM_PROMPT) // 4
    approx_out = len(text) // 4
    usage = {"input_tokens": approx_in, "output_tokens": approx_out, "approximate": True}

    result = _extract_json_array(text)
    if result is not None:
        return result, usage

    log.warning(f"scout_assets_claude_code: failed to parse: {text[:300]}")
    return [], usage


# ── Claude Code (CLI / subscription) — MCP path ──────────────────────────────

import json as _json
import os as _os
import tempfile as _tempfile

def _find_mcp_server() -> str:
    """Return the path to mcp_server.py, which lives next to this file."""
    return _os.path.join(_os.path.dirname(__file__), "mcp_server.py")


def _find_python() -> str:
    """Return the path of the Python interpreter running the backend."""
    return sys.executable


def _mcp_config_path() -> str:
    """Write a temporary MCP config file pointing to our mcp_server.py and return its path."""
    config = {
        "mcpServers": {
            "claunity": {
                "command": _find_python(),
                "args": [_find_mcp_server()],
            }
        }
    }
    fd, path = _tempfile.mkstemp(suffix=".json", prefix="claunity_mcp_")
    with _os.fdopen(fd, "w") as f:
        _json.dump(config, f)
    return path


def _tool_friendly_msg(tool_name: str, tool_input: dict) -> str:
    """Convert a Claude Code tool call to a short friendly chat message."""
    import os as _os2
    path = (tool_input.get("path") or tool_input.get("file_path") or
            tool_input.get("file") or tool_input.get("command") or
            tool_input.get("pattern") or tool_input.get("assetPath") or "")
    fname = _os2.path.basename(str(path)) if path else ""
    short_cmd = str(path)[:50] if path else ""

    msgs = {
        "Read":         f"📖 Reading {fname}..."      if fname else "📖 Reading file...",
        "Write":        f"✍️ Writing {fname}..."       if fname else "✍️ Writing file...",
        "Edit":         f"✏️ Editing {fname}..."       if fname else "✏️ Editing file...",
        "MultiEdit":    f"✏️ Editing {fname}..."       if fname else "✏️ Editing file...",
        "Bash":         f"⚡ {short_cmd}"              if short_cmd else "⚡ Running command...",
        "Glob":         "🔍 Searching files...",
        "Grep":         "🔍 Searching in files...",
        "LS":           f"📂 {fname}..."               if fname else "📂 Browsing files...",
        "Task":         "🤔 Thinking...",
        "WebSearch":    "🌐 Searching the web...",
        "WebFetch":     "🌐 Fetching page...",
        "TodoRead":     "📋 Checking todo list...",
        "TodoWrite":    "📋 Updating todo list...",
        "NotebookRead": f"📒 Reading {fname}..."       if fname else "📒 Reading notebook...",
        "NotebookEdit": f"📒 Editing {fname}..."       if fname else "📒 Editing notebook...",
        "task_complete":           "✅ Task complete",
        "ask_user":                "❓ Needs clarification...",
        "validate_script":         "🔍 Validating C# syntax...",
        "get_performance_stats":   "📊 Checking performance stats...",
        "get_audiosource_info":    "🔊 Getting AudioSource info...",
        "set_audiosource_property":"🔊 Setting audio property...",
        "assign_audioclip":        "🔊 Assigning AudioClip...",
        "list_assets":              f"📂 Listing {fname}..."  if fname else "📂 Listing project assets...",
        "assign_asset":             "🔗 Assigning asset...",
    }
    return msgs.get(tool_name, f"🔧 {tool_name}...")


_screenshot_tmp_path: str = None  # track temp file for cleanup


def _build_claude_code_prompt(message: str, context: str, history: list, image: str) -> str:
    global _screenshot_tmp_path
    import base64 as _b64, os as _os, tempfile as _tf

    # Clean up previous temp screenshot if any
    if _screenshot_tmp_path and _os.path.exists(_screenshot_tmp_path):
        try:
            _os.remove(_screenshot_tmp_path)
        except Exception:
            pass
    _screenshot_tmp_path = None

    prompt_parts = []
    for entry in (history or []):
        role_label = "User" if entry["role"] == "user" else "Assistant"
        prompt_parts.append(f"{role_label}: {entry['content']}")
    prompt_parts.append(f"User: {message}")
    full_prompt = "\n\n".join(prompt_parts)

    if image:
        try:
            img_bytes = _b64.b64decode(image)
            tmp = _tf.NamedTemporaryFile(suffix=".jpg", delete=False, prefix="claunity_screenshot_")
            tmp.write(img_bytes)
            tmp.close()
            _screenshot_tmp_path = tmp.name
            full_prompt += (
                f"\n\n[A Game View screenshot was captured and saved to: {tmp.name}]"
                f"\n[Use the Read tool to view it before writing the VISUAL section.]"
            )
        except Exception:
            full_prompt += "\n\n[Note: A screenshot was captured but could not be saved to disk.]"

    return full_prompt


def ask_claude_code_streaming(message: str, context: str = "", history: list = None,
                               mode: str = "chat", image: str = None,
                               on_event=None, project_path: str = None,
                               use_mcp: bool = True, personal_prompt: str = "",
                               model: str = "", proc_store: dict = None) -> tuple:
    """
    Streaming version of ask_claude_code.
    Calls on_event(type, text) for each event where type is "narration" | "tool" | "final" | "error".
    Returns (reply, stop_reason, usage) when done.
    """
    if not shutil.which("claude"):
        raise RuntimeError("Claude Code not found. Install it: npm install -g @anthropic-ai/claude-code")

    system = _build_system(mode, personal_prompt=personal_prompt)
    if context:
        system += f"\n\nProject files:\n{context}"

    full_prompt = _build_claude_code_prompt(message, context, history, image)
    mcp_config = _mcp_config_path()
    _log = log

    _log.debug(f"MCP server path: {_find_mcp_server()}")
    _log.debug(f"Python path: {_find_python()}")
    _log.debug(f"MCP config: {mcp_config}")

    final_text = ""
    has_any_tool = False
    output_texts = []   # collect all output for approximate token counting
    try:
        cwd = project_path if (project_path and _os.path.isdir(project_path)) else None
        cmd = [
            shutil.which("claude") or "claude", "-p", full_prompt,
            "--system-prompt", system,
            "--dangerously-skip-permissions",
            "--output-format", "stream-json",
            "--verbose",
        ]
        if model:
            cmd += ["--model", model]
        if use_mcp:
            cmd += ["--mcp-config", mcp_config, "--disallowedTools", "Bash,computer"]
        else:
            # No MCP tools, but allow built-in Read so Claude can view the screenshot file
            cmd += ["--strict-mcp-config", "--allowedTools", "Read"]
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            cwd=cwd,
        )
        if proc_store is not None:
            proc_store['proc'] = proc

        for raw_line in proc.stdout:
            line = raw_line.strip()
            if not line:
                continue
            try:
                event = _json.loads(line)
                etype = event.get("type", "")
                _log.debug(f"[stream] event type={etype}")

                if etype == "assistant":
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        btype = block.get("type", "")
                        if btype == "text":
                            text = block.get("text", "").strip()
                            if text:
                                output_texts.append(text)
                                if on_event:
                                    _log.debug(f"[stream] narration: {text[:80]!r}")
                                    on_event("narration", text)
                        elif btype == "tool_use":
                            has_any_tool = True
                            tool_name = block.get("name", "")
                            tool_input = block.get("input", {}) or {}
                            friendly = _tool_friendly_msg(tool_name, tool_input)
                            _log.debug(f"[stream] tool_use: {tool_name}")
                            if on_event:
                                on_event("tool", friendly)

                elif etype == "result":
                    final_text = event.get("result", "")
                    if final_text:
                        output_texts.append(final_text)
                    _log.debug(f"[stream] result: {final_text[:80]!r}")

            except _json.JSONDecodeError:
                if line:
                    final_text += line + "\n"

        stderr_out = proc.stderr.read()
        try:
            proc.wait(timeout=1200)
        except subprocess.TimeoutExpired:
            proc.kill()
            raise RuntimeError("Claude Code subprocess timed out after 120 seconds")
        _log.info(f"Claude Code streaming returncode={proc.returncode}")
        if stderr_out:
            _log.warning(f"Claude Code stderr: {stderr_out[:500]}")

        if proc.returncode != 0:
            raise RuntimeError(stderr_out.strip() or "Claude Code returned a non-zero exit code")

    finally:
        if proc and proc.poll() is None:
            proc.kill()
            proc.wait()
        try:
            _os.unlink(mcp_config)
        except Exception:
            pass

    # Approximate token counts: ~4 chars per token (reasonable for code/English mix)
    approx_input  = len(full_prompt + system) // 4
    approx_output = len(" ".join(output_texts)) // 4

    # If no tools were used, narration already showed the full reply — return "" to avoid duplication
    reply = final_text.strip() if has_any_tool else ""
    return reply, "stop", {"input_tokens": approx_input, "output_tokens": approx_output, "approximate": True}

