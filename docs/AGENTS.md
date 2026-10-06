# DealOS Agents

**As of:** 6 October 2026
**Environment:** Giga core's Environment (Dev), solution `DealOS`
**AI provider:** Google Gemini (default `gemini-3.5-flash`, with fallbacks)

There are **12 agents**, each deployed as a Dataverse Custom API named `gc_Agent_<Name>`. A Power Automate flow (or Copilot Studio, Power Pages, or any Web API client) calls an agent the same way it calls any other Dataverse action. Each agent:

1. **Pre-loads its data with code:** listings, facts, documents, parties and rules.
2. **Asks Gemini to reason** within a strict instruction set and a limited tool set.
3. **Writes only drafts, ledger rows and review tasks.**
4. **Applies code guards to the result** before returning it.

Evidence status, money, stage changes and approvals stay in code and with humans.

```mermaid
flowchart LR
  F[Power Automate flow<br/>or Copilot Studio / Web API] -->|Perform an unbound action<br/>gc_Agent_X| P[AgentPlugin<br/>DealOS.Agents.dll]
  P --> C[Prefetch context<br/>deterministic]
  C --> G[Gemini tool loop<br/>model fallback chain]
  G -->|tools| T[Read tools: allow-listed tables<br/>Write tools: drafts, questions,<br/>review tasks, matches, KYC Pending]
  T --> D[(Dataverse)]
  G --> V[Guards in code<br/>validator, caps, floors, gates]
  V --> R[Result JSON]
  P --> L[gc_agentrun · gc_modelcall · gc_auditevent]
  R --> F
```

---

## Agent catalogue

| Agent (Custom API) | SubjectId points at | What it does | What it can write | Human gate |
|---|---|---|---|---|
| `gc_Agent_DocumentIntelligence` | `gc_document` | Reads the attached PDF or image, classifies it, and extracts printed values with page and verbatim quote. Flags prompt injection. Then re-resolves evidence. | `gc_assertion` rows, document type and risk flags | — (assertions are only evidence) |
| `gc_Agent_ListingVerification` (DD1) | `gc_listing` | Reviews evidence. Asks the seller only for blocking gaps, never twice. Drafts an anonymous market teaser and a seller request. Recommends Publish / Needs Info / Hold / Reject. | `gc_question`, `gc_factsheet`, `gc_generatedmessage` (Draft), `gc_reviewtask` | Message Send, Listing Publish |
| `gc_Agent_OnboardingKYB` | `account` | Seller/party KYB: company, UBO, IDs, address, authority to sell, screening. | `gc_kyccheck` (Pending or Refer only), questions, draft request, review tasks | Tier Upgrade, Screening Clearance |
| `gc_Agent_BuyerVerification` | `account` | Buyer KYB, including proof of funds or bank reference. | Same as KYB | Same as KYB |
| `gc_Agent_Compliance` | `gc_deal` | Pre-contract gate: tiers, screening, FATF and CAHRA, controlled commodities, permits. Returns Clear / Refer / Block. | `gc_reviewtask` | Screening Clearance (whenever not Clear) |
| `gc_Agent_Matching` | `gc_buyerrequirement` | Scores published listings. Quantity, price, terms and trust are computed in code; the model judges spec fit only. | `gc_match` (Proposed, both opt-ins false) | Buyer and seller opt in |
| `gc_Agent_Pricing` | `gc_offer` | Explains landed cost (buyer) and net payout (seller). Numbers come only from `gc_CalculatePriceQuote`. Recalculates when cost inputs are given. | Price quotes (via the engine) | — |
| `gc_Agent_Negotiation` | `gc_offer` | Advises the buyer or seller: Accept / Counter / Reject / Wait / Need Info. Proposes a counter within your limits and drafts a reply. | Nothing (advice only) | The user clicks send |
| `gc_Agent_Contract` | `gc_deal` | Builds a term sheet strictly from the accepted offer and evidence. Refuses to draft while critical terms are missing. | `gc_contract` (Draft), `gc_reviewtask` | Contract Issue |
| `gc_Agent_Payment` | `gc_deal` | Plans the escrow tranches with commission (deterministic maths). Checks which releases have **verified** milestones. | `gc_reviewtask` (Fund Release, only when the code-checked milestones are verified) | Finance approves every release |
| `gc_Agent_Logistics` | `gc_deal` | Builds the corridor document checklist (domestic or international). Creates milestones, chases missing documents. | `gc_milestone`, draft chaser, `gc_reviewtask` | Shipment Booking |
| `gc_Agent_AdminSupervisor` | — (no subject) | Daily ops digest: approvals, stuck deals, listings waiting, failures, AI spend. | Nothing (read-only) | — |

