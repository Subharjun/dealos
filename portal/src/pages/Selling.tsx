import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { B, DOC_TYPES, FACT_STATUS, INCOTERMS, INVITE_STATUS, LISTING_STATUS, MATCH_STATUS, UNITS, create, day, get, label, list, money, qty, remove, update, uploadFile } from "../api";
import { Badge, Empty, Evidence, Loading, RequireCompany, Status, useAction, useLoad } from "../components";
import { useSession } from "../session";

interface MyListing {
  gc_listingid: string; gc_name: string; gc_status: number; gc_badge: number | null; gc_quantity: number | null; gc_quantityunit: number | null;
  gc_askprice: number | null; gc_currency: string | null; gc_incoterm: number | null; gc_namedplace: string | null; gc_grade: string | null; createdon: string;
}
interface Commodity { gc_commodityid: string; gc_name: string }
interface Country { gc_countryid: string; gc_name: string }
interface Fact { gc_factid: string; gc_attributekey: string; gc_displayvalue: string | null; gc_status: number | null }
interface Question { gc_questionid: string; gc_name: string; gc_askedon: string | null; _gc_answeredby_value: string | null }
interface Doc { gc_documentid: string; gc_filename: string | null; gc_name: string; gc_doctype: number | null; gc_parsestatus: number | null; createdon: string }
interface Invite { gc_rfqinviteid: string; gc_name: string; gc_status: number; gc_invitedon: string | null; gc_buyernote: string | null; _gc_listing_value: string | null }
interface Match { gc_matchid: string; gc_name: string; gc_score: number | null; gc_status: number; gc_buyeroptin: boolean; gc_selleroptin: boolean; _gc_listing_value: string | null }

const LISTING_SELECT = "gc_listingid,gc_name,gc_status,gc_badge,gc_quantity,gc_quantityunit,gc_askprice,gc_currency,gc_incoterm,gc_namedplace,gc_grade,createdon";
const BADGES = ["None", "Documented", "Verified"];
const PARSE = ["Waiting to be read", "Read", "Could not be read", "Quarantined"];
const LISTING_DOCS = [B + 1, B, B + 2, B + 3, B + 4, B + 5, B + 22, B + 23, B + 24, B + 27]; // COA, assay, inspection, licences, origin, photo, production, technical, other

export default function Selling() {
  return <RequireCompany role="seller"><SellerHome /></RequireCompany>;
}

