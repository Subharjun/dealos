# HANDOFF: resume here

**Last updated:** 6 October 2026
**Read first in any new chat:** this file, then [docs/AGENTS.md](docs/AGENTS.md), then [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md).

**Where things stand:**
- The 12 agents are built, deployed to Dev and tested.
- The Power Automate workflows that compose them are not built yet.
- Code is on GitHub (private): https://github.com/Subharjun/dealos, branch `main`.

---

## 1. What we are building

**DealOS** is an agent-driven, trust-first B2B marketplace for **rare earths, critical minerals and metals**. Think IndiaMART or Amazon, but only for verified mineral trade, and the platform also runs the deal end to end.

- **Sellers** list material. The platform proves their claims with an evidence engine that keeps Claimed, Documented and Verified apart.
- **Buyers** browse or post an RFQ, negotiate with **several sellers**, and choose one. The other offers close automatically.
- **Both parties** pass KYB and screening.
- **Escrow** holds the funds (a licensed partner, never the platform itself).
- **Physical inspection** at a warehouse happens before shipment, then domestic or international logistics.
- **Staged release:** funds are released in stages, and **our commission is deducted at release**. That is the revenue model.
- **Agents first:** users mostly talk to AI agents (web, WhatsApp, email), not forms. The agents work through **Power Automate workflows** over **one Dataverse record of truth**, and humans approve anything sensitive.

**The problem it solves.** Mineral trade runs on WhatsApp chains and PDFs, with three blockers:

1. **Authenticity:** forged COAs and licences, and fake mandates.
2. **Fragmented execution:** KYC, contract, payment, inspection, export papers and freight are all handled separately.
3. **No payment trust:** sellers want money first and buyers want goods first.

**DealOS** answers these with evidence-graded listings, agents that do the trade-desk legwork, and managed execution: escrow, inspection, logistics and staged release.

**Origin.** The project began from a "Master Prompt" for a local-first "AI Deal OS". Its core skill was **DD1**: turn a messy deal into two WhatsApp messages, a market teaser and a source request. The user then redirected it:

- build on **Microsoft Power Platform**, which the team was told to use
- make it a **marketplace with agents**
- drop Postgres and the local stack

DD1 survives as the **Listing Verification** agent.

---

## 2. Decisions made (do not re-litigate)

| Date | Decision | Notes |
|---|---|---|
| 6 Oct 2026 | Platform = **Power Platform**: Dataverse, Power Automate, Copilot Studio, Power Pages | User mandate; no Postgres or local stack |
| 6 Oct 2026 | Existing `DealOS` solution is the **baseline** to extend | It already had about 45 tables, an evidence engine, 3 custom APIs, an invariants plug-in, 4 flows and an admin app |
| 6 Oct 2026 | AI provider = **Google Gemini** | Key in `.env` and the `gc_secret` table |
| 6 Oct 2026 | Agents are **Dataverse-native**: each is a Custom API `gc_Agent_<Name>` backed by a C# plug-in running a Gemini tool loop | Chosen over "Copilot Studio only" and a "Python service". Flows call agents via **Perform an unbound action**. |
| 6 Oct 2026 | Order of work: **agents → Power Automate workflows → Copilot Studio front-door chat agents** | User's plan |
| 6 Oct 2026 | Documentation is written as **Markdown in this repo** (not Claude Docs) | User preference |

---

## 3. Environment and access

| Item | Value |
|---|---|
| Tenant | `gigacoreenergypvtltd.onmicrosoft.com` (user `gigacore@…`) |
| Dev environment | Giga core's Environment, `https://org61da3071.crm8.dynamics.com` (India region), env id `b77eedc7-f980-e3bb-a07e-757db98002d2` |
| Other environment | Gigacore Energy Pvt Ltd (default). Unused. **No Test or Prod yet.** |
| Solution | `DealOS` (unmanaged). Publisher prefix `gc`, choice-value prefix `30330` (values start at 303300000). |
| PAC CLI | `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec ~/.dotnet/tools/pac` (auth profile `dealos` already active) |
| .NET | SDK 10 at `/opt/homebrew/opt/dotnet/libexec/dotnet` (`dotnet` on PATH is v8; use the full path) |
| Web API from the CLI | `python3 tools/dv.py login` (device code, Azure CLI public client). Token cached in `.dv_token.json`, which git ignores. Then `tools/dv.py get/post/patch`. |
| Azure CLI | Logged in to a **different** tenant (personal student subscription). Not used. |
| Gemini key | `.env` (`GEMINI_API_KEY`, gitignored) and Dataverse `gc_secret` row `gemini.api_key`. **Free tier. Rotate it: it was pasted in chat.** |

