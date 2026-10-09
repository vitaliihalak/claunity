from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from collections import deque
import json
import logging
import os
import shutil
import subprocess
import threading
import time
import uuid

from claude_client import (
    start_agentic, continue_agentic,
    ask_claude_code_streaming, build_context,
    scout_assets, scout_assets_claude_code,
)

# ── Logging ───────────────────────────────────────────────────────────────────

_LOG_DIR = os.path.join(os.path.expanduser("~"), ".config", "claunity")
os.makedirs(_LOG_DIR, exist_ok=True)

logging.basicConfig(
    level=logging.DEBUG,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.FileHandler(os.path.join(_LOG_DIR, "claunity.log"), encoding="utf-8"),
        logging.StreamHandler(),
    ],
)
log = logging.getLogger("claunity")

app = FastAPI(title="Claunity Desktop App")

_CONFIG_DIR = os.path.join(os.path.expanduser("~"), ".config", "claunity")
os.makedirs(_CONFIG_DIR, exist_ok=True)
CONFIG_PATH = os.path.join(_CONFIG_DIR, "config.json")

APP_VERSION = "1.0.0"

DEFAULT_MODEL = "claude-sonnet-4-6"

MODELS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models.json")

FALLBACK_MODELS = {
    "claude": ["claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"],
}


# ── Rate limiter ──────────────────────────────────────────────────────────────

RATE_LIMIT_REQUESTS = 12
RATE_LIMIT_WINDOW   = 60

_rate_timestamps: deque = deque()
_rate_lock = threading.Lock()


def _check_rate_limit() -> bool:
    """Return True if request is allowed, False if rate limit exceeded."""
    now = time.time()
    with _rate_lock:
        while _rate_timestamps and now - _rate_timestamps[0] > RATE_LIMIT_WINDOW:
            _rate_timestamps.popleft()
        if len(_rate_timestamps) >= RATE_LIMIT_REQUESTS:
            return False
        _rate_timestamps.append(now)
        return True


# ── Daily token usage ─────────────────────────────────────────────────────────

USAGE_PATH = os.path.join(os.path.expanduser("~"), ".config", "claunity", "usage.json")


def _today() -> str:
    from datetime import date
    return date.today().isoformat()


def _load_usage() -> dict:
    try:
        if os.path.exists(USAGE_PATH):
            with open(USAGE_PATH) as f:
                return json.load(f)
    except Exception:
        pass
    return {}


def _save_usage(data: dict):
    try:
        with open(USAGE_PATH, "w") as f:
            json.dump(data, f, indent=2)
    except Exception:
        pass


def track_tokens(input_tokens: int, output_tokens: int, approximate: bool = False):
    """Add tokens to today's running total."""
    if not input_tokens and not output_tokens:
        return
    data  = _load_usage()
    today = _today()
    day   = data.get(today, {"input_tokens": 0, "output_tokens": 0, "requests": 0, "has_approx": False})
    day["input_tokens"]  += input_tokens
    day["output_tokens"] += output_tokens
    day["requests"]      += 1
    if approximate:
        day["has_approx"] = True
    data[today] = day
    _save_usage(data)


# ── Session storage ───────────────────────────────────────────────────────────

SESSION_TTL = 600

class _Session:
    def __init__(self, messages: list, model: str, mode: str, api_key: str, personal_prompt: str = ""):
        self.messages       = messages
        self.model          = model
        self.mode           = mode
        self.api_key        = api_key
        self.personal_prompt = personal_prompt
        self.touched        = time.time()

    def touch(self):
        self.touched = time.time()

_sessions: dict[str, _Session] = {}

# ── Streaming queues (Claude Code mode) ───────────────────────────────────────
_stream_queues: dict[str, list] = {}
_stream_done:   dict[str, bool] = {}
_stream_procs:  dict[str, object] = {}
_stream_lock = threading.Lock()


