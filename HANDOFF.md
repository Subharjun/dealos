# HANDOFF: resume here

**Last updated:** 7 October 2026, about 15:45 IST (email desk only; OpenAI **out of credits**, E2E run stopped; sellers take turns; seller-first lots; house style)
**Read first in any new chat:**
1. this file
2. [docs/EMAIL_DESK.md](docs/EMAIL_DESK.md), the current front door
3. [docs/AGENTS.md](docs/AGENTS.md)
4. [docs/WORKFLOWS.md](docs/WORKFLOWS.md)
5. [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md) for background

New team members: [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md).

## 0. Where things stand (read this first)

### What DealOS is now
An **email trade desk in Gmail** for minerals and metals. The bot is the trader in the middle, **back to back**: buyer and seller each deal only with us, in separate threads, and never learn who the other is. Our margin is the price difference (`trade.margin_percent` = 3, placeholder). **Every email to a party is a Gmail draft a person sends** (`email.autosend` = off). Live in Dev on `desk@gmail.com` (testing filter: only mail to the `+dealos` alias).

**Only the email desk remains** (the user's decision, 7 Oct): the website (Power Pages code, 2 chat agents, portal plug-ins) and the marketplace-only parts (6 agents, 18 flows incl. Notify party, escrow operations, catalog) were removed from the repo **and** from Dev. Tables and old data stay in Dev; the deployed site stays parked until its trial ends (about 4 Jan 2027). Removed code: git history at `c55f3bb`.

### How a deal runs
1. **Triage:** every email → Genuine / Review / Ignored, Gmail labels (`DealOS/...`).
2. **Two starting points:**
   - **Buyer first:** requirement → enquiries to seller leads (trade-data imports + **AI web search across Google, IndiaMART, Globalwitz/Volza/TradeIndia pages**). **Sellers take turns:** the first seller to quote is negotiated with the buyer; later quotes queue in reply order; non-responders stay open; "not interested" is left alone. If the deal breaks (seller withdraws or goes silent after a reminder, buyer turns the offer down) and the buyer still wants it, the next queued seller comes up.
   - **Seller first:** a seller's stock with price and quantity → **seller lot** → offered (masked, + margin) to known buyers and buyers found by web search. **Window per lot** from the seller's email (24 h, 48 h, ...) or the default; **open-ended** ("until sold") lets the seller decide: every bid goes to them at once. Timed lots: highest price wins at the deadline; if no bid reaches the seller's price, the best bid goes to the seller and the lot goes open-ended.
3. **Negotiation:** seller price + margin to the buyer; buyer price − margin to the seller; counters both ways. Acceptance → **Confirm deal** task (a person approves).
4. **Contract:** KYB + screening → Compliance agent → Contract agent → Contract Issue approval → two PDF contracts (sales to the buyer, purchase from the seller) drafted to each thread → signed copy → task → deal **Signed** (no escrow) → **inspection** requested.
5. **Briefings:** a `[DealOS] ...` email to the owner after each step; Desk timers every 15 min (lot closing, seller queue, chasers, reminders).

### House style (7 Oct, from the owner's real coal chat)
Short and plain like a trader: bulleted indicative specs with "~", one clear ask, "Let me confirm and revert". `draft_email` refuses template/AI phrases ("I hope this email finds you well", "We are pleased to", "do not hesitate"...), em dashes, exclamation marks and markdown; all code-written emails were rewritten the same way. Buyers' requests for **company profile / quality report** and **prices for another port** become a task for a person (no invented prices, seller documents masked first).

### BLOCKER (7 Oct 15:41): the OpenAI account has no credits
Every model call fails with `429 insufficient_quota / credit_balance_exhausted`, so triage and the Trade Desk stop. The full E2E run was stopped after triage passed. **Add credits at platform.openai.com → Billing**, then `python3 tools/e2e_all.py`. Or switch back to Gemini meanwhile: `python3 tools/deploy_agents.py --provider gemini` (free tier: slow, no web search).

**Incident, cleaned up:** web discovery ran during the noon and afternoon tests (the deployed plug-in predated the `email.discovery.enabled` switch) and found real companies; the desk then made enquiry drafts to them (Sinochem Nanjing, DS Alloyd, Forbes Pharma, Yogi Chem, Vanmo Tech, CDH Fine Chemical, Powder Pack, MPIL, Fitechem, CDHR Metal, Caymon Chem, Beijing Daoking). **None was sent**: all 18 were discarded and are gone from Gmail Drafts (checked). The switch is now deployed and `e2e_all.py` turns discovery off during tests. Their leads, seller threads and Inquiry deals remain as test data.

### AI provider
**OpenAI** since 7 Oct (`agents.provider` = openai, `gpt-5.4-mini` via the Responses API, fallback `gpt-4.1-mini`, `store=false`). `Infrastructure/ModelClient.cs` translates the runtime's requests. Gemini is standby: `python3 tools/deploy_agents.py --provider gemini`. **The OpenAI key was pasted in chat: rotate it.**

### Totals in Dev
- **8 agents:** Mail Triage, Trade Desk, Document Intelligence, Onboarding KYB, Buyer Verification, Compliance, Contract, Admin Supervisor
- **22 product flows** + 1 test-kit flow (list in [docs/WORKFLOWS.md](docs/WORKFLOWS.md))
- operations: `gc_AcceptOffer`, `gc_ReleaseDeal`, `gc_IngestEmail`, `gc_AttachEmailFile`, `gc_BuildEmailRaw`, `gc_SourceRequirement`, `gc_DiscoverSellers`, `gc_DiscoverBuyers`, `gc_MarketLot`, `gc_DeskBrief`, `gc_DeskContract`, `gc_CloseLots`, `gc_DeskFollowUps`

### Testing
- **All scenarios in one command:** `python3 tools/e2e_all.py` (about 1.5 to 2 h; logs in `build/e2e/`, summary in `build/e2e/summary.json`). It switches web discovery off while the scenarios run (`email.discovery.enabled` = false, so real companies never get test drafts), then checks discovery on its own (leads only, no drafts).
  | Scenario | Script | Last result |
  |---|---|---|
  | triage (7 samples) | `mail_test.py run` | **PASS** 7 Oct 15:08 (OpenAI) |
  | buyer first, full deal to inspection | `desk_e2e.py run` | passed 7 Oct 00:40 and 09:21 (Gemini); 15:26 run stopped by the OpenAI credit blocker |
  | sellers take turns | `desk_queue_e2e.py run` | steps 1 to 5 passed (OpenAI, 12:16); full pass pending |
  | lot, two buyers compete | `desk_lot_e2e.py run` | passed 7 Oct 10:11 (Gemini); OpenAI run pending |
  | lot, one buyer | `desk_lot_e2e.py single` | passed 7 Oct 10:37 (Gemini); OpenAI run pending |
  | seller first, open-ended | `desk_lot_e2e.py open` | passed 7 Oct 11:46 (Gemini); OpenAI run pending |
  | timed lot, best bid below price | `desk_lot_e2e.py below` | pending |
  | coal: other port, profile, report | `desk_lot_e2e.py coal` | pending |
  | web discovery (sellers + buyers) | `e2e_all.py discovery` | pending |
- Offline: `dotnet run --project tests/DealOS.Agents.Harness -- build/agents` (all checks pass).

### GitHub
`main` is at `41855d2`. **Everything from 7 Oct is uncommitted** (lots, follow-ups, seller first, windows, OpenAI, seller queue, removal of site and marketplace, house style, tests). Commit and push only when the user asks.

### History (most recent first)
- **7 Oct afternoon:** OpenAI replaces Gemini; sellers take turns; website and marketplace removed (repo and Dev); house style; discovery across Google / IndiaMART / Globalwitz; `e2e_all.py`. Found by the tests and fixed: a seller's own wording (with their price) copied into an offer to the buyer (`Desk.SafeTerms`); the agent overwriting a draft the desk had just written (draft lock per run); OpenAI filling empty optional output (`S.Prune`).
- **7 Oct midday:** seller first (buyer web search, offers, counters both ways) and per-lot windows; open-ended live test passed (deal at seller USD 31,500 / buyer USD 32,445).
- **7 Oct morning:** competing-buyer and single-buyer lot tests passed; chasers; duplicate COA fix; lot spec masking.
- **7 Oct 00:40:** first full buyer-first E2E passed (USD 9,850 quote → USD 10,145.50 offer → buyer USD 10,000 → bid USD 9,708.73 → Confirm deal → compliance → two contract PDFs → signed → inspection).
- **6 Oct:** front door changed from the website to the email desk.

Housekeeping waits until the build is finished (the user's decision): test data removal, key rotation (OpenAI and Gemini keys were both pasted in chat).

---

## 1. What we are building

**DealOS**: an agent-driven, trust-first B2B trade desk for **rare earths, critical minerals and metals**. It started as a marketplace design: listings, RFQs, escrow and a website (removed from the build on 7 Oct 2026). On 6 Oct 2026 the user redirected it to the way this trade actually works: **email**.

**The email desk flow** (the user's words: "buyer decides the price, pitch to me, I go to the seller, the seller counters, I tell the buyer, the buyer agrees, then contract sign — myself = bot/AI automation"):
1. **Classify** every incoming email. Only genuine trade mail is worked; scams, pitches and newsletters are ignored.
2. **Buyer requirement** → find sellers (trade-data leads, warehouses, web search) → masked enquiries; sellers take turns. Or **seller first**: a seller's stock → find buyers → masked offers.
3. **Negotiate** in the middle: seller price + margin to the buyer; buyer price − margin to the seller; counters both ways.
4. **Agreement** → KYB and screening of both sides → **contract** (back to back: sales contract to the buyer, purchase contract from the seller) → signed.
5. **Track**: inspection, shipment, delivery. **No payment handling and no escrow**: payment terms are between the parties per contract. Our margin is the price difference.

The problem it solves: mineral trade runs on WhatsApp and email chains, with forged COAs, fake mandates and scattered execution. The desk answers it with triage of every email, KYB and screening of both sides, Document Intelligence on COAs, masked back-to-back negotiation and contracts, and independent inspection.

---

## 2. Decisions made (do not re-litigate)

| Date | Decision | Notes |
|---|---|---|
| 6 Oct 2026 | Platform = **Power Platform** (Dataverse, Power Automate) | User mandate; no Postgres or local stack |
| 6 Oct 2026 | Existing `DealOS` solution is the baseline | About 45 tables, evidence engine, invariants plug-in |
| 6 Oct 2026 | AI = **Google Gemini**, agents are Dataverse Custom APIs `gc_Agent_<Name>` (C# plug-in, model tool loop) | Flows call agents with "Perform an unbound action" |
| 7 Oct 2026 | **AI provider = OpenAI** instead of Gemini (`gpt-5.4-mini`, Responses API); Gemini kept as a switchable fallback provider | The user supplied an OpenAI key |
| 7 Oct 2026 | **Buyer first: sellers take turns.** First seller to quote is negotiated with; later ones queued in reply order; next one only if the deal breaks and the buyer still wants | Replaces "wait for all quotes, offer the best" |
| 7 Oct 2026 | **Seller first is a starting point too** (a seller needing buyers → lot → buyer search → offers → negotiation both ways). Lot window per lot: 24 h / 48 h / open-ended from the seller's email, else the default | Open-ended: the seller decides when to close |
| 6 Oct 2026 | Docs are **Markdown in this repo** | User preference |
| 6 Oct 2026 | **Front door = email desk in Gmail**; the site is parked | Email is how this trade works |
| 7 Oct 2026 | **Only the email desk stays**: website and marketplace-only parts removed from repo and Dev | The user's decision; tables and data kept |
| 6 Oct 2026 | Classification first; only genuine mail is worked | Hard signals in code; known senders never ignored |
| 6 Oct 2026 | **Back to back**: buyer and seller never see each other; our margin = price difference (`trade.margin_percent`, 3 = placeholder) | Contract structure confirmed by the user's flow; margin % still open |
| 6 Oct 2026 | **No escrow and no payment handling**; after verification the contract; then inspection and shipment tracked | Escrow code removed 7 Oct |
| 6 Oct 2026 | Gmail via **our own Google OAuth app + custom connector "DealOS Gmail"** (option C, chosen over SMTP/IMAP or forwarding to Outlook) | The Microsoft Gmail connector on a consumer account can't share a flow with Dataverse and can't create drafts |
| 6 Oct 2026 | **Everything in Power Automate, deployed from code with the CLI tools** | Flows plus custom connector; plug-ins only do the work the flows call |
| 6 Oct 2026 | **The mailbox can change for production**: never hard-code the address | The flow reads the connected account from Gmail; switching = new connection + `deploy_connector.py bind` |
| 6 Oct 2026 | Approval: drafts in Gmail; a person presses Send. **Commitments need a person**: Confirm deal, contract issue, signed copy | Agents never accept offers themselves |
| 7 Oct 2026 | Seller and buyer discovery: **AI web search** across Google, IndiaMART listings, Globalwitz / Volza / TradeIndia trade-data pages and company sites (public business contacts only); **no logins, no scraping, no LinkedIn** | OpenAI `web_search`; the user asked for Globalwitz, IndiaMART and Google |
| 7 Oct 2026 | **House style for every email:** short and plain like a trader on WhatsApp/email (bulleted indicative specs with "~", one clear ask); `draft_email` refuses template/AI phrases, em dashes, exclamation marks and markdown | From the user's real coal chat (Tanzanian coal, FOB Mtwara / CIF Ennore) |
| 7 Oct 2026 | The bot briefs the owner by email after each step; optional auto-send of routine mail (`email.autosend`) | Default off |
| 6 Oct 2026 | Leads: trade-data exports (`import_leads.py`), warehouses, email offers, web search; IndiaMART only via its official API (later) | No scraping |

---

## 3. Environment and access

| Item | Value |
|---|---|
| Tenant | `gigacoreenergypvtltd.onmicrosoft.com` (user `gigacore@…`) |
| Dev environment | Giga core's Environment, `https://org61da3071.crm8.dynamics.com` (India), env id `b77eedc7-f980-e3bb-a07e-757db98002d2`. No Test or Prod yet. |
| Solution | `DealOS` (unmanaged), publisher prefix `gc`, choice values start at 303300000 |
| Web API from the CLI | `python3 tools/dv.py login` (browser sign-in; device code is blocked). Token in `.dv_token.json` (git-ignored). **A password change revokes it (AADSTS50173): log in again.** The same refresh token is exchanged for the Power Apps and Flow APIs (`deploy_connector.py bind`, `gmail_kit.py`). |
| PAC CLI | `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec ~/.dotnet/tools/pac` (profile `dealos`) |
| .NET | SDK 10 at `/opt/homebrew/opt/dotnet/libexec/dotnet` (`dotnet` on PATH is v8) |
| OpenAI key (in use) | `.env` `OPENAI_API_KEY` + Dataverse `gc_secret` `openai.api_key`. `agents.provider` = openai. **Rotate it** (it was pasted in chat), then `deploy_agents.py`. |
| Gemini key (standby) | `.env` `GEMINI_API_KEY` + `gc_secret` `gemini.api_key`. Free tier: no Google Search, about 5 requests/min, daily caps. Rotate it (it was pasted in chat). |
| Gmail | Mailbox `desk@gmail.com`. Google Cloud project **"My First Project"**: Gmail API on, OAuth consent screen "DealOS Email Desk" (External), scope `gmail.modify`. OAuth client "DealOS Power Automate", **Client ID** `set-the-google-client-id` (also in `.env` `GMAIL_CLIENT_ID`). The secret is only in the connector's Security tab. Redirect URI `https://global.consent.azure-apim.net/redirect/gc-5fdealos-20gmail-5fc12c8485be9726a2`. |
| Google app status | **Check whether it was published** ("In production"). If it's still in Testing with the user as test user, the Gmail connection **expires every 7 days**. Fix: Google Auth Platform → Audience → Publish app. |
| Power Automate | Custom connector **DealOS Gmail** (`shared_gc-5fdealos-20gmail-5fc12c8485be9726a2`, in the solution), connection reference `gc_gmail` → connection `fcbe6c4cbf774649b49bfe659161b8a8` (Connected) |
| Old site (removed from the build) | https://dealos-gigacore.powerappsportals.com still exists in Dev, parked, until its trial ends (about 4 Jan 2027). Delete it in the Power Pages admin centre whenever convenient. |
| Dev settings changed | plug-in trace log = All (set back to Exception before go-live) |
| Team | Krishna Shukla (`krishna38@…`) is in the tenant but not yet in the Dev environment |

---

## 4. Repo map

| Path | Content |
|---|---|
| `docs/EMAIL_DESK.md` | **The email desk**: design, mailbox setup, triage, Trade Desk, leads, contract, data model, build status, **section 12 = what a person does** |
| `docs/AGENTS.md`, `docs/WORKFLOWS.md` | Agent and flow catalogues, including the email desk agents and flows |
| `docs/ARCHITECTURE_AND_BUILD_PLAN.md`, `docs/TEAM_SETUP.md` | Original plan (background), team setup |
| `src/DealOS.Agents/` | Plug-in assembly (net462, signed). Model providers: `Infrastructure/ModelClient.cs` (OpenAI Responses API + provider switch), `Infrastructure/GeminiClient.cs`. Email desk code:<br>• `Mail/GmailMessage.cs` (parser)<br>• `Mail/MailSignals.cs` (hard signals, verdict)<br>• `Mail/MailPlugin.cs` (email operations)<br>• `Mail/Desk.cs` (margin maths, drafts, sourcing, contracts, briefings, auto-send)<br>• `Mail/Discovery.cs` (web search for sellers and buyers)<br>• `Mail/Lots.cs` (seller lots: windows, bids, seller decisions, close)<br>• `Mail/FollowUps.cs` (chasers, reminders)<br>• `Mail/Mime.cs` (RFC 2822 builder, PDF writer)<br>• `Agents/MailAgents.cs` (Mail Triage, Trade Desk)<br>• `Tools/DeskTools.cs` (Trade Desk tools and draft masking checks) |
| `tests/DealOS.Agents.Harness/` | Offline checks (all pass; email desk ones are named `gmail:`, `signals:`, `triage:`, `desk:`) |
| `tools/flows/definitions.py` | All flows as code. Email desk:<br>• `mailbox_sync`, `trade_desk`, `desk_drafts`, `desk_contract`, `seller_discovery`<br>• `email_test_kit` (in `TESTS`) |
| `tools/connectors/gmail.swagger.json`, `tools/deploy_connector.py` | The DealOS Gmail connector. `deploy_connector.py` creates or updates it, adds it to the solution and binds the connection. |
| `tools/import_leads.py` | Trade-data CSV/XLSX → `gc_lead` (supplier → seller, purchaser → buyer) |
| `tools/mail_test.py` | Triage test without Gmail (7 samples) |
| `tools/gmail_kit.py` | The test kit flow from the CLI: insert mail, send drafts, labels, profile, `run_flow` |
| `tools/desk_e2e.py` | **End-to-end test on the real mailbox**: `run`, `resume`, `status` |
| `tools/desk_lot_e2e.py` | Seller-lot tests on the real mailbox: `run` (two buyers compete), `single`, `open` (seller first, open-ended), `below` (timed, best bid to the seller) |
| `tools/e2e_all.py` | **Every scenario in one run** (triage, buyer first, seller queue, lots, open-ended, below, coal, web discovery); discovery off during the run; logs in `build/e2e/` |
| `tools/desk_queue_e2e.py` | Buyer first, sellers take turns: `run` (first quote active, later one queued, decline ignored, buyer rejects → next seller, accept) |
| `tools/deploy_schema.py`, `deploy_agents.py`, `deploy_flows.py` (`--tests` for the kit), `run_agent.py`, `watch.py`, `seed_test_data.py`, `export_solution.py` | Deploy and run tools |
| `solutions/DealOS/` | Unpacked solution, exported from Dev on 7 Oct 2026 |

---

## 5. What is built

### Email desk (6–7 Oct 2026); details in [docs/EMAIL_DESK.md](docs/EMAIL_DESK.md)

**Schema** (`deploy_schema.py`):
- `gc_conversation`: `gc_gmailthreadid`, `gc_triage`, `gc_triagescore`, `gc_category`, `gc_side`, `gc_requirement`, `gc_invite`
- `gc_message`: `gc_direction` (Inbound / Outbound / Draft), `gc_fromaddress`, `gc_toaddresses`, `gc_subject`, `gc_emailmeta`, triage columns, `gc_draftstatus`, `gc_toaddress`, `gc_gmaildraftid`, `gc_autosend`
- `gc_document.gc_message`
- `gc_buyerrequirement`: `gc_source`, `gc_deskstage` (Qualifying → Sourcing → Quoted → Negotiating → Agreed → Contract Sent → Signed → Closed), `gc_commoditytext`, `gc_deliverytext`, `gc_paymenttext`, `gc_ourprice`, `gc_confidential`, `gc_discoveredon`
- `gc_deal`: `gc_emaildesk`, `gc_buyerprice`
- `gc_offer.gc_terms`
- **new table `gc_lead`**
- `gc_agent` options Mail Triage (14), Trade Desk (15)

**Agents:**
- `gc_Agent_MailTriage`: category + score. The verdict is decided in code from the hard signals: DMARC/SPF, dangerous attachments, link shorteners, Reply-To redirect, known sender, our own thread, injection.
- `gc_Agent_TradeDesk` has 11 tools: `save_requirement`, `start_sourcing`, `save_seller_lead`, `save_seller_quote`, `quote_to_buyer`, `record_buyer_price`, `buyer_accepts`, `seller_accepts_bid`, `report_signed_contract`, `draft_email`, `create_review_task`.
- `draft_email` refuses: the other party's name, email, domain or price; our margin; contact details; links; bank details. It also strips the writer's own sign-off.

**Operations** (`MailPlugin`):
- `gc_IngestEmail`: Gmail message → thread + message. Mail sent from a desk thread closes its draft; other sent mail is skipped.
- `gc_AttachEmailFile`: attachment → Quarantined document. Released to Document Intelligence when a seller quote is genuine.
- `gc_BuildEmailRaw`: draft → RFC 2822 reply with In-Reply-To, Reply-To alias and attachments.
- `gc_SourceRequirement`: lead ranking → account, deal, invite, seller thread, enquiry draft. Leads without an email → "Find contacts" task.
- `gc_DiscoverSellers` / `gc_DiscoverBuyers`: AI web search → leads.
- `gc_DeskBrief`: briefing to the owner.
- `gc_DeskContract`: two contract PDFs + drafts.

**Flows** (all on):

| Flow | When it runs | What it does |
|---|---|---|
| **Mailbox sync** | every 3 minutes | 1. creates the labels<br>2. inbox: ingest → attachments → triage → labels<br>3. sent mail pass |
| **Trade desk** | an email becomes Genuine | Trade Desk agent → briefing |
| **Desk drafts** | a draft is created or replaced | Gmail draft in the thread (saves the thread id); auto-send when flagged; replaced draft → deleted |
| **Desk contract** | contract Sent For Signature on a desk deal | PDFs and drafts → briefing |
| **Seller discovery** | requirement desk stage → Sourcing | web search → new enquiries → briefing |
| **Buyer discovery** | a seller lot is created | web search → lot offered to new buyers → briefing |
| **Desk timers** | every 15 minutes | close timed lots, seller queue, chasers, reminders |

**The deal path flows (kept from the original build, desk-only since 7 Oct):** Document intake, Party onboarding, Offer pricing, Offer accepted, Terms agreed, Compliance check, Contracting, Contract signed (→ Signed, no escrow), Inspection booking (on Signed), Inspection result, Deal cancelled, Approvals, Review decisions (`desk.accept_offer`, `desk.contract_signed`), Daily digest, Flow failure triage. Full list: [docs/WORKFLOWS.md](docs/WORKFLOWS.md).

**Settings:**
- `email.enabled` = true
- `email.sync.query` = `to:{user}+dealos@{domain} newer_than:7d` (testing: only mail to the +dealos alias)
- `email.reply_to` = `{user}+dealos@{domain}`
- `email.autosend` = off
- `desk.owner_email` = `{mailbox}`
- `email.signature`, `email.sourcing.max_sellers` = 5, `email.discovery.max` = 8
- `trade.margin_percent` = 3
- `trade.company_name`, `trade.governing_law`
- `email.triage.proceed` = 65 / `email.triage.ignore` = 30
- `email.attachments.max_mb` = 10

### Earlier work (5–6 Oct 2026)

- **Back-office agents kept for the desk deal path:** DocumentIntelligence, OnboardingKYB, BuyerVerification, Compliance, Contract, AdminSupervisor.
- **Removed 7 Oct 2026:** the website (Power Pages code site, chat agents BuyerConcierge / SellerAssistant, `ChatPlugin`, `CatalogPlugin`, `PortalGuardPlugin`) and the marketplace-only agents and flows (ListingVerification, Matching, Pricing, Negotiation, Payment, Logistics; escrow, milestones, releases, disputes, ratings, invoices, deadlines, sweep, Notify party). In git history at `c55f3bb`.
- **Guardrails** (details in docs/AGENTS.md): message validator, do-not-ask-twice, money numbers only from engines, system-owned approvals, compliance floor, poisoned-transaction stop.

---

## 6. Test data in Dev (all `[AGENT-TEST]` / `[SMOKE …]`; removal is scheduled for the end of the build)

| What | Notes |
|---|---|
| Email desk E2E run `[AGENT-TEST] E2E 1007-0019` | State in `build/desk_e2e.json`.<br>• Buyer thread `73d24eaa-b6c1-f111-aaaf-7ced8daf451f`, requirement `286eedbf-b6c1-f111-aaaf-7ced8daf451f`<br>• Seller 1 (Panzhihua) thread `63d23fc7-…`, deal `60d23fc7-b6c1-f111-aaaf-7ced8daf451f`<br>• Seller 2 (Hunan) thread `6dd23fc7-…`<br>• The test set both parties' KYB to Passed with a Clear screening |
| Leads `[AGENT-TEST] Panzhihua Vanadium Test Co`, `Hunan Vanadium Test Ltd`, `Vanadium Trading Test (no email)` | From `build/e2e_leads.csv`; emails `*.example` (drafts sent to them bounce, which is expected) |
| 7 triage test threads (Gmail ids `agent-test-*`) | `python3 tools/mail_test.py cleanup` |
| Gmail inbox | Test messages and a "draft probe" thread; the first E2E buyer email (from a +alias, skipped as own mail). Delete in Gmail whenever convenient. |
| "DealOS desk briefing" thread | Briefing emails in the inbox |
| Marketplace test data (5–6 Oct) | SMOKE listing, deals, offers, accounts (KYB set to Verified for tests), copper listing, RFQs, chats, disputes, rating, inspection. See git history of this file for ids. |

`python3 tools/seed_test_data.py cleanup` removes most marketplace test rows. Append-only rows stay, so cancel deals instead of deleting them.

---

## 7. Lessons learned (avoid repeating these mistakes)

**Dataverse and plug-ins**
- **A failed Dataverse call inside a plug-in poisons the transaction.** Validate first (`Dv.Retrieve`, `Dv.Exists`, `Dv.Columns`); on a fault, stop.
- **Writing a field to its current value still fires "modified" triggers.** Code that a flow calls must not rewrite its trigger field. `Desk.Source` therefore only moves the desk stage forward.
- **OData `startswith(gc_name,'[AGENT-TEST]')` matches nothing** (`[` is a LIKE class); use `'[[]AGENT-TEST]'`.
- **Lookups created through the Web API keep schema-name case in binds** (`gc_Requirement@odata.bind`). Triggers carry no choice labels.
- `gc_TransitionDeal` never throws (`Allowed=false` + `Failures`). Its guards need **KYB Verified + Clear screening for both parties before Contracting**.
- `gc_auditevent.gc_hash` is required (flows pass `set-by-invariants-plugin`).
- `gc_paymentrelease`: commission + net = amount.
- `gc_document.gc_sha256` is required.
- File columns are written with `InitializeFileBlocksUpload` / `UploadBlock` / `CommitFileBlocksUpload`, which work inside plug-ins.

**Models (OpenAI since 7 Oct; Gemini before)**
- **OpenAI:** use the **Responses API**. Chat Completions refuses function tools with reasoning on `gpt-5.4-mini`. With `store=false`, pass the output items back as they are and request `reasoning.encrypted_content`. gpt-5 models take `reasoning.effort`, not `temperature`.
- **Gemini free tier:** about 5 requests/min per model, frequent 503s, daily caps (`gemini-3.5-flash` ran out during testing), and **no Google Search grounding** (429 on every model). **Enable billing.**
- Use the 3.x models (2.5-flash is retired).
- Pass `thoughtSignature` back unchanged.
- Reject `finish` in a turn that has other tool calls.
- Agents must never set money numbers: prices come from tools and engines only.

**Power Automate and Gmail**
- **A consumer Gmail account needs our own Google OAuth app.** The Microsoft Gmail connector on @gmail.com can't share a flow with Dataverse.
- The Google app must be **In production**; Testing sign-ins expire every 7 days. **Publish app** is greyed out until Branding is saved and the page is reloaded. Don't upload a logo (that forces a verification review).
- **The flow service is slow or flaky at times:**
  - updating a big flow (PATCH clientdata) can GatewayTimeout repeatedly; delete it (if off) and recreate
  - activation can take about 60 s
  - `gmail_kit.py` retries and caches the kit URL in `build/kit_url.txt`
- **A custom connector must be added to the solution** (`AddSolutionComponent`, type 372), or the export fails on its connection reference.
- **Gmail treats `+aliases` of the mailbox as the owner:** mail "from" `user+x@gmail.com` gets the SENT label. **Test counterparties must use other domains** (the E2E uses `.example`).
- **Sending a Gmail draft creates a new message id and drops the draft's labels, but keeps the thread id.** That's why sent mail is matched by thread.
- `users.messages.insert` (scope `gmail.modify`) puts test mail into the inbox without sending.
- The Flow API (`https://service.flow.microsoft.com//.default`) can run a scheduled flow on demand and give an HTTP trigger's URL.

**Process**
- A password change revokes every refresh token: run `python3 tools/dv.py login` (and `pac auth create`).
- In Python edit scripts, check generated flow expressions with `deploy_flows.py --dump`.
- E2E scripts must wait for the **specific** record, not "a pending draft exists". An older draft fooled step 3 once.
- **Code-drafted emails bypass `draft_email`'s masking checks.** The offer to the buyer once copied the seller's own wording ("USD 29,500 per MT") from the quote's terms; the queue E2E caught it. Anything from the other side that code puts in a draft must go through `Desk.SafeTerms` / `Desk.SafeSpec`, and E2E checks must search buyer drafts for seller names and prices.
- **OpenAI fills optional output blocks** (e.g. an empty `offer` on a buyer email) where Gemini left them out. `S.Prune` drops empty optional fields before validation.

---

## 8. How to resume (commands)

```bash
cd ~/Desktop/Power-Automate-lastry
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec; D=$DOTNET_ROOT/dotnet
python3 tools/dv.py get WhoAmI                     # 401? → python3 tools/dv.py login  (browser)
python3 tools/e2e_all.py                           # every scenario on the real mailbox (or: e2e_all.py queue coal ...)
python3 tools/desk_e2e.py status                   # where the buyer-first test is
python3 tools/watch.py 30m                         # what the flows did: audit, failures, review tasks
python3 tools/deploy_agents.py --provider openai   # or gemini: switch the AI provider

# after code changes
$D build -c Release src/DealOS.Agents && $D run --project tests/DealOS.Agents.Harness -- build/agents
python3 tools/deploy_schema.py                     # tables / columns / options (only what is missing)
python3 tools/deploy_agents.py                     # plug-in, agents, operations, settings (idempotent)
python3 tools/deploy_flows.py --only "Trade desk"  # one flow (all without --only also deletes retired flows); --tests for the test kit
python3 tools/deploy_connector.py [status|bind]    # Gmail connector / connection
python3 tools/import_leads.py <file.csv> --source "<provider>"   # trade-data leads
python3 tools/run_agent.py TradeDesk <conversation-guid> --dry --input '{"message_id":"<gc_message guid>"}'
python3 tools/export_solution.py                   # sync solutions/ with Dev
```

---

## 9. What remains (in priority order)

### Next
- [ ] **OpenAI credits** (blocker, section 0), then **run the full E2E** (`python3 tools/e2e_all.py`); fix whatever fails and rerun that scenario (`e2e_all.py <name>`).
- [ ] Optional resilience: fall back from OpenAI to Gemini automatically when OpenAI says `insufficient_quota`, so the desk keeps working when credits run out.
- [ ] **Re-export the solution** (`python3 tools/export_solution.py`) after the run: `solutions/` still contains the removed site plug-ins, marketplace flows and agents until it is exported again.
- [ ] **Commit and push** the 7 Oct work when the user asks.
- [ ] **Rotate the OpenAI key** (pasted in chat): new key → `.env` `OPENAI_API_KEY` → `python3 tools/deploy_agents.py`.
- [ ] **Check that the Google app is "In production"**, or the Gmail connection breaks after 7 days.
- [ ] **Company profile PDF**: buyers ask for it early (as in the coal chat). Today the desk opens a task; storing a profile document the desk can attach would remove that manual step.
- [x] E2E proof (7 Oct 00:40 and 09:21, Gemini); polish ("Dear <name>", "not this time" notes); OpenAI replaces Gemini billing (7 Oct).
- [ ] **User decisions:**
  - `trade.margin_percent`
  - `email.signature` (whose name): for the human tone a person's name reads better than "Trade Desk" (the coal chat is signed by the owner)
  - `email.autosend`: off / routine / all. The user's last message ("myself = bot/AI automation") suggests they may want more automation; ask before switching.
  - contract template: governing law with counsel
- [ ] **Go live on the real inbox:** `email.sync.query` = `in:inbox newer_than:7d` and `email.reply_to` = empty (OpenAI has the throughput now; decide when).

### Many buyers and sellers over days (7 Oct 2026; the user's decisions: highest buyer price wins, bids close at a deadline per lot)
- [x] **Seller lots** (`gc_sellerlot`, `Mail/Lots.cs`): an offer to sell with a price and quantity becomes a lot. It is offered (masked, +margin) to open requirements and buyer leads; buyers bid until `trade.bid_window_hours`. **Desk timers** (every 15 min, `gc_CloseLots`) ranks the bids: highest price first, ties to the earliest bid, while quantity lasts. Winners get Confirm deal; losers get "not this time".
- [x] ~~Quote window~~ → replaced by **sellers take turns** (7 Oct): first seller to quote is active, later ones queued, the next comes up when the deal breaks. Test: `python3 tools/desk_queue_e2e.py run`.
- [x] **Chasers** (`gc_DeskFollowUps`): one follow-up after `desk.chase_after_hours` (48 h) to silent sellers and buyers; a reminder to lot buyers before the deadline.
- [x] Several emails before we answer: the agent sees the unsent draft and keeps what matters. A seller's fresh email joins their open enquiry; a buyer's fresh email stays separate (the agent sees their other open requirements).
- [x] Same file sent twice (a COA reused across enquiries) no longer breaks attachment storage (`gc_duplicateof` + derived key). The attachment step now retries transient platform errors.
- [x] Daily digest goes out as a desk briefing (the Outlook send failed with 404: no Exchange mailbox).
- [x] Any number of buyers per lot (cap `email.marketing.max_buyers`). Offers close early once every buyer offered the lot has bid or declined, so a single buyer never waits for the deadline.
- [x] Masking for lots: buyers see only the technical specification (`Desk.SafeSpec` drops sentences with prices, terms, contacts or the seller's name; any appearance of the seller price figure drops the line).
- [x] Tests on the real mailbox: `python3 tools/desk_lot_e2e.py run` (two competing buyers: **passed 7 Oct 10:11**) and `single` (one buyer, early close).
- [x] Below-floor lot bids: at the deadline the best bid goes to the seller (draft) and the lot carries on open-ended (7 Oct).

### Seller first and per-lot windows (7 Oct 2026; the user's decisions)
- [x] **Two starting points:** a buyer requirement (find sellers) or a seller who needs buyers (a seller lot: find buyers, offer, negotiate both ways).
- [x] **Buyer web search** for a new lot: `gc_DiscoverBuyers` + `gc_MarketLot`, flow **Buyer discovery** (OpenAI `web_search`).
- [x] **Window per lot:** from the seller's email (24 h, 48 h, ... or "until sold" = open-ended), else `trade.bid_window_hours` (0 = open-ended). Shown in `gc_window`; you can change a lot by setting or clearing its Bid deadline.
- [x] **Open-ended lots: the seller decides.** Each buyer bid goes to the seller at once (buyer price − margin, all open bids listed). The seller accepts a bid price (`seller_closes_lot` → best bids at or above it win → Confirm deal), counters (new price → drafted to every buyer in play) or withdraws. One chaser if the seller is silent.
- [x] Fixed on the way: `save_seller_lot` and `buyer_declines_lot` were not in the Trade Desk's tool list (the decline tool was unreachable); a seller's new lot price now also updates each buyer's "our price", so `buyer_accepts` finds it.
- [x] Live test `python3 tools/desk_lot_e2e.py open` **passed** 7 Oct 11:46 (seller first, open-ended: buyer counter → seller, seller counter → buyer, buyer accepts, seller accepts our bid; deal at seller 31,500 / buyer 32,445).
- [ ] Live test `desk_lot_e2e.py below` (48 h window from the email; best bid below the price → to the seller): in the 7 Oct afternoon run.

### Email desk: still to build
- [ ] Approve by reply: the owner answers a briefing ("CONFIRM", "SEND") instead of using the admin app.
- [ ] Label corrections from Gmail feed triage (EMAIL_DESK.md 4.4).
- [ ] KYB documents read from email attachments (today a person sets KYB and screening).
- [x] A polite "not this time" draft to sellers whose deal was closed (7 Oct).
- [ ] Tracking updates (inspection booked, shipped, delivered) drafted to the parties.
- [ ] IndiaMART Lead Manager API (paid seller account): IndiaMART pages already come in through the web search; the API would bring in the enquiries IndiaMART sends us.
- [ ] E-signature instead of scan-and-return.
- [ ] Deal pipeline view for the owner (a model-driven app view or a simple dashboard of requirements by desk stage).

### Parked
- [ ] Add Krishna to Dev (System Administrator) and as co-owner of the flows.
- [ ] Integrations (registry/KYB APIs, sanctions screening, inspection agencies, forwarders, WhatsApp into `gc_message`), field security profiles, ALM (Test/Prod, pipeline, solution split), evidence benchmark.

### Housekeeping: deliberately last (the user's decision)
- [ ] Remove all test data: `[AGENT-TEST]`, `[SMOKE]`, test leads, test KYB/screenings, test Gmail messages and briefing thread.
- [ ] Key rotation: OpenAI (in use) and Gemini (standby) were both pasted in chat.
- [ ] Plug-in trace log back to Exception. Optionally delete the parked Power Pages site and the marketplace tables/data in Dev (listings, catalog, payments, chats).
- [ ] Optional: `agents.pricing` for cost tracking.

### Open business decisions
- [ ] Margin %, commodities and corridors, who does inspection, logistics (track only or arranged), when (if ever) identities are revealed, licences, Test/Prod permission, legal review (export controls, AML, DPDP, monazite and atomic minerals).

---

## 10. Working preferences

- Markdown docs in this repo; Power Platform only; everything as Power Automate flows and custom connectors deployed from code with the CLI tools.
- The mailbox address is configurable, never hard-coded.
- Humans approve commitments: agreements, contracts and signatures. Drafts are sent by a person unless the user turns on auto-send.
- Finish the build first; test-data removal and housekeeping last.
- Test with `--dry` first; mark test records `[AGENT-TEST]`; test counterparties use `.example` domains (not Gmail +aliases).
- Commit and push only when asked.