---

## 4. Repo map

| Path | Content |
|---|---|
| `docs/ARCHITECTURE_AND_BUILD_PLAN.md` | Full plan: problem, architecture, lifecycle, multi-agent design, workflow catalogue, data model additions, money/escrow, logistics, Power Pages, security, ALM, testing, phased plan, risks, open decisions |
| `docs/AGENTS.md` | Agent catalogue, calling contract, guardrails, settings, developer workflow, test results |
| `solutions/DealOS/` | Unpacked solution (re-export after any environment change; see section 8) |
| `src/DealOS.Agents/` | Agent plug-in (net462, signed): `Infrastructure/` (JSON, Gemini client, Dataverse helpers, schema), `Runtime/` (definition, context, runtime and logging), `Tools/` (tool catalogue, evidence helpers, message validator), `Agents/` (12 definitions), `AgentPlugin.cs` |
| `tests/DealOS.Agents.Harness/` | Offline checks (21 currently pass). Writes `build/agents/agents.manifest.json` and each agent's schemas. |
| `tools/` | `dv.py` (Web API client), `deploy_agents.py` (idempotent deploy), `run_agent.py` (run an agent), `seed_test_data.py` (test PDFs and cleanup) |
| `README.md`, `HANDOFF.md` | Entry points |
| GitHub | https://github.com/Subharjun/dealos (private). Git identity is set per repo to the user's GitHub no-reply email. |

---

## 5. What is built

### Existing before this work (in the `DealOS` solution)

- **About 45 `gc_` tables** covering:
    - parties and trust: account and contact extensions, KYC check, screening, country, country rule
    - evidence: document, document page, assertion, fact, conflict, verification, attribute definition
    - catalog: commodity, asset, licence, listing, lot
    - demand: buyer requirement, match
    - deal: deal, deal party, offer, price quote, stage transition
    - money: payment, payment release, payment event, commission plan, FX rate, tariff rate
    - execution: contract, shipment, milestone
    - agent ops: agent run, model call, generated message, fact sheet, question, review task, audit event, flow failure, platform setting
- **Custom APIs:**
    - `gc_ResolveEvidence(SubjectType int e.g. 303300004=Listing, SubjectId string)` rebuilds facts, conflicts, badge and gaps
    - `gc_TransitionDeal(DealId, TargetStage, Reason)`
    - `gc_CalculatePriceQuote(OfferId, cost inputs…)` returns landed cost, net payout and buyer/seller/admin quote ids
- **Plug-in `DealOS.Plugins`:** the invariants plug-in makes assertions, facts, audit events, verifications, stage transitions and payment events/releases append-only. **Source code is not in this repo; only the DLL.**
- **Flows:**
    - Listing verification (on listing Submitted, uses the **OpenAI** connector for extraction)
    - Approvals (review task → approval by role)
    - Daily sweep (02:00 UTC)
    - Offer pricing (on offer create)
- **Admin app:** model-driven app `gc_DealOSAdmin`.

### Built in this session

- **Table `gc_Secret`:** organisation-owned, no privileges for user roles. Holds `gemini.api_key`.
- **New `gc_agent` choice option:** "Document Intelligence" (303300011).
- **Plug-in assembly `DealOS.Agents`:** type `DealOS.Agents.AgentPlugin`.
- **12 Custom APIs**, all with the same contract:
    - inputs: `SubjectId`, `Input` (JSON), `DryRun`
    - outputs: `AgentRunId`, `Status`, `Summary`, `NeedsHuman`, `Result` (JSON)

| Agent | Subject | Status |
|---|---|---|
| DocumentIntelligence | gc_document | Tested live and dry; caught the injection PDF |
| ListingVerification (DD1) | gc_listing | Tested live; do-not-ask-twice confirmed |
| OnboardingKYB, BuyerVerification | account | Tested dry |
| Compliance, Contract, Payment, Logistics | gc_deal | Tested dry |
| Pricing, Negotiation | gc_offer | Tested dry |
| Matching | gc_buyerrequirement | Tested dry |
| AdminSupervisor | none | Tested live |

