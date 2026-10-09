#!/usr/bin/env python3
"""
Claunity MCP Server — exposes Unity tools to Claude Code via MCP protocol.
Runs as a stdio MCP server, forwards tool calls to ClaunityBridge (Unity :8766).
"""

import json
import sys
import urllib.request
import urllib.error

UNITY_BRIDGE_URL = "http://127.0.0.1:8766/execute"
BRIDGE_TIMEOUT   = 30  # seconds


def call_unity(tool: str, input_dict: dict) -> str:
    """Forward a tool call to Unity via ClaunityBridge HTTP server."""
    body = json.dumps({"tool": tool, "input": input_dict}).encode()
    req  = urllib.request.Request(
        UNITY_BRIDGE_URL,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=BRIDGE_TIMEOUT) as resp:
            data = json.loads(resp.read().decode())
            return data.get("result", "✗ empty result")
    except urllib.error.URLError:
        return "✗ Unity Editor is not running or ClaunityBridge is not active. Open Unity with your project."
    except Exception as e:
        return f"✗ {e}"


# ── MCP protocol (JSON-RPC 2.0 over stdio) ────────────────────────────────────

def send(obj: dict):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def handle(msg: dict):
    method = msg.get("method", "")
    mid    = msg.get("id")

    # ── initialize ────────────────────────────────────────────────────────────
    if method == "initialize":
        send({
            "jsonrpc": "2.0", "id": mid,
            "result": {
                "protocolVersion": "2024-11-05",
                "serverInfo": {"name": "claunity", "version": "1.0.74"},
                "capabilities": {"tools": {}},
            },
        })

    # ── tools/list ────────────────────────────────────────────────────────────
    elif method == "tools/list":
        send({"jsonrpc": "2.0", "id": mid, "result": {"tools": TOOLS}})

    # ── tools/call ────────────────────────────────────────────────────────────
    elif method == "tools/call":
        params    = msg.get("params", {})
        tool_name = params.get("name", "")
        tool_args = params.get("arguments", {})

        # Workflow tools — handled locally, not forwarded to Unity
        if tool_name == "task_complete":
            summary = tool_args.get("summary", "done")
            result = f"✓ Task complete: {summary}"
        elif tool_name == "ask_user":
            question = tool_args.get("question", "")
            result = (
                f"Note: Interactive ask_user is not available in this mode. "
                f"Your question was: \"{question}\". "
                f"Proceed with your best judgment and explain your assumptions."
            )
        else:
            result = call_unity(tool_name, tool_args)

        # If Unity returned an image, send it as MCP image content so Claude can see it
        if result.startswith("[IMAGE:") and result.endswith("]"):
            base64_data = result[7:-1]
            content = [{"type": "image", "data": base64_data, "mimeType": "image/jpeg"}]
            is_error = False
        else:
            content = [{"type": "text", "text": result}]
            is_error = result.startswith("✗")

        send({
            "jsonrpc": "2.0", "id": mid,
            "result": {
                "content": content,
                "isError": is_error,
            },
        })

    # ── notifications (no response needed) ────────────────────────────────────
    elif mid is None:
        pass  # notification — ignore

    # ── unknown ───────────────────────────────────────────────────────────────
    else:
        send({
            "jsonrpc": "2.0", "id": mid,
            "error": {"code": -32601, "message": f"Method not found: {method}"},
        })


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
            handle(msg)
        except json.JSONDecodeError:
            pass
        except Exception as e:
            sys.stderr.write(f"[ClaunityMCP] error: {e}\n")


# ── Tool definitions ──────────────────────────────────────────────────────────

