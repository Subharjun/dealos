import { useState } from "react";
import { B, DOC_TYPES, KYB_STATUS, TRUST_TIER, create, day, label, list, update, uploadFile } from "../api";
import { Loading, SignIn, Status, useAction, useLoad } from "../components";
import { useSession } from "../session";

interface Country { gc_countryid: string; gc_name: string }
interface Doc { gc_documentid: string; gc_filename: string | null; gc_name: string; gc_doctype: number | null; gc_parsestatus: number | null; createdon: string }

const ROLES: [number, string, string][] = [
  [B + 1, "Buyer", "Buy material, post RFQs and compare offers"],
  [B, "Seller", "List material and answer RFQs"],
  [B + 2, "Intermediary", "Trade on behalf of principals (mandate required)"],
];
const PARSE = ["Waiting to be read", "Read", "Could not be read", "Quarantined"];
const KYB_DOCS = [B + 6, B + 7, B + 8, B + 9, B + 10, B + 11, B + 27]; // incorporation, shareholders, UBO, ID, proof of funds, bank reference, other

export default function Company() {
  const { user, company, loading, refresh } = useSession();
  if (!user) {
    return (
      <div className="card empty stack" style={{ alignItems: "center" }}>
        <h2>Sign in to set up your company</h2>
        <SignIn />
      </div>
    );
  }
  if (loading) return <Loading />;
  return company ? <Profile /> : <Onboarding onDone={refresh} />;
}

function Onboarding({ onDone }: { onDone: () => Promise<void> }) {
  const { user } = useSession();
  const countries = useLoad(() => list<Country>("gc_countries?$select=gc_countryid,gc_name&$orderby=gc_name&$top=300"), []);
  const [name, setName] = useState("");
  const [reg, setReg] = useState("");
  const [country, setCountry] = useState("");
  const [roles, setRoles] = useState<number[]>([B + 1]);
  const action = useAction();

  const save = () => action.run(async () => {
    const body: Record<string, unknown> = {
      name: name.trim(),
      gc_registrationnumber: reg.trim() || null,
      gc_partyrole: roles.join(","),
      "primarycontactid@odata.bind": `/contacts(${user!.contactId})`,
    };
    if (country) body["gc_country@odata.bind"] = `/gc_countries(${country})`;
    const id = await create("accounts", body);
    await update("contacts", user!.contactId!, { "parentcustomerid_account@odata.bind": `/accounts(${id})` });
    await onDone();
  });

  return (
    <div className="stack" style={{ maxWidth: 760 }}>
      <div>
        <h1>Set up your company</h1>
        <p className="muted">DealOS checks every company before it trades (KYB). Start with the basics; our onboarding agent tells you which documents are needed next.</p>
      </div>
      <div className="card stack">
        <div className="form">
          <label className="field wide">Registered company name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
          <label className="field">Registration number (CIN, company no.)<input value={reg} onChange={(e) => setReg(e.target.value)} /></label>
          <label className="field">Country of registration
            <select value={country} onChange={(e) => setCountry(e.target.value)}>
              <option value="">Choose…</option>
              {(countries.data ?? []).map((c) => <option key={c.gc_countryid} value={c.gc_countryid}>{c.gc_name}</option>)}
            </select>
          </label>
          <div className="field wide">
            <span>We want to</span>
            <div className="checks">
              {ROLES.map(([value, text, help]) => (
                <label key={value} title={help}>
                  <input type="checkbox" checked={roles.includes(value)} onChange={(e) => setRoles(e.target.checked ? [...roles, value] : roles.filter((r) => r !== value))} />
                  {text}
                </label>
              ))}
            </div>
          </div>
        </div>
        <Status error={action.error} />
        <div><button className="primary" disabled={!name.trim() || roles.length === 0 || action.busy} onClick={save}>Create company profile</button></div>
      </div>
    </div>
  );
}