- **Settings** (`gc_platformsetting`):
    - `agents.model.default` = `gemini-3.5-flash`
    - `agents.model.document` = `gemini-3.5-flash`
    - `agents.model.fallbacks` = `gemini-3.1-flash-lite,gemini-3.8-flash`
    - `agents.time_budget_seconds` = `100`
    - `agents.call_timeout_seconds` = `45`
    - `agents.pricing` = `{}`
    - `agents.generation_config` = (empty)
    - `agents.provider` = `Gemini`
- **Guardrails in code:**
    - message validator
    - do-not-ask-twice ledger and outstanding-ask cap
    - one pending draft per kind
    - `finish` rejected when called in the same turn as other tools
    - KYB tier cap
    - compliance floor
    - payment release gate on verified milestones
    - pricing numbers overwritten from the engine
    - negotiation limit clamp and invented-term removal
    - validation of model-supplied GUIDs and columns
    - poisoned-transaction stop

    Details are in `docs/AGENTS.md`.

---

## 6. Test data in Dev (safe to reuse or delete)

| Record | Id |
|---|---|
| Listing `[SMOKE 20261005-194833] Copper cathode` (Draft; now has 5 Documented facts, 3 outstanding questions, draft messages and review tasks) | `f6e59aa2-c7c0-f111-aaaf-7ced8daf451f` |
| Deal `[SMOKE …] deal` (Negotiation; no price or Incoterm on the deal) | `f9b086a9-c7c0-f111-aaaf-7ced8daf451f` |
| Offer (9000 USD × 500 MT, FOB, Open, from seller) | `fab086a9-c7c0-f111-aaaf-7ced8daf451f` |
| Seller account `[SMOKE …] Seller Mining Ltd` | `f4e59aa2-c7c0-f111-aaaf-7ced8daf451f` |
| Buyer account `[SMOKE …] Buyer Metals GmbH` | `f5e59aa2-c7c0-f111-aaaf-7ced8daf451f` |
| Document `[AGENT-TEST] coa-copper-test.pdf` (clean COA, parsed) | `358f24e1-6ac1-f111-aaaf-7ced8daf451f` |
| Document `[AGENT-TEST] coa-copper-injection-test.pdf` (prompt injection) | `378f24e1-6ac1-f111-aaaf-7ced8daf451f` |
| Listing `[AGENT-TEST] Copper cathode 300 MT` (Published) | `74b77e15-6dc1-f111-aaaf-7ced8daf451f` |
| Buyer requirement `[AGENT-TEST] Need 250 MT copper cathode` (Open) | `75b77e15-6dc1-f111-aaaf-7ced8daf451f` |
| Commodity "Copper cathode (Grade A)" | `89f5bb75-c7c0-f111-aaaf-7ced8daf451f` |

---

## 7. Lessons learned (avoid repeating these mistakes)

- **A failed Dataverse call inside a plug-in poisons the transaction.** Catching it and continuing triggers "ISV code reduced the open transaction count", and everything rolls back, including the run logs. Validate before calling: `Dv.Retrieve` is a safe query, `Dv.Exists` checks ids, and `Dv.Columns` holds metadata. On a fault, stop.
- **`gc_question.gc_message` looks up `gc_message`** (inbound chat), not `gc_generatedmessage`.
- **`gemini-2.5-flash` is retired** for new users. Use the 3.x models.
- **The free tier allows 5 requests per minute per model**, and the free models return frequent 503s. The fallback chain plus one paused retry mitigates this, but runs can still fail. **Enable billing.**
- **Gemini 3 returns a `thoughtSignature`.** The runtime passes the model's content back unchanged. When switching models mid-run it replaces signatures with `skip_thought_signature_validator`.
- **Gemini can call several tools and `finish` in one turn.** The runtime now rejects `finish` in a turn that has other calls.
- **`ResolveEvidence` wants the full choice value** (303300004), not an index. It *writes* facts, because it rebuilds them.
- **`pac power-fx` can't reach these tables or custom APIs.** Use `tools/dv.py` instead.
- **The `gc_platformsetting.gc_valuetype` values** are Bool 303300000, Number 303300001, Text 303300002, Json 303300003.

---

## 8. How to resume (commands)

```bash
cd ~/Desktop/Power-Automate-lastry
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec; D=$DOTNET_ROOT/dotnet; PAC=~/.dotnet/tools/pac
python3 tools/dv.py get WhoAmI                        # token still valid? else: python3 tools/dv.py login
$D build -c Release src/DealOS.Agents                  # after code changes
$D run --project tests/DealOS.Agents.Harness -- build/agents
python3 tools/deploy_agents.py                         # idempotent
python3 tools/run_agent.py ListingVerification f6e59aa2-c7c0-f111-aaaf-7ced8daf451f --dry
# sync the repo with the environment after changes made there:
$PAC solution export --name DealOS --path /tmp/DealOS.zip --overwrite && $PAC solution unpack --zipfile /tmp/DealOS.zip --folder solutions/DealOS --packagetype Unmanaged --allowDelete true
```

