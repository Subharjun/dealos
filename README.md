# DealOS: Mineral Trade OS on Power Platform

This repository holds an agent-driven, trust-first marketplace for rare earths and critical minerals. It is built on Dataverse, Power Automate, Copilot Studio and Power Pages, with Google Gemini for AI.

**New session? Start with [HANDOFF.md](HANDOFF.md).** New to the team? Start with [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md).

| Path | What it is |
|---|---|
| [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md) | Problem statement, architecture, build plan |
| [docs/AGENTS.md](docs/AGENTS.md) | The 14 agents (12 specialists + Buyer Concierge and Seller Assistant chat): contract, guardrails, configuration, test results |
| [docs/PORTAL.md](docs/PORTAL.md) | The marketplace site (Power Pages code site): pages, security model, build and deploy, test checklist |
| [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md) | Access, machine setup and working rules for team members |
| [docs/WORKFLOWS.md](docs/WORKFLOWS.md) | The 33 flows and 5 operations APIs that run the trade end to end |
| [solutions/DealOS/](solutions/DealOS/) | Unpacked Dataverse solution: tables, flows, custom APIs, plug-ins, admin app |
| [src/DealOS.Agents/](src/DealOS.Agents/) | Plug-in (C#, net462): Gemini agent runtime, tools, agent definitions, chat trigger, deterministic operations, portal guard and catalog sync |
| [portal/](portal/) | Marketplace site: React + Vite + TypeScript, deployed with `pac pages upload-code-site`; `.powerpages-site/` holds web roles, table permissions and site settings |
| [tests/DealOS.Agents.Harness/](tests/DealOS.Agents.Harness/) | Offline checks; writes the agent manifest and schemas to `build/agents` |
| [tools/](tools/) | `dv.py` (Web API client), `deploy_schema.py` (tables), `deploy_agents.py` (plug-in, agents, steps), `deploy_flows.py` + `flows/` (flows as code), `portal_config.py` (site permissions), `chat.py`, `export_solution.py`, `run_agent.py`, `seed_test_data.py`, `watch.py` |

**Site:** https://dealos-gigacore.powerappsportals.com (private to the tenant).

**Local secrets are never committed.** `.env` holds `GEMINI_API_KEY`, and `.dv_token.json` caches the Dataverse sign-in.
