import { useState } from "react";
import { B, DEAL_STAGE, INCOTERMS, OFFER_STATUS, UNITS, create, day, label, list, money, qty } from "../api";
import { Empty, Loading, RequireCompany, Status, useAction, useLoad } from "../components";

interface Deal {
  gc_dealid: string; gc_name: string; gc_dealnumber: string | null; gc_stage: number; gc_statusoverlay: number | null; gc_quantity: number | null;
  gc_quantityunit: number | null; gc_price: number | null; gc_currency: string | null; gc_incoterm: number | null; gc_namedplace: string | null; modifiedon: string;
}
interface Offer { gc_offerid: string; gc_price: number | null; gc_quantity: number | null; gc_currency: string | null; gc_incoterm: number | null; gc_status: number; gc_round: number | null; gc_validuntil: string | null; _gc_deal_value: string }

const OVERLAY = ["", "On hold", "Disputed", "Compliance hold"];
const REASONS = ["Quality off spec", "Quantity short", "Late delivery", "Documents", "Non-payment", "Damage", "Other"];
const TRACK = [B, B + 1, B + 2, B + 3, B + 4, B + 5, B + 6, B + 7, B + 8, B + 9, B + 10];

export default function Deals() {
  return <RequireCompany><DealList /></RequireCompany>;
}

function DealList() {
  const deals = useLoad(() => list<Deal>("gc_deals?$select=gc_dealid,gc_name,gc_dealnumber,gc_stage,gc_statusoverlay,gc_quantity,gc_quantityunit,gc_price,gc_currency,gc_incoterm,gc_namedplace,modifiedon&$orderby=modifiedon desc"), []);
  const offers = useLoad(() => list<Offer>("gc_offers?$select=gc_offerid,gc_price,gc_quantity,gc_currency,gc_incoterm,gc_status,gc_round,gc_validuntil,_gc_deal_value&$orderby=createdon desc"), []);
  if (deals.loading) return <Loading />;
  return (
    <div className="stack">
      <div><h1>Deals</h1><p className="muted">Each deal moves through compliance, contract, escrow, inspection and shipment. Money is released in stages, only on verified milestones.</p></div>
      <Status error={deals.error} />
      {(deals.data ?? []).length === 0 ? <Empty>No deals yet. Deals open when a match is mutual or a seller accepts your RFQ invite.</Empty> :
        (deals.data ?? []).map((d) => <DealCard key={d.gc_dealid} deal={d} offers={(offers.data ?? []).filter((o) => o._gc_deal_value === d.gc_dealid)} />)}
    </div>
  );
}

function DealCard({ deal, offers }: { deal: Deal; offers: Offer[] }) {
  const [mode, setMode] = useState<"" | "rate" | "dispute">("");
  const [score, setScore] = useState(5);
  const [comment, setComment] = useState("");
  const [reason, setReason] = useState(B);
  const [text, setText] = useState("");
  const action = useAction();
  const stage = deal.gc_stage;
  const cancelled = stage === B + 12;
  const canRate = stage === B + 10 || stage === B + 11;
  const canDispute = stage >= B + 5 && stage <= B + 10;

  const rate = () => action.run(() => create("gc_ratings", { gc_name: `Rating: ${deal.gc_name}`.slice(0, 200), gc_score: score, gc_comment: comment || null,
    "gc_Deal@odata.bind": `/gc_deals(${deal.gc_dealid})` }).then(() => setMode("")), "Thank you. Your rating builds the other party's trust tier.");
  const dispute = () => action.run(() => create("gc_disputes", { gc_name: `Dispute: ${deal.gc_name}`.slice(0, 200), gc_reason: reason, gc_description: text,
    "gc_Deal@odata.bind": `/gc_deals(${deal.gc_dealid})` }).then(() => setMode("")), "Dispute raised. Payments on this deal are paused while a deal manager reviews it.");

  return (
    <div className="card stack">
      <div className="row" style={{ justifyContent: "space-between" }}>
        <div>
          <h2 style={{ margin: 0 }}>{deal.gc_name}</h2>
          <span className="muted small">{deal.gc_dealnumber ?? ""} · updated {day(deal.modifiedon)}</span>
        </div>
        <div className="row">
          <span className={`pill ${cancelled ? "danger" : "accent"}`}>{label(stage, DEAL_STAGE)}</span>
          {deal.gc_statusoverlay && deal.gc_statusoverlay !== B ? <span className="pill danger">{OVERLAY[deal.gc_statusoverlay - B]}</span> : null}
        </div>
      </div>
      {!cancelled ? (
        <div className="row" style={{ gap: 4 }} aria-label="Deal progress">
          {TRACK.map((s) => <span key={s} title={label(s, DEAL_STAGE)} style={{ flex: 1, height: 6, borderRadius: 3, background: s <= stage ? "var(--accent)" : "var(--line)" }} />)}
        </div>
      ) : null}
      <dl className="kv small">
        <dt>Quantity</dt><dd>{qty(deal.gc_quantity, label(deal.gc_quantityunit, UNITS))}</dd>
        <dt>Price</dt><dd>{deal.gc_price ? `${money(deal.gc_price, deal.gc_currency)} per unit` : "Not agreed yet"}</dd>
        <dt>Terms</dt><dd>{label(deal.gc_incoterm, INCOTERMS)}{deal.gc_namedplace ? ` ${deal.gc_namedplace}` : ""}</dd>
        <dt>Counterparty</dt><dd className="muted">{stage >= B + 5 && !cancelled ? "Shared in your contract documents" : "Identity shared after the contract is signed"}</dd>
      </dl>
      {offers.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>Round</th><th>Price</th><th>Quantity</th><th>Terms</th><th>Status</th><th>Valid until</th></tr></thead>
            <tbody>{offers.map((o) => (
              <tr key={o.gc_offerid}><td>{o.gc_round ?? 1}</td><td>{money(o.gc_price, o.gc_currency)}</td><td>{qty(o.gc_quantity, label(deal.gc_quantityunit, UNITS))}</td>
                <td>{label(o.gc_incoterm, INCOTERMS)}</td><td>{label(o.gc_status, OFFER_STATUS)}</td><td>{day(o.gc_validuntil)}</td></tr>
            ))}</tbody>
          </table>
        </div>
      ) : null}
      <div className="row">
        {canRate ? <button onClick={() => setMode(mode === "rate" ? "" : "rate")}>Rate the other party</button> : null}
        {canDispute ? <button className="danger" onClick={() => setMode(mode === "dispute" ? "" : "dispute")}>Raise a dispute</button> : null}
      </div>
      {mode === "rate" ? (
        <div className="form">
          <label className="field">Score<select value={score} onChange={(e) => setScore(Number(e.target.value))}>{[5, 4, 3, 2, 1].map((s) => <option key={s} value={s}>{s} / 5</option>)}</select></label>
          <label className="field wide">Comment<textarea value={comment} onChange={(e) => setComment(e.target.value)} /></label>
          <div><button className="primary" disabled={action.busy} onClick={rate}>Submit rating</button></div>
        </div>
      ) : null}
      {mode === "dispute" ? (
        <div className="form">
          <label className="field">Reason<select value={reason} onChange={(e) => setReason(Number(e.target.value))}>{REASONS.map((r, i) => <option key={r} value={B + i}>{r}</option>)}</select></label>
          <label className="field wide">What happened<textarea value={text} onChange={(e) => setText(e.target.value)} /></label>
          <div><button className="primary" disabled={!text.trim() || action.busy} onClick={dispute}>Raise dispute</button></div>
        </div>
      ) : null}
      <Status error={action.error} done={action.done} />
    </div>
  );
}
