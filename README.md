# ClaudetRelay

A **multi-agent AI workspace** for Windows — run up to 20 AI participants from different providers in the same conversation, assign them roles, let them collaborate on structured projects, and orchestrate who speaks when.

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

- **Multi-provider, multi-agent chat** — up to 20 participants from any mix of cloud providers and local Ollama models in a single shared conversation
- **Orchestration modes** — All Respond, Coordinator First, Coordinator Summarizes
- **Roles & personas** — Coordinator and Reasoner roles, custom names, answer-as aliases, tone slider, response-length settings, and saveable character files per participant
- **Project system** — named projects with typed templates (Novel, Theatre, Software, Game, Business, and more), per-project participant configuration, and persistent chat history
- **Roadmap** — built-in project roadmap with milestones, priorities, and progress tracking
- **AI file operations** — agents can read, write, and list files within the project folder
- **Backup** — one-click ZIP backup of any project, with per-project subfolders and progress display
- **Export** — save conversations as HTML or Markdown
- **Rate limiting** — per-provider RPM throttling to stay within API quotas
- **Secure key storage** — API keys stored exclusively in Windows Credential Manager, never written to disk
- **Themes** — 70+ built-in themes including Catppuccin, Tokyo Night, Dracula, Gruvbox, Leatherbound series, Skyrim, Deep Rock Galactic, and many more — plus full support for **custom themes** (see below)

---

## Customization

### Themes

Themes are plain XAML `ResourceDictionary` files stored in the `Themes/` folder next to the executable. Creating your own theme is as simple as copying an existing `.xaml` file, changing the color values, and restarting the app — no recompilation needed.

Each theme defines a small set of named brushes:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <SolidColorBrush x:Key="BackgroundBrush" Color="#1A1C22"/>
    <SolidColorBrush x:Key="SidebarBrush"    Color="#0F1014"/>
    <SolidColorBrush x:Key="InputBrush"      Color="#24282E"/>
    <SolidColorBrush x:Key="SurfaceBrush"    Color="#2C3038"/>

    <SolidColorBrush x:Key="TextBrush"       Color="#C8D0DC"/>
    <SolidColorBrush x:Key="SubtextBrush"    Color="#567090"/>

    <SolidColorBrush x:Key="ClaudeBrush"     Color="#6890C0"/>
    <SolidColorBrush x:Key="OllamaBrush"     Color="#486880"/>
    <SolidColorBrush x:Key="UserBrush"       Color="#8098B0"/>
    <SolidColorBrush x:Key="AccentBrush"     Color="#A8C8E8"/>

    <SolidColorBrush x:Key="ClaudeBubbleBrush" Color="#1E2230"/>
    <SolidColorBrush x:Key="OllamaBubbleBrush" Color="#1C1E26"/>
    <SolidColorBrush x:Key="UserBubbleBrush"   Color="#20222C"/>

</ResourceDictionary>
```

Drop the file into the `Themes/` folder and it appears immediately in the theme selector — no restart required.

> **A visual Theme Editor is currently in development** and will let you design and preview themes interactively without touching XAML directly.

### Project Types

Project types define the initial system prompt, suggested roles, and structural guidelines that ClaudetRelay applies when a new project is created. They live as XAML files in the `ProjectTypes/` folder and are loaded at runtime — no recompilation needed.

To add your own project type, copy one of the existing `.xaml` files in `ProjectTypes/`, adjust the name, description, and system-prompt text to fit your use case, and restart the app. Your new type will appear in the project type picker immediately.

> **A visual Project Type Editor is also planned** for a future release.

---
## Screenshots

### Application
| Settings | Projects |
|----------|----------|
| ![Settings](Assets/Settings.png) | ![Projects](Assets/projects.png) |

### Themes
| Cosmic Noir | Neon Warp |
|-------------|-----------|
| ![Cosmic Noir](Assets/themeCN.png) | ![Neon Warp](Assets/themeNW.png) |

| Emergency Breach Terminal | Meadow Bloom |
|---------------------------|--------------|
| ![Emergency Breach Terminal](Assets/themeEB.png) | ![Meadow Bloom](Assets/themeMB.png) |

There's about 60 themes. Themes are in .xaml format - be very welcome to make your own.

---

## Requirements

- Windows 10 / 11
- .NET 10 Desktop Runtime
- At least one API key (Anthropic, OpenAI, Google Gemini, Mistral, Groq, OpenRouter, xAI Grok) **OR** a running [Ollama](https://ollama.com) instance

---

## Known Bugs

| # | Description |
|---|-------------|
| 1 | **Reasoner role not respected in Coordinator-First mode** — Participants marked as Reasoners still respond to every user message instead of only responding when the Coordinator explicitly delegates a task to them by name. Architectural fixes have been partially applied (history filtering, coordinator system-prompt constraints) but the behaviour is not yet fully reliable. |

---

## Planned for Next Release

- **Fix Reasoner orchestration** — Reasoners must be fully silent until the Coordinator tags them. Requires a reliable end-to-end solution: the user message must never reach a Reasoner's context, and the delegation detection / call chain must be validated across all provider paths (Ollama + Cloud AI).
- **Theme Editor** — interactive visual editor for creating and previewing themes without editing XAML manually
- **Project Type Editor** — visual editor for defining custom project types and system prompts
- **Roadmap → HTML export** — render the roadmap as a shareable HTML page
