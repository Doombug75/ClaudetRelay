<div align="center">

<img src="Assets/Claudette.png" width="160" alt="Claudette — ClaudetRelay mascot"/>

# ClaudetRelay

**Multi-agent AI workspace for Windows**

*Many minds. One conversation.*

</div>

---

Run up to 20 AI participants from different providers in the same conversation — assign roles, let them collaborate on structured projects, and orchestrate who speaks when.

Connect cloud providers (Anthropic Claude, OpenAI, Google Gemini, Mistral, Groq, OpenRouter, xAI Grok) and local models via Ollama side by side. Each participant gets its own persona, tone, and role. You direct the conversation — or step back and let the agents coordinate among themselves.

---

## Screenshots

<p align="center">
  <img src="Assets/ScreenChat01.png" width="49%" alt="Multi-agent chat"/>
  <img src="Assets/ScreenProjectChat.png" width="49%" alt="Project chat"/>
</p>
<p align="center">
  <img src="Assets/ScreenParticipantsCfg.png" width="49%" alt="Participant configuration"/>
  <img src="Assets/ScreenProviders.png" width="49%" alt="Provider / API key setup"/>
</p>
<p align="center">
  <img src="Assets/ScreenProjects.png" width="49%" alt="Projects overview"/>
  <img src="Assets/ScreenProjectSettings.png" width="49%" alt="Project settings"/>
</p>
<p align="center">
  <img src="Assets/ScreenNewProject01.png" width="49%" alt="New project – type picker"/>
  <img src="Assets/ScreenNewproject02.png" width="49%" alt="New project – details"/>
</p>
<p align="center">
  <img src="Assets/ScreenRoadmap01.png" width="49%" alt="Roadmap view"/>
  <img src="Assets/ScreenRoadmap02.png" width="49%" alt="Roadmap milestone detail"/>
</p>
<p align="center">
  <img src="Assets/ScreenClaudetteHelp.png" width="49%" alt="Claudette help assistant"/>
  <img src="Assets/ScreenClaudetteHelpChat.png" width="49%" alt="Claudette help chat"/>
</p>

---

## Features

### Conversation
- **Multi-provider, multi-agent chat** — up to 20 participants from any mix of cloud providers and local Ollama models in a single shared conversation
- **Orchestration modes** — All Respond, Coordinator First, Coordinator Summarizes
- **Roles & personas** — Coordinator and Reasoner roles, custom names, answer-as aliases, tone slider, response-length settings, and saveable character files per participant
- **AI-to-AI dialogue** — enable multi-round dialogue so participants can read and reply to each other before the next user message; configurable turn limit
- **Chattiness control** — global and per-project slider controlling how eagerly participants join the conversation unprompted
- **Grounded responses** — system prompt injection prevents models from inventing personal traits, hobbies, or relationships unless a role instruction explicitly allows it
- **Rate limiting** — per-provider RPM throttling to stay within API quotas
- **Secure key storage** — API keys stored exclusively in Windows Credential Manager, never written to disk

### Projects
- **Project system** — named projects with typed templates (Novel, Theatre, Software, Game, Business, and more), per-project participant configuration, and persistent chat history
- **Roadmap** — built-in project roadmap with milestones, priorities, and progress tracking
- **AI file operations** — agents can read, write, and list files within the project folder
- **Backup** — one-click ZIP backup of any project
- **Export** — save conversations as HTML or Markdown

### World Builder
- **Entity editor** — create and manage Characters, Locations, Factions, and Lore entries with rich, schema-driven field sets
- **Character fields** — Role, Age, Level/Classes, Alignment, Background, Goal, Flaw, Arc, Voice, Health/Resources, Attributes, and Skills
- **Portrait support** — add portrait images to characters; duplicate-safe filename handling keeps the entity list clean
- **Faction membership** — characters can belong to multiple factions; each faction gets a colour badge from a 15-colour palette
- **Faction dots on character cards** — coloured dots on each character card show faction membership at a glance; wraps gracefully beyond ten factions
- **Board view** — free-canvas board for arranging all entity types spatially; drag cards, draw named relations between entities, and auto-arrange
- **Relation lines** — eight line styles (solid, dashed, dotted, double variants), custom captions, legend entries, and colour-coded strokes
- **Quick-add from board** — add any entity type directly from the board toolbar without switching views

### MCP / Bridge
- **MCP Server mode** — expose ClaudetRelay as a Model Context Protocol server; connect Claude Desktop, Claude Code, or any MCP-compatible client
- **MCP chat participation** — external clients (Claude Desktop, Claude Code) can read chat history and post messages as named participants via `chat_get_history`, `chat_post_message`, and `chat_wait_for_round` tools; enable per-project via the participant menu
- **Bridge agents** — register local Ollama models or cloud models as named Bridge agents; send tasks silently via `bridge_post_to_agents` without routing through the chat
- **Project agent roster** — save a Bridge agent roster per project; load it with one click when a project is open and restore the global roster automatically on close
- **Model Controller** — route and coordinate multiple local models through a cloud controller with a dedicated sub-tab UI; keeps heavy token work on-device
- **Configurable tool access** — enable or disable individual MCP tools per mode (Server vs. Controller) from the Bridge settings window

### Themes
- **101 built-in themes** — Catppuccin, Tokyo Night, Dracula, Gruvbox, Leatherbound series, Skyrim, Deep Rock Galactic, planetary series, Warhammer 40K factions, and many more
- **OXSUIT 1.0 format** — themes are plain `.oxsuit` XML files, drop them into the `Themes/` folder for instant loading — no restart required
- **[OXSUIT Theminator](https://github.com/Doombug75/Theminator)** — free standalone visual theme editor for creating and previewing themes interactively

---

## Requirements

- Windows 10 / 11
- .NET 10 Desktop Runtime
- At least one API key (Anthropic, OpenAI, Google Gemini, Mistral, Groq, OpenRouter, xAI Grok) **OR** a running [Ollama](https://ollama.com) instance

---

## Planned Features

| Feature | Notes |
|---|---|
| **Voice output** | Text-to-speech playback of AI responses |
| **Multi-language UI** | German as the first additional language alongside English |
| **LM Studio support** | Local model provider via LM Studio's OpenAI-compatible endpoint |
| **Codebase refactor** | Split `MainWindow.xaml.cs` into focused partial classes for better maintainability |

---

## Custom Themes

Themes use the [OXSUIT 1.0](https://github.com/Doombug75/OXSUIT) open theme standard — a lightweight XML format that defines colors and visual geometry.

```xml
<?xml version="1.0" encoding="utf-8"?>
<oxsuit version="1.0" name="My Theme">
  <colors>
    <color key="ContentBg"       value="#0D1117"/>
    <color key="ContentText"     value="#E6EDF3"/>
    <color key="SidebarBg"       value="#161B22"/>
    <!-- ... 27 core color keys total -->
  </colors>
  <tokens>
    <token key="CornerRadius" value="6" unit="px"/>
    <token key="ShadowDepth"  value="2"/>
    <!-- ... 9 geometry tokens total -->
  </tokens>
</oxsuit>
```

Drop any `.oxsuit` file into the `Themes/` folder and it appears in the theme selector immediately.  
Use **[OXSUIT Theminator](https://github.com/Doombug75/Theminator)** to create and preview themes visually — no XML editing required.

---

## Custom Project Types

Project types define the initial system prompt, suggested roles, and structural guidelines applied when a new project is created. They live as `.xaml` files in the `ProjectTypes/` folder and load at runtime — no recompilation needed.

Copy an existing file, adjust the name, description, and system prompt, and restart the app. Your new type appears in the project type picker immediately.
