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
- **Rate limiting** — per-provider RPM throttling to stay within API quotas
- **Secure key storage** — API keys stored exclusively in Windows Credential Manager, never written to disk

### Projects
- **Project system** — named projects with typed templates (Novel, Theatre, Software, Game, Business, and more), per-project participant configuration, and persistent chat history
- **Roadmap** — built-in project roadmap with milestones, priorities, and progress tracking
- **AI file operations** — agents can read, write, and list files within the project folder
- **Backup** — one-click ZIP backup of any project
- **Export** — save conversations as HTML or Markdown

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
