import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { B, INCOTERMS, INVITE_STATUS, MATCH_STATUS, RFQ_STATUS, UNITS, create, day, get, label, list, money, qty, remove, update } from "../api";
import { Empty, Loading, RequireCompany, Status, useAction, useLoad } from "../components";
import { useSession } from "../session";

interface Rfq {
  gc_buyerrequirementid: string; gc_name: string; gc_status: number; gc_quantity: number | null; gc_quantityunit: number | null; gc_incoterm: number | null;
  gc_targetprice: number | null; gc_currency: string | null; gc_specification: string | null; gc_destinationport: string | null; gc_validuntil: string | null;
  gc_inspectionrequired: boolean | null; createdon: string;
}
interface Commodity { gc_commodityid: string; gc_name: string }
interface Country { gc_countryid: string; gc_name: string }
interface Match { gc_matchid: string; gc_name: string; gc_score: number | null; gc_status: number; gc_buyeroptin: boolean; gc_selleroptin: boolean; gc_explanation: string | null }
interface Invite { gc_rfqinviteid: string; gc_name: string; gc_status: number; gc_invitedon: string | null; gc_respondedon: string | null }

const RFQ_SELECT = "gc_buyerrequirementid,gc_name,gc_status,gc_quantity,gc_quantityunit,gc_incoterm,gc_targetprice,gc_currency,gc_specification,gc_destinationport,gc_validuntil,gc_inspectionrequired,createdon";

export default function Buying() {
  return <RequireCompany role="buyer"><BuyerHome /></RequireCompany>;
}