TOOLS = [
    {"name": "get_scene_info",      "description": "Get all GameObjects in the current scene with their hierarchy, components and transform data.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "get_gameobject",      "description": "Get detailed info about a specific GameObject including all components.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "path": {"type": "string"}}}},
    {"name": "create_gameobject",   "description": "Create a new empty GameObject in the scene.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "parent": {"type": "string"}, "position": {"type": "object"}, "rotation": {"type": "object"}, "scale": {"type": "object"}}, "required": ["name"]}},
    {"name": "delete_gameobject",   "description": "Delete a GameObject from the scene.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "path": {"type": "string"}}}},
    {"name": "rename_gameobject",   "description": "Rename a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "newName": {"type": "string"}}, "required": ["name", "newName"]}},
    {"name": "duplicate_gameobject","description": "Duplicate a GameObject, optionally multiple times.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "Source GameObject name"}, "newName": {"type": "string", "description": "Name for the duplicate(s)"}, "count": {"type": "integer", "description": "How many copies to make (default 1)"}}, "required": ["name"]}},
    {"name": "update_gameobject",   "description": "Update GameObject properties (tag, layer, static flag).", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "tag": {"type": "string"}, "layerName": {"type": "string"}, "isStatic": {"type": "boolean"}}}},
    {"name": "set_active",          "description": "Enable or disable a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "active": {"type": "boolean"}}, "required": ["name", "active"]}},
    {"name": "reparent",            "description": "Change the parent of a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "parent": {"type": "string"}}, "required": ["name"]}},
    {"name": "move_gameobject",     "description": "Set the world position of a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "position": {"type": "object"}}, "required": ["name", "position"]}},
    {"name": "rotate_gameobject",   "description": "Set the rotation (euler angles) of a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "rotation": {"type": "object"}}, "required": ["name", "rotation"]}},
    {"name": "scale_gameobject",    "description": "Set the local scale of a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "scale": {"type": "object"}}, "required": ["name", "scale"]}},
    {"name": "set_transform",       "description": "Set local position, local rotation (Euler) and/or local scale of a GameObject in one call — relative to its parent (world if it has none). All fields are optional — include only what you want to change; omitted fields are left untouched. Use move_gameobject/rotate_gameobject for the dedicated world-space equivalents.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "position": {"type": "object"}, "rotation": {"type": "object"}, "scale": {"type": "object"}}, "required": ["name"]}},
    {"name": "select_gameobject",   "description": "Select a GameObject in the Unity Editor hierarchy and ping it in the scene view.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "path": {"type": "string"}}}},
    {"name": "add_component",       "description": "Add a component to a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}}, "required": ["name", "componentType"]}},
    {"name": "remove_component",    "description": "Remove a component from a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}}, "required": ["name", "componentType"]}},
    {"name": "set_component_property","description": "Set a public or [SerializeField] private field on a component. For ObjectReference fields, content accepts either 'ObjectName:ComponentType' (a scene object) OR a direct project asset path starting with 'Assets/' (e.g. 'Assets/Prefabs/Enemy.prefab') for prefabs, materials, textures and other project assets. Prefer assign_asset for project assets — it skips the format guesswork.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}, "propertyName": {"type": "string"}, "content": {"type": "string", "description": "Value to set. For ObjectReference: 'ObjectName:ComponentType' or 'Assets/path/to/asset'"}}, "required": ["name", "componentType", "propertyName", "content"]}},
    {"name": "get_component_property","description": "Get a field value on a component. Omit propertyName to dump all [SerializeField] fields.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}, "propertyName": {"type": "string", "description": "Field name. Omit to list all serialized fields."}}, "required": ["name", "componentType"]}},
    {"name": "set_serialized_property","description": "Set any SerializedProperty using dot-notation path. For ObjectReference, content accepts 'ObjectName:ComponentType' (scene object) OR a direct project asset path starting with 'Assets/' (prefab/material/texture/etc.). The target itself (which object/asset to modify) is given via name+componentType, or via the top-level assetPath param instead of name+componentType.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}, "propertyPath": {"type": "string", "description": "Dot-notation path"}, "content": {"type": "string", "description": "Value to set. For ObjectReference: 'ObjectName:ComponentType' or 'Assets/path/to/asset'"}, "assetPath": {"type": "string", "description": "Asset path of the TARGET to modify (alternative to name+componentType)"}}, "required": ["propertyPath", "content"]}},
    {"name": "get_serialized_property","description": "Get any SerializedProperty value using dot-notation path. Omit propertyPath to list all serialized properties.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "componentType": {"type": "string"}, "propertyPath": {"type": "string", "description": "Dot-notation path. Omit to list all."}, "assetPath": {"type": "string"}}, "required": ["name", "componentType"]}},
    {"name": "create_material",     "description": "Create a new Material asset. Use correct shader for the render pipeline: URP → 'Universal Render Pipeline/Lit', HDRP → 'HDRP/Lit', Built-in → 'Standard'.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "shader": {"type": "string"}, "savePath": {"type": "string"}, "color": {"type": "object", "description": "RGBA color (0-1 range)", "properties": {"r": {"type": "number"}, "g": {"type": "number"}, "b": {"type": "number"}, "a": {"type": "number"}}}}, "required": ["name"]}},
    {"name": "assign_material",     "description": "Assign a material to a GameObject's renderer.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "materialPath": {"type": "string"}}, "required": ["name", "materialPath"]}},
    {"name": "modify_material",     "description": "Modify a material property: color, or a float property (propertyName + propertyValue, e.g. '_Metallic', '_Smoothness').", "inputSchema": {"type": "object", "properties": {"materialPath": {"type": "string"}, "propertyName": {"type": "string", "description": "Float shader property name, e.g. '_Metallic'"}, "color": {"type": "object"}, "propertyValue": {"type": "number", "description": "Value for propertyName"}}, "required": ["materialPath"]}},
    {"name": "get_material_info",   "description": "Get info about a material: shader, color, float properties, assigned textures.", "inputSchema": {"type": "object", "properties": {"materialPath": {"type": "string"}}, "required": ["materialPath"]}},
    {"name": "save_scene",          "description": "Save the current scene.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "create_scene",        "description": "Create a new scene asset.", "inputSchema": {"type": "object", "properties": {"sceneName": {"type": "string"}, "savePath": {"type": "string"}}, "required": ["sceneName"]}},
    {"name": "load_scene",          "description": "Load a scene in the editor.", "inputSchema": {"type": "object", "properties": {"sceneName": {"type": "string"}, "additive": {"type": "boolean"}}, "required": ["sceneName"]}},
    {"name": "unload_scene",        "description": "Unload an additively loaded scene. Cannot unload the active scene.", "inputSchema": {"type": "object", "properties": {"sceneName": {"type": "string"}}, "required": ["sceneName"]}},
    {"name": "delete_scene",        "description": "Delete a scene asset from the project. The scene must not be currently loaded.", "inputSchema": {"type": "object", "properties": {"sceneName": {"type": "string"}}, "required": ["sceneName"]}},
    {"name": "delete_asset",        "description": "Delete any asset file from the project (script, prefab, material, etc.).", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Full path starting with Assets/ (e.g. Assets/Scripts/OldScript.cs)"}}, "required": ["assetPath"]}},
    {"name": "move_asset",          "description": "Move or rename an asset file. Creates destination folder if needed.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Source path (e.g. Assets/Scripts/MyScript.cs)"}, "savePath": {"type": "string", "description": "Destination path including filename (e.g. Assets/Code/MyScript.cs)"}}, "required": ["assetPath", "savePath"]}},
    {"name": "create_prefab",       "description": "Create a prefab from a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "savePath": {"type": "string"}}, "required": ["name"]}},
    {"name": "add_asset_to_scene",  "description": "Instantiate a prefab or asset into the scene.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string"}, "name": {"type": "string"}, "position": {"type": "object"}}, "required": ["assetPath"]}},
    {"name": "execute_menu_item",   "description": "Execute a Unity menu item by path.", "inputSchema": {"type": "object", "properties": {"menuPath": {"type": "string"}}, "required": ["menuPath"]}},
    {"name": "add_primitive",       "description": "Create a visible 3D primitive GameObject with MeshFilter and MeshRenderer already set up. PREFER this over create_gameobject when the user wants any 3D shape. primitiveType = shape (Cube, Sphere, Capsule, Cylinder, Plane, Quad), name = GameObject name.", "inputSchema": {"type": "object", "properties": {"primitiveType": {"type": "string", "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"], "description": "The primitive shape to create"}, "name": {"type": "string", "description": "Name for the GameObject (optional, defaults to primitiveType)"}, "parent": {"type": "string"}, "position": {"type": "object"}}, "required": ["primitiveType"]}},
    {"name": "set_mesh",            "description": "Assign a built-in Unity mesh (Cube, Sphere, Capsule, Cylinder, Plane, Quad) to an existing MeshFilter component on a GameObject. Use this when a GameObject already has a MeshFilter but no mesh assigned.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "Name of the GameObject"}, "meshType": {"type": "string", "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"], "description": "The built-in mesh to assign"}}, "required": ["name", "meshType"]}},
    {"name": "create_ui_element",   "description": "Create a UI element. elementType: Canvas/Panel/Button/Text/Image/InputField/Slider/Toggle/ScrollView/VerticalLayoutGroup/HorizontalLayoutGroup/GridLayoutGroup.", "inputSchema": {"type": "object", "properties": {"elementType": {"type": "string"}, "name": {"type": "string"}, "parent": {"type": "string"}, "text": {"type": "string"}, "anchor": {"type": "string", "description": "Anchor preset: center/top-left/stretch/etc."}, "size": {"type": "object", "description": "Width (x) and height (y)"}, "fontSize": {"type": "integer"}, "color": {"type": "object", "description": "RGBA color (0-1 range)", "properties": {"r": {"type": "number"}, "g": {"type": "number"}, "b": {"type": "number"}, "a": {"type": "number"}}}}, "required": ["elementType"]}},
    {"name": "set_rect_transform",  "description": "Set RectTransform properties for UI elements.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "anchor": {"type": "string"}, "position": {"type": "object"}, "size": {"type": "object"}}, "required": ["name"]}},
    {"name": "enter_play_mode",     "description": "Enter Play Mode in Unity Editor.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "exit_play_mode",      "description": "Exit Play Mode in Unity Editor.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "pause_play_mode",     "description": "Pause or unpause Play Mode.", "inputSchema": {"type": "object", "properties": {"active": {"type": "boolean"}}}},
    {"name": "read_script",         "description": "Read the contents of a C# script file.", "inputSchema": {"type": "object", "properties": {"scriptName": {"type": "string"}, "scriptPath": {"type": "string"}}}},
    {"name": "create_script",       "description": "Create a new C# script file. After this, always call recompile_scripts.", "inputSchema": {"type": "object", "properties": {"scriptName": {"type": "string"}, "content": {"type": "string"}, "savePath": {"type": "string", "description": "Folder path (e.g. Assets/Scripts/), defaults to Assets/Scripts"}}, "required": ["scriptName", "content"]}},
    {"name": "edit_script",         "description": "Overwrite an existing C# script file with new content.", "inputSchema": {"type": "object", "properties": {"scriptName": {"type": "string"}, "content": {"type": "string"}, "scriptPath": {"type": "string"}}, "required": ["scriptName", "content"]}},
    {"name": "attach_script",       "description": "Attach a script component to a GameObject.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "scriptName": {"type": "string"}}, "required": ["name", "scriptName"]}},
    {"name": "recompile_scripts",   "description": "Trigger a script recompile (AssetDatabase.Refresh).", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "get_console_logs",    "description": "Get recent Unity console errors and warnings. Use count to limit results (0 = all).", "inputSchema": {"type": "object", "properties": {"count": {"type": "integer", "description": "Max number of entries to return (0 = all)"}}}},
    {"name": "take_screenshot",     "description": "Take a screenshot. 'camera' renders directly from the main camera into a texture (reliable, works even if the Unity window isn't focused/on top). 'scene'/'game' grab pixels from the actual editor window on screen, which can capture the wrong window if Unity isn't focused/frontmost.", "inputSchema": {"type": "object", "properties": {"view": {"type": "string", "enum": ["camera", "scene", "game"]}}}},
    {"name": "create_animator_controller", "description": "Create an AnimatorController asset.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "savePath": {"type": "string"}}, "required": ["name"]}},
    {"name": "add_animator_parameter",     "description": "Add a parameter to an AnimatorController. assetPath = controller name (e.g. 'PlayerAnimator') or full path (e.g. 'Assets/PlayerAnimator.controller'). name = parameter name.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Controller name or full asset path"}, "name": {"type": "string", "description": "Parameter name"}, "parameterType": {"type": "string", "enum": ["Float", "Int", "Bool", "Trigger"]}}, "required": ["assetPath", "name", "parameterType"]}},
    {"name": "add_animator_state",         "description": "Add a state to an AnimatorController. assetPath = controller name or full path. name = state name.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Controller name or full asset path"}, "name": {"type": "string", "description": "State name"}, "motion": {"type": "string"}, "isDefault": {"type": "boolean"}, "layer": {"type": "integer", "description": "Animator layer index (default 0)"}}, "required": ["assetPath", "name"]}},
    {"name": "add_animator_transition",    "description": "Add a transition between animator states. assetPath = controller name or full path.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Controller name or full asset path"}, "fromState": {"type": "string", "description": "Source state (or 'Any State'/'Entry')"}, "toState": {"type": "string"}, "hasExitTime": {"type": "boolean"}, "exitTime": {"type": "number"}, "transitionDuration": {"type": "number"}, "conditions": {"type": "array", "description": "Optional conditions gating the transition", "items": {"type": "object", "properties": {"parameter": {"type": "string"}, "mode": {"type": "string", "description": "Greater/Less/Equals/NotEqual/If/IfNot"}, "threshold": {"type": "number"}}}}}, "required": ["assetPath", "fromState", "toState"]}},
    {"name": "get_animator_info",          "description": "Get AnimatorController info (states, parameters, transitions). Pass controller name (e.g. 'PlayerAnimator') or full asset path.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Controller name or full asset path"}}, "required": ["assetPath"]}},
    {"name": "create_input_action_asset",  "description": "Create an Input Action Asset for the new Input System.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "savePath": {"type": "string"}}, "required": ["name"]}},
    {"name": "add_input_action_map",       "description": "Add an action map to an Input Action Asset.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string"}, "name": {"type": "string"}}, "required": ["assetPath", "name"]}},
    {"name": "add_input_action",           "description": "Add an action to an action map.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string"}, "name": {"type": "string"}, "parent": {"type": "string", "description": "Action map name (defaults to the first map)"}, "parameterType": {"type": "string", "enum": ["Button", "Value", "PassThrough"], "description": "Action type (default Button)"}, "text": {"type": "string", "description": "Expected control type, e.g. 'Vector2', 'float' (optional)"}}, "required": ["assetPath", "name"]}},
    {"name": "add_input_binding",          "description": "Add a binding to an input action. For WASD composite: propertyName=2DVector, content=up=<Keyboard>/w,down=<Keyboard>/s,left=<Keyboard>/a,right=<Keyboard>/d", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string"}, "name": {"type": "string", "description": "Action name"}, "parent": {"type": "string", "description": "Action map name (defaults to the first map)"}, "content": {"type": "string", "description": "Binding path (e.g. '<Keyboard>/space') or composite definition"}, "propertyName": {"type": "string", "description": "Composite type (e.g. 2DVector) — omit for a simple binding"}}, "required": ["assetPath", "name", "content"]}},
    {"name": "get_input_asset_info",       "description": "Get info about an Input Action Asset.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string"}}, "required": ["assetPath"]}},
    {"name": "create_scriptable_object",   "description": "Create a ScriptableObject asset.", "inputSchema": {"type": "object", "properties": {"scriptName": {"type": "string", "description": "The ScriptableObject class name"}, "name": {"type": "string", "description": "Asset filename (optional, defaults to scriptName)"}, "savePath": {"type": "string", "description": "Folder path (optional, defaults to Assets/Data)"}}, "required": ["scriptName"]}},
    {"name": "add_tag",             "description": "Add a new tag to the project.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "add_layer",           "description": "Add a new layer to the project.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "set_physics_gravity", "description": "Set the physics gravity vector.", "inputSchema": {"type": "object", "properties": {"position": {"type": "object"}}}},
    {"name": "set_time_setting",    "description": "Set a Time project setting. propertyName options: fixedTimestep/fixedDeltaTime, maximumDeltaTime, timeScale, maximumParticleDeltaTime.", "inputSchema": {"type": "object", "properties": {"propertyName": {"type": "string"}, "content": {"type": "string", "description": "New value as string"}}, "required": ["propertyName", "content"]}},
    {"name": "bake_navmesh",        "description": "Bake the NavMesh for the current scene.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "add_package",         "description": "Add a Unity package via Package Manager.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "build_player",        "description": "Build the Unity project for a target platform.", "inputSchema": {"type": "object", "properties": {"content": {"type": "string", "description": "Output path, e.g. 'Builds/Windows64/MyGame.exe'"}, "propertyName": {"type": "string", "description": "Platform: windows64/windows32/macos/linux64/android/ios/webgl"}, "parent": {"type": "string", "description": "Which scenes to include: 'all' (default, enabled scenes from Build Settings) or 'current' (active scene only)"}}, "required": ["content", "propertyName"]}},
    {"name": "task_complete",       "description": "Call this when the current project task is fully executed.", "inputSchema": {"type": "object", "properties": {"summary": {"type": "string", "description": "Brief summary of what was done"}}, "required": ["summary"]}},
    {"name": "ask_user",            "description": "Call this when you are genuinely blocked and cannot proceed without user input.", "inputSchema": {"type": "object", "properties": {"question": {"type": "string", "description": "The specific question to ask the user"}}, "required": ["question"]}},

    # ── Script Validation ──────────────────────────────────────────────────────
    {"name": "validate_script", "description": "Validate C# script syntax before saving. Returns errors with line numbers using Roslyn, or passes a basic brace-balance check as fallback. Use this BEFORE create_script or edit_script to catch syntax errors without triggering a domain reload.", "inputSchema": {"type": "object", "properties": {"content": {"type": "string", "description": "Full C# source code to validate"}}, "required": ["content"]}},

    # ── Performance Stats ──────────────────────────────────────────────────────
    {"name": "get_performance_stats", "description": "Get current performance stats: memory (allocated/reserved/mono), draw calls, batches, triangles, vertices, and FPS (in Play Mode). Equivalent to the Unity Game View Stats panel.", "inputSchema": {"type": "object", "properties": {}}},

    # ── Audio Tools ────────────────────────────────────────────────────────────
    {"name": "get_audiosource_info",     "description": "Get all AudioSource properties from a GameObject (clip, volume, pitch, loop, spatialBlend, mixer group, etc.).", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "GameObject name"}}, "required": ["name"]}},
    {"name": "set_audiosource_property", "description": "Set a single AudioSource property on a GameObject. propertyName options: volume (0-1), pitch, loop (true/false), mute, playOnAwake, spatialBlend (0=2D 1=3D), minDistance, maxDistance, priority (0-256), stereoPan (-1 to 1).", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "GameObject name"}, "propertyName": {"type": "string", "description": "Property to set"}, "content": {"type": "string", "description": "Value as string"}}, "required": ["name", "propertyName", "content"]}},
    {"name": "assign_audioclip",         "description": "Assign an AudioClip asset to the AudioSource on a GameObject. Accepts full asset path (Assets/Audio/Shot.wav) or clip name.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "GameObject name"}, "assetPath": {"type": "string", "description": "AudioClip asset path or clip name"}}, "required": ["name", "assetPath"]}},

    # ── Project Assets ────────────────────────────────────────────────────────
    {"name": "list_assets",  "description": "List files and subfolders directly inside a project folder (non-recursive by default). Use this BEFORE referencing an asset by path — don't guess asset paths, look them up.", "inputSchema": {"type": "object", "properties": {"assetPath": {"type": "string", "description": "Folder path, e.g. 'Assets/Prefabs' (defaults to 'Assets')"}, "recursive": {"type": "boolean", "description": "List files in all subfolders too (default false)"}}, "required": []}},
    {"name": "assign_asset", "description": "Assign a project asset (prefab, material, texture, ScriptableObject, AnimationClip, etc.) to an ObjectReference field on a component. Use this instead of set_component_property whenever the value is a project asset rather than a scene object — no 'ObjectName:ComponentType' formatting needed, just a plain assetPath.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "GameObject name (the scene object that has the component)"}, "componentType": {"type": "string"}, "propertyName": {"type": "string", "description": "Field name on the component"}, "assetPath": {"type": "string", "description": "Project asset path, e.g. 'Assets/Prefabs/Enemy.prefab'"}}, "required": ["name", "componentType", "propertyName", "assetPath"]}},
]


if __name__ == "__main__":
    main()
