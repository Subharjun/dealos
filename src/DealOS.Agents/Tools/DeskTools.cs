using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Mail;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Tools
{
    /// <summary>
    /// Tools of the Trade Desk agent (subject: an email thread, gc_conversation). They turn what a buyer or seller wrote into
    /// records (requirement, seller quote, buyer price) and draft replies. Prices come from the desk's margin maths, never from the model.
    /// Commitments (accepting an offer, a signed contract) only open a task for a person to confirm.
    /// </summary>
    public static class DeskTools
    {
        private static readonly Regex NumberRx = new Regex(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
        private static readonly Regex EmailRx = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        private static readonly Regex PhoneRx = new Regex(@"\+?\d[\d\s().-]{8,}\d", RegexOptions.Compiled);
        private static readonly string[] BankWords = { "iban", "swift", "account number", "account no", "a/c no", "bank details", "beneficiary", "routing number", "ifsc" };
        private static readonly string[] SourcingNotStated = { "Not stated" };
        /// <summary>Phrases that make an email read like a template or an AI rather than a trader (the house style is short and plain).</summary>
        private static readonly string[] TemplateTells =
        {
            "hope this email finds you", "hope this finds you", "hope you are doing well", "hope you're doing well", "thank you for reaching out",
            "we are pleased to", "i am pleased to", "we are delighted", "i am delighted", "certainly", "rest assured", "do not hesitate", "don't hesitate",
            "feel free to", "as an ai", "i'd be happy to", "we would be happy to", "happy to assist", "delve", "leverage", "seamless", "valued partner",
            "furthermore", "moreover", "in conclusion", "we appreciate your patience", "i trust this", "we trust this", "please be advised"
        };

        public static IEnumerable<Tool> Build()
        {
            var units = DeskChoice.Units;
            var incoterms = DeskChoice.Incoterms.Concat(SourcingNotStated).ToArray();
            var payments = DeskChoice.PaymentTerms.Concat(SourcingNotStated).ToArray();

            yield return new Tool
            {
                Name = "save_requirement",
                Description = "Buyer thread: record (or update) what the buyer wants to buy, exactly as written. Creates the buyer company and contact if new. Call again when the buyer adds details.",
                Writes = true,
                Parameters = S.Obj(null,
                    "commodity", S.Str("Material as written, e.g. 'Vanadium Pentoxide (V2O5)'."),
                    "specification?", S.Str("Grade, purity, impurity limits, sizing as written (one item per line)."),
                    "quantity", S.Num("Quantity as a number (lower end of a range, e.g. 10 for '10-15 MT')."),
                    "quantity_text?", S.Str("Quantity exactly as written, e.g. '10-15 MT'."),
                    "unit", S.Enum("Quantity unit.", units),
                    "incoterm", S.Enum("Delivery basis (FOR/Delivered India = DAP or DDP; write 'Not stated' if unclear).", incoterms),
                    "delivery?", S.Str("Delivery place and timing as written, e.g. 'Delivered/FOR India, immediate'."),
                    "packing?", S.Str("Packing as written."),
                    "payment_terms?", S.Str("Payment terms as written, e.g. '45 days credit'."),
                    "target_price?", S.Num("Price the buyer stated, only if a number is in their email."),
                    "currency?", S.Str("ISO currency, default USD."),
                    "confidential", S.Bool("True if the buyer asked for confidentiality."),
                    "company?", S.Str("Buyer company as written."),
                    "contact_name?", S.Str("Buyer person's name as written.")),
                Run = SaveRequirement
            };

            yield return new Tool
            {
                Name = "start_sourcing",
                Description = "Buyer thread: when the requirement has commodity, quantity and delivery basis, send masked enquiries (as drafts) to the best-matching seller leads. Once per requirement unless more=true.",
                Writes = true,
                Parameters = S.Obj(null, "more?", S.Bool("True to contact further sellers after an earlier round.")),
                Run = StartSourcing
            };

            yield return new Tool
            {
                Name = "save_seller_lead",
                Description = "Unsolicited offer to sell (a thread that is not one of our enquiries): keep the seller as a lead. If the email gives a price AND a quantity, pass price, quantity and unit: the offer is then saved as a seller lot and offered to our buyers (same as save_seller_lot).",
                Writes = true,
                Parameters = S.Obj(null,
                    "commodity", S.Str("What they offer, as written."),
                    "details?", S.Str("Specification, quantity, origin, port, terms as written."),
                    "price?", S.Num("Seller's price per unit, if written."),
                    "quantity?", S.Num("Quantity available, if written."),
                    "unit?", S.Enum("Quantity unit.", units),
                    "incoterm?", S.Enum("Delivery basis.", DeskChoice.Incoterms),
                    "named_place?", S.Str("Port or place of the delivery basis."),
                    "origin?", S.Str("Country of origin."),
                    "company?", S.Str("Seller company."),
                    "contact_name?", S.Str("Person."),
                    "country?", S.Str("Country of the seller or the material."),
                    "window_hours?", S.Num("How long buyers may bid, in hours, ONLY if the seller wrote it (e.g. 'valid 24 hours' = 24, '2 days' = 48)."),
                    "open_ended?", S.Bool("True ONLY if the seller wrote that the offer stays open until sold / no deadline: buyers' offers then go to the seller as they come."),
                    "website?", S.Str("Website if written.")),
                Run = SaveSellerLead
            };

            yield return new Tool
            {
                Name = "buyer_declines_lot",
                Description = "Buyer thread: the buyer says they are not interested in a lot we offered (a supplier quote with offers_close). Closes their bid so the desk does not wait for them.",
                Writes = true,
                Parameters = S.Obj(null, "reason?", S.Str("Their reason, as written.")),
                Run = BuyerDeclinesLot
            };

            yield return new Tool
            {
                Name = "save_seller_lot",
                Description = "A seller offers stock with a price AND a quantity (an offer to sell, not an answer to our enquiry): save it as a lot. A new lot is offered (masked) to matching buyers. " +
                              "Timed lot (window_hours, or the default): buyers bid until the deadline and the highest prices win. Open-ended lot: each buyer's offer goes to the seller, who decides. " +
                              "Call again in the same thread when the seller changes price (a counter: it goes to the buyers), quantity or window.",
                Writes = true,
                Parameters = S.Obj(null,
                    "commodity", S.Str("Material as written."),
                    "specification?", S.Str("Grade, purity, sizing as written (one item per line)."),
                    "quantity", S.Num("Quantity available, as a number."),
                    "unit", S.Enum("Quantity unit.", units),
                    "price", S.Num("Seller's price per unit as written in their email."),
                    "currency?", S.Str("ISO currency, default USD."),
                    "incoterm?", S.Enum("Delivery basis.", DeskChoice.Incoterms),
                    "named_place?", S.Str("Port or place of the delivery basis."),
                    "origin?", S.Str("Country of origin."),
                    "valid_until?", S.Str("Offer validity as an ISO date, if written."),
                    "window_hours?", S.Num("How long buyers may bid, in hours, ONLY if the seller wrote it (e.g. 'valid 24 hours' = 24, '2 days' = 48)."),
                    "open_ended?", S.Bool("True ONLY if the seller wrote that the offer stays open until sold / no deadline: buyers' offers then go to the seller as they come."),
                    "terms_text?", S.Str("Payment, lead time, packing and other terms as written."),
                    "company?", S.Str("Seller company."),
                    "contact_name?", S.Str("Person.")),
                Run = SaveSellerLot
            };

            yield return new Tool
            {
                Name = "seller_closes_lot",
                Description = "Seller lot thread: the seller accepts one of our bids (give the price per unit they accept, as in our bid email or theirs) or withdraws the lot. " +
                              "The best bids at or above that price win while quantity lasts; Confirm deal tasks and the confirmation drafts are created for you. A NEW price from the seller is a counter: use save_seller_lot instead.",
                Writes = true,
                Parameters = S.Obj(null,
                    "accept_price?", S.Num("Price per unit the seller accepts (one of our bids), as written in the thread."),
                    "withdraw?", S.Bool("True if the seller withdraws the lot (sold elsewhere, no longer available)."),
                    "evidence", S.Str("The seller's words, quoted.")),
                Run = SellerClosesLot
            };

            yield return new Tool
            {
                Name = "save_seller_quote",
                Description = "Seller thread (our enquiry): record the seller's quote or counter-offer, or that they decline / withdraw. The price must be written in their email. " +
                              "Sellers take turns: the first to quote is negotiated with the buyer, later ones are queued (the result says which).",
                Writes = true,
                Parameters = S.Obj(null,
                    "declined", S.Bool("True if the seller cannot or will not supply."),
                    "decline_reason?", S.Str("Why, as written."),
                    "price?", S.Num("Price per unit as written."),
                    "currency?", S.Str("ISO currency, default USD."),
                    "quantity?", S.Num("Quantity offered."),
                    "unit?", S.Enum("Unit.", units),
                    "incoterm?", S.Enum("Delivery basis of the price.", incoterms),
                    "named_place?", S.Str("Port or place of the delivery basis."),
                    "origin?", S.Str("Origin country or region."),
                    "lead_time?", S.Str("Lead time or shipment date as written."),
                    "payment_terms?", S.Enum("Closest payment term.", payments),
                    "terms_text?", S.Str("All other terms as written (payment, packing, inspection, validity)."),
                    "valid_until?", S.Str("YYYY-MM-DD if a validity date is written.")),
                Run = SaveSellerQuote
            };

            yield return new Tool
            {
                Name = "quote_to_buyer",
                Description = "Our price to the buyer for one seller quote (the system adds our margin). Returns the price to use in the buyer email and the buyer thread to draft in.",
                Writes = true,
                Parameters = S.Obj(null, "offer_id", S.Str("Seller quote id (offer_id from CONTEXT or save_seller_quote).")),
                Run = QuoteToBuyer
            };

            yield return new Tool
            {
                Name = "record_buyer_price",
                Description = "Buyer thread: the buyer proposes the price they will pay (must be written in their email). The system turns it into our firm bid to the seller and tells you the seller thread to draft in.",
                Writes = true,
                Parameters = S.Obj(null,
                    "price", S.Num("Price per unit the buyer proposes, as written."),
                    "offer_id?", S.Str("Which supplier quote it answers; default the best one."),
                    "quantity?", S.Num("Quantity the buyer wants, if they wrote one (matters for lots).")),
                Run = RecordBuyerPrice
            };

            yield return new Tool
            {
                Name = "buyer_accepts",
                Description = "Buyer thread: the buyer clearly accepts our quoted price. Opens a 'Confirm deal' task; a person confirms before anything is binding.",
                Writes = true,
                Parameters = S.Obj(null, "offer_id?", S.Str("The supplier quote behind our price; default the one we last quoted."), "evidence", S.Str("The buyer's words of acceptance, quoted."),
                                   "quantity?", S.Num("Quantity the buyer wants, if they wrote one (matters for lots).")),
                Run = BuyerAccepts
            };

            yield return new Tool
            {
                Name = "buyer_rejects_offer",
                Description = "Buyer thread: the buyer turns down our current offer outright (not a counter-offer with a price: use record_buyer_price for that). " +
                              "The deal with the current supplier closes; if the buyer is still looking, the next supplier who quoted comes up and its offer is drafted to the buyer automatically.",
                Writes = true,
                Parameters = S.Obj(null,
                    "still_looking", S.Bool("True if the buyer still wants the material (e.g. 'too expensive', 'not this offer'); false if they no longer need it."),
                    "reason", S.Str("The buyer's reason, as written.")),
                Run = BuyerRejectsOffer
            };

            yield return new Tool
            {
                Name = "seller_accepts_bid",
                Description = "Seller thread: the seller clearly accepts our bid. Opens a 'Confirm deal' task; a person confirms before anything is binding.",
                Writes = true,
                Parameters = S.Obj(null, "evidence", S.Str("The seller's words of acceptance, quoted.")),
                Run = SellerAcceptsBid
            };

            yield return new Tool
            {
                Name = "report_signed_contract",
                Description = "The buyer or seller says they signed the contract (usually with an attachment). Opens a task for a person to check the signature and mark the contract signed.",
                Writes = true,
                Parameters = S.Obj(null, "evidence", S.Str("What they wrote and which file is attached.")),
                Run = ReportSigned
            };

            yield return new Tool
            {
                Name = "save_kyb_documents",
                Description = "The counterparty sends company documents for our KYB (company registration / incorporation certificate, GST, IEC, PAN, director or authorised signatory ID, " +
                              "UBO / shareholder list, bank reference, export licence). Files them on their company and starts the KYB check; a person approves the result.",
                Writes = true,
                Parameters = S.Obj(null,
                    "documents", S.Arr("Attachments of the latest email that are KYB documents.", S.Obj(null,
                        "file", S.Str("File name exactly as in latest_email.attachments."),
                        "type", S.Enum("What the document is.", KybTypes))),
                    "company?", S.Str("Legal company name as written in the documents or email."),
                    "registration_number?", S.Str("Company registration / CIN / GST / IEC number as written, if stated.")),
                Run = SaveKybDocuments
            };

            yield return new Tool
            {
                Name = "record_shipment_update",
                Description = "After signing: the seller (or buyer) reports loading, sailing, arrival or delivery. Updates the deal's shipment; the update to the OTHER side is drafted for you " +
                              "(masked). You only draft a short reply in this thread.",
                Writes = true,
                Parameters = S.Obj(null,
                    "status", S.Enum("What happened.", "Loading", "In Transit", "Arrived", "Delivered"),
                    "bl_number?", S.Str("B/L or AWB number as written."),
                    "etd?", S.Str("Departure date, YYYY-MM-DD, if written."),
                    "eta?", S.Str("Arrival date, YYYY-MM-DD, if written."),
                    "origin_port?", S.Str("Loading port as written."),
                    "destination_port?", S.Str("Discharge port as written."),
                    "evidence", S.Str("Their words, quoted.")),
                Run = RecordShipmentUpdate
            };

            yield return new Tool
            {
                Name = "draft_email",
                Description = "Draft an email for a person to check and send from Gmail. Plain text only, no greeting line needed if replying, no signature (added automatically). Replaces any earlier unsent draft of that thread.",
                Writes = true,
                Parameters = S.Obj(null,
                    "thread", S.Enum("Where: this thread, the buyer's thread, or a seller thread (give thread_id).", "this_thread", "buyer_thread", "seller_thread"),
                    "thread_id?", S.Str("For seller_thread: the seller thread id from CONTEXT or a tool result."),
                    "body", S.Str("The email text, written like an experienced commodity trader: short, specific, polite."),
                    "attach?", S.Enum("Attach our company profile PDF (only when CONTEXT.company_profile_on_file is true and the buyer or seller asked for it).", "company_profile")),
                Run = DraftEmail
            };
        }

        // ---------------------------------------------------------------- requirement

        private static object SaveRequirement(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            var side = Side(conv);
            if (side == DeskChoice.Side.Seller) throw new ToolRefusal("This is a seller thread; save_requirement is for the buyer's thread.");
            var qty = J.Num(a, "quantity");
            if (qty == null || qty <= 0) throw new ToolRefusal("quantity must be a positive number from the email.");
            var target = J.Num(a, "target_price");
            if (target != null && !InThread(ctx, target.Value)) throw new ToolRefusal("target_price " + target + " is not written in the buyer's emails; leave it out.");
            var w = new DeskWriter(ctx.Dv, ctx);

            var accountId = Ref(conv, "gc_counterparty") ?? BuyerAccount(ctx, w, J.Str(a, "company"), J.Str(a, "contact_name"));
            var reqId = Ref(conv, "gc_requirement");
            var commodity = J.Str(a, "commodity");
            var r = reqId == null ? new Entity("gc_buyerrequirement") : new Entity("gc_buyerrequirement", reqId.Value);
            r["gc_name"] = Desk.Cut("Email RFQ: " + commodity + " " + (J.Str(a, "quantity_text") ?? qty.Value.ToString(CultureInfo.InvariantCulture) + " " + J.Str(a, "unit")), 100);
            r["gc_commoditytext"] = Desk.Cut(commodity, 400);
            if (J.Str(a, "specification") != null) r["gc_specification"] = J.Str(a, "specification");
            r["gc_quantity"] = (decimal)qty.Value;
            var unit = Choice.ValueOf(DeskChoice.Units, J.Str(a, "unit"));
            if (unit >= 0) r["gc_quantityunit"] = new OptionSetValue(unit);
            var inc = Choice.ValueOf(DeskChoice.Incoterms, J.Str(a, "incoterm"));
            if (inc >= 0) r["gc_incoterm"] = new OptionSetValue(inc);
            if (J.Str(a, "delivery") != null) r["gc_deliverytext"] = Desk.Cut(J.Str(a, "delivery"), 1000);
            if (J.Str(a, "packing") != null) r["gc_packaging"] = Desk.Cut(J.Str(a, "packing"), 200);
            if (J.Str(a, "payment_terms") != null) r["gc_paymenttext"] = Desk.Cut(J.Str(a, "payment_terms"), 1000);
            if (target != null) r["gc_targetprice"] = (decimal)target.Value;
            r["gc_currency"] = (J.Str(a, "currency") ?? "USD").ToUpperInvariant();
            r["gc_confidential"] = J.Bool(a, "confidential");
            if (accountId != null) r["gc_buyer"] = new EntityReference("account", accountId.Value);
            var commodityId = MatchCommodity(ctx, commodity);
            if (commodityId != null) r["gc_commodity"] = new EntityReference("gc_commodity", commodityId.Value);
            if (reqId == null)
            {
                r["gc_source"] = new OptionSetValue(DeskChoice.RequirementSource.Email);
                r["gc_status"] = new OptionSetValue(DeskChoice.Requirement.Open);
                r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Qualifying);
                reqId = ctx.Create(r, "create_requirement", J.Obj("commodity", commodity, "quantity", qty));
            }
            else ctx.Update(r, "update_requirement", J.Obj("commodity", commodity, "quantity", qty));

            var c = new Entity("gc_conversation", conv.Id);
            c["gc_side"] = new OptionSetValue(DeskChoice.Side.Buyer);
            if (!ctx.DryRun) c["gc_requirement"] = new EntityReference("gc_buyerrequirement", reqId.Value);
            if (accountId != null) c["gc_counterparty"] = new EntityReference("account", accountId.Value);
            ctx.Update(c, "link_buyer_thread");
            conv["gc_side"] = new OptionSetValue(DeskChoice.Side.Buyer);
            if (!ctx.DryRun) conv["gc_requirement"] = new EntityReference("gc_buyerrequirement", reqId.Value);

            var missing = new List<object>();
            if (inc < 0 && string.IsNullOrWhiteSpace(J.Str(a, "delivery"))) missing.Add("delivery basis and place");
            if (string.IsNullOrWhiteSpace(J.Str(a, "specification"))) missing.Add("specification (grade / purity)");
            return J.Obj("ok", true, "requirement_id", ctx.DryRun ? null : reqId.ToString(), "missing_for_sourcing", missing,
                         "ready_to_source", missing.Count == 0 || (inc >= 0 || !string.IsNullOrWhiteSpace(J.Str(a, "delivery"))));
        }

        private static Guid? BuyerAccount(AgentContext ctx, DeskWriter w, string company, string person)
        {
            var msg = Latest(ctx);
            var from = msg == null ? null : msg.GetAttributeValue<string>("gc_fromaddress");
            if (string.IsNullOrWhiteSpace(from)) return null;
            var contact = ctx.Dv.Query("contact", new[] { "contactid", "parentcustomerid" }, 1, "emailaddress1", ConditionOperator.Equal, from).FirstOrDefault();
            var parent = contact == null ? null : contact.GetAttributeValue<EntityReference>("parentcustomerid");
            if (parent != null && parent.LogicalName == "account") return parent.Id;
            var domain = from.Substring(from.IndexOf('@') + 1);
            var name = !string.IsNullOrWhiteSpace(company) ? company : (msg.GetAttributeValue<string>("gc_senderlabel") ?? domain);
            var acc = new Entity("account");
            acc["name"] = Desk.Cut(name, 160);
            acc["emailaddress1"] = from;
            var accountId = ctx.Create(acc, "create_buyer_account", J.Obj("name", name));
            if (contact == null)
            {
                var c = new Entity("contact");
                var full = (person ?? (msg.GetAttributeValue<string>("gc_senderlabel") ?? "").Split('<')[0]).Trim().Trim('"');
                var parts = full.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                c["firstname"] = parts.Length > 1 ? parts[0] : null;
                c["lastname"] = parts.Length > 1 ? parts[1] : (parts.Length == 1 ? parts[0] : from);
                c["emailaddress1"] = from;
                if (!ctx.DryRun) c["parentcustomerid"] = new EntityReference("account", accountId);
                ctx.Create(c, "create_buyer_contact", J.Obj("email", from));
            }
            return ctx.DryRun ? (Guid?)null : accountId;
        }

        private static Guid? MatchCommodity(AgentContext ctx, string text)
        {
            var tokens = Desk.Tokens(text);
            if (tokens.Count == 0) return null;
            var q = new QueryExpression("gc_commodity") { ColumnSet = new ColumnSet("gc_name"), TopCount = 1 };
            q.Criteria.AddCondition("gc_name", ConditionOperator.Like, "%" + tokens[0] + "%");
            var e = ctx.Dv.System.RetrieveMultiple(q).Entities.FirstOrDefault();
            return e == null ? (Guid?)null : e.Id;
        }

        private static object StartSourcing(AgentContext ctx, Dictionary<string, object> a)
        {
            var reqId = Ref(ctx.Subject, "gc_requirement");
            if (Side(ctx.Subject) != DeskChoice.Side.Buyer || reqId == null) throw new ToolRefusal("Save the buyer's requirement first (save_requirement).");
            var req = ctx.Dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_quantity", "gc_commoditytext", "gc_deskstage");
            if (req.GetAttributeValue<decimal?>("gc_quantity") == null) throw new ToolRefusal("The requirement has no quantity yet; ask the buyer.");
            var invites = ctx.Dv.Query("gc_rfqinvite", new[] { "gc_rfqinviteid" }, 50, "gc_requirement", ConditionOperator.Equal, reqId.Value).Count;
            if (invites > 0 && !J.Bool(a, "more")) throw new ToolRefusal(invites + " sellers were already asked. Use more=true only if the buyer needs more options.");
            if (ctx.DryRun) return J.Obj("ok", true, "dry_run", true, "note", "Sourcing would contact the best-matching seller leads.");
            var result = Desk.Source(new DeskWriter(ctx.Dv, ctx), reqId.Value);
            // The buyer thread never sees who the sellers are.
            return J.Obj("ok", true, "sellers_contacted", J.Get(result, "invited"), "matching_leads", J.Get(result, "matched_leads"),
                         "leads_without_email", J.Get(result, "leads_without_email"),
                         "note", "Enquiry drafts were created in each seller thread for a person to send.");
        }

        private static object SaveSellerLead(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            if (Ref(conv, "gc_requirement") != null) throw new ToolRefusal("This thread belongs to an enquiry; use save_seller_quote.");
            var msg = Latest(ctx);
            if (!J.Bool(a, "from_lot"))
            {
                if (J.Num(a, "price") != null && J.Num(a, "quantity") != null)
                {
                    if (J.Str(a, "unit") == null) a["unit"] = "MT";
                    return SaveSellerLot(ctx, a);
                }
                if (msg != null && Desk.HasPriceAndQuantity(msg.GetAttributeValue<string>("gc_text")))
                    throw new ToolRefusal("The email states a price and a quantity: call again with price, quantity and unit (and incoterm, named_place, origin) so the offer is saved as a lot and offered to our buyers.");
            }
            var from = msg == null ? null : msg.GetAttributeValue<string>("gc_fromaddress");
            var existing = string.IsNullOrEmpty(from) ? null : ctx.Dv.Query("gc_lead", new[] { "gc_leadid", "gc_commodities" }, 1, "gc_email", ConditionOperator.Equal, from).FirstOrDefault();
            var products = (J.Str(a, "commodity") + (J.Str(a, "details") == null ? "" : " — " + J.Str(a, "details"))).Trim();
            if (existing != null)
            {
                var u = new Entity("gc_lead", existing.Id);
                u["gc_commodities"] = Desk.Cut((existing.GetAttributeValue<string>("gc_commodities") ?? "") + "\n" + products, 100000);
                u["gc_lastseen"] = DateTime.UtcNow;
                ctx.Update(u, "update_lead");
                return J.Obj("ok", true, "lead_id", existing.Id.ToString(), "note", "Known lead; offer added.");
            }
            var l = new Entity("gc_lead");
            l["gc_name"] = Desk.Cut(J.Str(a, "company") ?? (msg == null ? null : msg.GetAttributeValue<string>("gc_senderlabel")) ?? from ?? "Seller", 200);
            l["gc_role"] = new OptionSetValue(DeskChoice.LeadRole.Seller);
            l["gc_commodities"] = products;
            l["gc_email"] = from;
            l["gc_contactname"] = Desk.Cut(J.Str(a, "contact_name"), 200);
            l["gc_country"] = Desk.Cut(J.Str(a, "country"), 100);
            l["gc_website"] = Desk.Cut(J.Str(a, "website"), 400);
            l["gc_source"] = new OptionSetValue(DeskChoice.LeadSource.Email);
            l["gc_sourceref"] = Desk.Cut("Email: " + (msg == null ? "" : msg.GetAttributeValue<string>("gc_subject")), 400);
            l["gc_lastseen"] = DateTime.UtcNow;
            l["gc_status"] = new OptionSetValue(DeskChoice.LeadStatus.Responded);
            var id = ctx.Create(l, "create_lead", J.Obj("commodity", J.Str(a, "commodity")));
            var c = new Entity("gc_conversation", conv.Id);
            c["gc_side"] = new OptionSetValue(DeskChoice.Side.Seller);
            ctx.Update(c, "mark_seller_thread");
            return J.Obj("ok", true, "lead_id", ctx.DryRun ? null : id.ToString());
        }

        private static object BuyerDeclinesLot(AgentContext ctx, Dictionary<string, object> a)
        {
            var reqId = Ref(ctx.Subject, "gc_requirement");
            if (Side(ctx.Subject) != DeskChoice.Side.Buyer || reqId == null) throw new ToolRefusal("buyer_declines_lot is for a buyer thread.");
            foreach (var d in ctx.Dv.Query("gc_deal", new[] { "gc_sellerlot", "gc_stage" }, 50, "gc_requirement", ConditionOperator.Equal, reqId.Value))
            {
                var lot = Ref(d, "gc_sellerlot") == null ? null : Lots.Get(ctx.Dv, Ref(d, "gc_sellerlot").Value);
                if (lot == null || Lots.StatusOf(lot) != Lots.Status.Open || Opt(d, "gc_stage") > DeskChoice.DealStage.Negotiation) continue;
                return Lots.Decline(new DeskWriter(ctx.Dv, ctx), lot, d.Id, J.Str(a, "reason"));
            }
            throw new ToolRefusal("This buyer has no open lot offer to decline.");
        }

        private static object SaveSellerLot(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            if (Ref(conv, "gc_requirement") != null) throw new ToolRefusal("This thread answers one of our enquiries; use save_seller_quote.");
            var price = J.Num(a, "price");
            if (price == null || price <= 0) throw new ToolRefusal("A lot needs the seller's price per unit; without it use save_seller_lead and ask for the price.");
            if (!InThread(ctx, price.Value)) throw new ToolRefusal("Price " + price + " is not written in the seller's emails. Record only prices they wrote.");
            var qty = J.Num(a, "quantity");
            if (qty == null || qty <= 0) throw new ToolRefusal("A lot needs the quantity available; without it use save_seller_lead and ask for it.");
            var msg = Latest(ctx);
            var from = (msg == null ? null : msg.GetAttributeValue<string>("gc_fromaddress") ?? "").Trim().ToLowerInvariant();
            var w = new DeskWriter(ctx.Dv, ctx);
            var sellerId = Ref(conv, "gc_counterparty");
            if (sellerId == null)
            {
                if (string.IsNullOrEmpty(from)) throw new ToolRefusal("The seller's email address is not known.");
                var lead = ctx.Dv.Query("gc_lead", new[] { "gc_leadid", "gc_account", "gc_website", "gc_phone" }, 1, "gc_email", ConditionOperator.Equal, from).FirstOrDefault();
                if (lead == null)
                {
                    SaveSellerLead(ctx, J.Obj("from_lot", true, "commodity", J.Str(a, "commodity"), "details", "Lot: " + qty + " " + J.Str(a, "unit") + " at " + price,
                                              "company", J.Str(a, "company"), "contact_name", J.Str(a, "contact_name"), "country", J.Str(a, "origin")));
                    lead = ctx.Dv.Query("gc_lead", new[] { "gc_leadid", "gc_account", "gc_website", "gc_phone" }, 1, "gc_email", ConditionOperator.Equal, from).FirstOrDefault();
                }
                if (lead == null) return J.Obj("ok", true, "note", "Dry run: the seller lead and the lot would be created.");
                sellerId = Desk.SellerAccount(w, lead, from, J.Str(a, "company") ?? (msg.GetAttributeValue<string>("gc_senderlabel") ?? from));
            }
            if (ctx.DryRun) return J.Obj("ok", true, "note", "Dry run: the lot would be saved and offered to matching buyers.");
            var result = Lots.Save(w, conv, sellerId.Value, a);
            LeadResponded(ctx, sellerId);
            return result;
        }

        private static object SellerClosesLot(AgentContext ctx, Dictionary<string, object> a)
        {
            var lotId = Ref(ctx.Subject, "gc_sellerlot");
            if (Side(ctx.Subject) != DeskChoice.Side.Seller || lotId == null) throw new ToolRefusal("seller_closes_lot is for the seller's lot thread.");
            var lot = Lots.Get(ctx.Dv, lotId.Value);
            var price = J.Num(a, "accept_price");
            if (price != null && !InThread(ctx, price.Value) && !InOurEmails(ctx, (decimal)price.Value))
                throw new ToolRefusal("Price " + price + " is not in this thread. Give the bid price the seller accepts, as written.");
            return Lots.SellerDecides(new DeskWriter(ctx.Dv, ctx), lot, (decimal?)price, J.Bool(a, "withdraw"), J.Str(a, "evidence"));
        }

        /// <summary>A price we wrote in this thread (our bids to a lot seller are in our own emails, not theirs).</summary>
        private static bool InOurEmails(AgentContext ctx, decimal price)
        {
            var numbers = new HashSet<string>();
            foreach (var m in ctx.Dv.Query("gc_message", new[] { "gc_text" }, 50, "gc_conversation", ConditionOperator.Equal, ctx.Subject.Id))
                MessageValidator.CollectNumbers(m.GetAttributeValue<string>("gc_text"), numbers);
            return MessageValidator.Normalise(price.ToString(CultureInfo.InvariantCulture)).Any(numbers.Contains);
        }

        // ---------------------------------------------------------------- seller side

        private static object SaveSellerQuote(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            var dealId = Ref(conv, "gc_deal");
            var inviteId = Ref(conv, "gc_invite");
            if (Side(conv) != DeskChoice.Side.Seller || dealId == null) throw new ToolRefusal("save_seller_quote is for a seller thread of one of our enquiries.");
            var deal = ctx.Dv.Retrieve("gc_deal", dealId.Value, "gc_seller", "gc_buyer", "gc_quantity", "gc_quantityunit", "gc_currency", "gc_requirement", "gc_stage");
            var sellerId = Ref(deal, "gc_seller");
            if (J.Bool(a, "declined"))
            {
                if (inviteId != null)
                {
                    var inv = new Entity("gc_rfqinvite", inviteId.Value);
                    inv["gc_status"] = new OptionSetValue(DeskChoice.Invite.Declined);
                    inv["gc_respondedon"] = DateTime.UtcNow;
                    inv["gc_declinereason"] = Desk.Cut(J.Str(a, "decline_reason"), 4000);
                    ctx.Update(inv, "seller_declined");
                }
                LeadResponded(ctx, sellerId);
                var reqOfDeal = Ref(deal, "gc_requirement");
                if (reqOfDeal != null && Desk.ActiveDeal(ctx.Dv, reqOfDeal.Value) == dealId.Value)
                {
                    // The seller we were negotiating with walks away: the next queued seller comes up for the buyer.
                    var moved = Desk.Break(new DeskWriter(ctx.Dv, ctx), reqOfDeal.Value, "the seller withdrew" + (J.Str(a, "decline_reason") == null ? "" : ": " + Desk.Cut(J.Str(a, "decline_reason"), 200)), true, false);
                    return J.Obj("ok", true, "declined", true, "deal_off", true, "next", moved,
                                 "note", "No draft to this seller is needed (a short thank-you is optional). " + J.Str(moved, "note"));
                }
                return J.Obj("ok", true, "declined", true);
            }
            var price = J.Num(a, "price");
            if (price == null || price <= 0) throw new ToolRefusal("A quote needs the price per unit written in the seller's email (or set declined=true).");
            if (!InThread(ctx, price.Value)) throw new ToolRefusal("Price " + price + " is not written in the seller's emails. Record only prices they wrote.");
            var round = ctx.Dv.Query("gc_offer", new[] { "gc_offerid" }, 50, "gc_deal", ConditionOperator.Equal, dealId.Value).Count + 1;
            var lastBid = OurBids(ctx, dealId.Value, Ref(deal, "gc_buyer")).FirstOrDefault();

            var o = new Entity("gc_offer");
            o["gc_name"] = Desk.Cut("Seller quote " + round + ": " + price.Value.ToString(CultureInfo.InvariantCulture) + " " + (J.Str(a, "currency") ?? "USD"), 100);
            o["gc_deal"] = new EntityReference("gc_deal", dealId.Value);
            if (sellerId != null) o["gc_fromparty"] = new EntityReference("account", sellerId.Value);
            o["gc_price"] = (decimal)price.Value;
            o["gc_currency"] = (J.Str(a, "currency") ?? deal.GetAttributeValue<string>("gc_currency") ?? "USD").ToUpperInvariant();
            o["gc_quantity"] = J.Num(a, "quantity") != null ? (decimal)J.Num(a, "quantity").Value : deal.GetAttributeValue<decimal?>("gc_quantity");
            var inc = Choice.ValueOf(DeskChoice.Incoterms, J.Str(a, "incoterm"));
            if (inc >= 0) o["gc_incoterm"] = new OptionSetValue(inc);
            if (J.Str(a, "named_place") != null) o["gc_namedplace"] = Desk.Cut(J.Str(a, "named_place"), 200);
            var pay = Choice.ValueOf(DeskChoice.PaymentTerms, J.Str(a, "payment_terms"));
            if (pay >= 0) o["gc_paymentterms"] = new OptionSetValue(pay);
            o["gc_status"] = new OptionSetValue(DeskChoice.Offer.Open);
            o["gc_round"] = round;
            if (lastBid != null) o["gc_parentoffer"] = new EntityReference("gc_offer", lastBid.Id);
            DateTime valid;
            if (DateTime.TryParse(J.Str(a, "valid_until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out valid)) o["gc_validuntil"] = valid;
            var terms = new List<string>();
            if (J.Str(a, "origin") != null) terms.Add("Origin: " + J.Str(a, "origin"));
            if (J.Str(a, "lead_time") != null) terms.Add("Lead time: " + J.Str(a, "lead_time"));
            if (J.Str(a, "terms_text") != null) terms.Add(J.Str(a, "terms_text"));
            o["gc_terms"] = string.Join("\n", terms);
            var offerId = ctx.Create(o, "save_seller_quote", J.Obj("price", price, "round", round));
            if (lastBid != null)
            {
                var countered = new Entity("gc_offer", lastBid.Id);
                countered["gc_status"] = new OptionSetValue(DeskChoice.Offer.Countered);
                ctx.Update(countered, "our_bid_countered");
            }
            if (inviteId != null)
            {
                var inv = new Entity("gc_rfqinvite", inviteId.Value);
                inv["gc_status"] = new OptionSetValue(DeskChoice.Invite.Accepted);
                inv["gc_respondedon"] = DateTime.UtcNow;
                ctx.Update(inv, "seller_responded");
            }
            LeadResponded(ctx, sellerId);
            ReleaseAttachments(ctx, dealId.Value);
            var reqId = Ref(deal, "gc_requirement");
            var turn = reqId == null || ctx.DryRun ? "active" : Desk.TakeTurn(new DeskWriter(ctx.Dv, ctx), reqId.Value, dealId.Value);
            if (turn == "active") AdvanceStage(ctx, reqId, DeskChoice.Stage.Quoted);
            return J.Obj("ok", true, "offer_id", ctx.DryRun ? null : offerId.ToString(), "round", round, "turn", turn,
                         "next", NextAfterQuote(ctx, reqId, offerId, round > 1 || lastBid != null, turn));
        }

        /// <summary>What to do after a seller quote: quote the buyer (active seller), or thank and wait (queued seller).</summary>
        private static string NextAfterQuote(AgentContext ctx, Guid? reqId, Guid offerId, bool counter, string turn)
        {
            if (reqId == null) return "Draft a short thank-you to the seller if useful.";
            if (turn == "queued")
            {
                var position = Desk.Queue(ctx.Dv, reqId.Value).FindIndex(d => d.Id == Ref(ctx.Subject, "gc_deal")) + 1;
                return "QUEUED (position " + Math.Max(1, position) + "): the buyer is already negotiating with another supplier who answered first. Do NOT quote the buyer. " +
                       "Draft a short thank-you to this seller: we have noted their offer and will come back to them. Never mention other suppliers or buyers.";
            }
            return (counter ? "This is the active supplier's counter-offer" : "This supplier answered first and is now the ACTIVE supplier for this buyer") +
                   ": call quote_to_buyer with offer_id " + (ctx.DryRun ? "(this quote)" : offerId.ToString()) + ", then draft our offer to the buyer thread. A short thank-you to the seller is optional.";
        }

        private static object QuoteToBuyer(AgentContext ctx, Dictionary<string, object> a)
        {
            var offer = SellerOffer(ctx, J.Str(a, "offer_id"));
            var deal = ctx.Dv.Retrieve("gc_deal", Ref(offer, "gc_deal").Value, "gc_requirement", "gc_quantityunit");
            var reqId = Ref(deal, "gc_requirement");
            if (reqId != null && Lots.OfDeal(ctx.Dv, deal.Id) == null && Desk.ActiveDeal(ctx.Dv, reqId.Value) != deal.Id)
                throw new ToolRefusal("This supplier is queued: the buyer is negotiating with the supplier who answered first. Do not quote the buyer from this offer.");
            var ours = Desk.PriceToBuyer(offer.GetAttributeValue<decimal>("gc_price"), Desk.Margin(ctx.Dv));
            if (reqId != null)
            {
                var r = new Entity("gc_buyerrequirement", reqId.Value);
                r["gc_ourprice"] = ours;
                ctx.Update(r, "our_price_to_buyer", J.Obj("price", ours));
                AdvanceStage(ctx, reqId, DeskChoice.Stage.Quoted);
            }
            var buyerThread = reqId == null ? null : BuyerThread(ctx, reqId.Value);
            var result = J.Obj("our_price_to_buyer", ours, "currency", offer.GetAttributeValue<string>("gc_currency") ?? "USD",
                               "quantity", offer.GetAttributeValue<decimal?>("gc_quantity"), "unit", Desk.UnitLabel(deal.GetAttributeValue<OptionSetValue>("gc_quantityunit")),
                               "incoterm", Desk.Label(DeskChoice.Incoterms, offer.GetAttributeValue<OptionSetValue>("gc_incoterm")),
                               "named_place", offer.GetAttributeValue<string>("gc_namedplace"),
                               "terms", Desk.SafeTerms(offer.GetAttributeValue<string>("gc_terms"), Desk.SellerName(ctx.Dv, deal.Id), offer.GetAttributeValue<decimal?>("gc_price")),
                               "buyer_thread", buyerThread == null ? null : buyerThread.Value.ToString(),
                               "rule", "Quote ONLY our_price_to_buyer to the buyer. Never mention the seller, their price or our margin.");
            ctx.Remember(result);
            return result;
        }

        private static object SellerAcceptsBid(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            var dealId = Ref(conv, "gc_deal");
            var lotId = Ref(conv, "gc_sellerlot");
            if (Side(conv) == DeskChoice.Side.Seller && dealId == null && lotId != null)
            {
                // A lot seller accepting "our bid": unambiguous only when one price is on the table.
                var prices = Lots.OpenBids(ctx.Dv, lotId.Value).Select(b => b.GetAttributeValue<decimal>("gc_price")).Distinct().ToList();
                if (prices.Count != 1)
                    throw new ToolRefusal(prices.Count == 0 ? "There is no open bid of ours on this lot." : "Several bids are open (" + string.Join(", ", prices.Select(Desk.Num)) + "): use seller_closes_lot with the accept_price the seller accepts.");
                return Lots.SellerDecides(new DeskWriter(ctx.Dv, ctx), Lots.Get(ctx.Dv, lotId.Value), prices[0], false, J.Str(a, "evidence"));
            }
            if (Side(conv) != DeskChoice.Side.Seller || dealId == null) throw new ToolRefusal("seller_accepts_bid is for a seller thread.");
            var deal = ctx.Dv.Retrieve("gc_deal", dealId.Value, "gc_buyer", "gc_requirement", "gc_name");
            var bid = OurBids(ctx, dealId.Value, Ref(deal, "gc_buyer")).FirstOrDefault(b => Opt(b, "gc_status") == DeskChoice.Offer.Open);
            if (bid == null) throw new ToolRefusal("There is no open bid of ours on this deal to accept.");
            var req = ctx.Dv.Retrieve("gc_buyerrequirement", Ref(deal, "gc_requirement") ?? Guid.Empty, "gc_targetprice");
            var buyerPrice = req == null ? null : req.GetAttributeValue<decimal?>("gc_targetprice");
            return ConfirmTask(ctx, bid, buyerPrice ?? Desk.PriceToBuyer(bid.GetAttributeValue<decimal>("gc_price"), Desk.Margin(ctx.Dv)), dealId.Value,
                               deal.GetAttributeValue<string>("gc_name"), "Seller accepted our bid", J.Str(a, "evidence"));
        }

        // ---------------------------------------------------------------- buyer side

        private static object RecordBuyerPrice(AgentContext ctx, Dictionary<string, object> a)
        {
            var reqId = Ref(ctx.Subject, "gc_requirement");
            if (Side(ctx.Subject) != DeskChoice.Side.Buyer || reqId == null) throw new ToolRefusal("record_buyer_price is for the buyer's thread with a saved requirement.");
            var p = J.Num(a, "price");
            if (p == null || p <= 0) throw new ToolRefusal("price must be the buyer's proposed price per unit.");
            if (!InLatest(ctx, p.Value)) throw new ToolRefusal("Price " + p + " is not in the buyer's latest email. Record only what they wrote.");
            var offer = string.IsNullOrEmpty(J.Str(a, "offer_id")) ? BestQuote(ctx, reqId.Value) : SellerOffer(ctx, J.Str(a, "offer_id"));
            if (offer == null) throw new ToolRefusal("No supplier quote yet. Tell the buyer we are collecting offers.");
            var margin = Desk.Margin(ctx.Dv);
            var ourQuote = Desk.PriceToBuyer(offer.GetAttributeValue<decimal>("gc_price"), margin);
            var dealId = Ref(offer, "gc_deal").Value;
            // On a lot buyers compete: any price is a bid, including one above our asking price.
            var lot = Lots.OfDeal(ctx.Dv, dealId);
            if (lot != null) return Lots.RecordBid(new DeskWriter(ctx.Dv, ctx), lot, dealId, reqId.Value, (decimal)p.Value, (decimal?)J.Num(a, "quantity"));
            if ((decimal)p.Value >= ourQuote)
                throw new ToolRefusal("The buyer's price is at or above our quoted price (" + ourQuote.ToString(CultureInfo.InvariantCulture) + "): treat it as acceptance (buyer_accepts).");
            var deal = ctx.Dv.Retrieve("gc_deal", dealId, "gc_buyer", "gc_quantity");
            var bid = Desk.BidToSeller((decimal)p.Value, margin);
            var round = ctx.Dv.Query("gc_offer", new[] { "gc_offerid" }, 50, "gc_deal", ConditionOperator.Equal, dealId).Count + 1;
            var o = new Entity("gc_offer");
            o["gc_name"] = Desk.Cut("Our bid " + round + ": " + bid.ToString(CultureInfo.InvariantCulture) + " " + (offer.GetAttributeValue<string>("gc_currency") ?? "USD"), 100);
            o["gc_deal"] = new EntityReference("gc_deal", dealId);
            if (Ref(deal, "gc_buyer") != null) o["gc_fromparty"] = new EntityReference("account", Ref(deal, "gc_buyer").Value);
            o["gc_price"] = bid;
            o["gc_currency"] = offer.GetAttributeValue<string>("gc_currency") ?? "USD";
            o["gc_quantity"] = offer.GetAttributeValue<decimal?>("gc_quantity") ?? deal.GetAttributeValue<decimal?>("gc_quantity");
            if (offer.GetAttributeValue<OptionSetValue>("gc_incoterm") != null) o["gc_incoterm"] = offer.GetAttributeValue<OptionSetValue>("gc_incoterm");
            if (offer.GetAttributeValue<string>("gc_namedplace") != null) o["gc_namedplace"] = offer.GetAttributeValue<string>("gc_namedplace");
            if (offer.GetAttributeValue<OptionSetValue>("gc_paymentterms") != null) o["gc_paymentterms"] = offer.GetAttributeValue<OptionSetValue>("gc_paymentterms");
            o["gc_status"] = new OptionSetValue(DeskChoice.Offer.Open);
            o["gc_round"] = round;
            o["gc_parentoffer"] = new EntityReference("gc_offer", offer.Id);
            o["gc_terms"] = "Our firm bid on behalf of our buyer.";
            var bidId = ctx.Create(o, "our_bid_to_seller", J.Obj("bid", bid, "round", round));
            var countered = new Entity("gc_offer", offer.Id);
            countered["gc_status"] = new OptionSetValue(DeskChoice.Offer.Countered);
            ctx.Update(countered, "seller_quote_countered");
            var r = new Entity("gc_buyerrequirement", reqId.Value);
            r["gc_targetprice"] = (decimal)p.Value;
            ctx.Update(r, "buyer_price", J.Obj("price", p));
            AdvanceStage(ctx, reqId, DeskChoice.Stage.Negotiating);
            var sellerThread = ctx.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_deal", ConditionOperator.Equal, dealId,
                                            "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
            var result = J.Obj("bid_to_seller", bid, "currency", offer.GetAttributeValue<string>("gc_currency") ?? "USD", "offer_id", ctx.DryRun ? null : bidId.ToString(),
                               "seller_thread", sellerThread == null ? null : sellerThread.Id.ToString(),
                               "rule", "Put ONLY bid_to_seller to the seller, as our firm bid. Never mention the buyer, their price or our margin.");
            ctx.Remember(result);
            return result;
        }

        private static object BuyerAccepts(AgentContext ctx, Dictionary<string, object> a)
        {
            var reqId = Ref(ctx.Subject, "gc_requirement");
            if (Side(ctx.Subject) != DeskChoice.Side.Buyer || reqId == null) throw new ToolRefusal("buyer_accepts is for the buyer's thread.");
            var req = ctx.Dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_ourprice");
            var ours = req.GetAttributeValue<decimal?>("gc_ourprice");
            if (ours == null) throw new ToolRefusal("We have not quoted the buyer a price yet.");
            var margin = Desk.Margin(ctx.Dv);
            Entity offer;
            if (!string.IsNullOrEmpty(J.Str(a, "offer_id"))) offer = SellerOffer(ctx, J.Str(a, "offer_id"));
            else offer = SellerQuotes(ctx, reqId.Value).FirstOrDefault(o => Desk.PriceToBuyer(o.GetAttributeValue<decimal>("gc_price"), margin) == ours.Value);
            if (offer == null) throw new ToolRefusal("Could not find the supplier quote behind our price " + ours + "; give offer_id.");
            var deal = ctx.Dv.Retrieve("gc_deal", Ref(offer, "gc_deal").Value, "gc_name");
            var lot = Lots.OfDeal(ctx.Dv, deal.Id);
            if (lot != null)
                return Lots.RecordBid(new DeskWriter(ctx.Dv, ctx), lot, deal.Id, reqId.Value, Desk.PriceToBuyer(offer.GetAttributeValue<decimal>("gc_price"), margin), (decimal?)J.Num(a, "quantity"));
            return ConfirmTask(ctx, offer, Desk.PriceToBuyer(offer.GetAttributeValue<decimal>("gc_price"), margin), deal.Id, deal.GetAttributeValue<string>("gc_name"),
                               "Buyer accepted our price", J.Str(a, "evidence"));
        }

        private static object BuyerRejectsOffer(AgentContext ctx, Dictionary<string, object> a)
        {
            var reqId = Ref(ctx.Subject, "gc_requirement");
            if (Side(ctx.Subject) != DeskChoice.Side.Buyer || reqId == null) throw new ToolRefusal("buyer_rejects_offer is for the buyer's thread.");
            if (Desk.ActiveDeal(ctx.Dv, reqId.Value) == null) throw new ToolRefusal("There is no supplier offer in play for this buyer. If they no longer need the material, say so in your draft.");
            return Desk.Break(new DeskWriter(ctx.Dv, ctx), reqId.Value, "the buyer turned the offer down" + (J.Str(a, "reason") == null ? "" : ": " + Desk.Cut(J.Str(a, "reason"), 200)),
                              J.Bool(a, "still_looking", true), true);
        }

        /// <summary>Acceptance never binds by itself: a Deal Manager confirms, then the Review decisions flow accepts the offer.</summary>
        private static object ConfirmTask(AgentContext ctx, Entity offer, decimal buyerPrice, Guid dealId, string dealName, string what, string evidence)
        {
            var open = ctx.Dv.Query("gc_reviewtask", new[] { "gc_reviewtaskid" }, 1, "gc_deal", ConditionOperator.Equal, dealId,
                                    "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open, "gc_name", ConditionOperator.Like, "Confirm deal%").FirstOrDefault();
            if (open != null) return J.Obj("ok", true, "note", "A 'Confirm deal' task is already open for this deal.", "task_id", open.Id.ToString());
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = Desk.Cut("Confirm deal: " + dealName, 100);
            t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Approval"));
            t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Other"));
            t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Deal Manager"));
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_deal"] = new EntityReference("gc_deal", dealId);
            t["gc_payload"] = Json.Serialize(J.Obj("action", "desk.accept_offer", "offerId", offer.Id.ToString(), "dealId", dealId.ToString(),
                "buyerPrice", buyerPrice, "sellerPrice", offer.GetAttributeValue<decimal>("gc_price"), "currency", offer.GetAttributeValue<string>("gc_currency"),
                "what", what, "evidence", evidence, "agent", ctx.Agent.ApiName, "run", ctx.RunId.ToString(),
                "approve", "Approve = the offer is accepted, the deal moves to Terms Agreed, compliance checks and the contract follow. Send the drafted confirmations from Gmail."));
            var id = ctx.Create(t, "confirm_deal_task", J.Obj("what", what));
            var reqId = Ref(ctx.Dv.Retrieve("gc_deal", dealId, "gc_requirement"), "gc_requirement");
            AdvanceStage(ctx, reqId, DeskChoice.Stage.Agreed);
            return J.Obj("ok", true, "task_id", ctx.DryRun ? null : id.ToString(), "buyer_price", buyerPrice,
                         "note", "A person confirms the deal. Draft the confirmations (buyer: price confirmed, contract and KYB documents follow; seller: confirmed, purchase contract follows).",
                         "buyer_thread", BuyerThreadOf(ctx, reqId), "seller_thread", SellerThreadOf(ctx, dealId));
        }

        private static object ReportSigned(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            Guid? dealId = Ref(conv, "gc_deal");
            if (dealId == null && Ref(conv, "gc_requirement") != null)
            {
                var d = ctx.Dv.Query("gc_deal", new[] { "gc_dealid", "gc_stage" }, 20, "gc_requirement", ConditionOperator.Equal, Ref(conv, "gc_requirement").Value)
                          .FirstOrDefault(x => Opt(x, "gc_stage") >= DeskChoice.DealStage.TermsAgreed && Opt(x, "gc_stage") != DeskChoice.DealStage.Cancelled);
                dealId = d == null ? (Guid?)null : d.Id;
            }
            if (dealId == null) throw new ToolRefusal("No agreed deal on this thread yet.");
            var contract = ctx.Dv.Query("gc_contract", new[] { "gc_contractid", "gc_status", "gc_name" }, 5, "gc_deal", ConditionOperator.Equal, dealId.Value)
                              .FirstOrDefault(c => Dv.Label(c, "gc_status") != "Void");
            if (contract == null) throw new ToolRefusal("There is no contract on this deal yet.");
            var msg = Latest(ctx);
            foreach (var doc in ctx.Dv.Query("gc_document", new[] { "gc_documentid" }, 5, "gc_message", ConditionOperator.Equal, msg.Id))
            {
                var link = new Entity("gc_document", doc.Id);
                link["gc_deal"] = new EntityReference("gc_deal", dealId.Value);
                ctx.Update(link, "link_signed_copy");
            }
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = Desk.Cut("Signed contract received: " + contract.GetAttributeValue<string>("gc_name"), 100);
            t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Approval"));
            t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Other"));
            t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Deal Manager"));
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_deal"] = new EntityReference("gc_deal", dealId.Value);
            t["gc_payload"] = Json.Serialize(J.Obj("action", "desk.contract_signed", "contractId", contract.Id.ToString(), "side", Side(conv) == DeskChoice.Side.Buyer ? "buyer" : "seller",
                "evidence", J.Str(a, "evidence"), "messageId", msg.Id.ToString(),
                "approve", "Approve after checking the signed copy (attached to the deal). When both sides have signed, the contract is set to Signed."));
            var id = ctx.Create(t, "signed_contract_task");
            return J.Obj("ok", true, "task_id", ctx.DryRun ? null : id.ToString());
        }

        // ---------------------------------------------------------------- KYB documents and tracking

        private static readonly string[] KybTypes = { "Incorporation Certificate", "Tax Or Trade Registration", "Id Document", "UBO Declaration", "Shareholder Register",
                                                      "Bank Reference", "Proof Of Funds", "Export Permit", "Mining Licence", "Other" };

        /// <summary>KYB documents from email → filed on the counterparty's company (released from quarantine to Document Intelligence) → KYB agent → Tier upgrade approval.</summary>
        private static object SaveKybDocuments(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            var side = Side(conv);
            var accountId = Ref(conv, "gc_counterparty");
            if (accountId == null)
            {
                var dealId = Ref(conv, "gc_deal") ?? (Ref(conv, "gc_requirement") == null ? null : Desk.ActiveDeal(ctx.Dv, Ref(conv, "gc_requirement").Value));
                var deal = dealId == null ? null : ctx.Dv.Retrieve("gc_deal", dealId.Value, "gc_buyer", "gc_seller");
                accountId = Ref(deal, side == DeskChoice.Side.Seller ? "gc_seller" : "gc_buyer");
            }
            if (accountId == null) throw new ToolRefusal("This thread is not linked to a company yet (no deal or known sender); create_review_task for a Deal Manager to file the documents.");
            var msg = Latest(ctx);
            var attached = ctx.Dv.Query("gc_document", new[] { "gc_documentid", "gc_filename", "gc_parsestatus" }, 10, "gc_message", ConditionOperator.Equal, msg.Id);
            var filed = new List<object>();
            foreach (var d in J.Arr(a, "documents").OfType<Dictionary<string, object>>())
            {
                var file = J.Str(d, "file");
                var doc = attached.FirstOrDefault(x => string.Equals(x.GetAttributeValue<string>("gc_filename"), file, StringComparison.OrdinalIgnoreCase));
                if (doc == null) throw new ToolRefusal("No attachment named '" + file + "' on the latest email. Use the names in latest_email.attachments.");
                var type = J.Str(d, "type");
                var docType = type == "Tax Or Trade Registration" ? "Incorporation Certificate" : type;
                var u = new Entity("gc_document", doc.Id);
                u["gc_account"] = new EntityReference("account", accountId.Value);
                var dt = Choice.ValueOf(Choice.DocTypes, docType);
                if (dt >= 0) u["gc_doctype"] = new OptionSetValue(dt);
                u["gc_name"] = Desk.Cut("KYB: " + type + " (" + file + ")", 200);
                if (Opt(doc, "gc_parsestatus") == MailChoice.DocumentQuarantined) u["gc_parsestatus"] = new OptionSetValue(Choice.ParseStatus.Pending);
                ctx.Update(u, "file_kyb_document", J.Obj("file", file, "type", type));
                filed.Add(file);
            }
            if (filed.Count == 0) throw new ToolRefusal("List the KYB attachments in documents.");
            var acc = ctx.Dv.Retrieve("account", accountId.Value, "name", "gc_partyrole", "gc_registrationnumber");
            var upd = new Entity("account", accountId.Value);
            // The KYB agents run for a company with a party role (seller: Onboarding KYB; buyer: Buyer Verification).
            var roles = (acc.GetAttributeValue<OptionSetValueCollection>("gc_partyrole") ?? new OptionSetValueCollection()).Select(x => x.Value).ToList();
            var role = side == DeskChoice.Side.Seller ? Choice.Base : Choice.Base + 1;
            if (!roles.Contains(role)) { roles.Add(role); upd["gc_partyrole"] = new OptionSetValueCollection(roles.Select(x => new OptionSetValue(x)).ToList()); }
            var reg = J.Str(a, "registration_number");
            if (!string.IsNullOrWhiteSpace(reg) && string.IsNullOrWhiteSpace(acc.GetAttributeValue<string>("gc_registrationnumber"))) upd["gc_registrationnumber"] = Desk.Cut(reg.Trim(), 100);
            if (upd.Attributes.Count > 0) ctx.Update(upd, "kyb_party", J.Obj("role", side == DeskChoice.Side.Seller ? "Seller" : "Buyer"));
            return J.Obj("ok", true, "filed", filed, "company", acc.GetAttributeValue<string>("name"),
                         "note", "The KYB check runs now; a person approves the result. Draft a short thanks: documents received, we will revert if anything else is needed.");
        }

        private static object RecordShipmentUpdate(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            Guid? dealId = Ref(conv, "gc_deal");
            if (dealId == null && Ref(conv, "gc_requirement") != null)
            {
                var d = ctx.Dv.Query("gc_deal", new[] { "gc_dealid", "gc_stage" }, 20, "gc_requirement", ConditionOperator.Equal, Ref(conv, "gc_requirement").Value)
                          .FirstOrDefault(x => Opt(x, "gc_stage") >= DeskChoice.DealStage.TermsAgreed && Opt(x, "gc_stage") != DeskChoice.DealStage.Cancelled);
                dealId = d == null ? (Guid?)null : d.Id;
            }
            if (dealId == null) throw new ToolRefusal("No agreed deal on this thread.");
            var deal = ctx.Dv.Retrieve("gc_deal", dealId.Value, "gc_name", "gc_stage");
            var status = J.Str(a, "status");
            var w = new DeskWriter(ctx.Dv, ctx);
            var existing = ctx.Dv.Query("gc_shipment", new[] { "gc_shipmentid", "gc_status" }, 1, "gc_deal", ConditionOperator.Equal, dealId.Value).FirstOrDefault();
            var s = new Entity("gc_shipment");
            if (existing != null) s.Id = existing.Id;
            s["gc_status"] = new OptionSetValue(Choice.ValueOf(Tracking.ShipmentStatuses, status));
            if (!string.IsNullOrWhiteSpace(J.Str(a, "bl_number"))) s["gc_blnumber"] = Desk.Cut(J.Str(a, "bl_number"), 100);
            if (!string.IsNullOrWhiteSpace(J.Str(a, "origin_port"))) s["gc_originport"] = Desk.Cut(J.Str(a, "origin_port"), 100);
            if (!string.IsNullOrWhiteSpace(J.Str(a, "destination_port"))) s["gc_destinationport"] = Desk.Cut(J.Str(a, "destination_port"), 100);
            DateTime when;
            if (DateTime.TryParse(J.Str(a, "etd"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)) s["gc_etd"] = when;
            if (DateTime.TryParse(J.Str(a, "eta"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)) s["gc_eta"] = when;
            Guid shipmentId;
            if (existing != null) { ctx.Update(s, "shipment_update", J.Obj("status", status)); shipmentId = existing.Id; }
            else
            {
                s["gc_name"] = Desk.Cut("Shipment - " + deal.GetAttributeValue<string>("gc_name"), 100);
                s["gc_deal"] = new EntityReference("gc_deal", dealId.Value);
                shipmentId = ctx.Create(s, "shipment_created", J.Obj("status", status));
            }
            if (ctx.DryRun) return J.Obj("ok", true, "status", status, "note", "dry run");
            // The other side's update is drafted now (masked); this side's answer is the agent's own draft.
            var reporter = Side(conv) == DeskChoice.Side.Buyer ? "buyer" : "seller";
            var tracked = Tracking.Run(w, "shipment", shipmentId, reporter);
            return J.Obj("ok", true, "status", status, "other_side", J.Get(tracked, "drafted_to"),
                         "note", "The update to the other side is drafted. Draft only a short reply in this thread (thanks; ask for the shipping documents if they are not attached).");
        }

        // ---------------------------------------------------------------- drafting

        private static object DraftEmail(AgentContext ctx, Dictionary<string, object> a)
        {
            var conv = ctx.Subject;
            Guid target;
            switch (J.Str(a, "thread"))
            {
                case "buyer_thread":
                    if (Side(conv) == DeskChoice.Side.Buyer) { target = conv.Id; break; }
                    var reqId = Ref(conv, "gc_requirement");
                    var bt = reqId == null ? null : BuyerThread(ctx, reqId.Value);
                    if (bt == null) throw new ToolRefusal("There is no buyer thread for this enquiry.");
                    target = bt.Value;
                    break;
                case "seller_thread":
                    Guid id;
                    if (!Guid.TryParse(J.Str(a, "thread_id"), out id)) throw new ToolRefusal("seller_thread needs thread_id (a seller thread id from CONTEXT or a tool result).");
                    target = id;
                    break;
                default:
                    target = conv.Id;
                    break;
            }
            object drafted;
            if (ctx.Scratch.TryGetValue("desk_drafted_threads", out drafted) && ((HashSet<Guid>)drafted).Contains(target))
                throw new ToolRefusal("The desk already drafted the email for that thread in this run (for example the next supplier's offer or a confirmation); " +
                                      "it must not be replaced. Do not draft to that thread again; finish.");
            var t = target == conv.Id ? conv : ctx.Dv.Retrieve("gc_conversation", target, "gc_side", "gc_requirement", "gc_deal", "gc_counterparty");
            if (t == null) throw new ToolRefusal("Thread " + target + " was not found.");
            if (target != conv.Id && Ref(t, "gc_requirement") != Ref(conv, "gc_requirement"))
                throw new ToolRefusal("That thread belongs to another enquiry.");
            var body = (J.Str(a, "body") ?? "").Trim();
            var problems = CheckDraft(ctx, t, body);
            if (problems.Count > 0) throw new ToolRefusal("Draft refused: " + string.Join(" ", problems) + " Rewrite it.");
            body = Desk.Greet(body, Desk.ContactName(ctx.Dv, target));

            var to = Desk.ReplyAddress(ctx.Dv, target);
            if (string.IsNullOrWhiteSpace(to))
            {
                var acc = Ref(t, "gc_counterparty");
                var account = acc == null ? null : ctx.Dv.Retrieve("account", acc.Value, "emailaddress1");
                to = account == null ? null : account.GetAttributeValue<string>("emailaddress1");
            }
            if (string.IsNullOrWhiteSpace(to)) throw new ToolRefusal("No email address is known for that thread.");
            Guid[] files = null;
            if (J.Str(a, "attach") == "company_profile")
            {
                var profile = Desk.CompanyProfile(ctx.Dv);
                if (profile == null) throw new ToolRefusal("No company profile is on file. Say it will follow and create_review_task for a Deal Manager to send it.");
                files = new[] { profile.Value };
            }
            var auto = Desk.AutoSend(ctx.Dv, "reply", body);
            var draftId = Desk.Draft(new DeskWriter(ctx.Dv, ctx), target, to, Desk.ReplySubject(ctx.Dv, target), body, files, "draft_email", auto);
            ctx.Scratch["drafted"] = true;
            return J.Obj("ok", true, "draft_id", ctx.DryRun ? null : draftId.ToString(), "thread", target.ToString(),
                         "delivery", auto ? "sent automatically (email.autosend)" : "waits in Gmail Drafts for a person to send");
        }

        /// <summary>Masking and grounding checks for a draft to the thread's counterparty.</summary>
        public static List<string> CheckDraft(AgentContext ctx, Entity thread, string body)
        {
            var errors = new List<string>();
            if (body.Length < 20) errors.Add("The email is too short.");
            if (body.Length > 3000) errors.Add("Keep the email under 3,000 characters.");
            if (EmailRx.IsMatch(body) || PhoneRx.IsMatch(body)) errors.Add("No email addresses or phone numbers in the text (the signature is added automatically).");
            if (Regex.IsMatch(body, @"https?://|www\.", RegexOptions.IgnoreCase)) errors.Add("No links.");
            var lower = body.ToLowerInvariant();
            foreach (var w in BankWords.Where(lower.Contains)) errors.Add("No bank or payment details ('" + w + "').");
            if (Regex.IsMatch(lower, @"\bmargin\b|\bmark-?up\b|\bour commission\b")) errors.Add("Never mention our margin or commission.");
            var tells = TemplateTells.Where(lower.Contains).ToList();
            if (tells.Count > 0) errors.Add("Reads like a template or an AI, not a trader: '" + string.Join("', '", tells) + "'. Say it plainly in your own short words.");
            if (body.Contains("—") || body.Contains("!")) errors.Add("No em dashes or exclamation marks; write plain sentences.");
            if (Regex.IsMatch(body, @"\*\*|^#", RegexOptions.Multiline)) errors.Add("Plain text only: no markdown (** or #).");

            var unknown = new List<string>();
            foreach (Match m in NumberRx.Matches(body))
            {
                var forms = MessageValidator.Normalise(m.Value).ToList();
                decimal d;
                if (forms.Count == 1 && decimal.TryParse(forms[0], NumberStyles.Number, CultureInfo.InvariantCulture, out d) && d < 10 && d == Math.Floor(d)) continue;
                if (!forms.Any(ctx.KnownNumbers.Contains)) unknown.Add(m.Value);
            }
            if (unknown.Count > 0) errors.Add("Numbers not in the data or tool results: " + string.Join(", ", unknown.Distinct()) + ".");

            var reqId = Ref(thread, "gc_requirement");
            var side = Side(thread);
            if (reqId != null && side != null)
            {
                var forbiddenTerms = new List<string>();
                var forbiddenNumbers = new HashSet<string>();
                var req = ctx.Dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_buyer", "gc_targetprice", "gc_ourprice");
                var deals = ctx.Dv.Query("gc_deal", new[] { "gc_seller", "gc_buyerprice" }, 50, "gc_requirement", ConditionOperator.Equal, reqId.Value);
                if (side == DeskChoice.Side.Buyer)
                {
                    foreach (var d in deals)
                    {
                        AddParty(ctx, Ref(d, "gc_seller"), forbiddenTerms);
                        foreach (var o in ctx.Dv.Query("gc_offer", new[] { "gc_price" }, 50, "gc_deal", ConditionOperator.Equal, d.Id))
                            AddNumber(o.GetAttributeValue<decimal?>("gc_price"), forbiddenNumbers);
                    }
                    // Our own price to the buyer may legitimately equal nothing on the seller side, so keep it allowed.
                    var ours = req.GetAttributeValue<decimal?>("gc_ourprice");
                    if (ours != null) foreach (var n in MessageValidator.Normalise(ours.Value.ToString(CultureInfo.InvariantCulture))) forbiddenNumbers.Remove(n);
                }
                else
                {
                    AddParty(ctx, Ref(req, "gc_buyer"), forbiddenTerms);
                    AddNumber(req.GetAttributeValue<decimal?>("gc_targetprice"), forbiddenNumbers);
                    AddNumber(req.GetAttributeValue<decimal?>("gc_ourprice"), forbiddenNumbers);
                    foreach (var d in deals) AddNumber(d.GetAttributeValue<decimal?>("gc_buyerprice"), forbiddenNumbers);
                    var dealId = Ref(thread, "gc_deal");
                    if (dealId != null)
                        foreach (var o in ctx.Dv.Query("gc_offer", new[] { "gc_price" }, 50, "gc_deal", ConditionOperator.Equal, dealId.Value))
                            foreach (var n in MessageValidator.Normalise((o.GetAttributeValue<decimal?>("gc_price") ?? 0).ToString(CultureInfo.InvariantCulture))) forbiddenNumbers.Remove(n);
                }
                foreach (var term in forbiddenTerms.Where(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (lower.Contains(term.Trim().ToLowerInvariant())) errors.Add("Do not reveal the other party: \"" + term.Trim() + "\".");
                foreach (Match m in NumberRx.Matches(body))
                    if (MessageValidator.Normalise(m.Value).Any(forbiddenNumbers.Contains))
                        errors.Add("The number " + m.Value + " is the other side's price; " + (side == DeskChoice.Side.Buyer ? "quote only our price to the buyer." : "offer only our bid to the seller."));
            }
            return errors.Distinct().ToList();
        }

        private static void AddParty(AgentContext ctx, Guid? accountId, List<string> terms)
        {
            if (accountId == null) return;
            var a = ctx.Dv.Retrieve("account", accountId.Value, "name", "emailaddress1", "websiteurl");
            if (a == null) return;
            terms.Add(a.GetAttributeValue<string>("name"));
            var email = a.GetAttributeValue<string>("emailaddress1");
            if (!string.IsNullOrEmpty(email))
            {
                terms.Add(email);
                var domain = MailSignals.Registrable(email.Substring(email.IndexOf('@') + 1));
                if (domain != null && !new[] { "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "163.com", "qq.com", "126.com", "rediffmail.com" }.Contains(domain))
                    terms.Add(domain.Split('.')[0].Length >= 4 ? domain.Split('.')[0] : domain);
            }
            foreach (var c in ctx.Dv.Query("contact", new[] { "fullname" }, 10, "parentcustomerid", ConditionOperator.Equal, accountId.Value))
                terms.Add(c.GetAttributeValue<string>("fullname"));
            var lead = ctx.Dv.Query("gc_lead", new[] { "gc_contactname", "gc_name" }, 1, "gc_account", ConditionOperator.Equal, accountId.Value).FirstOrDefault();
            if (lead != null) { terms.Add(lead.GetAttributeValue<string>("gc_contactname")); terms.Add(lead.GetAttributeValue<string>("gc_name")); }
        }

        private static void AddNumber(decimal? v, HashSet<string> into)
        {
            if (v == null || v.Value < 10) return;
            foreach (var n in MessageValidator.Normalise(v.Value.ToString(CultureInfo.InvariantCulture))) into.Add(n);
        }

        // ---------------------------------------------------------------- helpers

        public static int? Side(Entity conv)
        {
            var v = conv == null ? null : conv.GetAttributeValue<OptionSetValue>("gc_side");
            return v == null ? (int?)null : v.Value;
        }

        public static Entity Latest(AgentContext ctx)
        {
            object v;
            return ctx.Scratch.TryGetValue("message", out v) ? v as Entity : null;
        }

        private static bool InLatest(AgentContext ctx, double value)
        {
            var msg = Latest(ctx);
            var known = new HashSet<string>();
            MessageValidator.CollectNumbers(msg == null ? "" : msg.GetAttributeValue<string>("gc_text"), known);
            return MessageValidator.Normalise(((decimal)value).ToString(CultureInfo.InvariantCulture)).Any(known.Contains);
        }

        private static bool InThread(AgentContext ctx, double value)
        {
            if (InLatest(ctx, value)) return true;
            object v;
            var inbound = ctx.Scratch.TryGetValue("inbound_numbers", out v) ? v as HashSet<string> : null;
            return inbound != null && MessageValidator.Normalise(((decimal)value).ToString(CultureInfo.InvariantCulture)).Any(inbound.Contains);
        }

        private static Guid? Ref(Entity e, string column) { return Desk.H(e, column); }

        private static int Opt(Entity e, string column)
        {
            var v = e == null ? null : e.GetAttributeValue<OptionSetValue>(column);
            return v == null ? -1 : v.Value;
        }

        private static Guid? BuyerThread(AgentContext ctx, Guid requirementId)
        {
            var c = ctx.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, requirementId,
                                 "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
            return c == null ? (Guid?)null : c.Id;
        }

        private static string BuyerThreadOf(AgentContext ctx, Guid? reqId)
        {
            var t = reqId == null ? null : BuyerThread(ctx, reqId.Value);
            return t == null ? null : t.Value.ToString();
        }

        private static string SellerThreadOf(AgentContext ctx, Guid dealId)
        {
            var c = ctx.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_deal", ConditionOperator.Equal, dealId,
                                 "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
            return c == null ? null : c.Id.ToString();
        }

        /// <summary>Quotes from sellers (offers whose sender is the deal's seller) on the requirement's live deals, newest first.</summary>
        /// <summary>The quotes the buyer side works with: the active seller's (sellers take turns) and lot offers. Queued sellers stay hidden.</summary>
        public static List<Entity> SellerQuotes(AgentContext ctx, Guid requirementId)
        {
            var list = new List<Entity>();
            var active = Desk.ActiveDeal(ctx.Dv, requirementId);
            foreach (var d in ctx.Dv.Query("gc_deal", new[] { "gc_seller", "gc_stage", "gc_sellerlot" }, 50, "gc_requirement", ConditionOperator.Equal, requirementId))
            {
                if (Opt(d, "gc_stage") == DeskChoice.DealStage.Cancelled) continue;
                if (d.Id != active && Ref(d, "gc_sellerlot") == null) continue;
                var seller = Ref(d, "gc_seller");
                list.AddRange(ctx.Dv.Query("gc_offer", new[] { "gc_price", "gc_currency", "gc_quantity", "gc_incoterm", "gc_namedplace", "gc_terms", "gc_status", "gc_fromparty", "gc_deal", "gc_paymentterms", "createdon" }, 20,
                                           "gc_deal", ConditionOperator.Equal, d.Id).Where(o => Ref(o, "gc_fromparty") == seller));
            }
            return list.OrderByDescending(o => o.GetAttributeValue<DateTime>("createdon")).ToList();
        }

        private static Entity BestQuote(AgentContext ctx, Guid requirementId)
        {
            return SellerQuotes(ctx, requirementId).Where(o => Opt(o, "gc_status") == DeskChoice.Offer.Open || Opt(o, "gc_status") == DeskChoice.Offer.Countered)
                                                  .OrderBy(o => o.GetAttributeValue<decimal>("gc_price")).FirstOrDefault();
        }

        private static List<Entity> OurBids(AgentContext ctx, Guid dealId, Guid? buyerId)
        {
            return ctx.Dv.Query("gc_offer", new[] { "gc_price", "gc_status", "gc_fromparty", "createdon", "gc_currency" }, 20, "gc_deal", ConditionOperator.Equal, dealId)
                         .Where(o => buyerId != null && Ref(o, "gc_fromparty") == buyerId).ToList();
        }

        private static Entity SellerOffer(AgentContext ctx, string id)
        {
            Guid g;
            if (!Guid.TryParse(id, out g)) throw new ToolRefusal("offer_id must be an offer GUID from CONTEXT or a tool result.");
            var o = ctx.Dv.Retrieve("gc_offer", g, "gc_price", "gc_currency", "gc_quantity", "gc_incoterm", "gc_namedplace", "gc_terms", "gc_status", "gc_fromparty", "gc_deal", "gc_paymentterms");
            if (o == null) throw new ToolRefusal("Offer " + id + " was not found.");
            var deal = ctx.Dv.Retrieve("gc_deal", Ref(o, "gc_deal") ?? Guid.Empty, "gc_seller", "gc_requirement");
            if (deal == null || Ref(o, "gc_fromparty") != Ref(deal, "gc_seller")) throw new ToolRefusal("Offer " + id + " is not a seller quote.");
            var convReq = Ref(ctx.Subject, "gc_requirement");
            if (convReq != null && Ref(deal, "gc_requirement") != convReq) throw new ToolRefusal("Offer " + id + " belongs to another enquiry.");
            return o;
        }

        private static void LeadResponded(AgentContext ctx, Guid? accountId)
        {
            if (accountId == null) return;
            var lead = ctx.Dv.Query("gc_lead", new[] { "gc_leadid" }, 1, "gc_account", ConditionOperator.Equal, accountId.Value).FirstOrDefault();
            if (lead == null) return;
            var l = new Entity("gc_lead", lead.Id);
            l["gc_status"] = new OptionSetValue(DeskChoice.LeadStatus.Responded);
            ctx.Update(l, "lead_responded");
        }

        /// <summary>Seller documents (COA, specification) of a genuine quote go to Document Intelligence as evidence on the deal.</summary>
        private static void ReleaseAttachments(AgentContext ctx, Guid dealId)
        {
            var msg = Latest(ctx);
            if (msg == null) return;
            foreach (var doc in ctx.Dv.Query("gc_document", new[] { "gc_documentid", "gc_parsestatus" }, 5, "gc_message", ConditionOperator.Equal, msg.Id))
            {
                if (Opt(doc, "gc_parsestatus") != MailChoice.DocumentQuarantined) continue;
                var d = new Entity("gc_document", doc.Id);
                d["gc_deal"] = new EntityReference("gc_deal", dealId);
                d["gc_parsestatus"] = new OptionSetValue(Choice.ParseStatus.Pending);
                ctx.Update(d, "release_document");
            }
        }

        private static void AdvanceStage(AgentContext ctx, Guid? requirementId, int stage)
        {
            if (requirementId == null) return;
            var r = ctx.Dv.Retrieve("gc_buyerrequirement", requirementId.Value, "gc_deskstage");
            if (r == null || Opt(r, "gc_deskstage") >= stage) return;
            var u = new Entity("gc_buyerrequirement", requirementId.Value);
            u["gc_deskstage"] = new OptionSetValue(stage);
            ctx.Update(u, "desk_stage", J.Obj("stage", Desk.Label(DeskChoice.Stages, new OptionSetValue(stage))));
        }
    }
}