def _stream_put(session_id: str, event: dict):
    with _stream_lock:
        if session_id in _stream_queues:
            _stream_queues[session_id].append(event)


def _cleanup_sessions():
    now = time.time()
    expired = [k for k, v in _sessions.items() if now - v.touched > SESSION_TTL]
    for k in expired:
        _sessions.pop(k, None)
    stale_streams = [k for k in _stream_done if k not in _stream_queues]
    for k in stale_streams:
        _stream_done.pop(k, None)


def _periodic_cleanup():
    """Background thread: clean expired sessions every 60 seconds."""
    while True:
        time.sleep(60)
        try:
            _cleanup_sessions()
        except Exception:
            pass


threading.Thread(target=_periodic_cleanup, daemon=True).start()


# ── Config helpers ────────────────────────────────────────────────────────────

def fetch_models() -> dict:
    """Model list shown in the Unity window; edit models.json to add or remove entries."""
    try:
        with open(MODELS_PATH, encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict) and data:
            return data
    except Exception:
        pass
    return FALLBACK_MODELS


def load_config() -> dict:
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r") as f:
            return json.load(f)
    return {}


def save_config(config: dict):
    with open(CONFIG_PATH, "w") as f:
        json.dump(config, f, indent=2)


# ── Request / Response models ─────────────────────────────────────────────────

class HistoryMessage(BaseModel):
    role: str
    content: str


class ChatRequest(BaseModel):
    message:        str
    project_files:  str = ""
    project_path:   str = ""
    history:        list[HistoryMessage] = []
    mode:           str = "chat"
    image:          str = ""
    model_override: str = ""


class ChatContinueRequest(BaseModel):
    session_id:  str
    tool_use_id: str
    tool_result: str


class ConfigRequest(BaseModel):
    api_key:         str  = ""
    model:           str  = DEFAULT_MODEL
    use_claude_code: bool = False
    personal_prompt: str  = ""
    history_limit:   int  = 4


# ── Error handler helper ──────────────────────────────────────────────────────

def _handle_ai_error(e: Exception, config: dict) -> JSONResponse:
    msg = str(e)
    if "401" in msg or "invalid_api_key" in msg or "authentication" in msg.lower():
        detail, code = "Invalid API key — check it in Settings.", "invalid_key"
    elif "403" in msg or "forbidden" in msg.lower():
        detail, code = "Access denied — your API key may not have access to this model.", "forbidden"
    elif "429" in msg or "rate_limit" in msg.lower():
        detail, code = "Rate limit hit — wait a moment and try again.", "rate_limit"
    elif "529" in msg or "overloaded" in msg.lower():
        detail, code = "AI provider is overloaded — try again in a few seconds.", "overloaded"
    elif "insufficient_quota" in msg or "billing" in msg.lower():
        detail, code = "API quota exhausted — check your billing at Anthropic.", "quota"
    elif "model" in msg.lower() and "not found" in msg.lower():
        m = config.get("model", DEFAULT_MODEL)
        detail, code = f"Model '{m}' is not available — pick another in Settings.", "model_not_found"
    elif "timeout" in msg.lower() or "timed out" in msg.lower():
        detail, code = "Request timed out — the AI took too long. Please retry.", "timeout"
    elif "connection" in msg.lower():
        detail, code = "Cannot reach Anthropic — check your internet connection.", "connection"
    elif "Claude Code not found" in msg:
        detail, code = msg, "claude_code_not_found"
    else:
        detail, code = "Something went wrong. Please try again.", "unknown"
    return JSONResponse(status_code=500, content={"detail": detail, "error_code": code})


# ── Endpoints ─────────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/shutdown")
def shutdown():
    import threading
    def _stop():
        time.sleep(0.3)
        os.kill(os.getpid(), __import__("signal").SIGTERM)
    threading.Thread(target=_stop, daemon=True).start()
    return {"status": "shutting_down"}


