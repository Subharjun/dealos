import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { B, create, day, get, list, money, qty } from "../api";
import { Badge, EVIDENCE_HELP, Evidence, Loading, SignIn, Status, useAction, useLoad } from "../components";
import { useSession } from "../session";
import { CATALOG_SELECT, facts, type CatalogEntry } from "./Catalog";

interface OpenRfq { gc_buyerrequirementid: string; gc_name: string }

export default function Listing() {
  const { id } = useParams();
  const { user, company, isBuyer } = useSession();
  const { data: entry, error, loading } = useLoad(() => get<CatalogEntry>(`gc_catalogentries(${id})?$select=${CATALOG_SELECT}`), [id]);
  const rfqs = useLoad(() => (company && isBuyer
    ? list<OpenRfq>(`gc_buyerrequirements?$select=gc_buyerrequirementid,gc_name&$filter=gc_status eq ${B + 1}&$orderby=createdon desc`)
    : Promise.resolve([])), [company?.accountid, isBuyer]);
  const [rfq, setRfq] = useState("");
  const action = useAction();

  if (loading) return <Loading />;
  if (error || !entry) return <Status error={error ?? "Listing not found."} />;

  const invite = () => action.run(() => create("gc_rfqinvites", {
    gc_name: `Invite: ${entry.gc_name}`.slice(0, 200),
    "gc_Requirement@odata.bind": `/gc_buyerrequirements(${rfq})`,
    "gc_Listing@odata.bind": `/gc_listings(${entry._gc_listing_value})`,
  }), "Invite sent. The seller is told (your identity stays hidden) and can accept with an offer.");

  const shownFacts = facts(entry);
  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <Link to="/" className="small">← Catalog</Link>
          <h1>{entry.gc_commodity}{entry.gc_grade ? ` · ${entry.gc_grade}` : ""}</h1>
          <div className="row"><Badge badge={entry.gc_badge} /><span className="muted small">Published {day(entry.gc_publishedon)}</span></div>
        </div>
      </div>

      <div className="split">
        <div className="card stack">
          <h2>Specification and evidence</h2>
          {shownFacts.length === 0 ? (
            <p className="muted">The seller has not yet supported any values with evidence. Ask for documents through an RFQ invite.</p>
          ) : (
            <div className="table-wrap">
              <table className="facts">
                <thead><tr><th>Attribute</th><th>Value</th><th>Evidence</th></tr></thead>
                <tbody>
                  {shownFacts.map((f) => (
                    <tr key={f.key}><td>{f.name}</td><td>{f.value}</td><td><Evidence status={f.status} />{f.fresh_until ? <div className="muted small">valid until {day(f.fresh_until)}</div> : null}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="small muted stack" style={{ gap: 4 }}>
            {(["Verified", "Documented", "Claimed"] as const).map((s) => <div key={s}><Evidence status={s} /> {EVIDENCE_HELP[s]}</div>)}
          </div>
        </div>

        <div className="stack">
          <div className="card">
            <div className="listing-card">
              <div className="price">{money(entry.gc_askprice, entry.gc_currency)}<span className="muted small">{entry.gc_askprice ? ` / ${entry.gc_unit ?? "unit"}` : ""}</span></div>
              <dl className="kv small">
                <dt>Available</dt><dd>{qty(entry.gc_availablequantity ?? entry.gc_quantity, entry.gc_unit)}</dd>
                <dt>Listed</dt><dd>{qty(entry.gc_quantity, entry.gc_unit)}</dd>
                <dt>Terms</dt><dd>{entry.gc_incoterm ?? "Open"}{entry.gc_namedplace ? ` ${entry.gc_namedplace}` : ""}</dd>
                <dt>Price basis</dt><dd>{entry.gc_pricebasis ?? "—"}</dd>
                <dt>Origin</dt><dd>{entry.gc_origincountry ?? "On request"}</dd>
                <dt>Family</dt><dd>{entry.gc_family ?? "—"}{entry.gc_form ? ` · ${entry.gc_form}` : ""}</dd>
              </dl>
            </div>
          </div>

          <div className="card stack">
            <h3>Request a quote</h3>
            {!user ? (
              <><p className="muted small">Sign in as a buyer to invite this seller to quote.</p><SignIn /></>
            ) : !company || !isBuyer ? (
              <p className="muted small">Set up your company as a buyer on <Link to="/company">Company</Link> first.</p>
            ) : (rfqs.data ?? []).length === 0 ? (
              <p className="muted small">Invites go out against an open RFQ. <Link to="/buy">Post an RFQ</Link>, then come back to invite this seller.</p>
            ) : (
              <>
                <label className="field">Your open RFQ
                  <select value={rfq} onChange={(e) => setRfq(e.target.value)}>
                    <option value="">Choose…</option>
                    {(rfqs.data ?? []).map((r) => <option key={r.gc_buyerrequirementid} value={r.gc_buyerrequirementid}>{r.gc_name}</option>)}
                  </select>
                </label>
                <button className="primary" disabled={!rfq || action.busy} onClick={invite}>Invite seller to quote</button>
              </>
            )}
            <Status error={action.error} done={action.done} />
            <p className="muted small">The seller's identity, mine and exact location are shared only after a contract is signed.</p>
            <Link to={`/chat?assistant=buyer&about=${encodeURIComponent(entry.gc_name)}`} className="small">Ask the Buyer Concierge about this listing →</Link>
          </div>
        </div>
      </div>
    </div>
  );
}
