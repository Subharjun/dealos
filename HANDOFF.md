# HANDOFF: resume here

**Last updated:** 6 October 2026
**Read first in any new chat:** this file, then [docs/WORKFLOWS.md](docs/WORKFLOWS.md), [docs/AGENTS.md](docs/AGENTS.md), then [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md).

**Where things stand:**
- The 12 agents are built, deployed to Dev and tested.
- **The workflow phase is built:**
    - 22 cloud flows, all on in Dev
    - 4 deterministic operations APIs: `gc_AcceptOffer`, `gc_OpenEscrow`, `gc_ReleaseDeal`, `gc_InstructRelease`
    - the trade has been run end to end on `[AGENT-TEST]` data
- **Next:** Copilot Studio front-door agents, then the Power Pages marketplace (section 9).
- **Housekeeping is deliberately left until the build is finished** (the user's decision): removing test data, a paid Gemini key and key rotation.
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
| Web API from the CLI | `python3 tools/dv.py login` opens a **browser sign-in** (auth code + PKCE on localhost, Azure CLI public client). The tenant's security defaults **block the device-code flow** (`login --device`), so don't use it. The token is cached in `.dv_token.json`, which git ignores. Then use `tools/dv.py get/post/patch`. When a refresh fails with AADSTS530035, sign in again. |
| Azure CLI | Logged in to a **different** tenant (personal student subscription). Not used. |
| Gemini key | `.env` (`GEMINI_API_KEY`, gitignored) and Dataverse `gc_secret` row `gemini.api_key`. **Free tier. Rotate it: it was pasted in chat.** |

---

## 4. Repo map

| Path | Content |
|---|---|
| `docs/ARCHITECTURE_AND_BUILD_PLAN.md` | Full plan: problem, architecture, lifecycle, multi-agent design, workflow catalogue, data model additions, money/escrow, logistics, Power Pages, security, ALM, testing, phased plan, risks, open decisions |
| `docs/AGENTS.md` | Agent catalogue, calling contract, guardrails, settings, developer workflow, test results |
| `docs/WORKFLOWS.md` | Flow catalogue, review decisions, operations APIs, notifications, conventions |
| `solutions/DealOS/` | Unpacked solution (re-export after any environment change; see section 8) |
| `src/DealOS.Agents/` | Plug-in assembly (net462, signed): `Infrastructure/` (JSON, Gemini client, Dataverse helpers, schema), `Runtime/` (definition, context, runtime and logging), `Tools/` (tool catalogue, evidence helpers, message validator), `Agents/` (12 definitions), `AgentPlugin.cs`, `Operations/OperationsPlugin.cs` (the 4 deterministic operations) |
| `tests/DealOS.Agents.Harness/` | Offline checks (26 currently pass, including the escrow maths). Writes `build/agents/agents.manifest.json` and each agent's schemas. |
| `tools/` | `dv.py` (Web API client), `deploy_agents.py` (plug-in, agents, operations, settings), `deploy_flows.py` (all flows), `flows/` (flow definitions as code), `run_agent.py` (run an agent), `seed_test_data.py` (test PDFs, test deal scenario, cleanup), `watch.py` (what happened since a time: audit, failures, review tasks) |
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

### Built in the workflow session (6 Oct 2026, see [docs/WORKFLOWS.md](docs/WORKFLOWS.md))

- **22 cloud flows, all on.** They are generated from `tools/flows/definitions.py` and deployed by `tools/deploy_flows.py`:
    - Listing verification (rewritten: agents instead of OpenAI)
    - Document intake, Party onboarding
    - RFQ matching, New listing matching, Match notifications, Match accepted
    - Offer pricing (extended), Offer accepted
    - Terms agreed, Compliance check, Contracting, Contract signed, Escrow funded
    - Milestone progress, Release settled, Deal cancelled
    - Review decisions (new), Approvals (now ask-and-record only)
    - Notify party (child flow), Daily digest, Daily sweep (now on)
- **4 operations custom APIs** (plug-in type `DealOS.Agents.Operations.OperationsPlugin`, no AI):
    - `gc_AcceptOffer`: lot reserve or split, competing deals closed, row-lock race safety
    - `gc_OpenEscrow`
    - `gc_ReleaseDeal`
    - `gc_InstructRelease`: re-checks the Finance approval, funding and overlay
- **New column** `gc_deal.gc_requirement`, a lookup to the RFQ (bind name `gc_Requirement`).
- **New settings:**
    - `escrow.default_schedule` (30/70), `escrow.partner`, `escrow.funding_days`
    - `notifications.email.enabled` = **false** (flows record messages instead of emailing), `notifications.email.redirect`
    - `admin.digest.recipients`
- **Agent fixes:**
    - The system now opens Listing Publish, Tier Upgrade, Fund Release, Contract Issue and Message Send tasks; the model can't. The Tier Upgrade payload carries the tier.
    - A risk-flagged document forces **Hold**.
    - The Pricing agent can no longer re-price an offer with costs it assumed.

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
| Deal `[AGENT-TEST] deal A` (second run): the **full lifecycle to Settled**, with escrow, 8 milestones, 2 settled releases and a shipment | `1f6be868-73c1-f111-aaaf-7ced8daf451f` |
| Deals `[AGENT-TEST] deal A/B` (first run): A at Terms Agreed (accepted before the flows existed), B Cancelled | `1d9b9b73-…`, `1f9b9b73-…` |
| Account `[AGENT-TEST] Onboarding Seller Pvt Ltd` (KYB In Progress, approved request recorded) | `87b21f9b-74c1-f111-aaaf-7ced8daf451f` |
| RFQ `[AGENT-TEST] RFQ 200 MT copper cathode`, its match (Mutual) and deal `RFQ: [AGENT-TEST] Copper cathode 300 MT` (Inquiry) | `8ab21f9b-74c1-f111-aaaf-7ced8daf451f` |
| Listing `[AGENT-TEST] Copper cathode 500 MT (flow test)` (In Verification, Hold, 2 PDFs) | `aa1fb1ba-74c1-f111-aaaf-7ced8daf451f` |

**Changed for testing:** both `[SMOKE]` accounts were set to KYB Verified / Passed and given a Clear `[AGENT-TEST]` screening, so that deals pass the Contracting guard. Approval requests for the test tasks are still open in the gigacore user's Approvals (Teams or Outlook). They can be ignored or cancelled, because the tasks were decided directly.

`python3 tools/seed_test_data.py cleanup` removes the `[AGENT-TEST]` deals, offers, lots, payments, releases, milestones, contracts and documents. Append-only rows (stage transitions, audit, payment events) stay by design, so deals with transitions may refuse deletion; cancel them instead. The full removal of test data is scheduled for the end of the build.

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
- **`gc_auditevent.gc_hash` is a required column**, even though the invariants plug-in computes it. A flow's *Create row* must pass a placeholder (the flows use `set-by-invariants-plugin`). Without it, the flow cannot be saved or turned on. This is why the 4 original flows had never been activated.
- **OData `startswith(gc_name,'[AGENT-TEST]')` matches nothing,** because Dataverse turns it into SQL `LIKE`, where `[` opens a character class. Escape it as `'[[]AGENT-TEST]'`.
- **The lookup created by the Web API keeps its schema-name case in OData binds:** `gc_Requirement@odata.bind`, not `gc_requirement@…`. The older lookups are lower-case.
- **Dataverse triggers carry no choice labels** (`…@OData.Community.Display.V1.FormattedValue`) on create or update. Map the values in the flow instead; see `incoterm_label` in `tools/flows/definitions.py`.
- **`gc_TransitionDeal` returns `Allowed=false` plus `Failures`; it never throws.** Its guards are the real gate: KYB Verified for both parties and screening cleared before Contracting, signed contract, escrow opened or funded, milestones, all releases settled.
- **`gc_paymentrelease` has invariants:** amount > 0, and commission + net must equal the amount on every create or update.
- **Writing a field to its current value still fires "modified" triggers.** Code that a flow calls must not rewrite the field that triggered that flow (see `gc_AcceptOffer`).
- **Agents must not be able to change money numbers on their own.** The Pricing agent re-priced an offer with assumed costs, so `calculate_price_quote` now takes cost inputs only from the caller's TASK INPUT.

---

## 8. How to resume (commands)

```bash
cd ~/Desktop/Power-Automate-lastry
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec; D=$DOTNET_ROOT/dotnet; PAC=~/.dotnet/tools/pac
python3 tools/dv.py get WhoAmI                        # token still valid? else: python3 tools/dv.py login  (browser sign-in)
$D build -c Release src/DealOS.Agents                  # after code changes
$D run --project tests/DealOS.Agents.Harness -- build/agents
python3 tools/deploy_agents.py                         # idempotent
python3 tools/deploy_flows.py                          # all flows (or --only "<name>")
python3 tools/run_agent.py ListingVerification f6e59aa2-c7c0-f111-aaaf-7ced8daf451f --dry
python3 tools/watch.py 15m                             # what the flows did: audit, failures, review tasks
# sync the repo with the environment after changes made there (PAC sign-in is blocked by security defaults; this uses dv.py):
python3 tools/export_solution.py
```

---

## 9. What remains (in priority order)

### Workflow phase: done (6 Oct 2026)

All items are built and tested live on `[AGENT-TEST]` data; see [docs/WORKFLOWS.md](docs/WORKFLOWS.md#test-results-6-oct-2026-dev).

**Small follow-ups, not blocking:**
- The **Daily digest** has not run yet; it first runs at 03:00 UTC. It emails `admin.digest.recipients`, or the `approvals.assignees` default when that is empty. **Teams** needs a Teams connection created in the maker portal; until then the digest goes by email.
- **Plan flows not built yet:**
    - #6 Screening: a sanctions provider is needed first
    - #13 Inspection booking: needs the inspection table
    - #17 Dispute
    - #18 Flow failure replay: for now, resubmit from the run link in `gc_flowfailure`
    - Daily sweep additions: funding deadlines and stale RFQs
- The Pricing / Negotiation agents run on request (portal or chat) with `Input` cost inputs or limits; no flow calls Negotiation.

### Next phase: Copilot Studio front-door agents (then Power Pages)

- [ ] Copilot Studio front-door agents, Buyer Concierge and Seller Assistant (`pac copilot init/push` works from this Mac), calling the `gc_Agent_*` APIs as tools.
- [ ] Power Pages marketplace: catalog with badges, onboarding, listings, RFQ, side-by-side offer comparison, deal room. Entra External ID, web roles, table permissions, masked identities until contract.
- [ ] Integrations: escrow/payment partner, e-signature, registry/KYB (MCA, GSTIN), sanctions screening, inspection agencies, forwarders/tracking, WhatsApp Business, email notifications.
- [ ] New tables from plan section 8: RFQ invites, inspection, warehouse, shipment documents, invoice, rating, dispute, notification preferences. Field security for restricted attributes.
- [ ] ALM: create Test and Prod environments, add a pipeline, and split the solution (Core / Flows / Agents / Portal) as it grows.
- [ ] Evidence benchmark of 30–50 real past deals; release gates are in plan section 16.

### Housekeeping: deliberately last (the user's decision, 6 Oct 2026)

Do these only when the whole build is finished:
- [ ] **Remove all mock and test data permanently,** so real people can use the platform: `[AGENT-TEST]`, `[SMOKE …]`, the sample commission plan, and the test KYB/screening changes on the smoke accounts.
- [ ] **Paid Gemini key.** Subscribe or enable billing, **rotate the key** (it was pasted in chat), update `.env`, then run `python3 tools/deploy_agents.py`.
- [ ] Turn on `notifications.email.enabled` and set the real `admin.digest.recipients` and `approvals.assignees`.
- [ ] Optional: fill `agents.pricing` with Gemini token prices so `gc_modelcall.gc_costusd` is recorded.
- [x] The repo is on GitHub (private): https://github.com/Subharjun/dealos. Commit and push only when the user asks.

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
- Agents first, then workflows (done), then Copilot Studio, then Power Pages.
- **Finish the whole build first.** Test-data removal and the paid Gemini key come last.
- Test with `--dry` before live runs.
- Mark test records with `[AGENT-TEST]`.