---

## Calling an agent

**Inputs** (the same for every agent):

| Name | Type | Required | Meaning |
|---|---|---|---|
| `SubjectId` | String (GUID) | Yes, except AdminSupervisor | The record the agent works on |
| `Input` | String (JSON object) | No | Extra data, e.g. `{"perspective":"buyer","limits":{"max_price":8800,"preferred_payment_terms":["LC Sight"]}}` for Negotiation, or cost inputs for Pricing |
| `DryRun` | Boolean | No | `true` = full reasoning, but no records written; actions are listed in the result |

**Outputs:**

| Name | Type | Meaning |
|---|---|---|
| `AgentRunId` | String | The `gc_agentrun` record for this run |
| `Status` | String | `Succeeded` or `Failed` |
| `Summary` | String | 2–3 sentences for a human |
| `NeedsHuman` | Boolean | `true` when a person must act before the process continues |
| `Result` | String (JSON) | `{agent, run_id, status, model, steps, tokens_in, tokens_out, seconds, error, result, actions}`. `result` follows the agent's schema; `actions` lists every write. |

**From Power Automate:** use the Dataverse connector action **Perform an unbound action**. Pick `gc_Agent_<Name>`, map `SubjectId` from the trigger, then use **Parse JSON** on `Result` (schemas are in `build/agents/<Name>.output.json` after running the harness). Branch on `Status` and `NeedsHuman`.

**From the command line (dev):**

```bash
python3 tools/run_agent.py ListingVerification <listing-guid> --dry
python3 tools/run_agent.py Negotiation <offer-guid> --input '{"perspective":"buyer","limits":{"max_price":8800}}'
```

---

## Guardrails enforced in code (not by the prompt)

| Guard | Where | Effect |
|---|---|---|
| Evidence status is never set by AI | `gc_ResolveEvidence` | Agents create assertions. Only the resolver computes Verified / Documented / Claimed / Conflicting. |
| Message validator | `draft_message` | Rejects any number not in the data shown to the agent. Rejects confidential terms (seller name, contacts, asset name, Internal/Restricted facts) in market messages, and emails or phone numbers. Rejects "verified" with no Verified fact, and Claimed values without hedging ("seller states…"). |
| Do not ask twice | `ask_question` | Refuses values that are already Documented or Verified, or already given (Claimed → ask for evidence instead). Refuses repeats within the cool-down (`questions.cooldown_hours`). Limits outstanding questions to `dd1.max_asks` per counterparty. |
| One pending draft | `draft_message` | No new draft while one of the same kind is waiting for approval |
| `finish` timing | Runtime | `finish` is not accepted in the same turn as other tool calls, so the agent has seen its tool results before it reports |
| Facts in the result | `AfterFinish` | "drafted" and "asks" fields are set from the actions actually taken |
| Tier cap | KYB `AfterFinish` | The recommended tier can never exceed what recorded checks and screening support. Trade Verified / Trusted are never recommended by an agent. |
| Compliance floor | Compliance `AfterFinish` | Confirmed match → Block. Potential/missing screening, hold or low tier → at least Refer. Any non-Clear result opens a review task. |
| Money gate | Payment `AfterFinish` | A Fund Release task opens only when the mapped milestones are Verified (or Completed with an evidence document) and no overlay is active. Agents never release money. |
| Engine numbers | Pricing `AfterFinish` | Landed cost and net payout are overwritten with the values in `gc_pricequote` |
| Negotiation limits | Negotiation `AfterFinish` | The counter price is clamped to your min/max. An invented Incoterm or payment term is removed. A reply with unknown numbers or party names is dropped. |
| Risk-flag floor | ListingVerification `AfterFinish` | A listing with any risk-flagged document (prompt injection, tampering) is raised to **Hold** |
| System-owned approvals | `create_review_task` | The model can't open Listing Publish, Tier Upgrade, Fund Release, Contract Issue or Message Send. Code opens these with structured payloads, which the Review decisions flow applies; Tier Upgrade carries `tier`/`tierValue` |
| Caller-only cost inputs | `calculate_price_quote` | Re-pricing uses only the cost inputs in the caller's `Input`, never values the model supplies; it is refused when there are none |
| Prompt injection | All | Documents and messages are wrapped as data. Injection is flagged. No tool can change a status, publish, approve or pay. |
| Transaction safety | Tools | Model-supplied GUIDs and columns are validated before any Dataverse call. A failing call stops the run with a clear error, because Dataverse rolls back the whole plug-in transaction. |
| Least data | Read tools | Each agent has a table allow-list. `gc_secret` is never readable. Reads run as the calling user (security roles apply). |

