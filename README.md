<div align="center">

<img src="Assets/Claudette.png" width="160" alt="Claudette — ClaudetRelay mascot"/>

# ClaudetRelay

**Multi-agent AI workspace for creative projects on Windows**

*Brainstorm. Build. Organise. Create.*

</div>

---

ClaudetRelay is a **creative project workspace** powered by a team of AI participants.
Whether you are writing a novel, designing an RPG campaign, building a game, or mapping out a software project — you direct a group of AI agents who brainstorm, plan, research, and generate documents alongside you.

Unlike a simple chatbot, ClaudetRelay organises your work into **structured projects** with their own file systems, roadmaps, world-builder databases, and agent rosters. Everything the AI produces is saved as real, plain files you can open, edit, and diff in any tool — no locked-in database, no hidden state.

---

### What can you do with it?

| Use case | How ClaudetRelay helps |
|---|---|
| **RPG world building** | Build a campaign setting with Characters, Locations, Factions, and Lore. Visualise relationships on a free-canvas board. Let AI agents invent NPCs, histories, and plot hooks on demand. |
| **Fiction & story writing** | Assign one agent as your plot architect, another as a character voice, a third as continuity checker. Run them in parallel and compare their takes. |
| **Game design** | Use the roadmap to track milestones, let AI agents produce design docs, dialogue trees, and balance spreadsheets — all saved as real files in your project folder. |
| **Software planning** | Break a codebase into tasks on the roadmap, have agents draft specs, architecture diagrams, and READMEs, output them as PDF or Word docs. |
| **Brainstorming & research** | Throw a question to six models at once and compare answers side by side. Use a Coordinator agent to synthesise the best ideas from the group. |

---

### Boards on boards — the World Builder

The heart of ClaudetRelay for creative projects is the **World Builder** — a spatial canvas where you place entity cards (Characters, Locations, Factions, Lore items) and draw named relation lines between them.

Each entity has a rich set of fields (role, alignment, backstory, arc, resources, skills, portrait…). Cards can carry **nested boards** — so a Faction card can open its own internal board listing its members and their relationships. You build as deep as your world needs.

AI agents can read the world state, suggest new entities, and write directly into the project's entity files. They see the same structured data you see.

---

### Behaviour controls — directed or autonomous

A layered set of controls lets you tune how the AI team behaves:

| Setting | Scope | What it does |
|---|---|---|
| **Tone** | Global | Formal ↔ casual, or a fixed personality mode (Mockingbird / Buccaneer) |
| **Chattiness** | Per chat / per project | How eagerly participants join without being prompted |
| **Autonomy** | Per project | How independently agents act when left to run |
| **Response length** | Global + per participant in project | Concise ↔ exhaustive |
| **Role** | Per participant | Coordinator (summarises, delegates) or Reasoner (executes, creates) |
| **Role instructions** | Per participant in project | Free-text instructions that override or extend the default behaviour for that agent in this project |

Point them at pure creative output and step back — or keep tight control and use them as smart research assistants. The same app supports both styles.

---

### Transparent file output

Every file the AI produces lands in your project's folder as a real file:

```
MyProject/
  INPUT/       ← files you give the agents to read
  OUTPUT/       ← files agents generate
  PROJECTPLAN/  ← roadmap, design docs, notes
  AI-Characters/ ← per-project agent persona files
```

Agents can **read** `.txt`, `.md`, `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`, `.odp` from INPUT and **write** formatted output to OUTPUT:

| Output tag | Format |
|---|---|
| `<output file="notes.md">` | Plain Markdown |
| `<outputpdf file="report.pdf">` | PDF (A4, styled headings, tables, code blocks) |
| `<outputoffice file="report.docx">` | Word document |
| `<outputoffice file="report.odt">` | LibreOffice Writer |
| `<outputoffice file="data.xlsx">` | Excel (each Markdown table → its own sheet) |
| `<outputoffice file="data.ods">` | LibreOffice Calc |

No conversion tools, no LibreOffice install, no Adobe — everything is generated natively from Markdown.

---

### Connect any AI provider

| Cloud | Local |
|---|---|
| Anthropic Claude | Ollama (any model) |
| OpenAI GPT | LM Studio (local server) |
| Google Gemini | |
| Mistral | |
| Groq | |
| OpenRouter | |
| xAI Grok | |

Mix cloud and local agents in the same project. Run a fast local model for first drafts and a large cloud model for final polish. API keys are stored exclusively in Windows Credential Manager — never written to disk.

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

## Full Feature List

