# DealOS: Mineral Trade OS on Power Platform

This repository holds an agent-driven, trust-first marketplace for rare earths and critical minerals. It is built on Dataverse, Power Automate, Copilot Studio and Power Pages, with Google Gemini for AI.

**New session? Start with [HANDOFF.md](HANDOFF.md).**

| Path | What it is |
|---|---|
| [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md) | Problem statement, architecture, build plan |
| [docs/AGENTS.md](docs/AGENTS.md) | The 12 agents: contract, guardrails, configuration, test results |
| [docs/WORKFLOWS.md](docs/WORKFLOWS.md) | The 22 flows and 4 operations APIs that run the trade end to end |
| [solutions/DealOS/](solutions/DealOS/) | Unpacked Dataverse solution: tables, flows, custom APIs, plug-ins, admin app |
| [src/DealOS.Agents/](src/DealOS.Agents/) | Plug-in (C#, net462): Gemini agent runtime, tools, agent definitions, deterministic operations |
| [tests/DealOS.Agents.Harness/](tests/DealOS.Agents.Harness/) | Offline checks; writes the agent manifest and schemas to `build/agents` |
| [tools/](tools/) | `dv.py` (Web API client), `deploy_agents.py`, `deploy_flows.py` + `flows/` (flows as code), `export_solution.py`, `run_agent.py`, `seed_test_data.py`, `watch.py` |

**Local secrets are never committed.** `.env` holds `GEMINI_API_KEY`, and `.dv_token.json` caches the Dataverse sign-in.
