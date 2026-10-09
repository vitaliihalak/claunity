# Claunity

An AI assistant inside the Unity Editor. Describe what you want in plain language and it works on your open project: it can inspect the scene, create and edit GameObjects and scripts, fix compile errors, run your game and look at the result, or plan a larger piece of work step by step.

It runs a small **local** Python backend on your machine and talks to Claude with **your own** Anthropic API key, or through the Claude Code CLI if you have it installed. There is no Claunity server and no account.

> **Unofficial.** Claunity is an independent project. It is not affiliated with, endorsed by, or sponsored by Unity Technologies or Anthropic.

## Status

- **Claude only.** The backend uses the Anthropic API (or the `claude` CLI). Other providers are not implemented.
- **Developed and tested on Linux.** Windows and macOS code paths exist (platform-specific paths and process handling are handled), but they have not been tested by the author. Reports and fixes are welcome.
- Unity 2021.3 or newer.
- It started life as a commercial product prepared for the Unity Asset Store, which it never reached. It is now released under the MIT license as-is.

## What it does

The window has four tabs (Project, Test and Scout are marked BETA in the UI):

| Tab | What it is for |
|---|---|
| **Chat** | Talk to the assistant. It plans, then carries out the steps itself and summarises when done. |
| **Test** | Play-and-test: it enters Play Mode, looks at screenshots and the console, and reports what it found. |
| **Project** | Describe a bigger goal; it asks clarifying questions, writes an epic/task plan, then executes the tasks one by one. |
| **Scout** | Describe an asset you need; it searches for matching Unity Asset Store packages with prices. |

Under the hood the assistant has about 75 editor tools: scene and GameObject inspection and editing, components and serialized properties, scripts (create, edit, validate, recompile), materials, prefabs, scenes, UI elements, animator controllers, input actions, audio, packages, tags and layers, physics and time settings, NavMesh baking, Play Mode control, screenshots, console logs, performance stats and player builds.

## Requirements

- Unity 2021.3+
- Python 3.9+ on your `PATH` (`python3`, `python`, or `py -3` on Windows)
- One of:
  - an [Anthropic API key](https://console.anthropic.com/), or
  - the [Claude Code](https://docs.claude.com/en/docs/claude-code) CLI (`claude`) installed and logged in

## Install

**Package Manager (git URL)**

1. In Unity: `Window → Package Manager → + → Add package from git URL…`
2. Paste:
   ```
   https://github.com/vitaliihalak/claunity.git?path=/unity-package
   ```

**Or manually**

1. Clone this repository.
2. `Window → Package Manager → + → Add package from disk…` and choose `unity-package/package.json`.

## First run

1. Open `Window → Claunity`.
2. Click **Set up Claunity backend**. This creates a private virtual environment in your user data folder and installs the four Python dependencies (about a minute, needs internet once).
3. Click **Start Claunity**. The backend starts on `127.0.0.1:8765`.
4. Open Settings (⚙). Under *AI source* choose **API Key** and paste your key, or choose **Claude Code**. Then start chatting.

The virtual environment lives outside your project:

| OS | Location |
|---|---|
| Linux | `~/.local/share/Claunity/venv` |
| macOS | `~/Library/Application Support/Claunity/venv` |
| Windows | `%LOCALAPPDATA%\Claunity\venv` |

The backend log is `~/.config/claunity/claunity.log` (in your user profile on every OS). Use the *Stop* / *Restart* buttons in Settings to control the backend; it keeps running in the background until you stop it.

## How it works

```
Claunity window (Unity EditorWindow, C#)
        │  HTTP, localhost:8765
        ▼
Python backend (FastAPI)  ──────►  Anthropic API   (your key)
        │                    └───►  claude CLI      (optional)
        │  tool calls
        ▼
Unity bridge (HTTP listener, localhost:8766)  ──►  Unity Editor API, main thread
```

Everything stays on localhost except the calls to the Anthropic API (or whatever the Claude Code CLI does).

```
claunity/
├─ unity-package/
│  ├─ Editor/      the Unity package: window, UI, tool executor, bridge
│  └─ Backend~/    the Python backend (the ~ keeps Unity from importing it)
│     └─ models.json   model list shown in the window, edit freely
├─ THIRD_PARTY_NOTICES.md
└─ LICENSE
```

## Things you should know

- **Your API key is stored locally in plain text**, in Unity's `EditorPrefs` and in `~/.config/claunity/config.json`. Treat that machine account accordingly, and never commit that file.
- **The assistant edits your project.** It creates, changes and deletes assets and scripts, and can build your player. Use version control and commit before long sessions.
- **Claude Code mode runs the CLI with `--dangerously-skip-permissions`** so it can act without prompting for each step. Only enable it on projects you are comfortable letting an agent change freely.
- Chat history is saved inside your project at `Assets/Claunity/UserSettings/`. Add that folder to your own `.gitignore` if you do not want it in version control.
- There is no rate limiting beyond a built-in guard of 12 chat requests per minute. API usage is billed to your own key; the Settings tab shows today's token count.

## Contributing

Issues and pull requests are welcome, especially for Windows and macOS. If you add a provider, keep the existing tool layer and add the provider behind `Backend~/claude_client.py`.

## License

MIT. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