function Profile() {
  const { company, refresh } = useSession();
  const docs = useLoad(() => list<Doc>(`gc_documents?$select=gc_documentid,gc_filename,gc_name,gc_doctype,gc_parsestatus,createdon&$filter=_gc_listing_value eq null and _gc_deal_value eq null&$orderby=createdon desc`), [company?.accountid]);
  const [docType, setDocType] = useState(String(B + 6));
  const [file, setFile] = useState<File | null>(null);
  const upload = useAction();
  const roles = (company!.gc_partyrole ?? "").split(",").map(Number);
  const rolesAction = useAction();

  const send = () => upload.run(async () => {
    const id = await create("gc_documents", { gc_name: file!.name.slice(0, 100), gc_filename: file!.name.slice(0, 200), gc_doctype: Number(docType),
      gc_mimetype: file!.type || "application/octet-stream", "gc_account@odata.bind": `/accounts(${company!.accountid})` });
    await uploadFile("gc_documents", id, "gc_file", file!);
    setFile(null);
    docs.reload();
  }, "Uploaded. Our document agent reads it within a few minutes.");

  const toggleRole = (value: number, on: boolean) => rolesAction.run(async () => {
    const next = on ? [...roles, value] : roles.filter((r) => r !== value);
    await update("accounts", company!.accountid, { gc_partyrole: next.join(",") });
    await refresh();
  });

  return (
    <div className="stack">
      <div className="page-head">
        <div><h1>{company!.name}</h1><p className="muted">Company profile and checks</p></div>
      </div>
      <div className="split">
        <div className="card stack">
          <h2>Company checks (KYB)</h2>
          <dl className="kv">
            <dt>Status</dt><dd><span className="pill accent">{label(company!.gc_kybstatus, KYB_STATUS)}</span></dd>
            <dt>Trust tier</dt><dd><span className="pill neutral">{label(company!.gc_trusttier, TRUST_TIER)}</span></dd>
            <dt>Registration</dt><dd>{company!.gc_registrationnumber ?? "—"}</dd>
          </dl>
          <p className="muted small">
            Upload your incorporation certificate, shareholder register / UBO declaration and a director's ID. Buyers also add proof of funds or a bank reference.
            A verification officer approves each tier; the onboarding agent never can.
          </p>
          <div className="form">
            <label className="field">Document type
              <select value={docType} onChange={(e) => setDocType(e.target.value)}>
                {KYB_DOCS.map((v) => <option key={v} value={v}>{label(v, DOC_TYPES)}</option>)}
              </select>
            </label>
            <label className="field">File (PDF or image)<input type="file" accept=".pdf,image/*" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></label>
          </div>
          <div><button className="primary" disabled={!file || upload.busy} onClick={send}>Upload</button></div>
          <Status error={upload.error} done={upload.done} />
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Document</th><th>Type</th><th>Status</th><th>Uploaded</th></tr></thead>
              <tbody>
                {(docs.data ?? []).map((d) => (
                  <tr key={d.gc_documentid}><td>{d.gc_filename ?? d.gc_name}</td><td>{label(d.gc_doctype, DOC_TYPES)}</td><td>{label(d.gc_parsestatus, PARSE)}</td><td>{day(d.createdon)}</td></tr>
                ))}
                {(docs.data ?? []).length === 0 ? <tr><td colSpan={4} className="muted">No company documents yet.</td></tr> : null}
              </tbody>
            </table>
          </div>
        </div>
        <div className="card stack">
          <h2>Roles</h2>
          <div className="checks" style={{ flexDirection: "column" }}>
            {ROLES.map(([value, text, help]) => (
              <label key={value}>
                <input type="checkbox" checked={roles.includes(value)} disabled={rolesAction.busy} onChange={(e) => toggleRole(value, e.target.checked)} />
                <span>{text}<br /><span className="muted small">{help}</span></span>
              </label>
            ))}
          </div>
          <Status error={rolesAction.error} />
        </div>
      </div>
    </div>
  );
}