### Conversation
- **Multi-provider, multi-agent chat** — up to 20 participants from any mix of cloud providers and local Ollama models in a single shared conversation
- **Orchestration modes** — All Respond, Coordinator First, Coordinator Summarizes
- **Roles & personas** — Coordinator and Reasoner roles, custom names, answer-as aliases, and saveable character files per participant
- **Tone** — global formal ↔ casual slider; or lock to Mockingbird (warm/dramatic) or Buccaneer (pirate) personality mode
- **Chattiness** — per-chat and per-project slider controlling how eagerly participants join without being prompted
- **Response length** — global default; can be overridden per participant inside a project
- **AI-to-AI dialogue** — enable multi-round dialogue so participants can read and reply to each other before the next user message; configurable turn limit
- **Grounded responses** — system prompt injection prevents models from inventing personal traits, hobbies, or relationships unless a role instruction explicitly allows it
- **Rate limiting** — per-provider RPM throttling to stay within API quotas
- **Secure key storage** — API keys stored exclusively in Windows Credential Manager, never written to disk

### Projects
- **Project system** — named projects with typed templates (Novel, Theatre, Software, Game, Business, and more), per-project participant configuration, and persistent chat history
- **Per-project autonomy** — slider that controls how independently agents operate when you step back; each project can have its own setting
- **Per-participant role instructions** — free-text instructions per agent inside a project, letting you specialise behaviour beyond the global persona
- **Roadmap** — built-in project roadmap with milestones, priorities, and progress tracking
- **File browser** — collapsible folder sections with search filter; browse INPUT, OUTPUT, PROJECTPLAN folders inside the app
- **File checkout** — read-only and read-write file locking so multiple agents can research in parallel without overwriting each other; smart idle-timeout reminder system
- **AI file reading** — agents can read `.txt`, `.md`, `.rst`, `.html`, `.csv`, `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`, `.odp` directly from INPUT
- **AI file writing** — agents can write Markdown, PDF, Word, LibreOffice Writer, Excel, and LibreOffice Calc files to OUTPUT with a single tag
- **Backup** — one-click ZIP backup of any project
- **Export** — save conversations as HTML or Markdown

### World Builder
- **Entity editor** — create and manage Characters, Locations, Factions, and Lore entries with rich, schema-driven field sets
- **Character fields** — Role, Age, Level/Classes, Alignment, Background, Goal, Flaw, Arc, Voice, Health/Resources, Attributes, and Skills
- **Portrait support** — add portrait images to characters; duplicate-safe filename handling keeps the entity list clean
- **Faction membership** — characters can belong to multiple factions; each faction gets a colour badge from a 15-colour palette
- **Faction dots on character cards** — coloured dots on each character card show faction membership at a glance
- **Board view** — free-canvas board for arranging all entity types spatially; drag cards, draw named relations between entities, and auto-arrange
- **Nested boards** — boards can contain boards; build hierarchy as deep as your world requires
- **Relation lines** — eight line styles (solid, dashed, dotted, double variants), custom captions, legend entries, and colour-coded strokes
- **Quick-add from board** — add any entity type directly from the board toolbar without switching views

### MCP / Bridge
- **MCP Server mode** — expose ClaudetRelay as a Model Context Protocol server; connect Claude Desktop, Claude Code, or any MCP-compatible client
- **MCP chat participation** — external clients can read chat history and post messages as named participants via `chat_get_history`, `chat_post_message`, and `chat_wait_for_round` tools
- **Bridge agents** — register local Ollama models or cloud models as named Bridge agents; send tasks silently via `bridge_post_to_agents` without routing through the chat
- **Project agent roster** — save a Bridge agent roster per project; load it with one click when a project is open
- **Model Controller** — route and coordinate multiple local models through a cloud controller
- **Configurable tool access** — enable or disable individual MCP tools per mode from the Bridge settings window

### Themes
- **101 built-in themes** — Catppuccin, Tokyo Night, Dracula, Gruvbox, Leatherbound series, Skyrim, Deep Rock Galactic, planetary series, Warhammer 40K factions, and many more
- **OXSUIT 1.0 format** — themes are plain `.oxsuit` XML files, drop them into the `Themes/` folder for instant loading — no restart required
- **[OXSUIT Theminator](https://github.com/Doombug75/Theminator)** — free standalone visual theme editor for creating and previewing themes interactively

---

## Requirements

- Windows 10 / 11
- .NET 10 Desktop Runtime
- At least one API key **OR** a running [Ollama](https://ollama.com) instance

---

## Planned Features

| Feature | Notes |
|---|---|
| **Voice input** | Push-to-talk and voice-activation using a locally-running speech recogniser — no cloud required |
| **Voice output** | Text-to-speech playback of AI responses |
| **Multi-language UI** | German as the first additional language alongside English |
| ~~**LM Studio support**~~ | ✅ Done — local server and LM Studio Cloud, with live model listing |
| ~~**Codebase refactor**~~ | ✅ Done — 18 800 lines split into 6 focused partial classes |
| ~~**Buccaneer mode**~~ | ✅ Done — three-way pill toggle (Neutral · 🐦 Mockingbird · 🏴‍☠️ Buccaneer) |

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
