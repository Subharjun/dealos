# Team setup

How to get access to DealOS and start working on it. Read [README.md](../README.md) for what the project is, then [HANDOFF.md](../HANDOFF.md) for where the build stands.

---

## 1. Get access

Send the project owner (Subharjun) your **GitHub username** and the **email you want to use for Microsoft**. You get:

| What | Where | Gives you |
|---|---|---|
| Code | https://github.com/Subharjun/dealos (private) | Clone, branch, open pull requests |
| Microsoft account | `you@gigacoreenergypvtltd.onmicrosoft.com`, or your own email invited as a guest | Sign-in to everything below |
| Dev environment | Giga core's Environment, `https://org61da3071.crm8.dynamics.com` | Tables, data, custom APIs, through `tools/dv.py` |
| Flows | [make.powerautomate.com](https://make.powerautomate.com), environment "Giga core's Environment", **Solutions → DealOS** | See the 22 flows and their run history |
| Admin app | [make.powerapps.com](https://make.powerapps.com), **Apps → DealOS Admin** | Browse listings, deals, review tasks, agent runs |

You need a Power Apps licence or trial to open the environment. If you are told you don't have one, start the free [Power Apps Developer Plan](https://powerapps.microsoft.com/developerplan/) with the same account.

---

## 2. Set up your machine

You need:

- **git**
- **Python 3.9 or later.** The tools use only the standard library, so there is nothing to `pip install`.
- **.NET SDK 10** ([download](https://dotnet.microsoft.com/download)). It builds the plug-in (net462) and the test harness. The harness also builds with SDK 8.
- **PAC CLI**, to export the solution: `dotnet tool install --global Microsoft.PowerApps.CLI.Tool`, then `pac auth create --environment https://org61da3071.crm8.dynamics.com`

Then:

```bash
git clone https://github.com/Subharjun/dealos.git
cd dealos
python3 tools/dv.py login          # opens a browser; sign in with your Microsoft account above
python3 tools/dv.py get WhoAmI     # should print your user id
dotnet build -c Release src/DealOS.Agents
dotnet run --project tests/DealOS.Agents.Harness -- build/agents   # "ALL CHECKS PASSED"
```

If you have more than one .NET install (common with Homebrew on a Mac), point `DOTNET_ROOT` at the SDK 10 one, for example `export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`. On Windows PowerShell use `$env:DOTNET_ROOT = "..."`.

Use `python3 tools/dv.py login`, **not** `login --device`. The tenant's security defaults block the device-code sign-in. When a command later says the token expired or was revoked, sign in again.

### The AI keys (OpenAI, Gemini)

The agents run on **OpenAI** (`agents.provider` = openai); Gemini is the standby provider. You **don't need either key** for most work. The keys are already stored in Dataverse, and the agents read them from there. Without a key in `.env`, `tools/deploy_agents.py` deploys everything else and leaves the stored keys unchanged.

If you do need it, ask the owner directly. Never commit it, paste it in a chat or put it in a ticket. Copy [.env.example](../.env.example) to `.env` and fill it in.

---

## 3. Working rules

**There is one shared Dev environment.** Everyone's deploys land in the same place, so:

- **Say in the team chat before you deploy** (`deploy_agents.py`, `deploy_flows.py`). A deploy overwrites the plug-in, agents or flows with what is on your machine.
- **Pull `main` before deploying**, so you don't roll back someone else's change.
- **Deploy flows from the owner's account for now.** The flows use the owner's Dataverse, Approvals and Outlook connections. A deploy from another account may fail to turn flows on.

**Tables are code too.** Add a table or column in [tools/deploy_schema.py](../tools/deploy_schema.py) and run it; it only creates what is missing.


**Flows are code.** Change them in [tools/flows/definitions.py](../tools/flows/definitions.py) and deploy with `python3 tools/deploy_flows.py --only "<flow name>"`. An edit made in the Power Automate designer is overwritten by the next deploy.

**Portal changes must come back to git.** After you change tables, columns, forms or the app in the maker portal, run `python3 tools/export_solution.py` and commit the changes under `solutions/DealOS/`.

**Test data:**
- Put `[AGENT-TEST]` at the start of the name of anything you create for testing.
- Run agents with `--dry` first: `python3 tools/run_agent.py <Agent> <id> --dry`.
- `python3 tools/watch.py 15m` shows what the flows did recently: audit events, failures and review tasks.

**Git:** work on a branch and open a pull request into `main`. Don't push to `main` directly.

---

## 4. Read next

| Document | For |
|---|---|
| [HANDOFF.md](../HANDOFF.md) | Current state, decisions already made, lessons learned (section 7 saves hours) |
| [docs/EMAIL_DESK.md](EMAIL_DESK.md) | The email desk: how it works and what a person does |
| [docs/AGENTS.md](AGENTS.md) | The 8 agents, how to call them, their guardrails |
| [docs/WORKFLOWS.md](WORKFLOWS.md) | The 22 flows and the operations APIs |
| [docs/ARCHITECTURE_AND_BUILD_PLAN.md](ARCHITECTURE_AND_BUILD_PLAN.md) | The original plan (background) |