---

## Configuration (`gc_platformsetting`)

| Key | Default | Meaning |
|---|---|---|
| `agents.model.default` | `gemini-3.5-flash` | Model for all agents |
| `agents.model.document` | `gemini-3.5-flash` | Model for Document Intelligence |
| `agents.model.fallbacks` | `gemini-3.1-flash-lite,gemini-3.8-flash` | Tried in order on 429, 503, 404 or timeout; the whole chain is retried once after a pause |
| `agents.time_budget_seconds` | `100` | Per run (the Dataverse plug-in limit is 120 s) |
| `agents.call_timeout_seconds` | `45` | Per Gemini call |
| `agents.pricing` | `{}` | USD per 1M tokens per model prefix, so `gc_modelcall.gc_costusd` is filled |
| `agents.generation_config` | (empty) | Extra Gemini `generationConfig` (e.g. thinking settings) |
| `dd1.max_asks`, `questions.cooldown_hours`, `evidence.extraction_threshold` | existing | Reused by the agents |

**The Gemini key** is stored in `gc_secret` (`gemini.api_key`). That table is organisation-owned, has no privileges for user roles, and is read only by the plug-in as SYSTEM. It is never in the repo; locally it lives in `.env`, which git ignores.

---

## Developer workflow

```bash
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
dotnet build -c Release src/DealOS.Agents                              # plug-in (net462, signed)
dotnet run --project tests/DealOS.Agents.Harness -- build/agents       # offline checks + manifest + schemas
python3 tools/deploy_agents.py                                         # idempotent deploy into the DealOS solution
python3 tools/run_agent.py AdminSupervisor                             # run an agent
python3 tools/seed_test_data.py docs <listing-guid>                    # test PDFs (clean COA + injection COA)
python3 tools/seed_test_data.py cleanup                                # remove [AGENT-TEST] documents
pac solution export --name DealOS --path /tmp/DealOS.zip && pac solution unpack --zipfile /tmp/DealOS.zip --folder solutions/DealOS --packagetype Unmanaged --allowDelete true
```

The first time, `python3 tools/dv.py login` does a device sign-in. The token is cached in `.dv_token.json`, which git ignores.

---

## Test results (6 Oct 2026, Dev, smoke-test data)

| Agent | Mode | Outcome |
|---|---|---|
| AdminSupervisor | live | Correct digest from live data. 23 s, 6 steps. |
| DocumentIntelligence | dry, injection PDF | `injection_detected = true` and a `prompt_injection` flag. Literal values only, nothing marked verified. |
| DocumentIntelligence | live, clean COA | 5 assertions with page and quote. The resolver moved them to Documented. 10.5 s. |
| ListingVerification | live | 3 blocking asks, a seller request and an anonymous teaser (Documented values only, no seller identity), both queued for approval. Re-run: no new asks while 3 are outstanding. |
| Negotiation | dry | Counter at the buyer's max (8,800). Keeps FOB from the offer. Asks for payment terms instead of inventing them. |
| Pricing | dry | Explanation grounded in the engine quote: landed cost 4,628,223.99, net payout 4,432,500 USD |
| Compliance | dry | Refer: parties not KYB-verified and not screened. Review task. |
| Contract | dry | Refused to draft: price, Incoterm and payment terms missing on the deal |
| Payment | dry | 30/70 schedule with 1.5% commission from the plan, computed by the tool. Blocker: not funded. |
| Logistics | dry | International checklist (South Africa → Germany). Waits for Funded before acting. |
| OnboardingKYB / BuyerVerification | dry | Pending checks recorded, 3 asks maximum, tier stays Unverified |
| Matching | dry | One candidate proposed, score 68, explained (S level unknown) |

---

## Known limits and next steps

1. **The Gemini key is on the free tier.** The limit is 5 requests per minute per model, and the free models frequently return "high demand" 503s. In testing, one run failed after every model in the chain was busy. **Enable billing on the Google AI Studio project before real use.** Also check Google's Gemini API terms on how free-tier content may be used, before sending real deal documents.
2. **Rotate the API key.** It was pasted into a chat. After you switch to a paid key, run `python3 tools/deploy_agents.py` to store the new one.
3. **Plug-in limits:** each run gets at most about 100 seconds. Documents are limited to 14 MB, and only PDF, image or text files are supported (convert DOCX/XLSX first).
4. **Workflows are built.** 22 flows and 4 operations APIs compose these agents across the whole trade; see [WORKFLOWS.md](WORKFLOWS.md).
5. **Front-door chat agents:** Buyer Concierge and Seller Assistant, in Copilot Studio (via `pac copilot init/push`), calling these Custom APIs as tools.
