// Power Pages Web API client. Reads are scoped by table permissions; every write is also checked by the
// PortalGuardPlugin in Dataverse (ownership, protected fields, allowed status changes).

export const B = 303300000; // choice value base (publisher prefix 30330)

export interface PortalUser {
  userName: string;
  firstName: string;
  lastName: string;
  email?: string;
  contactId?: string;
  userRoles?: string[];
}

interface PortalGlobal {
  Microsoft?: { Dynamic365?: { Portal?: { User?: PortalUser; tenant?: string } } };
}

export function portalUser(): PortalUser | null {
  const user = (window as unknown as PortalGlobal).Microsoft?.Dynamic365?.Portal?.User;
  return user && user.userName ? user : null;
}

export function portalTenant(): string {
  return (window as unknown as PortalGlobal).Microsoft?.Dynamic365?.Portal?.tenant ?? "";
}

let tokenPromise: Promise<string> | null = null;

/** Anti-forgery token required on every Web API write. */
export function antiForgeryToken(): Promise<string> {
  if (!tokenPromise) {
    tokenPromise = fetch("/_layout/tokenhtml")
      .then((r) => r.text())
      .then((html) => {
        const m = html.match(/value="([^"]+)"/);
        return m ? m[1] : "";
      })
      .catch(() => "");
  }
  return tokenPromise;
}

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function errorFrom(res: Response): Promise<ApiError> {
  let message = `Request failed (${res.status}).`;
  try {
    const body = await res.json();
    message = body?.error?.innererror?.message || body?.error?.message || message;
  } catch {
    /* not JSON */
  }
  // Messages thrown by the DealOS guard are written for users; strip the platform prefix if present.
  message = message.replace(/^.*?InvalidPluginExecutionException:\s*/i, "");
  return new ApiError(res.status, message);
}

export async function get<T>(path: string): Promise<T> {
  const res = await fetch(`/_api/${path}`, { headers: { Accept: "application/json" } });
  if (!res.ok) throw await errorFrom(res);
  return res.json() as Promise<T>;
}

export async function list<T>(path: string): Promise<T[]> {
  const body = await get<{ value: T[] }>(path);
  return body.value ?? [];
}

async function write(method: string, path: string, body?: unknown, extraHeaders: Record<string, string> = {}): Promise<Response> {
  const token = await antiForgeryToken();
  const res = await fetch(`/_api/${path}`, {
    method,
    headers: { "Content-Type": "application/json", Accept: "application/json", __RequestVerificationToken: token, ...extraHeaders },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw await errorFrom(res);
  return res;
}

/** Creates a record and returns its id (from the OData-EntityId header). */
export async function create(entitySet: string, body: Record<string, unknown>): Promise<string> {
  const res = await write("POST", entitySet, body);
  const location = res.headers.get("OData-EntityId") ?? res.headers.get("entityid") ?? "";
  const m = location.match(/\(([0-9a-f-]{36})\)/i);
  return m ? m[1] : res.headers.get("entityid") ?? "";
}

export async function update(entitySet: string, id: string, body: Record<string, unknown>): Promise<void> {
  await write("PATCH", `${entitySet}(${id})`, body);
}

export async function remove(entitySet: string, id: string): Promise<void> {
  await write("DELETE", `${entitySet}(${id})`);
}

/** Uploads a file into a file column (single request; files up to 16 MB). */
export async function uploadFile(entitySet: string, id: string, column: string, file: File): Promise<void> {
  const token = await antiForgeryToken();
  const res = await fetch(`/_api/${entitySet}(${id})/${column}?x-ms-file-name=${encodeURIComponent(file.name)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/octet-stream", __RequestVerificationToken: token },
    body: file,
  });
  if (!res.ok) throw await errorFrom(res);
}

export function label(value: number | null | undefined, labels: string[]): string {
  if (value === null || value === undefined) return "—";
  return labels[value - B] ?? "—";
}

export const INCOTERMS = ["EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP"];
export const UNITS = ["MT", "DMT", "WMT", "Kg", "Lb", "Troy Oz", "Short Ton", "Long Ton", "Flask", "Unit"];
export const LISTING_STATUS = ["Draft", "Submitted", "In verification", "Needs info", "Published", "Suspended", "Withdrawn", "Sold out"];
export const RFQ_STATUS = ["Draft", "Open", "Matched", "Fulfilled", "Expired", "Withdrawn"];
export const INVITE_STATUS = ["Invited", "Viewed", "Accepted", "Declined", "Expired", "Withdrawn"];
export const MATCH_STATUS = ["Proposed", "Buyer interested", "Mutual", "Declined", "Expired"];
export const DEAL_STAGE = ["Inquiry", "Negotiation", "Terms agreed", "Compliance check", "Contracting", "Signed", "Awaiting funding",
  "Funded", "In transit", "Delivered", "Settled", "Closed", "Cancelled"];
export const OFFER_STATUS = ["Open", "Countered", "Accepted", "Rejected", "Expired", "Withdrawn"];
export const KYB_STATUS = ["Not started", "In progress", "Passed", "Failed", "Referred"];
export const TRUST_TIER = ["Unverified", "Basic", "KYB verified", "Trade verified", "Trusted"];
export const FACT_STATUS = ["Verified", "Documented", "Claimed", "Unverified", "Conflicting", "Outdated", "Missing", "Not applicable"];
export const DOC_TYPES = ["Assay", "Certificate of analysis", "Inspection report", "Mining licence", "Export permit", "Certificate of origin",
  "Incorporation certificate", "Shareholder register", "UBO declaration", "ID document", "Proof of funds", "Bank reference", "LOI", "ICPO", "FCO",
  "SPA", "Contract", "Invoice", "Packing list", "Bill of lading", "Insurance certificate", "Warehouse receipt", "Photo", "Production report",
  "Technical report", "Mandate", "Chat export", "Other", "Unknown"];

export function money(amount: number | null | undefined, currency?: string | null): string {
  if (amount === null || amount === undefined) return "Price on request";
  return `${currency ?? ""} ${amount.toLocaleString(undefined, { maximumFractionDigits: 2 })}`.trim();
}

export function qty(amount: number | null | undefined, unit?: string | null): string {
  if (amount === null || amount === undefined) return "—";
  return `${amount.toLocaleString(undefined, { maximumFractionDigits: 3 })} ${unit ?? ""}`.trim();
}

export function day(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }) : "—";
}