function BuyerHome() {
  const rfqs = useLoad(() => list<Rfq>(`gc_buyerrequirements?$select=${RFQ_SELECT}&$orderby=createdon desc`), []);
  return (
    <div className="stack">
      <div className="page-head">
        <div><h1>Buying</h1><p className="muted">Post what you need; DealOS matches verified sellers and you compare their offers at landed cost.</p></div>
        <div className="row"><Link className="button" to="/">Browse catalog</Link><Link className="button primary" to="/buy/new">New RFQ</Link></div>
      </div>
      <div className="card">
        <h2>My requests for quotation</h2>
        {rfqs.loading ? <Loading /> : (rfqs.data ?? []).length === 0 ? (
          <Empty>No RFQs yet. <Link to="/buy/new">Post one</Link> or ask the <Link to="/chat?assistant=buyer">Buyer Concierge</Link> to draft it.</Empty>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>RFQ</th><th>Status</th><th>Quantity</th><th>Terms</th><th>Target</th><th>Posted</th></tr></thead>
              <tbody>{(rfqs.data ?? []).map((r) => (
                <tr key={r.gc_buyerrequirementid}>
                  <td><Link to={`/buy/${r.gc_buyerrequirementid}`}>{r.gc_name}</Link></td>
                  <td><span className={`pill ${r.gc_status === B ? "accent" : "neutral"}`}>{label(r.gc_status, RFQ_STATUS)}</span></td>
                  <td>{qty(r.gc_quantity, label(r.gc_quantityunit, UNITS))}</td>
                  <td>{label(r.gc_incoterm, INCOTERMS)}</td>
                  <td>{r.gc_targetprice ? money(r.gc_targetprice, r.gc_currency) : "—"}</td>
                  <td>{day(r.createdon)}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

export function NewRfq() {
  return <RequireCompany role="buyer"><RfqForm /></RequireCompany>;
}

function RfqForm() {
  const navigate = useNavigate();
  const { company } = useSession();
  const commodities = useLoad(() => list<Commodity>("gc_commodities?$select=gc_commodityid,gc_name&$orderby=gc_name&$top=500"), []);
  const countries = useLoad(() => list<Country>("gc_countries?$select=gc_countryid,gc_name&$orderby=gc_name&$top=300"), []);
  const [f, setF] = useState({ commodity: "", quantity: "", unit: String(B), spec: "", incoterm: "", country: "", port: "", target: "", currency: "USD", until: "", inspection: true });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const action = useAction();
  const commodityName = (commodities.data ?? []).find((c) => c.gc_commodityid === f.commodity)?.gc_name ?? "";

  const save = () => action.run(async () => {
    const body: Record<string, unknown> = {
      gc_name: `RFQ: ${f.quantity} ${UNITS[Number(f.unit) - B]} ${commodityName}`.slice(0, 100),
      gc_quantity: Number(f.quantity), gc_quantityunit: Number(f.unit), gc_specification: f.spec || null,
      gc_destinationport: f.port || null, gc_inspectionrequired: f.inspection, "gc_commodity@odata.bind": `/gc_commodities(${f.commodity})`,
      "gc_buyer@odata.bind": `/accounts(${company!.accountid})`,
    };
    if (f.incoterm) body.gc_incoterm = Number(f.incoterm);
    if (f.target) { body.gc_targetprice = Number(f.target); body.gc_currency = f.currency.toUpperCase().slice(0, 3); }
    if (f.until) body.gc_validuntil = new Date(f.until).toISOString();
    if (f.country) body["gc_destinationcountry@odata.bind"] = `/gc_countries(${f.country})`;
    const id = await create("gc_buyerrequirements", body);
    navigate(`/buy/${id}`);
  });

  return (
    <div className="stack" style={{ maxWidth: 820 }}>
      <div><Link to="/buy" className="small">← Buying</Link><h1>New request for quotation</h1>
        <p className="muted">Saved as a draft. When you send it, DealOS matches published listings and you can invite sellers to quote. Your identity stays hidden until a contract is signed.</p></div>
      <div className="card stack">
        <div className="form">
          <label className="field wide">Commodity
            <select value={f.commodity} onChange={set("commodity")}>
              <option value="">Choose…</option>
              {(commodities.data ?? []).map((c) => <option key={c.gc_commodityid} value={c.gc_commodityid}>{c.gc_name}</option>)}
            </select>
          </label>
          <label className="field">Quantity<input type="number" min="0" value={f.quantity} onChange={set("quantity")} /></label>
          <label className="field">Unit<select value={f.unit} onChange={set("unit")}>{UNITS.map((u, i) => <option key={u} value={B + i}>{u}</option>)}</select></label>
          <label className="field">Incoterm<select value={f.incoterm} onChange={set("incoterm")}><option value="">Open</option>{INCOTERMS.map((t, i) => <option key={t} value={B + i}>{t}</option>)}</select></label>
          <label className="field wide">Specification<textarea value={f.spec} onChange={set("spec")} placeholder="Grade, purity, impurity limits, moisture, packaging" /></label>
          <label className="field">Destination country<select value={f.country} onChange={set("country")}><option value="">Choose…</option>{(countries.data ?? []).map((c) => <option key={c.gc_countryid} value={c.gc_countryid}>{c.gc_name}</option>)}</select></label>
          <label className="field">Destination port / place<input value={f.port} onChange={set("port")} /></label>
          <label className="field">Target price per unit (optional)<input type="number" min="0" value={f.target} onChange={set("target")} /></label>
          <label className="field">Currency<input value={f.currency} maxLength={3} onChange={set("currency")} /></label>
          <label className="field">Valid until<input type="date" value={f.until} onChange={set("until")} /></label>
          <div className="field"><span>Inspection</span><div className="checks"><label><input type="checkbox" checked={f.inspection} onChange={(e) => setF({ ...f, inspection: e.target.checked })} />Independent inspection before shipment</label></div></div>
        </div>
        <Status error={action.error} />
        <div><button className="primary" disabled={!f.commodity || !(Number(f.quantity) > 0) || action.busy} onClick={save}>Save draft</button></div>
      </div>
    </div>
  );
}

export function MyRfq() {
  return <RequireCompany role="buyer"><RfqDetail /></RequireCompany>;
}

function RfqDetail() {
  const { id } = useParams();
  const navigate = useNavigate();
  const rfq = useLoad(() => get<Rfq>(`gc_buyerrequirements(${id})?$select=${RFQ_SELECT}`), [id]);
  const matches = useLoad(() => list<Match>(`gc_matchs?$select=gc_matchid,gc_name,gc_score,gc_status,gc_buyeroptin,gc_selleroptin,gc_explanation&$filter=_gc_requirement_value eq ${id}&$orderby=gc_score desc`), [id]);
  const invites = useLoad(() => list<Invite>(`gc_rfqinvites?$select=gc_rfqinviteid,gc_name,gc_status,gc_invitedon,gc_respondedon&$filter=_gc_requirement_value eq ${id}&$orderby=createdon desc`), [id]);
  const action = useAction();

  if (rfq.loading) return <Loading />;
  if (!rfq.data) return <Status error={rfq.error ?? "RFQ not found."} />;
  const r = rfq.data;
  const setStatus = (to: number, msg: string) => action.run(async () => { await update("gc_buyerrequirements", r.gc_buyerrequirementid, { gc_status: to }); rfq.reload(); }, msg);
  const optIn = (m: Match) => action.run(async () => { await update("gc_matchs", m.gc_matchid, { gc_buyeroptin: true }); matches.reload(); },
    "You're interested. When the seller opts in too, a deal opens for offers.");
  const withdrawInvite = (i: Invite) => action.run(async () => { await update("gc_rfqinvites", i.gc_rfqinviteid, { gc_status: B + 5 }); invites.reload(); }, "Invite withdrawn.");
  const explain = (m: Match) => {
    try { return (JSON.parse(m.gc_explanation ?? "{}").spec_reason as string) ?? ""; } catch { return ""; }
  };

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <Link to="/buy" className="small">← Buying</Link>
          <h1>{r.gc_name}</h1>
          <span className="pill neutral">{label(r.gc_status, RFQ_STATUS)}</span>
        </div>
        <div className="row">
          {r.gc_status === B ? <button className="primary" disabled={action.busy} onClick={() => setStatus(B + 1, "Sent. Matching runs now; matching sellers appear below.")}>Send RFQ</button> : null}
          {r.gc_status === B || r.gc_status === B + 1 ? <button disabled={action.busy} onClick={() => setStatus(B + 5, "Withdrawn.")}>Withdraw</button> : null}
          {r.gc_status === B ? <button className="danger" disabled={action.busy} onClick={() => action.run(async () => { await remove("gc_buyerrequirements", r.gc_buyerrequirementid); navigate("/buy"); })}>Delete draft</button> : null}
        </div>
      </div>
      <Status error={action.error} done={action.done} />
      <div className="split">
        <div className="stack">
          <div className="card stack">
            <h2>Matched listings</h2>
            {r.gc_status === B ? <p className="muted">Send the RFQ to start matching.</p> : (matches.data ?? []).length === 0 ? <p className="muted">No matches yet. You can also invite sellers directly from the <Link to="/">catalog</Link>.</p> : (
              (matches.data ?? []).map((m) => (
                <div key={m.gc_matchid} className="row" style={{ justifyContent: "space-between", borderBottom: "1px solid var(--line)", paddingBottom: 10 }}>
                  <div><b>{m.gc_name}</b><div className="muted small">Score {m.gc_score ?? "—"} · {label(m.gc_status, MATCH_STATUS)}{m.gc_selleroptin ? " · seller is interested" : ""}</div>
                    {explain(m) ? <div className="small">{explain(m)}</div> : null}</div>
                  {m.gc_buyeroptin ? <span className="pill verified">You're interested</span> : <button className="primary" disabled={action.busy} onClick={() => optIn(m)}>I'm interested</button>}
                </div>
              ))
            )}
          </div>
          <div className="card stack">
            <h2>Invited sellers</h2>
            {(invites.data ?? []).length === 0 ? <p className="muted">None yet. Open a listing in the catalog and choose “Invite seller to quote”.</p> : (
              (invites.data ?? []).map((i) => (
                <div key={i.gc_rfqinviteid} className="row" style={{ justifyContent: "space-between" }}>
                  <div>{i.gc_name}<div className="muted small">{label(i.gc_status, INVITE_STATUS)} · invited {day(i.gc_invitedon)}</div></div>
                  {i.gc_status === B || i.gc_status === B + 1 ? <button disabled={action.busy} onClick={() => withdrawInvite(i)}>Withdraw</button> : null}
                </div>
              ))
            )}
          </div>
        </div>
        <div className="card">
          <dl className="kv small">
            <dt>Quantity</dt><dd>{qty(r.gc_quantity, label(r.gc_quantityunit, UNITS))}</dd>
            <dt>Incoterm</dt><dd>{label(r.gc_incoterm, INCOTERMS)}</dd>
            <dt>Destination</dt><dd>{r.gc_destinationport ?? "—"}</dd>
            <dt>Target</dt><dd>{r.gc_targetprice ? money(r.gc_targetprice, r.gc_currency) : "—"}</dd>
            <dt>Valid until</dt><dd>{day(r.gc_validuntil)}</dd>
            <dt>Inspection</dt><dd>{r.gc_inspectionrequired ? "Required" : "Not required"}</dd>
            <dt>Specification</dt><dd style={{ whiteSpace: "pre-wrap" }}>{r.gc_specification ?? "—"}</dd>
          </dl>
        </div>
      </div>
    </div>
  );
}