@app.get("/version")
def get_version():
    return {
        "version":          APP_VERSION,
        "update_available": False,
        "update_required":  False,
        "latest_version":   APP_VERSION,
        "downloads":        {},
    }


@app.get("/check-claude-code")
def check_claude_code_endpoint():
    if not shutil.which("claude"):
        return {"found": False, "version": None}
    try:
        result = subprocess.run([shutil.which("claude") or "claude", "--version"], capture_output=True, text=True, timeout=5)
        return {"found": True, "version": result.stdout.strip() or result.stderr.strip()}
    except Exception:
        return {"found": False, "version": None}


@app.get("/models")
def get_models():
    return fetch_models()


@app.get("/usage")
def get_usage():
    data  = _load_usage()
    today = _today()
    return {"today": data.get(today, {"input_tokens": 0, "output_tokens": 0, "requests": 0, "has_approx": False})}


@app.post("/usage/reset")
def reset_usage():
    data  = _load_usage()
    today = _today()
    data[today] = {"input_tokens": 0, "output_tokens": 0, "requests": 0, "has_approx": False}
    _save_usage(data)
    return {"status": "reset"}


@app.post("/chat/stream/cancel/{session_id}")
def cancel_stream(session_id: str):
    with _stream_lock:
        proc_store = _stream_procs.get(session_id)
        _stream_done[session_id] = True
    if proc_store and 'proc' in proc_store:
        try:
            proc_store['proc'].kill()
            log.info(f"Cancelled stream session {session_id}")
        except Exception as e:
            log.warning(f"Cancel stream error: {e}")
    _stream_procs.pop(session_id, None)
    _stream_queues.pop(session_id, None)
    return {"status": "cancelled"}


@app.post("/config")
def set_config(req: ConfigRequest):
    config = load_config()
    config["api_key"]         = req.api_key
    config["model"]           = req.model
    config["use_claude_code"] = req.use_claude_code
    config["personal_prompt"] = req.personal_prompt
    config["history_limit"]   = max(1, req.history_limit)
    save_config(config)
    return {"status": "saved"}