---

## 9. What remains (in priority order)

### Immediate housekeeping

- [ ] Enable billing on the Gemini (Google AI Studio) project. **Rotate the key**, update `.env`, then run `python3 tools/deploy_agents.py`.
- [ ] Decide whether to keep or clean the test data (section 6). `python3 tools/seed_test_data.py cleanup` removes the `[AGENT-TEST]` documents.
- [x] First commit pushed to the private repo https://github.com/Subharjun/dealos (6 Oct 2026). Commit and push again after changes when the user asks.
- [ ] Optional: fill `agents.pricing` with Gemini token prices so `gc_modelcall.gc_costusd` is recorded.

### Next phase: Power Automate workflows that compose the agents (the user's stated next step)

Each flow uses **Perform an unbound action** `gc_Agent_*`, then **Parse JSON** on `Result` (schemas are in `build/agents/*.output.json`), then branches on `Status` and `NeedsHuman`. The flows must be added to the `DealOS` solution with connection references, and keep the existing convention: a Try/Catch scope plus `gc_flowfailure` on error.

- [ ] **Listing pipeline.** Listing Submitted → for each pending document, `gc_Agent_DocumentIntelligence` → `gc_Agent_ListingVerification` → existing Approvals flow. Replace the OpenAI step in the existing "Listing verification" flow.
- [ ] **Document uploaded later** (seller answers) → DocumentIntelligence → ListingVerification.
- [ ] **Party onboarding.** Account created or KYB documents uploaded → OnboardingKYB or BuyerVerification, depending on role.
- [ ] **RFQ.** Buyer requirement Open → Matching → notify both sides for opt-in.
- [ ] **Offer.** Offer created → existing pricing flow → Pricing explanation. Negotiation runs on request (from the portal or chat).
- [ ] **Offer accepted.** New custom API `gc_AcceptOffer`: reserve the lot, close competing deals, call TransitionDeal.
- [ ] **Terms Agreed** → Compliance → (if Clear) Contract.
- [ ] **Signed** → Payment (schedule) → escrow partner.
- [ ] **Funded** → Logistics.
- [ ] **Milestone verified** → Payment (release readiness) → Finance approval.
- [ ] **Daily** → AdminSupervisor digest posted to Teams.

### Later phases (see the plan, section 17)

- [ ] Copilot Studio front-door agents, Buyer Concierge and Seller Assistant (`pac copilot init/push` works from this Mac), calling the `gc_Agent_*` APIs as tools.
- [ ] Power Pages marketplace: catalog with badges, onboarding, listings, RFQ, side-by-side offer comparison, deal room. Entra External ID, web roles, table permissions, masked identities until contract.
- [ ] Integrations: escrow/payment partner, e-signature, registry/KYB (MCA, GSTIN), sanctions screening, inspection agencies, forwarders/tracking, WhatsApp Business, email notifications.
- [ ] New tables from plan section 8: RFQ invites, inspection, warehouse, shipment documents, invoice, rating, dispute, notification preferences. Field security for restricted attributes.
- [ ] ALM: create Test and Prod environments, add a pipeline, and split the solution (Core / Flows / Agents / Portal) as it grows.
- [ ] Evidence benchmark of 30–50 real past deals; release gates are in plan section 16.

### Open business decisions (the user's to make; plan section 19)

- [ ] Launch commodities and trade routes
- [ ] Escrow partner and bank
- [ ] Who does physical inspection (own warehouses, partners or agencies)
- [ ] Commission rate, base and payer (a 1.5% plan exists in the test data)
- [ ] Logistics: track only or arranged
- [ ] Licences held (Power Pages, Copilot Studio, Premium)
- [ ] Permission to create Test and Prod
- [ ] When identities are revealed (proposed: after contract signing)
- [ ] Compliance to confirm with counsel: RBI escrow rules, monazite and atomic minerals, export controls, AML, DPDP

---

## 10. Working preferences

- Write documents as Markdown in this repo.
- Build on Power Platform; don't propose alternative stacks.
- Agents first, then workflows.
- Test with `--dry` before live runs.
- Mark test records with `[AGENT-TEST]`.
