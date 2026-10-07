# DealOS: email trade desk for minerals and metals, on Power Platform

DealOS is an agent-driven, back-to-back trade desk for rare earths, critical minerals and metals. It works by email: it reads the Gmail mailbox, sorts genuine trade from noise, finds sellers for a buyer (or buyers for a seller), carries prices between the two sides with our margin, and takes an agreed deal through KYB, compliance, contract and inspection. Every email to a party is a Gmail draft that a person sends. It is built on Dataverse and Power Automate, with OpenAI as the AI provider (Gemini as standby).

**New session? Start with [HANDOFF.md](HANDOFF.md).** New to the team? Start with [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md).

| Path | What it is |
|---|---|
| [docs/EMAIL_DESK.md](docs/EMAIL_DESK.md) | **The email desk**: design, mailbox setup, triage, negotiation, seller lots, contract, what a person does |
| [docs/AGENTS.md](docs/AGENTS.md) | The 8 agents: contract, guardrails, configuration, test results |
| [docs/WORKFLOWS.md](docs/WORKFLOWS.md) | The 22 flows and the operations APIs |
| [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md) | The original problem statement and marketplace plan (background; the website and marketplace parts were removed on 7 Oct 2026) |
| [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md) | Access, machine setup and working rules for team members |
| [solutions/DealOS/](solutions/DealOS/) | Unpacked Dataverse solution: tables, flows, custom APIs, plug-ins, the Gmail connector, admin app |
| [src/DealOS.Agents/](src/DealOS.Agents/) | Plug-in (C#, net462): agent runtime (OpenAI / Gemini), tools, agent definitions, email desk logic (`Mail/`), deal operations |
| [tests/DealOS.Agents.Harness/](tests/DealOS.Agents.Harness/) | Offline checks; writes the agent manifest and schemas to `build/agents` |
| [tools/](tools/) | `dv.py` (Web API client), `deploy_schema.py`, `deploy_agents.py`, `deploy_flows.py` + `flows/` (flows as code), `deploy_connector.py` (Gmail connector), `import_leads.py`, end-to-end tests on the real mailbox (`desk_e2e.py`, `desk_lot_e2e.py`, `desk_queue_e2e.py`), `gmail_kit.py`, `mail_test.py`, `run_agent.py`, `watch.py`, `export_solution.py`, `seed_test_data.py` |

**Local secrets are never committed.** `.env` holds `OPENAI_API_KEY` (and the standby `GEMINI_API_KEY`), and `.dv_token.json` caches the Dataverse sign-in.
