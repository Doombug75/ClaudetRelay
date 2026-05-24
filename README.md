# ClaudetRelay

A multi-AI group chat application for Windows that connects Claude (Anthropic), Gemini (Google), OpenAI, Mistral, Cohere, local Ollama models, and more — all in one shared conversation.

---

## Features

- **Multi-provider chat** — mix cloud AI providers and local Ollama models in the same conversation
- **Orchestration modes** — All Respond, Coordinator First, Coordinator Summarizes
- **Roles & personas** — assign Coordinator and Reasoner roles, custom names, tone, and response-length settings per participant
- **Project system** — save and reload named conversation projects with their own participant configurations
- **Rate limiting** — per-provider RPM throttling to stay within API quotas
- **Secure key storage** — API keys stored exclusively in Windows Credential Manager, never on disk
- **Dark/light theme** — follows system preference

---

## Requirements

- Windows 10 / 11
- .NET 10 Desktop Runtime
- At least one API key (Anthropic, OpenAI, Google Gemini, Mistral, Cohere, xAI) **or** a running [Ollama](https://ollama.com) instance

---

## Known Bugs

| # | Description |
|---|-------------|
| 1 | **Reasoner role not respected in Coordinator-First mode** — Participants marked as Reasoners still respond to every user message instead of only responding when the Coordinator explicitly delegates a task to them by name. Architectural fixes have been partially applied (history filtering, coordinator system-prompt constraints) but the behaviour is not yet fully reliable. |

---

## Planned for Next Release

- **Fix Reasoner orchestration** — Reasoners must be fully silent until the Coordinator tags them. Requires a reliable end-to-end solution: the user message must never reach a Reasoner's context, and the delegation detection / call chain must be validated across all provider paths (Ollama + Cloud AI).