@app.post("/chat")
def chat(req: ChatRequest):
    """Start a new agentic conversation turn."""
    if not _check_rate_limit():
        log.warning("/chat blocked by rate limiter")
        return JSONResponse(status_code=429, content={
            "detail": "Too many requests — Claunity sent more than 12 messages in 60 seconds. This may indicate a bug. Please wait a moment.",
            "error_code": "rate_limit_local",
        })

    _cleanup_sessions()
    config          = load_config()
    use_claude_code = config.get("use_claude_code", False)
    personal_prompt = config.get("personal_prompt", "")
    effective_model = req.model_override if req.model_override else config.get("model", DEFAULT_MODEL)

    log.info(f"/chat mode={req.mode} claude_code={use_claude_code} model={effective_model} msg={req.message[:60]!r}")

    try:
        # ── Claude Code path (streaming) ──────────────────────────────────
        if use_claude_code:
            context = build_context(req.message, req.project_files)
            history = [{"role": m.role, "content": m.content} for m in req.history]
            session_id = str(uuid.uuid4())

            with _stream_lock:
                _stream_queues[session_id] = []
                _stream_done[session_id]   = False

            log.info(f"Claude Code streaming: session={session_id}, context_len={len(context)}")

            msg_copy      = req.message
            mode_copy     = req.mode
            image_copy    = req.image or None
            path_copy     = req.project_path or None
            pp_copy       = personal_prompt
            model_copy    = effective_model
            proc_store    = {}

            with _stream_lock:
                _stream_procs[session_id] = proc_store

            def _run_streaming():
                try:
                    def on_event(etype, text):
                        _stream_put(session_id, {"type": etype, "text": text})

                    reply, stop_reason, usage = ask_claude_code_streaming(
                        msg_copy, context=context, history=history,
                        mode=mode_copy, image=image_copy, on_event=on_event,
                        project_path=path_copy,
                        use_mcp=(mode_copy != "test"),
                        personal_prompt=pp_copy,
                        model=model_copy,
                        proc_store=proc_store,
                    )
                    log.info(f"Claude Code streaming done: reply_len={len(reply)}")
                    track_tokens(usage.get("input_tokens", 0), usage.get("output_tokens", 0),
                                 approximate=usage.get("approximate", False))
                    _stream_put(session_id, {"type": "final", "text": reply})
                except Exception as exc:
                    log.error(f"Claude Code streaming error: {exc}")
                    _stream_put(session_id, {"type": "error", "text": str(exc)})
                finally:
                    with _stream_lock:
                        _stream_done[session_id] = True

            threading.Thread(target=_run_streaming, daemon=True).start()

            return {
                "type":          "streaming",
                "session_id":    session_id,
                "input_tokens":  0,
                "output_tokens": 0,
            }

        # ── Claude API path (agentic tool_use) ───────────────────────────
        api_key = config.get("api_key")
        if not api_key:
            raise HTTPException(status_code=400, detail="API key not set. Open Settings in Claunity.")

        model   = effective_model
        context = build_context(req.message, req.project_files, api_key)

        messages = []

        for entry in req.history:
            messages.append({"role": entry.role, "content": entry.content})

        if req.image:
            text_part = req.message or "Analyze this screenshot."
            if context:
                text_part = f"{context}\n\n{text_part}"
            user_content = [
                {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": req.image}},
                {"type": "text",  "text": text_part},
            ]
        else:
            user_content = req.message
            if context:
                user_content = f"[Project files]\n{context}\n\n{req.message}"

        messages.append({"role": "user", "content": user_content})

        if req.mode == "test":
            from claude_client import _get_client, _build_system, _max_tokens
            client     = _get_client(api_key)
            test_model = "claude-haiku-4-5-20251001"
            response = client.messages.create(
                model=test_model,
                max_tokens=_max_tokens(test_model),
                system=_build_system("test", personal_prompt=personal_prompt),
                messages=messages,
            )
            reply = next((b.text for b in response.content if hasattr(b, "text")), "")
            track_tokens(response.usage.input_tokens, response.usage.output_tokens)
            return {
                "type":          "final",
                "reply":         reply,
                "stop_reason":   response.stop_reason,
                "input_tokens":  response.usage.input_tokens,
                "output_tokens": response.usage.output_tokens,
            }

        result = start_agentic(api_key, model, messages, req.mode, personal_prompt=personal_prompt)

        if result["type"] == "final":
            track_tokens(result["usage"]["input_tokens"], result["usage"]["output_tokens"])
            return {
                "type":          "final",
                "reply":         result["reply"],
                "stop_reason":   result["stop_reason"],
                "input_tokens":  result["usage"]["input_tokens"],
                "output_tokens": result["usage"]["output_tokens"],
            }

        session_id = str(uuid.uuid4())

        session_messages = messages + [{
            "role": "assistant",
            "content": [{
                "type":  "tool_use",
                "id":    result["tool_use_id"],
                "name":  result["tool_name"],
                "input": result["tool_input"],
            }],
        }]

        _sessions[session_id] = _Session(session_messages, model, req.mode, api_key, personal_prompt=personal_prompt)

        track_tokens(result["usage"]["input_tokens"], result["usage"]["output_tokens"])
        narration = result.get("narration", "")
        return {
            "type":           "tool_request",
            "session_id":     session_id,
            "tool_name":      result["tool_name"],
            "tool_input_json": json.dumps(result["tool_input"]),
            "tool_use_id":    result["tool_use_id"],
            "narration":      narration,
            "input_tokens":   result["usage"]["input_tokens"],
            "output_tokens":  result["usage"]["output_tokens"],
        }

    except HTTPException:
        raise
    except subprocess.TimeoutExpired:
        return JSONResponse(status_code=500, content={
            "detail": "Claude Code timed out. Try a simpler prompt or retry.",
            "error_code": "timeout",
        })
    except Exception as e:
        return _handle_ai_error(e, config)


@app.post("/chat/continue")
def chat_continue(req: ChatContinueRequest):
    """Continue an agentic loop after Unity executed a tool."""
    _cleanup_sessions()

    session = _sessions.get(req.session_id)
    if not session:
        return JSONResponse(status_code=404, content={
            "detail": "Session expired or not found. Please start a new message.",
            "error_code": "session_expired",
        })

    session.touch()

    try:
        session.messages.append({
            "role": "user",
            "content": [{
                "type":        "tool_result",
                "tool_use_id": req.tool_use_id,
                "content":     req.tool_result,
            }],
        })

        result = continue_agentic(session.api_key, session.model, session.messages, session.mode, personal_prompt=session.personal_prompt)

        if result["type"] == "final":
            _sessions.pop(req.session_id, None)
            track_tokens(result["usage"]["input_tokens"], result["usage"]["output_tokens"])
            return {
                "type":          "final",
                "reply":         result["reply"],
                "stop_reason":   result["stop_reason"],
                "input_tokens":  result["usage"]["input_tokens"],
                "output_tokens": result["usage"]["output_tokens"],
            }

        session.messages.append({
            "role": "assistant",
            "content": [{
                "type":  "tool_use",
                "id":    result["tool_use_id"],
                "name":  result["tool_name"],
                "input": result["tool_input"],
            }],
        })

        track_tokens(result["usage"]["input_tokens"], result["usage"]["output_tokens"])
        narration = result.get("narration", "")
        return {
            "type":           "tool_request",
            "session_id":     req.session_id,
            "tool_name":      result["tool_name"],
            "tool_input_json": json.dumps(result["tool_input"]),
            "tool_use_id":    result["tool_use_id"],
            "narration":      narration,
            "input_tokens":   result["usage"]["input_tokens"],
            "output_tokens":  result["usage"]["output_tokens"],
        }

    except Exception as e:
        _sessions.pop(req.session_id, None)
        return _handle_ai_error(e, load_config())


@app.get("/chat/stream/{session_id}")
def get_stream_events(session_id: str):
    """Poll for streaming events from a Claude Code subprocess."""
    with _stream_lock:
        if session_id not in _stream_queues and session_id not in _stream_done:
            return {"events": [], "done": True}
        events = list(_stream_queues.pop(session_id, []))
        done   = _stream_done.get(session_id, False)
        if done:
            _stream_done.pop(session_id, None)
        else:
            _stream_queues[session_id] = []
    reply = next((e["text"] for e in events if e.get("type") == "final"), "")
    return {"events": events, "done": done, "reply": reply}


# ── Scout ─────────────────────────────────────────────────────────────────────

ASSET_STORE_SEARCH_BASE = "https://assetstore.unity.com/search#q={query}&orderBy=4"

class ScoutRequest(BaseModel):
    description: str
    free_only:   bool = False


@app.post("/scout")
def scout(req: ScoutRequest):
    """Ask Claude to find real Asset Store assets using web search."""
    config          = load_config()
    api_key         = config.get("api_key", "")
    use_claude_code = config.get("use_claude_code", False)

    try:
        if use_claude_code:
            assets, usage = scout_assets_claude_code(req.description, req.free_only)
            track_tokens(usage.get("input_tokens", 0), usage.get("output_tokens", 0),
                         approximate=usage.get("approximate", False))
        else:
            if not api_key:
                return JSONResponse(status_code=400, content={
                    "detail": "API key not set. Add one in Settings.",
                    "error_code": "no_key",
                })
            assets, usage = scout_assets(api_key, req.description, req.free_only)
            track_tokens(usage.get("input_tokens", 0), usage.get("output_tokens", 0))
    except Exception as e:
        return _handle_ai_error(e, config)

    return {"assets": assets}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8765)
