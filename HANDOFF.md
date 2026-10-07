# HANDOFF: resume here

**Last updated:** 7 October 2026, about 00:30 IST (email desk session)
**Read first in any new chat:**
1. this file
2. [docs/EMAIL_DESK.md](docs/EMAIL_DESK.md), the current front door
3. [docs/AGENTS.md](docs/AGENTS.md)
4. [docs/WORKFLOWS.md](docs/WORKFLOWS.md)
5. [docs/ARCHITECTURE_AND_BUILD_PLAN.md](docs/ARCHITECTURE_AND_BUILD_PLAN.md) for background

New team members: [docs/TEAM_SETUP.md](docs/TEAM_SETUP.md).

## 0. Where things stand (read this first)

- **The front door is an email desk in Gmail**, not the website (the user's decision, 6 Oct 2026). The bot works like a trader in the middle, **back to back**: the buyer and each seller deal only with us, in separate email threads, and never learn who the other is.
- **Built and live in Dev on the Gmail mailbox `desk@gmail.com`:**
  1. **Classification:** every email is triaged Genuine / Review / Ignored and labelled in Gmail (`DealOS/Buyer`, `Seller`, `Genuine`, `Review`, `Ignored`, `Processed`, `Error`, hidden `Seen`).
  2. **Buyer requirement → sourcing:** the requirement is recorded. Seller leads are matched (trade-data imports; web search too, once Gemini billing is on). Each seller gets a masked enquiry.
  3. **Negotiation:**
     - the seller quotes → we quote the buyer the seller price + margin
     - the buyer proposes a price → we bid the seller the buyer price − margin
     - an acceptance opens a **Confirm deal** task for a person
  4. **Contract and tracking:** compliance (KYB and screening) → Contract agent → two PDF contracts (sales to the buyer, purchase from the seller), each drafted to its thread. The signed copy → task → contract Signed → deal Signed (**no escrow**) → inspection booking.
  5. **Briefings to the owner:** a `[DealOS] …` email in one "DealOS desk briefing" thread after every step.
- **Every email to a buyer or seller is a Gmail draft that a person sends** (`email.autosend` = `off`). Auto-send for routine mail exists but is switched off; see section 9.
- **End-to-end test on the real mailbox** ([tools/desk_e2e.py](tools/desk_e2e.py)), run `[AGENT-TEST] E2E 1007-0019`. **Verified live:**
  - buyer email → Genuine (score 90) → requirement saved → 2 sellers contacted
  - drafts in Gmail → sent → recorded
  - seller 1 quotes USD 9,850 CIF, seller 2 declines
  - our offer to the buyer: **USD 10,145.50** (3% margin, seller not named)
  - buyer counters at USD 10,000 → our bid to the seller: **USD 9,708.73**, buyer not named
  - seller accepts
  - **Confirm deal** task opened → approved → offer accepted → deal **Terms Agreed** → **Compliance** referred it to the officer (screening clearance, approved as the officer would)
  - Contract agent → Contract Issue approved → **two contract PDFs** generated and drafted to each side: `Contract-Sales-…` to the buyer at USD 10,000; `Contract-Purchase-…` to the seller at USD 9,708.73
  - buyer's signed copy → "Signed contract received" task → approved → **contract Signed → deal Signed, no escrow (0 payments)** → requirement stage Signed → **inspection Requested**
  - **"END TO END PASSED"** at 00:40 IST, 7 Oct 2026 (`python3 tools/desk_e2e.py status` shows it)
- **Fixed during the run:**
  1. **Sent-mail detection closed every pending draft in the thread**, including drafts created after the send. It now closes only drafts created before the sent time.
  2. **One attachment upload failed** while the plug-in was being redeployed. This was a one-off: removing the `DealOS/Error` label made the sync retry it, and it worked.
- **Test script note:** `desk_e2e.py` sends whatever draft is pending. At steps 3 and 4 it twice sent an older draft before the new one existed (the bot was right both times). Make `send_all` wait for the specific draft: the bid offer exists, or the draft has an attachment for contracts. Then run one clean full `run`.
- **Web seller discovery is built but unavailable:** the Gemini key is free tier, and the free tier has no Google Search quota. The briefing says so, and sourcing continues with the imported leads. **Enable billing on the Gemini key to switch it on.** That's also needed for throughput: `gemini-3.5-flash` used up its free daily quota during testing, and the agents fall back to `gemini-3.1-flash-lite`.
- **Earlier work (still deployed):**
  - 12 specialist agents and 2 chat agents
  - the marketplace flows, with escrow for marketplace deals
  - the Power Pages site: deployed and **parked**, trial until about 4 Jan 2027
- **Totals in Dev:**
  - **16 agents**, **39 product flows** plus 1 test-kit flow
  - operations APIs: `gc_AcceptOffer`, `gc_OpenEscrow`, `gc_ReleaseDeal`, `gc_InstructRelease`, `gc_RefreshCatalog`, `gc_IngestEmail`, `gc_AttachEmailFile`, `gc_BuildEmailRaw`, `gc_SourceRequirement`, `gc_DiscoverSellers`, `gc_DeskBrief`, `gc_DeskContract`
- **Nothing from 6–7 Oct is committed to git yet** (front-door, portal and email desk work). Commit and push only when the user asks.
- Housekeeping waits until the build is finished (the user's decision): test data removal, paid Gemini key, key rotation.

---

## 1. What we are building

**DealOS**: an agent-driven, trust-first B2B trade desk for **rare earths, critical minerals and metals**. It started as a marketplace design: listings, RFQs, escrow and a website. On 6 Oct 2026 the user redirected it to the way this trade actually works: **email**.

**The email desk flow** (the user's words: "buyer decides the price, pitch to me, I go to the seller, the seller counters, I tell the buyer, the buyer agrees, then contract sign — myself = bot/AI automation"):
1. **Classify** every incoming email. Only genuine trade mail is worked; scams, pitches and newsletters are ignored.
2. **Buyer requirement** → find sellers (trade-data leads, warehouses, web search) → masked enquiries.
3. **Negotiate** in the middle: seller price + margin to the buyer; buyer price − margin to the seller; counters both ways.
4. **Agreement** → KYB and screening of both sides → **contract** (back to back: sales contract to the buyer, purchase contract from the seller) → signed.
5. **Track**: inspection, shipment, delivery. **No payment handling and no escrow**: payment terms are between the parties per contract. Our margin is the price difference.

The problem it solves: mineral trade runs on WhatsApp and email chains, with forged COAs, fake mandates and scattered execution. DD1, the original "messy deal → two messages" skill, survives as the Listing Verification agent.

---

## 2. Decisions made (do not re-litigate)

| Date | Decision | Notes |
|---|---|---|
| 6 Oct 2026 | Platform = **Power Platform** (Dataverse, Power Automate, Power Pages) | User mandate; no Postgres or local stack |
| 6 Oct 2026 | Existing `DealOS` solution is the baseline | About 45 tables, evidence engine, invariants plug-in |
| 6 Oct 2026 | AI = **Google Gemini**, agents are Dataverse Custom APIs `gc_Agent_<Name>` (C# plug-in, Gemini tool loop) | Flows call agents with "Perform an unbound action" |
| 6 Oct 2026 | No Copilot Studio; own Gemini chat agents | No Copilot credits |
| 6 Oct 2026 | Docs are **Markdown in this repo** | User preference |
| 6 Oct 2026 | **Front door = email desk in Gmail**; the site is parked | Email is how this trade works |
| 6 Oct 2026 | Classification first; only genuine mail is worked | Hard signals in code; known senders never ignored |
| 6 Oct 2026 | **Back to back**: buyer and seller never see each other; our margin = price difference (`trade.margin_percent`, 3 = placeholder) | Contract structure confirmed by the user's flow; margin % still open |
| 6 Oct 2026 | **No escrow and no payment handling**; after verification the contract; then inspection and shipment tracked | Escrow code stays for marketplace deals |
| 6 Oct 2026 | Gmail via **our own Google OAuth app + custom connector "DealOS Gmail"** (option C, chosen over SMTP/IMAP or forwarding to Outlook) | The Microsoft Gmail connector on a consumer account can't share a flow with Dataverse and can't create drafts |
| 6 Oct 2026 | **Everything in Power Automate, deployed from code with the CLI tools** | Flows plus custom connector; plug-ins only do the work the flows call |
| 6 Oct 2026 | **The mailbox can change for production**: never hard-code the address | The flow reads the connected account from Gmail; switching = new connection + `deploy_connector.py bind` |
| 6 Oct 2026 | Approval: drafts in Gmail; a person presses Send. **Commitments need a person**: Confirm deal, contract issue, signed copy | Agents never accept offers themselves |
| 7 Oct 2026 | Seller discovery: **Gemini + Google Search** (public business contacts); **no LinkedIn scraping** (terms and blocking) | Needs Gemini billing |
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
| Gemini key | `.env` `GEMINI_API_KEY` + Dataverse `gc_secret` `gemini.api_key`. **Free tier**: no Google Search, about 5 requests/min, daily caps. Rotate it (it was pasted in chat). |
| Gmail | Mailbox `desk@gmail.com`. Google Cloud project **"My First Project"**: Gmail API on, OAuth consent screen "DealOS Email Desk" (External), scope `gmail.modify`. OAuth client "DealOS Power Automate", **Client ID** `set-the-google-client-id` (also in `.env` `GMAIL_CLIENT_ID`). The secret is only in the connector's Security tab. Redirect URI `https://global.consent.azure-apim.net/redirect/gc-5fdealos-20gmail-5fc12c8485be9726a2`. |
| Google app status | **Check whether it was published** ("In production"). If it's still in Testing with the user as test user, the Gmail connection **expires every 7 days**. Fix: Google Auth Platform → Audience → Publish app. |
| Power Automate | Custom connector **DealOS Gmail** (`shared_gc-5fdealos-20gmail-5fc12c8485be9726a2`, in the solution), connection reference `gc_gmail` → connection `fcbe6c4cbf774649b49bfe659161b8a8` (Connected) |
| Site (parked) | https://dealos-gigacore.powerappsportals.com (private, trial to about 4 Jan 2027) |
| Dev settings changed | `js` unblocked for attachments (code site); plug-in trace log = All (set back to Exception before go-live) |
| Team | Krishna Shukla (`krishna38@…`) is in the tenant but not yet in the Dev environment |

---

## 4. Repo map

| Path | Content |
|---|---|
| `docs/EMAIL_DESK.md` | **The email desk**: design, mailbox setup, triage, Trade Desk, leads, contract, data model, build status, **section 12 = what a person does** |
| `docs/AGENTS.md`, `docs/WORKFLOWS.md` | Agent and flow catalogues, including the email desk agents and flows |
| `docs/ARCHITECTURE_AND_BUILD_PLAN.md`, `docs/PORTAL.md`, `docs/TEAM_SETUP.md` | Original plan, the (parked) site, team setup |
| `src/DealOS.Agents/` | Plug-in assembly (net462, signed). Email desk code:<br>• `Mail/GmailMessage.cs` (parser)<br>• `Mail/MailSignals.cs` (hard signals, verdict)<br>• `Mail/MailPlugin.cs` (email operations)<br>• `Mail/Desk.cs` (margin maths, drafts, sourcing, contracts, briefings, auto-send)<br>• `Mail/Discovery.cs` (web search)<br>• `Mail/Mime.cs` (RFC 2822 builder, PDF writer)<br>• `Agents/MailAgents.cs` (Mail Triage, Trade Desk)<br>• `Tools/DeskTools.cs` (Trade Desk tools and draft masking checks) |
| `tests/DealOS.Agents.Harness/` | Offline checks (all pass; email desk ones are named `gmail:`, `signals:`, `triage:`, `desk:`) |
| `tools/flows/definitions.py` | All flows as code. Email desk:<br>• `mailbox_sync`, `trade_desk`, `desk_drafts`, `desk_contract`, `seller_discovery`<br>• `email_test_kit` (in `TESTS`) |
| `tools/connectors/gmail.swagger.json`, `tools/deploy_connector.py` | The DealOS Gmail connector. `deploy_connector.py` creates or updates it, adds it to the solution and binds the connection. |
| `tools/import_leads.py` | Trade-data CSV/XLSX → `gc_lead` (supplier → seller, purchaser → buyer) |
| `tools/mail_test.py` | Triage test without Gmail (7 samples) |
| `tools/gmail_kit.py` | The test kit flow from the CLI: insert mail, send drafts, labels, profile, `run_flow` |
| `tools/desk_e2e.py` | **End-to-end test on the real mailbox**: `run`, `resume`, `status` |
| `tools/deploy_schema.py`, `deploy_agents.py`, `deploy_flows.py` (`--tests` for the kit), `run_agent.py`, `watch.py`, `seed_test_data.py`, `export_solution.py` | Deploy and run tools |
| `solutions/DealOS/` | Unpacked solution: **stale**, see section 9 |

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
- `gc_DiscoverSellers`: Gemini + Google Search → leads.
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

**Changed flows:**
- **Contract signed:** desk deal → Signed, no escrow
- **Inspection booking:** on Signed for desk deals
- **Offer pricing:** no Pricing agent or party notice for desk deals
- **RFQ matching:** skips email requirements
- **RFQ invite sent / answered:** portal invites only
- **Review decisions:** handles `desk.accept_offer` and `desk.contract_signed`

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

- **12 specialist agents:** DocumentIntelligence, ListingVerification (DD1), OnboardingKYB, BuyerVerification, Compliance, Contract, Payment, Logistics, Pricing, Negotiation, Matching, AdminSupervisor.
- **2 chat agents:** BuyerConcierge, SellerAssistant (`ChatPlugin`; it ignores Email-channel threads).
- **Marketplace flows (34):** listing verification, document intake, party onboarding, matching, offers, terms agreed → compliance → contracting → contract signed → escrow → milestones → releases → settled, disputes, ratings, invoices, daily jobs, flow failure triage, review decisions, approvals, notify party.
- **Data model** from plan section 8, masked catalog, `PortalGuardPlugin`, Power Pages code site (parked).
- **Guardrails** (details in docs/AGENTS.md): message validator, do-not-ask-twice, money numbers only from engines, system-owned approvals, compliance floor, release gate, poisoned-transaction stop.

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

**Gemini**
- **Free tier:** about 5 requests/min per model, frequent 503s, daily caps (`gemini-3.5-flash` ran out during testing), and **no Google Search grounding** (429 on every model). **Enable billing.**
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

---

## 8. How to resume (commands)

```bash
cd ~/Desktop/Power-Automate-lastry
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec; D=$DOTNET_ROOT/dotnet
python3 tools/dv.py get WhoAmI                     # 401? → python3 tools/dv.py login  (browser)
python3 tools/desk_e2e.py status                   # where the end-to-end test is
python3 tools/desk_e2e.py resume                   # continue it from the bid to the seller (steps 4-5)
python3 tools/watch.py 30m                         # what the flows did: audit, failures, review tasks

# after code changes
$D build -c Release src/DealOS.Agents && $D run --project tests/DealOS.Agents.Harness -- build/agents
python3 tools/deploy_schema.py                     # tables / columns / options (only what is missing)
python3 tools/deploy_agents.py                     # plug-in, agents, operations, settings (idempotent)
python3 tools/deploy_flows.py --only "Trade desk"  # one flow (or all without --only); --tests for the test kit
python3 tools/deploy_connector.py [status|bind]    # Gmail connector / connection
python3 tools/import_leads.py <file.csv> --source "<provider>"   # trade-data leads
python3 tools/run_agent.py TradeDesk <conversation-guid> --dry --input '{"message_id":"<gc_message guid>"}'
python3 tools/export_solution.py                   # sync solutions/ with Dev
```

---

## 9. What remains (in priority order)

### Next
- [x] **E2E proof done** (7 Oct, 00:40 IST): buyer email → triage → sourcing → quote → counter → bid → acceptance → Confirm deal → compliance → contract PDFs → signed → deal Signed (no escrow) → inspection Requested.
- [ ] Harden the `desk_e2e.py` waits (section 0), then one clean full `python3 tools/desk_e2e.py run`.
- [ ] Small polish seen in the run: the buyer confirmation said "Dear Buyer" (use the contact's name); a closed-deal "not this time" note to other sellers.
- [ ] **Gemini billing** (the user decides; it's needed now): web seller discovery and reliable throughput. Then rotate the key → `.env` → `deploy_agents.py`. Test discovery with a requirement (`gc_DiscoverSellers`, `Force` = true).
- [ ] **Check that the Google app is "In production"**, or the Gmail connection breaks after 7 days.
- [ ] **Commit and push** the 6–7 Oct work when the user asks. Then run `export_solution.py`; its export failed earlier on the connector, which has since been added to the solution.
- [ ] **User decisions:**
  - `trade.margin_percent`
  - `email.signature` (whose name)
  - `email.autosend`: off / routine / all. The user's last message ("myself = bot/AI automation") suggests they may want more automation; ask before switching.
  - contract template: governing law with counsel
- [ ] **Go live on the real inbox:** `email.sync.query` = `in:inbox newer_than:7d` and `email.reply_to` = empty (after billing, so the free tier isn't flooded).

### Email desk: still to build
- [ ] Approve by reply: the owner answers a briefing ("CONFIRM", "SEND") instead of using the admin app.
- [ ] Label corrections from Gmail feed triage (EMAIL_DESK.md 4.4).
- [ ] KYB documents read from email attachments (today a person sets KYB and screening).
- [ ] A polite "not this time" draft to sellers whose deal was closed.
- [ ] Tracking updates (inspection booked, shipped, delivered) drafted to the parties.
- [ ] IndiaMART Lead Manager API (paid seller account).
- [ ] E-signature instead of scan-and-return.
- [ ] Deal pipeline view for the owner (a model-driven app view or a simple dashboard of requirements by desk stage).

### Parked
- [ ] The site: real sign-in test ([docs/PORTAL.md](docs/PORTAL.md#test-checklist-first-sign-in)), External ID, offers from the site. Trial ends about 4 Jan 2027.
- [ ] Add Krishna to Dev (System Administrator) and as co-owner of the flows.
- [ ] Integrations (registry/KYB APIs, sanctions screening, inspection agencies, forwarders, WhatsApp into `gc_message`), field security profiles, ALM (Test/Prod, pipeline, solution split), evidence benchmark.

### Housekeeping: deliberately last (the user's decision)
- [ ] Remove all test data: `[AGENT-TEST]`, `[SMOKE]`, test leads, test KYB/screenings, test Gmail messages and briefing thread.
- [ ] Paid Gemini key + rotation (it may come earlier; see Next).
- [ ] Plug-in trace log back to Exception; review site settings (`js`, inner errors).
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