function SellerHome() {
  const listings = useLoad(() => list<MyListing>(`gc_listings?$select=${LISTING_SELECT}&$orderby=createdon desc`), []);
  const invites = useLoad(() => list<Invite>(`gc_rfqinvites?$select=gc_rfqinviteid,gc_name,gc_status,gc_invitedon,gc_buyernote,_gc_listing_value&$filter=gc_status eq ${B} or gc_status eq ${B + 1}&$orderby=createdon desc`), []);
  const matches = useLoad(() => list<Match>(`gc_matchs?$select=gc_matchid,gc_name,gc_score,gc_status,gc_buyeroptin,gc_selleroptin,_gc_listing_value&$filter=gc_selleroptin ne true and gc_status ne ${B + 3}&$orderby=createdon desc`), []);
  const action = useAction();

  const answer = (inv: Invite, status: number) => action.run(async () => {
    await update("gc_rfqinvites", inv.gc_rfqinviteid, { gc_status: status });
    invites.reload();
  }, status === B + 2 ? "Accepted. A deal is opening where you can send your offer." : "Declined.");
  const optIn = (m: Match) => action.run(async () => {
    await update("gc_matchs", m.gc_matchid, { gc_selleroptin: true });
    matches.reload();
  }, "You opted in. When the buyer opts in too, a deal opens.");

  return (
    <div className="stack">
      <div className="page-head">
        <div><h1>Selling</h1><p className="muted">Your listings, their evidence, and buyers who want to hear from you.</p></div>
        <Link className="button primary" to="/sell/new">New listing</Link>
      </div>
      <Status error={action.error} done={action.done} />

      {(invites.data ?? []).length > 0 ? (
        <div className="card stack">
          <h2>RFQ invites</h2>
          {(invites.data ?? []).map((i) => (
            <div key={i.gc_rfqinviteid} className="row" style={{ justifyContent: "space-between", borderBottom: "1px solid var(--line)", paddingBottom: 10 }}>
              <div><b>{i.gc_name}</b><div className="muted small">Invited {day(i.gc_invitedon)} · {label(i.gc_status, INVITE_STATUS)}{i.gc_buyernote ? ` · “${i.gc_buyernote}”` : ""}</div></div>
              <div className="row">
                <button className="primary" disabled={action.busy} onClick={() => answer(i, B + 2)}>Accept and quote</button>
                <button disabled={action.busy} onClick={() => answer(i, B + 3)}>Decline</button>
              </div>
            </div>
          ))}
        </div>
      ) : null}

      {(matches.data ?? []).length > 0 ? (
        <div className="card stack">
          <h2>Buyer requirements matching your listings</h2>
          {(matches.data ?? []).map((m) => (
            <div key={m.gc_matchid} className="row" style={{ justifyContent: "space-between" }}>
              <div><b>{m.gc_name}</b><div className="muted small">Score {m.gc_score ?? "—"} · {label(m.gc_status, MATCH_STATUS)}{m.gc_buyeroptin ? " · buyer is interested" : ""}</div></div>
              <button className="primary" disabled={action.busy} onClick={() => optIn(m)}>I'm interested</button>
            </div>
          ))}
        </div>
      ) : null}

      <div className="card">
        <h2>My listings</h2>
        {listings.loading ? <Loading /> : (listings.data ?? []).length === 0 ? (
          <Empty>No listings yet. <Link to="/sell/new">Create your first listing</Link> or ask the <Link to="/chat?assistant=seller">Seller Assistant</Link>.</Empty>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Listing</th><th>Status</th><th>Evidence</th><th>Quantity</th><th>Ask</th></tr></thead>
              <tbody>
                {(listings.data ?? []).map((l) => (
                  <tr key={l.gc_listingid}>
                    <td><Link to={`/sell/${l.gc_listingid}`}>{l.gc_name}</Link></td>
                    <td><span className="pill neutral">{label(l.gc_status, LISTING_STATUS)}</span></td>
                    <td><Badge badge={label(l.gc_badge, BADGES)} /></td>
                    <td>{qty(l.gc_quantity, label(l.gc_quantityunit, UNITS))}</td>
                    <td>{money(l.gc_askprice, l.gc_currency)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

export function NewListing() {
  return <RequireCompany role="seller"><ListingForm /></RequireCompany>;
}

function ListingForm() {
  const navigate = useNavigate();
  const { company } = useSession();
  const commodities = useLoad(() => list<Commodity>("gc_commodities?$select=gc_commodityid,gc_name&$orderby=gc_name&$top=500"), []);
  const countries = useLoad(() => list<Country>("gc_countries?$select=gc_countryid,gc_name&$orderby=gc_name&$top=300"), []);
  const [f, setF] = useState({ commodity: "", grade: "", quantity: "", unit: String(B), price: "", currency: "USD", incoterm: "", place: "", origin: "" });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const action = useAction();
  const commodityName = (commodities.data ?? []).find((c) => c.gc_commodityid === f.commodity)?.gc_name ?? "";

  const save = () => action.run(async () => {
    const body: Record<string, unknown> = {
      gc_name: `${commodityName}${f.grade ? ` ${f.grade}` : ""} ${f.quantity} ${UNITS[Number(f.unit) - B]}`.slice(0, 100),
      gc_grade: f.grade || null, gc_quantity: Number(f.quantity), gc_quantityunit: Number(f.unit),
      gc_namedplace: f.place || null, "gc_commodity@odata.bind": `/gc_commodities(${f.commodity})`,
      "gc_seller@odata.bind": `/accounts(${company!.accountid})`,
    };
    if (f.price) { body.gc_askprice = Number(f.price); body.gc_currency = f.currency.toUpperCase().slice(0, 3); }
    if (f.incoterm) body.gc_incoterm = Number(f.incoterm);
    if (f.origin) body["gc_origincountry@odata.bind"] = `/gc_countries(${f.origin})`;
    const id = await create("gc_listings", body);
    navigate(`/sell/${id}`);
  });

  return (
    <div className="stack" style={{ maxWidth: 820 }}>
      <div><Link to="/sell" className="small">← Selling</Link><h1>New listing</h1>
        <p className="muted">Saved as a draft. Next you upload the certificate of analysis, licences and photos, then submit it for verification.</p></div>
      <div className="card stack">
        <div className="form">
          <label className="field wide">Commodity
            <select value={f.commodity} onChange={set("commodity")}>
              <option value="">Choose…</option>
              {(commodities.data ?? []).map((c) => <option key={c.gc_commodityid} value={c.gc_commodityid}>{c.gc_name}</option>)}
            </select>
          </label>
          <label className="field">Grade<input value={f.grade} onChange={set("grade")} placeholder="e.g. LME Grade A, 99.5% TREO" /></label>
          <label className="field">Quantity<input type="number" min="0" value={f.quantity} onChange={set("quantity")} /></label>
          <label className="field">Unit
            <select value={f.unit} onChange={set("unit")}>{UNITS.map((u, i) => <option key={u} value={B + i}>{u}</option>)}</select>
          </label>
          <label className="field">Ask price per unit (optional)<input type="number" min="0" value={f.price} onChange={set("price")} /></label>
          <label className="field">Currency<input value={f.currency} maxLength={3} onChange={set("currency")} /></label>
          <label className="field">Incoterm
            <select value={f.incoterm} onChange={set("incoterm")}><option value="">Open</option>{INCOTERMS.map((t, i) => <option key={t} value={B + i}>{t}</option>)}</select>
          </label>
          <label className="field">Named place / port<input value={f.place} onChange={set("place")} placeholder="e.g. Chennai port" /></label>
          <label className="field">Origin country
            <select value={f.origin} onChange={set("origin")}><option value="">Choose…</option>{(countries.data ?? []).map((c) => <option key={c.gc_countryid} value={c.gc_countryid}>{c.gc_name}</option>)}</select>
          </label>
        </div>
        <Status error={action.error} />
        <div><button className="primary" disabled={!f.commodity || !(Number(f.quantity) > 0) || action.busy} onClick={save}>Save draft</button></div>
      </div>
    </div>
  );
}

export function MyListing() {
  return <RequireCompany role="seller"><ListingDetail /></RequireCompany>;
}

function ListingDetail() {
  const { id } = useParams();
  const { company } = useSession();
  const navigate = useNavigate();
  const listing = useLoad(() => get<MyListing>(`gc_listings(${id})?$select=${LISTING_SELECT}`), [id]);
  const facts = useLoad(() => list<Fact>(`gc_facts?$select=gc_factid,gc_attributekey,gc_displayvalue,gc_status&$filter=_gc_listing_value eq ${id}&$orderby=gc_attributekey`), [id]);
  const questions = useLoad(() => list<Question>(`gc_questions?$select=gc_questionid,gc_name,gc_askedon,_gc_answeredby_value&$filter=_gc_listing_value eq ${id}&$orderby=gc_askedon desc`), [id]);
  const docs = useLoad(() => list<Doc>(`gc_documents?$select=gc_documentid,gc_filename,gc_name,gc_doctype,gc_parsestatus,createdon&$filter=_gc_listing_value eq ${id}&$orderby=createdon desc`), [id]);
  const [docType, setDocType] = useState(String(B + 1));
  const [file, setFile] = useState<File | null>(null);
  const action = useAction();

  if (listing.loading) return <Loading />;
  if (!listing.data) return <Status error={listing.error ?? "Listing not found."} />;
  const l = listing.data;
  const status = l.gc_status - B;

  const upload = () => action.run(async () => {
    const docId = await create("gc_documents", { gc_name: file!.name.slice(0, 100), gc_filename: file!.name.slice(0, 200), gc_doctype: Number(docType),
      gc_mimetype: file!.type || "application/octet-stream", "gc_listing@odata.bind": `/gc_listings(${l.gc_listingid})`,
      "gc_account@odata.bind": `/accounts(${company!.accountid})` });
    await uploadFile("gc_documents", docId, "gc_file", file!);
    setFile(null);
    docs.reload();
  }, "Uploaded. The document agent reads it and updates the evidence within a few minutes.");
  const setStatus = (to: number, msg: string) => action.run(async () => {
    await update("gc_listings", l.gc_listingid, { gc_status: to });
    listing.reload();
  }, msg);
  const del = () => action.run(async () => {
    await remove("gc_listings", l.gc_listingid);
    navigate("/sell");
  });
  const open = (questions.data ?? []).filter((q) => !q._gc_answeredby_value);

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <Link to="/sell" className="small">← Selling</Link>
          <h1>{l.gc_name}</h1>
          <div className="row"><span className="pill neutral">{label(l.gc_status, LISTING_STATUS)}</span><Badge badge={label(l.gc_badge, BADGES)} /></div>
        </div>
        <div className="row">
          {status === 0 || status === 3 ? <button className="primary" disabled={action.busy} onClick={() => setStatus(B + 1, "Submitted. Verification starts now; you'll hear what, if anything, is still needed.")}>Submit for verification</button> : null}
          {status !== 6 && status !== 7 && status !== 0 ? <button disabled={action.busy} onClick={() => setStatus(B + 6, "Withdrawn.")}>Withdraw</button> : null}
          {status === 0 ? <button className="danger" disabled={action.busy} onClick={del}>Delete draft</button> : null}
        </div>
      </div>
      <Status error={action.error} done={action.done} />

      {open.length > 0 ? (
        <div className="card stack">
          <h2>The verification team asks</h2>
          {open.map((q) => <div key={q.gc_questionid}>• {q.gc_name} <span className="muted small">({day(q.gc_askedon)})</span></div>)}
          <p className="muted small">Answer by uploading the document below, or reply in the <Link to={`/chat?assistant=seller&about=${encodeURIComponent(l.gc_name)}`}>Seller Assistant</Link> (a typed answer counts as “seller states”).</p>
        </div>
      ) : null}

      <div className="split">
        <div className="card stack">
          <h2>Evidence</h2>
          {(facts.data ?? []).length === 0 ? <p className="muted">No values extracted yet. Upload a certificate of analysis or assay report to start.</p> : (
            <table className="facts">
              <thead><tr><th>Attribute</th><th>Value</th><th>Status</th></tr></thead>
              <tbody>{(facts.data ?? []).map((f) => (
                <tr key={f.gc_factid}><td className="mono small">{f.gc_attributekey}</td><td>{f.gc_displayvalue ?? "—"}</td><td><Evidence status={label(f.gc_status, FACT_STATUS)} /></td></tr>
              ))}</tbody>
            </table>
          )}
        </div>
        <div className="card stack">
          <h2>Documents</h2>
          {status === 0 || status === 1 || status === 3 ? (
            <>
              <label className="field">Type
                <select value={docType} onChange={(e) => setDocType(e.target.value)}>{LISTING_DOCS.map((v) => <option key={v} value={v}>{label(v, DOC_TYPES)}</option>)}</select>
              </label>
              <label className="field">File (PDF or image, up to 14 MB)<input type="file" accept=".pdf,image/*" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></label>
              <div><button className="primary" disabled={!file || action.busy} onClick={upload}>Upload</button></div>
            </>
          ) : <p className="muted small">Documents can be added while the listing is a draft, submitted or waiting for information.</p>}
          {(docs.data ?? []).map((d) => (
            <div key={d.gc_documentid} className="small"><b>{d.gc_filename ?? d.gc_name}</b> · {label(d.gc_doctype, DOC_TYPES)} · <span className="muted">{label(d.gc_parsestatus, PARSE)}</span></div>
          ))}
        </div>
      </div>
    </div>
  );
}
