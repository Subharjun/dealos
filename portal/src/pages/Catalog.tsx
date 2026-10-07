import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { INCOTERMS, list, money, qty } from "../api";
import { Badge, Empty, Evidence, Loading, Status, useLoad } from "../components";

export interface CatalogEntry {
  gc_catalogentryid: string;
  gc_name: string;
  gc_commodity: string | null;
  gc_family: string | null;
  gc_form: string | null;
  gc_grade: string | null;
  gc_quantity: number | null;
  gc_unit: string | null;
  gc_availablequantity: number | null;
  gc_askprice: number | null;
  gc_currency: string | null;
  gc_pricebasis: string | null;
  gc_incoterm: string | null;
  gc_namedplace: string | null;
  gc_origincountry: string | null;
  gc_badge: string | null;
  gc_publishedon: string | null;
  gc_facts: string | null;
  _gc_listing_value: string | null;
}

export interface MarketFact { key: string; name: string; value: string; status: string; fresh_until?: string | null }

export const CATALOG_SELECT =
  "gc_catalogentryid,gc_name,gc_commodity,gc_family,gc_form,gc_grade,gc_quantity,gc_unit,gc_availablequantity,gc_askprice,gc_currency," +
  "gc_pricebasis,gc_incoterm,gc_namedplace,gc_origincountry,gc_badge,gc_publishedon,gc_facts,_gc_listing_value";

export function facts(entry: CatalogEntry): MarketFact[] {
  try {
    return JSON.parse(entry.gc_facts ?? "[]") as MarketFact[];
  } catch {
    return [];
  }
}

const FAMILIES = ["Rare Earths", "Battery Metals", "Base Metals", "Precious Metals", "Minor Metals", "Ferrous And Alloys", "Industrial Minerals", "Scrap And Recycled"];

export default function Catalog() {
  const { data, error, loading } = useLoad(() => list<CatalogEntry>(`gc_catalogentries?$select=${CATALOG_SELECT}&$orderby=gc_publishedon desc&$top=200`), []);
  const [text, setText] = useState("");
  const [family, setFamily] = useState("");
  const [incoterm, setIncoterm] = useState("");
  const [badge, setBadge] = useState("");

  const shown = useMemo(() => (data ?? []).filter((e) => {
    const hay = `${e.gc_name} ${e.gc_commodity} ${e.gc_grade} ${e.gc_origincountry}`.toLowerCase();
    if (text && !text.toLowerCase().split(/\s+/).every((w) => hay.includes(w))) return false;
    if (family && e.gc_family !== family) return false;
    if (incoterm && e.gc_incoterm !== incoterm) return false;
    if (badge === "Documented" && e.gc_badge !== "Documented" && e.gc_badge !== "Verified") return false;
    if (badge === "Verified" && e.gc_badge !== "Verified") return false;
    return true;
  }), [data, text, family, incoterm, badge]);

  return (
    <>
      <section className="hero">
        <h1>Verified mineral trade</h1>
        <p>
          Rare earths, critical minerals and metals from sellers who prove their claims. Every value shows its evidence, funds sit with a
          licensed escrow partner, and an independent agency inspects the goods before shipment.
        </p>
        <div className="steps">
          <div className="step"><b>Evidence on every value</b><span className="muted small">Verified, documented or seller-stated</span></div>
          <div className="step"><b>Compare offers at landed cost</b><span className="muted small">Ask several sellers, choose one</span></div>
          <div className="step"><b>Escrow and inspection</b><span className="muted small">Money moves only on verified milestones</span></div>
          <div className="step"><b>Staged release</b><span className="muted small">Sellers are paid as milestones are met</span></div>
        </div>
      </section>

      <div className="card" style={{ marginBottom: 16 }}>
        <div className="form">
          <label className="field">Search<input value={text} onChange={(e) => setText(e.target.value)} placeholder="e.g. NdPr oxide, copper cathode" /></label>
          <label className="field">Family
            <select value={family} onChange={(e) => setFamily(e.target.value)}>
              <option value="">All</option>
              {FAMILIES.map((f) => <option key={f}>{f}</option>)}
            </select>
          </label>
          <label className="field">Incoterm
            <select value={incoterm} onChange={(e) => setIncoterm(e.target.value)}>
              <option value="">Any</option>
              {INCOTERMS.map((i) => <option key={i}>{i}</option>)}
            </select>
          </label>
          <label className="field">Evidence
            <select value={badge} onChange={(e) => setBadge(e.target.value)}>
              <option value="">Any listing</option>
              <option value="Documented">Documented or better</option>
              <option value="Verified">Verified only</option>
            </select>
          </label>
        </div>
      </div>

      <Status error={error} />
      {loading ? <Loading what="Loading the catalog" /> : shown.length === 0 ? (
        <Empty>No listings match. <Link to="/buy">Post a request for quotation</Link> and matching sellers are invited automatically.</Empty>
      ) : (
        <div className="grid">
          {shown.map((e) => (
            <Link key={e.gc_catalogentryid} to={`/listing/${e.gc_catalogentryid}`} className="card listing-card" style={{ color: "inherit", textDecoration: "none" }}>
              <div className="row" style={{ justifyContent: "space-between" }}>
                <Badge badge={e.gc_badge} />
                <span className="muted small">{e.gc_origincountry ?? "Origin on request"}</span>
              </div>
              <h3 style={{ margin: 0 }}>{e.gc_commodity}{e.gc_grade ? ` · ${e.gc_grade}` : ""}</h3>
              <div className="price">{money(e.gc_askprice, e.gc_currency)}<span className="muted small">{e.gc_askprice ? ` / ${e.gc_unit ?? "unit"}` : ""}</span></div>
              <div className="row small muted">
                <span>{qty(e.gc_availablequantity ?? e.gc_quantity, e.gc_unit)} available</span>
                <span>·</span>
                <span>{e.gc_incoterm ?? "Incoterm open"}{e.gc_namedplace ? ` ${e.gc_namedplace}` : ""}</span>
              </div>
              <div className="row" style={{ gap: 6 }}>
                {facts(e).slice(0, 3).map((f) => (
                  <span key={f.key} className="small">{f.name}: {f.value} <Evidence status={f.status} /></span>
                ))}
              </div>
            </Link>
          ))}
        </div>
      )}
    </>
  );
}
